using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using Seamstress.Core.IO;

namespace Seamstress.Mcp;

/// <summary>An 8-bit RGBA picture.</summary>
public sealed record RgbaImage(int Width, int Height, byte[] Rgba)
{
    public static RgbaImage Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"The image '{path}' does not exist.", path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G')
            return DecodePng(bytes) ?? throw new InvalidDataException($"'{path}' is a PNG this reader does not handle (interlaced or an unusual bit depth); save it as an ordinary 8-bit PNG.");
        if (WebpDecoder.IsWebp(bytes) && WebpDecoder.TryDecode(path, out int w, out int h, out var rgba))
            return new RgbaImage(w, h, rgba);
        if (OperatingSystem.IsWindows() && ConvertWithWindows(path) is { } converted)
            return converted;
        throw new InvalidDataException($"Cannot read '{path}': use a PNG or WebP picture.");
    }

    /// <summary>Other formats (JPEG, BMP, GIF, TIFF) through the Windows imaging that PowerShell carries.</summary>
    private static RgbaImage? ConvertWithWindows(string path)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"seamstress-ref-{Guid.NewGuid():N}.png");
        try
        {
            string Quote(string s) => "'" + s.Replace("'", "''") + "'";
            var start = new ProcessStartInfo("powershell.exe")
            {
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-Command",
                    $"Add-Type -AssemblyName System.Drawing; $i = [System.Drawing.Image]::FromFile({Quote(Path.GetFullPath(path))}); "
                    + $"$i.Save({Quote(temp)}, [System.Drawing.Imaging.ImageFormat]::Png); $i.Dispose()",
                },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var process = Process.Start(start);
            if (process == null || !process.WaitForExit(30000) || !File.Exists(temp))
                return null;
            return DecodePng(File.ReadAllBytes(temp));
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>8- or 16-bit, non-interlaced PNG of any colour type, with its transparency.</summary>
    public static RgbaImage? DecodePng(byte[] file)
    {
        try
        {
            int width = 0, height = 0, colorType = 0, depth = 8, at = 8;
            byte[]? palette = null, trns = null;
            using var idat = new MemoryStream();
            while (at + 8 <= file.Length)
            {
                int length = (file[at] << 24) | (file[at + 1] << 16) | (file[at + 2] << 8) | file[at + 3];
                string type = System.Text.Encoding.ASCII.GetString(file, at + 4, 4);
                int data = at + 8;
                if (type == "IHDR")
                {
                    width = (file[data] << 24) | (file[data + 1] << 16) | (file[data + 2] << 8) | file[data + 3];
                    height = (file[data + 4] << 24) | (file[data + 5] << 16) | (file[data + 6] << 8) | file[data + 7];
                    depth = file[data + 8];
                    colorType = file[data + 9];
                    if ((depth != 8 && depth != 16) || file[data + 12] != 0)
                        return null;
                }
                else if (type == "PLTE")
                    palette = file.AsSpan(data, length).ToArray();
                else if (type == "tRNS")
                    trns = file.AsSpan(data, length).ToArray();
                else if (type == "IDAT")
                    idat.Write(file, data, length);
                else if (type == "IEND")
                    break;
                at = data + length + 4;
            }
            int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
            if (channels == 0 || width <= 0 || height <= 0 || (colorType == 3 && depth != 8))
                return null;
            int bpp = channels * depth / 8;
            int stride = width * bpp;
            var raw = new byte[(stride + 1) * height];
            idat.Position = 0;
            using (var z = new ZLibStream(idat, CompressionMode.Decompress))
                z.ReadExactly(raw);
            var rgba = new byte[width * height * 4];
            byte[] previous = new byte[stride], line = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                int filter = raw[y * (stride + 1)];
                Buffer.BlockCopy(raw, y * (stride + 1) + 1, line, 0, stride);
                for (int i = 0; i < stride; i++)
                {
                    int left = i >= bpp ? line[i - bpp] : 0, up = previous[i], upLeft = i >= bpp ? previous[i - bpp] : 0;
                    int add = filter switch
                    {
                        1 => left,
                        2 => up,
                        3 => (left + up) / 2,
                        4 => Paeth(left, up, upLeft),
                        _ => 0,
                    };
                    line[i] = (byte)(line[i] + add);
                }
                int step = depth / 8;
                for (int x = 0; x < width; x++)
                {
                    int o = 4 * (y * width + x);
                    byte S(int c) => line[(x * channels + c) * step];
                    switch (colorType)
                    {
                        case 0:
                            rgba[o] = rgba[o + 1] = rgba[o + 2] = S(0);
                            rgba[o + 3] = 255;
                            break;
                        case 2:
                            rgba[o] = S(0); rgba[o + 1] = S(1); rgba[o + 2] = S(2);
                            rgba[o + 3] = 255;
                            break;
                        case 3:
                            int index = line[x];
                            if (palette != null && index * 3 + 2 < palette.Length)
                            {
                                rgba[o] = palette[index * 3]; rgba[o + 1] = palette[index * 3 + 1]; rgba[o + 2] = palette[index * 3 + 2];
                            }
                            rgba[o + 3] = trns != null && index < trns.Length ? trns[index] : (byte)255;
                            break;
                        case 4:
                            rgba[o] = rgba[o + 1] = rgba[o + 2] = S(0);
                            rgba[o + 3] = S(1);
                            break;
                        default:
                            rgba[o] = S(0); rgba[o + 1] = S(1); rgba[o + 2] = S(2); rgba[o + 3] = S(3);
                            break;
                    }
                }
                (previous, line) = (line, previous);
            }
            return new RgbaImage(width, height, rgba);
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Box-filtered resize (area average when shrinking, nearest when growing).</summary>
    public RgbaImage Resize(int width, int height)
    {
        var result = new byte[width * height * 4];
        float sx = Width / (float)width, sy = Height / (float)height;
        for (int y = 0; y < height; y++)
        {
            int y0 = (int)(y * sy), y1 = Math.Max(y0 + 1, Math.Min(Height, (int)((y + 1) * sy)));
            for (int x = 0; x < width; x++)
            {
                int x0 = (int)(x * sx), x1 = Math.Max(x0 + 1, Math.Min(Width, (int)((x + 1) * sx)));
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int yy = y0; yy < y1 && yy < Height; yy++)
                {
                    for (int xx = x0; xx < x1 && xx < Width; xx++)
                    {
                        int i = 4 * (yy * Width + xx);
                        r += Rgba[i]; g += Rgba[i + 1]; b += Rgba[i + 2]; a += Rgba[i + 3];
                        n++;
                    }
                }
                int o = 4 * (y * width + x);
                n = Math.Max(n, 1);
                result[o] = (byte)(r / n); result[o + 1] = (byte)(g / n); result[o + 2] = (byte)(b / n); result[o + 3] = (byte)(a / n);
            }
        }
        return new RgbaImage(width, height, result);
    }
}

