using System.IO.Compression;
using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;

namespace Seamstress.Mcp;

/// <summary>One camera of a preview: where it looks from and what it frames.</summary>
public sealed record PreviewView(string Name, float YawDegrees, float PitchDegrees);

/// <summary>
/// A software renderer for checking a drape without the desktop app or a GPU window: smooth shading, the pieces' own
/// colours and PNG textures, any camera angle, and framing on the whole figure, a region or a single piece.
/// </summary>
public static class PreviewRenderer
{
    public static readonly Dictionary<string, PreviewView> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["front"] = new("front", 0, 4),
        ["back"] = new("back", 180, 4),
        ["side"] = new("side", 90, 4),
        ["left"] = new("left", 90, 4),
        ["right"] = new("right", -90, 4),
        ["quarter"] = new("quarter", 35, 10),
        ["quarter_left"] = new("quarter_left", 35, 10),
        ["quarter_right"] = new("quarter_right", -35, 10),
        ["quarter_back"] = new("quarter_back", 145, 10),
        ["top"] = new("top", 0, 70),
    };

    private sealed record Draw(Vector3[] Positions, Vector3[] Normals, int[] Triangles, Vector3 Color, Vector2[]? Uvs,
        Texture? Texture, float UvScale, bool Body, int[][]? Outline = null);

    private sealed record Texture(int Width, int Height, byte[] Rgb);

    public static void Render(Document doc, Func<Garment, GarmentMesh> meshFor, Seamstress.Core.Simulation.TriMesh? body,
        string outputPath, int width, int height, IReadOnlyList<PreviewView> views, Vector3? focus, float? spanMetres,
        bool showBody = true)
    {
        var draws = new List<Draw>();
        var textures = new Dictionary<string, Texture?>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var bodyMin = min;
        var bodyMax = max;
        if (body != null && showBody)
        {
            draws.Add(new Draw(body.Positions, Mesher.ComputeNormals(body.Positions, body.Triangles), body.Triangles,
                new Vector3(0.62f, 0.60f, 0.60f), null, null, 1, true));
            foreach (var p in body.Positions)
            {
                bodyMin = Vector3.Min(bodyMin, p);
                bodyMax = Vector3.Max(bodyMax, p);
            }
        }
        foreach (var g in doc.Garments.Where(g => g.Visible))
        {
            var gm = meshFor(g);
            var layout = UvLayout.For(g);
            foreach (var pm in gm.Pieces)
            {
                var piece = g.FindPiece(pm.PieceId)!;
                if (!piece.Visible)
                    continue;
                var mat = doc.FindMaterial(piece.MaterialId);
                var pos = Mesher.CurrentPositions(piece, pm);
                Texture? tex = null;
                if (mat?.Texture is { Length: > 0 } path && !textures.TryGetValue(path, out tex))
                    textures[path] = tex = LoadPng(path);
                draws.Add(new Draw(pos, Mesher.ComputeNormals(pos, pm.Triangles), pm.Triangles, Hex(mat?.Color ?? "#4F8FD9"),
                    tex != null ? layout.MapMaterial(piece, pm, mat) : null, tex, mat?.TextureScale ?? 1, false, pm.EdgeChains));
                foreach (var p in pos)
                {
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
        }
        if (draws.Count == 0)
            throw new InvalidOperationException("There is nothing to render.");
        if (min.X > max.X)
        {
            // No garments yet: frame the body.
            min = bodyMin;
            max = bodyMax;
        }
        else if (bodyMin.X <= bodyMax.X)
        {
            // Frame the clothes with the whole height of the figure, not the outstretched hands.
            min = new Vector3(min.X, MathF.Min(min.Y, bodyMin.Y), min.Z);
            max = new Vector3(max.X, MathF.Max(max.Y, bodyMax.Y), max.Z);
        }

        const int ss = 2;   // supersampling
        int W = width * ss, H = height * ss;
        var color = new float[W * H * 3];
        var depth = new float[W * H];
        Array.Fill(depth, float.NegativeInfinity);
        var background = new Vector3(0.105f, 0.115f, 0.14f);
        for (int i = 0; i < W * H; i++)
        {
            color[3 * i] = background.X;
            color[3 * i + 1] = background.Y;
            color[3 * i + 2] = background.Z;
        }

        var target = focus ?? (min + max) / 2;
        int panelW = W / views.Count;
        var key = Vector3.Normalize(new Vector3(-0.45f, 0.55f, 0.70f));
        var fill = Vector3.Normalize(new Vector3(0.70f, 0.10f, 0.55f));
        for (int v = 0; v < views.Count; v++)
        {
            float yaw = views[v].YawDegrees * MathF.PI / 180f, pitch = views[v].PitchDegrees * MathF.PI / 180f;
            float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw), cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
            Vector3 Rotate(Vector3 q)
            {
                float x = q.X * cy - q.Z * sy, z = q.X * sy + q.Z * cy;
                return new Vector3(x, q.Y * cp - z * sp, q.Y * sp + z * cp);
            }
            // Fit what is framed into the panel: the given span, or the extent of everything as seen from here.
            float span = spanMetres ?? 0;
            if (span <= 0)
            {
                float halfW = 0, halfH = 0;
                foreach (var corner in new[] { min, max, new(min.X, min.Y, max.Z), new(min.X, max.Y, min.Z), new(max.X, min.Y, min.Z),
                             new(min.X, max.Y, max.Z), new(max.X, min.Y, max.Z), new Vector3(max.X, max.Y, min.Z) })
                {
                    var r = Rotate(corner - target);
                    halfW = MathF.Max(halfW, MathF.Abs(r.X));
                    halfH = MathF.Max(halfH, MathF.Abs(r.Y));
                }
                span = MathF.Max(2 * halfH, 2 * halfW * H / panelW) * 1.06f;
            }
            float scale = H / span;
            int x0 = v * panelW, x1 = x0 + panelW - 1;

            foreach (var d in draws)
            {
                var screen = new Vector3[d.Positions.Length];
                var normals = new Vector3[d.Positions.Length];
                for (int i = 0; i < screen.Length; i++)
                {
                    var r = Rotate(d.Positions[i] - target);
                    screen[i] = new Vector3(x0 + panelW * 0.5f + r.X * scale, H * 0.5f - r.Y * scale, r.Z + (d.Body ? 0 : 0.0015f));
                    normals[i] = Rotate(d.Normals[i]);
                }
                var tris = d.Triangles;
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
                    var a = screen[ia];
                    var b = screen[ib];
                    var c = screen[ic];
                    int minX = Math.Max(x0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
                    int maxX = Math.Min(x1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
                    int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
                    int maxY = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
                    if (minX > maxX || minY > maxY)
                        continue;
                    float denom = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                    if (MathF.Abs(denom) < 1e-7f)
                        continue;
                    float inv = 1f / denom;
                    for (int py = minY; py <= maxY; py++)
                    {
                        for (int px = minX; px <= maxX; px++)
                        {
                            float w0 = ((b.Y - c.Y) * (px + 0.5f - c.X) + (c.X - b.X) * (py + 0.5f - c.Y)) * inv;
                            float w1 = ((c.Y - a.Y) * (px + 0.5f - c.X) + (a.X - c.X) * (py + 0.5f - c.Y)) * inv;
                            float w2 = 1f - w0 - w1;
                            if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f)
                                continue;
                            float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                            int idx = py * W + px;
                            if (z <= depth[idx])
                                continue;
                            depth[idx] = z;
                            var n = normals[ia] * w0 + normals[ib] * w1 + normals[ic] * w2;
                            float len = n.Length();
                            n = len > 1e-9f ? n / len : Vector3.UnitZ;
                            if (n.Z < 0)
                                n = -n;                 // cloth is seen from both sides
                            var albedo = d.Color;
                            if (d.Texture is { } tex && d.Uvs != null)
                            {
                                var uv = (d.Uvs[ia] * w0 + d.Uvs[ib] * w1 + d.Uvs[ic] * w2) * d.UvScale;
                                int tx = (int)((uv.X - MathF.Floor(uv.X)) * tex.Width) % tex.Width;
                                int ty = (int)((1f - (uv.Y - MathF.Floor(uv.Y))) * tex.Height) % tex.Height;
                                int ti = 3 * (ty * tex.Width + tx);
                                albedo *= new Vector3(Linear(tex.Rgb[ti]), Linear(tex.Rgb[ti + 1]), Linear(tex.Rgb[ti + 2]));
                            }
                            else
                            {
                                albedo = new Vector3(LinearF(albedo.X), LinearF(albedo.Y), LinearF(albedo.Z));
                            }
                            float light = 0.20f + 0.85f * MathF.Max(0, Vector3.Dot(n, key)) + 0.30f * MathF.Max(0, Vector3.Dot(n, fill));
                            light *= 0.62f + 0.38f * n.Z;       // darker where the surface turns away: folds read clearly
                            color[3 * idx] = albedo.X * light;
                            color[3 * idx + 1] = albedo.Y * light;
                            color[3 * idx + 2] = albedo.Z * light;
                        }
                    }
                }

                // The cut edges of every piece are drawn a little darker, so seams, hems and sewn-on patches of the
                // same cloth can be told apart, as their shadows would do in a full render.
                if (d.Outline == null)
                    continue;
                foreach (var chain in d.Outline)
                {
                    for (int k = 0; k + 1 < chain.Length; k++)
                    {
                        var p = screen[chain[k]];
                        var q = screen[chain[k + 1]];
                        int steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(q.X - p.X), MathF.Abs(q.Y - p.Y))) + 1;
                        for (int i = 0; i <= steps; i++)
                        {
                            var at = Vector3.Lerp(p, q, i / (float)steps);
                            for (int oy = 0; oy < 2; oy++)
                            {
                                for (int ox = 0; ox < 2; ox++)
                                {
                                    int px = (int)at.X + ox, py = (int)at.Y + oy;
                                    if (px < x0 || px > x1 || py < 0 || py >= H)
                                        continue;
                                    int idx = py * W + px;
                                    if (at.Z + 0.004f < depth[idx])
                                        continue;
                                    color[3 * idx] *= 0.80f;
                                    color[3 * idx + 1] *= 0.80f;
                                    color[3 * idx + 2] *= 0.80f;
                                    depth[idx] = MathF.Max(depth[idx], at.Z + 0.0001f);
                                }
                            }
                        }
                    }
                }
            }
        }

        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int j = 0; j < ss; j++)
                {
                    for (int i = 0; i < ss; i++)
                    {
                        int idx = 3 * ((y * ss + j) * W + x * ss + i);
                        r += color[idx];
                        g += color[idx + 1];
                        b += color[idx + 2];
                    }
                }
                int o = 4 * (y * width + x);
                rgba[o] = Srgb(r / (ss * ss));
                rgba[o + 1] = Srgb(g / (ss * ss));
                rgba[o + 2] = Srgb(b / (ss * ss));
                rgba[o + 3] = 255;
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        PngWriter.WriteRgba(outputPath, width, height, rgba);
    }

    private static readonly float[] LinearTable = Enumerable.Range(0, 256).Select(i => LinearF(i / 255f)).ToArray();
    private static float Linear(byte v) => LinearTable[v];
    private static float LinearF(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    private static byte Srgb(float v)
    {
        v = Math.Clamp(v * 1.9f, 0f, 1f);       // exposure: dark fabrics stay readable
        float s = v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp(s * 255f + 0.5f, 0, 255);
    }

    private static Vector3 Hex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int v))
            return new Vector3(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
        return new Vector3(0.31f, 0.56f, 0.85f);
    }

    /// <summary>Reads an 8-bit, non-interlaced PNG as RGB. Anything else (JPEG, 16-bit, interlaced) gives null.</summary>
    private static Texture? LoadPng(string path)
    {
        try
        {
            byte[] file = File.ReadAllBytes(path);
            if (file.Length < 33 || file[0] != 0x89 || file[1] != 'P')
                return null;
            int width = 0, height = 0, colorType = 0, at = 8;
            byte[]? palette = null;
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
                    colorType = file[data + 9];
                    if (file[data + 8] != 8 || file[data + 12] != 0)
                        return null;
                }
                else if (type == "PLTE")
                    palette = file.AsSpan(data, length).ToArray();
                else if (type == "IDAT")
                    idat.Write(file, data, length);
                else if (type == "IEND")
                    break;
                at = data + length + 4;
            }
            int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
            if (channels == 0 || width <= 0 || height <= 0)
                return null;
            int stride = width * channels;
            byte[] raw = new byte[(stride + 1) * height];
            idat.Position = 0;
            using (var z = new ZLibStream(idat, CompressionMode.Decompress))
                z.ReadExactly(raw);
            byte[] rgb = new byte[width * height * 3];
            byte[] previous = new byte[stride], line = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                int filter = raw[y * (stride + 1)];
                Buffer.BlockCopy(raw, y * (stride + 1) + 1, line, 0, stride);
                for (int i = 0; i < stride; i++)
                {
                    int left = i >= channels ? line[i - channels] : 0, up = previous[i], upLeft = i >= channels ? previous[i - channels] : 0;
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
                for (int x = 0; x < width; x++)
                {
                    int o = 3 * (y * width + x);
                    switch (colorType)
                    {
                        case 2: case 6:
                            rgb[o] = line[x * channels]; rgb[o + 1] = line[x * channels + 1]; rgb[o + 2] = line[x * channels + 2];
                            break;
                        case 3 when palette != null && line[x] * 3 + 2 < palette.Length:
                            rgb[o] = palette[line[x] * 3]; rgb[o + 1] = palette[line[x] * 3 + 1]; rgb[o + 2] = palette[line[x] * 3 + 2];
                            break;
                        default:
                            rgb[o] = rgb[o + 1] = rgb[o + 2] = line[x * channels];
                            break;
                    }
                }
                (previous, line) = (line, previous);
            }
            return new Texture(width, height, rgb);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