/// <summary>
/// Puts a render of the outfit next to a reference picture taken with the same camera and scores the match: how well
/// the silhouettes overlap, how wide the figure is at a few heights, and how far apart the colours are per region.
/// </summary>
public static class ReferenceComparer
{
    /// <summary>Regions of the figure from the top of its silhouette (0) to the bottom (1).</summary>
    public static readonly (string Name, float Top, float Bottom)[] Regions =
    {
        ("head", 0f, 0.13f),
        ("shoulders_chest", 0.13f, 0.35f),
        ("waist_hips", 0.35f, 0.55f),
        ("legs", 0.55f, 0.88f),
        ("feet", 0.88f, 1f),
    };

    public static object Compare(string referencePath, string outputPath, int panelHeight, Action<string, int, int> render)
    {
        var original = RgbaImage.Load(referencePath);
        int h = panelHeight;
        int w = Math.Clamp((int)MathF.Round(h * original.Width / (float)original.Height), 32, 4 * h);
        var reference = original.Resize(w, h);
        string renderPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
            Path.GetFileNameWithoutExtension(outputPath) + "_render.png");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        render(renderPath, w, h);
        var rendered = RgbaImage.Load(renderPath);
        if (rendered.Width != w || rendered.Height != h)
            rendered = rendered.Resize(w, h);

        var refMask = Segment(reference);
        var renMask = SegmentRender(rendered);
        var scores = Score(reference, refMask, rendered, renMask);
        WriteSideBySide(outputPath, reference, rendered, refMask, renMask, scores.Aligned);

        return new
        {
            ok = true,
            sideBySidePath = Path.GetFullPath(outputPath),
            renderPath = Path.GetFullPath(renderPath),
            panels = "reference | render | silhouettes scaled to the same height (red: reference only, cyan: render only, white: both)",
            silhouette = new
            {
                iou = scores.Iou,
                iouSameFrame = scores.IouSameFrame,
                referenceCoverage = scores.ReferenceCoverage,
                renderCoverage = scores.RenderCoverage,
                referenceAspect = scores.ReferenceAspect,
                renderAspect = scores.RenderAspect,
                widthRatios = scores.WidthRatios.Select(r => new { atHeight = r.At, renderOverReference = r.Ratio }),
                segmentation = scores.ReferenceSegmentation,
            },
            palette = scores.Palettes.Select(p => new
            {
                region = p.Region,
                deltaE = p.DeltaE,
                meanDeltaE = p.MeanDeltaE,
                reference = p.Reference.Select(c => new { hex = c.Hex, share = c.Share }),
                render = p.Render.Select(c => new { hex = c.Hex, share = c.Share }),
            }),
            note = "iou compares the silhouettes after scaling both figures to the same height (1 = identical; above 0.85 is "
                + "close, below 0.7 the outline differs a lot). widthRatios under 1 mean the render is narrower than the "
                + "reference at that height (0 = top of the figure). deltaE is a CIELAB colour distance between the regions' "
                + "palettes: under 10 is close, over 25 clearly different. Background segmentation of the reference is a guess: "
                + "check the silhouettes panel before trusting the numbers.",
        };
    }

    public sealed record WidthRatio(float At, float Ratio);
    public sealed record PaletteColour(string Hex, float Share, Vector3 Lab);
    public sealed record RegionPalette(string Region, float DeltaE, float MeanDeltaE, List<PaletteColour> Reference, List<PaletteColour> Render);

    public sealed record Scores(float Iou, float IouSameFrame, float ReferenceCoverage, float RenderCoverage, float ReferenceAspect,
        float RenderAspect, List<WidthRatio> WidthRatios, List<RegionPalette> Palettes, string ReferenceSegmentation, Aligned Aligned);

    /// <summary>A mask's bounding box and the scale that brings its height to the common one.</summary>
    public sealed record Frame(int MinX, int MinY, int MaxX, int MaxY)
    {
        public int Width => MaxX - MinX + 1;
        public int Height => MaxY - MinY + 1;
        public float CentreX => (MinX + MaxX) * 0.5f;
    }

    public sealed record Aligned(int Width, int Height, bool[] Reference, bool[] Render);

    public static Scores Score(RgbaImage reference, bool[] refMask, RgbaImage rendered, bool[] renMask)
    {
        int w = reference.Width, h = reference.Height;
        float sameFrame = Iou(refMask, renMask);
        var rf = Bounds(refMask, w, h);
        var nf = Bounds(renMask, rendered.Width, rendered.Height);
        if (rf == null || nf == null)
        {
            return new Scores(0, sameFrame, Coverage(refMask), Coverage(renMask), 0, 0, new(), new(),
                rf == null ? "no figure found in the reference" : "nothing rendered", new Aligned(1, 1, new bool[1], new bool[1]));
        }
        const int H = 256;
        float wide = MathF.Max(rf.Width / (float)rf.Height, nf.Width / (float)nf.Height);
        int W = Math.Clamp((int)MathF.Ceiling(H * wide * 1.1f) + 4, 16, 4 * H);
        var a = Normalise(refMask, w, rf, W, H);
        var b = Normalise(renMask, rendered.Width, nf, W, H);
        var ratios = new List<WidthRatio>();
        for (int k = 1; k <= 9; k++)
        {
            int row = Math.Clamp((int)(k / 10f * H), 0, H - 1);
            float wa = RowWidth(a, W, row), wb = RowWidth(b, W, row);
            if (wa > 0)
                ratios.Add(new WidthRatio(k / 10f, MathF.Round(wb / wa, 2)));
        }
        var palettes = new List<RegionPalette>();
        foreach (var (name, top, bottom) in Regions)
        {
            var pa = Palette(reference, refMask, rf, top, bottom);
            var pb = Palette(rendered, renMask, nf, top, bottom);
            if (pa.Count == 0 || pb.Count == 0)
                continue;
            palettes.Add(new RegionPalette(name, MathF.Round(PaletteDistance(pa, pb), 1),
                MathF.Round(Vector3.Distance(MeanLab(pa), MeanLab(pb)), 1), pa, pb));
        }
        return new Scores(MathF.Round(Iou(a, b), 3), MathF.Round(sameFrame, 3), MathF.Round(Coverage(refMask), 3),
            MathF.Round(Coverage(renMask), 3), MathF.Round(rf.Width / (float)rf.Height, 3), MathF.Round(nf.Width / (float)nf.Height, 3),
            ratios, palettes, LastSegmentation, new Aligned(W, H, a, b));
    }

    private static float Coverage(bool[] mask) => mask.Count(m => m) / (float)Math.Max(1, mask.Length);

    public static float Iou(bool[] a, bool[] b)
    {
        int both = 0, either = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] && b[i]) both++;
            if (a[i] || b[i]) either++;
        }
        return either == 0 ? 0 : both / (float)either;
    }

    public static Frame? Bounds(bool[] mask, int w, int h)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (mask[y * w + x])
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
        return maxX < 0 ? null : new Frame(minX, minY, maxX, maxY);
    }

    /// <summary>The mask scaled so its figure is <paramref name="H"/> high, centred in a W x H canvas.</summary>
    private static bool[] Normalise(bool[] mask, int w, Frame f, int W, int H)
    {
        var result = new bool[W * H];
        float scale = f.Height / (float)H;
        for (int y = 0; y < H; y++)
        {
            int sy = Math.Clamp(f.MinY + (int)((y + 0.5f) * scale), f.MinY, f.MaxY);
            for (int x = 0; x < W; x++)
            {
                int sx = (int)MathF.Round(f.CentreX + (x + 0.5f - W * 0.5f) * scale);
                if (sx >= f.MinX && sx <= f.MaxX)
                    result[y * W + x] = mask[sy * w + sx];
            }
        }
        return result;
    }

    private static float RowWidth(bool[] mask, int W, int row)
    {
        int first = -1, last = -1;
        for (int x = 0; x < W; x++)
        {
            if (!mask[row * W + x])
                continue;
            if (first < 0) first = x;
            last = x;
        }
        return first < 0 ? 0 : last - first + 1;
    }

    // ---- Segmentation --------------------------------------------------------------------------------------------

    [ThreadStatic] private static string? _lastSegmentation;
    private static string LastSegmentation => _lastSegmentation ?? "";

    /// <summary>
    /// The figure in a reference picture: its alpha when it has a transparent background, otherwise everything not
    /// reached by growing the background in from the border. The background may shade gradually (a vignette, a glow);
    /// the figure's outline is a sharper step.
    /// </summary>
    public static bool[] Segment(RgbaImage image)
    {
        int w = image.Width, h = image.Height, n = w * h;
        int transparent = 0;
        for (int i = 0; i < n; i++)
            if (image.Rgba[4 * i + 3] < 128)
                transparent++;
        if (transparent > n * 0.02f)
        {
            _lastSegmentation = "alpha channel";
            var alpha = new bool[n];
            for (int i = 0; i < n; i++)
                alpha[i] = image.Rgba[4 * i + 3] >= 128;
            return KeepFigure(alpha, w, h);
        }

        var lab = new Vector3[n];
        for (int i = 0; i < n; i++)
            lab[i] = Lab(image.Rgba[4 * i], image.Rgba[4 * i + 1], image.Rgba[4 * i + 2]);
        // Background colours from a thin ring round the picture.
        int ring = Math.Max(2, Math.Min(w, h) / 50);
        var border = new List<Vector3>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (x < ring || y < ring || x >= w - ring || y >= h - ring)
                    border.Add(lab[y * w + x]);
        // Colours covering only a little of the border are the figure touching the edge (feet, a hand), not background.
        var model = KMeans(border.Select(c => (c, 1f)).ToList(), 6)
            .Where(c => c.Weight >= 0.08f * border.Count)
            .Select(c => c.Centre).ToList();
        float Far(Vector3 c) => model.Min(m => Vector3.Distance(m, c));
        var borderFar = border.Select(Far).OrderBy(d => d).ToList();
        float global = Math.Clamp(borderFar[(int)(borderFar.Count * 0.85f)] * 2.5f, 10f, 25f);
        const float step = 3.2f;   // largest colour change between neighbours inside the background

        var background = new bool[n];
        var queue = new Queue<int>();
        void Seed(int i)
        {
            if (!background[i] && Far(lab[i]) < global)
            {
                background[i] = true;
                queue.Enqueue(i);
            }
        }
        for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
        // Grow through gentle gradients only: a step bigger than `step` from the neighbour, or a colour far from
        // anything at the border, stops the background.
        float loose = MathF.Max(global * 3f, 40f);
        // Cloth, skin and hair have texture; a backdrop is smooth even where it is as dark as the clothes.
        var rough = new float[n];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float r = 0;
                if (x > 0) r = MathF.Max(r, Vector3.Distance(lab[i], lab[i - 1]));
                if (x < w - 1) r = MathF.Max(r, Vector3.Distance(lab[i], lab[i + 1]));
                if (y > 0) r = MathF.Max(r, Vector3.Distance(lab[i], lab[i - w]));
                if (y < h - 1) r = MathF.Max(r, Vector3.Distance(lab[i], lab[i + w]));
                rough[i] = r;
            }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % w, y = i / w;
            foreach (int j in new[] { x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, y > 0 ? i - w : -1, y < h - 1 ? i + w : -1 })
            {
                if (j < 0 || background[j])
                    continue;
                float near = Vector3.Distance(lab[i], lab[j]);
                float far = Far(lab[j]);
                if (near < step && rough[j] < 2f * step && far < loose)
                {
                    background[j] = true;
                    queue.Enqueue(j);
                }
            }
        }
        var figure = new bool[n];
        for (int i = 0; i < n; i++)
            figure[i] = !background[i];
        // How far the figure's colours are from the backdrop's: a dark figure on a dark backdrop cannot be told apart
        // by colour, and the silhouette is then a rough guess.
        var inside = Enumerable.Range(0, n).Where(i => figure[i]).Select(i => Far(lab[i])).OrderBy(d => d).ToList();
        float contrast = inside.Count > 0 ? inside[inside.Count / 2] : 0f;
        _lastSegmentation = contrast < 20f
            ? $"background grown from the border; LOW CONTRAST (median figure colour {contrast:0} deltaE from the backdrop): the "
              + "silhouette is unreliable, use a reference with a transparent or plain light background"
            : $"background grown from the border ({model.Count} backdrop colours, median figure contrast {contrast:0} deltaE)";
        return KeepFigure(Tidy(figure, w, h), w, h);
    }

    /// <summary>The render's figure: everything that is not the renderer's flat background colour.</summary>
    public static bool[] SegmentRender(RgbaImage image)
    {
        int w = image.Width, h = image.Height;
        var bg = Lab(image.Rgba[0], image.Rgba[1], image.Rgba[2]);
        var mask = new bool[w * h];
        for (int i = 0; i < mask.Length; i++)
            mask[i] = Vector3.Distance(Lab(image.Rgba[4 * i], image.Rgba[4 * i + 1], image.Rgba[4 * i + 2]), bg) > 2.5f;
        return mask;
    }

    /// <summary>One pass of erosion (or dilation) with a 3 x 3 square.</summary>
    private static bool[] Morph(bool[] src, int w, int h, bool erode)
    {
        var dst = new bool[src.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool v = erode;
                for (int dy = -1; dy <= 1 && v == erode; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = Math.Clamp(x + dx, 0, w - 1), yy = Math.Clamp(y + dy, 0, h - 1);
                        if (src[yy * w + xx] != erode)
                        {
                            v = !erode;
                            break;
                        }
                    }
                dst[y * w + x] = v;
            }
        return dst;
    }

    /// <summary>
    /// Tidies a figure mask: specks removed, narrow cracks closed (where the background leaked in along a soft
    /// edge), and holes that do not reach the border filled.
    /// </summary>
    private static bool[] Tidy(bool[] mask, int w, int h)
    {
        mask = Morph(Morph(mask, w, h, true), w, h, false);
        int r = Math.Max(2, h / 160);
        for (int i = 0; i < r; i++)
            mask = Morph(mask, w, h, false);
        for (int i = 0; i < r; i++)
            mask = Morph(mask, w, h, true);
        var outside = new bool[mask.Length];
        var queue = new Queue<int>();
        void Seed(int i)
        {
            if (!mask[i] && !outside[i])
            {
                outside[i] = true;
                queue.Enqueue(i);
            }
        }
        for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % w, y = i / w;
            if (x > 0) Seed(i - 1);
            if (x < w - 1) Seed(i + 1);
            if (y > 0) Seed(i - w);
            if (y < h - 1) Seed(i + w);
        }
        var result = new bool[mask.Length];
        for (int i = 0; i < mask.Length; i++)
            result[i] = !outside[i];
        return result;
    }

    /// <summary>The largest connected part and every part at least a twentieth of its size.</summary>
    private static bool[] KeepFigure(bool[] mask, int w, int h)
    {
        var label = new int[mask.Length];
        var sizes = new List<int> { 0 };
        var queue = new Queue<int>();
        for (int s = 0; s < mask.Length; s++)
        {
            if (!mask[s] || label[s] != 0)
                continue;
            int id = sizes.Count, size = 0;
            label[s] = id;
            queue.Enqueue(s);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                size++;
                int x = i % w, y = i / w;
                foreach (int j in new[] { x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, y > 0 ? i - w : -1, y < h - 1 ? i + w : -1 })
                {
                    if (j >= 0 && mask[j] && label[j] == 0)
                    {
                        label[j] = id;
                        queue.Enqueue(j);
                    }
                }
            }
            sizes.Add(size);
        }
        if (sizes.Count == 1)
            return mask;
        int largest = sizes.Max();
        var result = new bool[mask.Length];
        for (int i = 0; i < mask.Length; i++)
            result[i] = label[i] != 0 && sizes[label[i]] * 20 >= largest;
        return result;
    }

    // ---- Colour --------------------------------------------------------------------------------------------------

    public static Vector3 Lab(byte r, byte g, byte b)
    {
        static float L(byte v)
        {
            float c = v / 255f;
            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        float lr = L(r), lg = L(g), lb = L(b);
        float x = (0.4124f * lr + 0.3576f * lg + 0.1805f * lb) / 0.95047f;
        float y = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
        float z = (0.0193f * lr + 0.1192f * lg + 0.9505f * lb) / 1.08883f;
        static float F(float t) => t > 0.008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;
        float fx = F(x), fy = F(y), fz = F(z);
        return new Vector3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
    }

    public static string Hex(Vector3 lab)
    {
        float fy = (lab.X + 16f) / 116f, fx = fy + lab.Y / 500f, fz = fy - lab.Z / 200f;
        static float Inv(float t) => t * t * t > 0.008856f ? t * t * t : (t - 16f / 116f) / 7.787f;
        float x = Inv(fx) * 0.95047f, y = Inv(fy), z = Inv(fz) * 1.08883f;
        float r = 3.2406f * x - 1.5372f * y - 0.4986f * z;
        float g = -0.9689f * x + 1.8758f * y + 0.0415f * z;
        float b = 0.0557f * x - 0.2040f * y + 1.0570f * z;
        static int S(float v)
        {
            v = Math.Clamp(v, 0f, 1f);
            float s = v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;
            return (int)MathF.Round(s * 255f);
        }
        return $"#{S(r):X2}{S(g):X2}{S(b):X2}";
    }

    private static List<PaletteColour> Palette(RgbaImage image, bool[] mask, Frame f, float top, float bottom)
    {
        int y0 = f.MinY + (int)(top * f.Height), y1 = f.MinY + (int)(bottom * f.Height);
        var samples = new List<(Vector3, float)>();
        int stride = Math.Max(1, (int)MathF.Sqrt((y1 - y0) * f.Width / 4000f));
        for (int y = y0; y < Math.Min(y1, image.Height); y += stride)
            for (int x = f.MinX; x <= f.MaxX; x += stride)
            {
                int i = y * image.Width + x;
                // Inside the outline only: edge pixels are blended with the backdrop.
                if (mask[i] && x > 0 && x < image.Width - 1 && y > 0 && y < image.Height - 1
                    && mask[i - 1] && mask[i + 1] && mask[i - image.Width] && mask[i + image.Width])
                    samples.Add((Lab(image.Rgba[4 * i], image.Rgba[4 * i + 1], image.Rgba[4 * i + 2]), 1f));
            }
        if (samples.Count < 8)
            return new();
        float total = samples.Count;
        // A few stray pixels (a speck of backdrop, a bright highlight) would take seeds of their own: cluster into more
        // colours than are kept, and keep the main ones.
        var main = KMeans(samples, 6)
            .Where(c => c.Weight >= 0.03f * total)
            .OrderByDescending(c => c.Weight)
            .Take(4)
            .ToList();
        float kept = main.Sum(c => c.Weight);
        return main.Select(c => new PaletteColour(Hex(c.Centre), MathF.Round(c.Weight / kept, 2), c.Centre)).ToList();
    }

    private static Vector3 MeanLab(List<PaletteColour> p)
    {
        var sum = Vector3.Zero;
        float w = 0;
        foreach (var c in p)
        {
            sum += c.Lab * c.Share;
            w += c.Share;
        }
        return w > 0 ? sum / w : Vector3.Zero;
    }

    /// <summary>Earth mover's distance between two small palettes, by moving the closest shares first.</summary>
    public static float PaletteDistance(List<PaletteColour> a, List<PaletteColour> b)
    {
        float sa = a.Sum(c => c.Share), sb = b.Sum(c => c.Share);
        var left = a.Select(c => c.Share / sa).ToArray();
        var right = b.Select(c => c.Share / sb).ToArray();
        var pairs = (from i in Enumerable.Range(0, a.Count) from j in Enumerable.Range(0, b.Count)
                     select (i, j, d: Vector3.Distance(a[i].Lab, b[j].Lab))).OrderBy(p => p.d);
        float cost = 0, moved = 0;
        foreach (var (i, j, d) in pairs)
        {
            float m = MathF.Min(left[i], right[j]);
            if (m <= 0)
                continue;
            left[i] -= m;
            right[j] -= m;
            cost += m * d;
            moved += m;
        }
        return moved > 0 ? cost / moved : 0;
    }

    private static List<(Vector3 Centre, float Weight)> KMeans(List<(Vector3 Colour, float Weight)> samples, int k)
    {
        if (samples.Count == 0)
            return new();
        // Deterministic start: the samples spread furthest apart (farthest-point seeding from the lightest).
        var centres = new List<Vector3> { samples.MaxBy(s => s.Colour.X).Colour };
        while (centres.Count < k)
        {
            var next = samples.MaxBy(s => centres.Min(c => Vector3.DistanceSquared(c, s.Colour)));
            if (centres.Min(c => Vector3.DistanceSquared(c, next.Colour)) < 1f)
                break;
            centres.Add(next.Colour);
        }
        var weights = new float[centres.Count];
        for (int it = 0; it < 12; it++)
        {
            var sums = new Vector3[centres.Count];
            Array.Clear(weights);
            foreach (var (c, wgt) in samples)
            {
                int best = 0;
                float bd = float.MaxValue;
                for (int j = 0; j < centres.Count; j++)
                {
                    float d = Vector3.DistanceSquared(c, centres[j]);
                    if (d < bd) { bd = d; best = j; }
                }
                sums[best] += c * wgt;
                weights[best] += wgt;
            }
            for (int j = 0; j < centres.Count; j++)
                if (weights[j] > 0)
                    centres[j] = sums[j] / weights[j];
        }
        return centres.Select((c, j) => (c, weights[j])).ToList();
    }

    // ---- Output --------------------------------------------------------------------------------------------------

    private static void WriteSideBySide(string path, RgbaImage reference, RgbaImage rendered, bool[] refMask, bool[] renMask, Aligned aligned)
    {
        int w = reference.Width, h = reference.Height;
        int overlayW = (int)(aligned.Width * h / (float)aligned.Height);
        const int gap = 6;
        int total = w * 2 + overlayW + gap * 2;
        var rgba = new byte[total * h * 4];
        for (int i = 0; i < total * h; i++)
        {
            rgba[4 * i] = 40; rgba[4 * i + 1] = 40; rgba[4 * i + 2] = 44; rgba[4 * i + 3] = 255;
        }
        void Blit(RgbaImage img, int ox)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < img.Width; x++)
                {
                    int s = 4 * (y * img.Width + x), d = 4 * (y * total + ox + x);
                    float a = img.Rgba[s + 3] / 255f;
                    for (int c = 0; c < 3; c++)
                        rgba[d + c] = (byte)(img.Rgba[s + c] * a + 30 * (1 - a));
                }
        }
        Blit(reference, 0);
        Blit(rendered, w + gap);
        int ox2 = 2 * (w + gap);
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Clamp(y * aligned.Height / h, 0, aligned.Height - 1);
            for (int x = 0; x < overlayW; x++)
            {
                int sx = Math.Clamp(x * aligned.Width / overlayW, 0, aligned.Width - 1);
                bool a = aligned.Reference[sy * aligned.Width + sx], b = aligned.Render[sy * aligned.Width + sx];
                var (r, g, bl) = a && b ? (235, 235, 235) : a ? (220, 60, 60) : b ? (60, 200, 220) : (20, 20, 24);
                int d = 4 * (y * total + ox2 + x);
                rgba[d] = (byte)r; rgba[d + 1] = (byte)g; rgba[d + 2] = (byte)bl;
            }
        }
        PngWriter.WriteRgba(path, total, h, rgba);
    }
}
