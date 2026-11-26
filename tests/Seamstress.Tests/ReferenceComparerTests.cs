using Seamstress.Core.IO;
using Seamstress.Mcp;

namespace Seamstress.Tests;

/// <summary>compare_to_reference: silhouettes and colours of a render scored against a reference picture.</summary>
public class ReferenceComparerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "seamstress-compare-" + Guid.NewGuid().ToString("N")[..8]);

    public ReferenceComparerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A picture of a "figure" (an ellipse body over a rectangle of legs) on a flat backdrop.</summary>
    private static void Figure(string path, int w, int h, (byte, byte, byte) backdrop, (byte, byte, byte) top, (byte, byte, byte) legs,
        float widthScale = 1f, float heightScale = 1f, bool transparent = false)
    {
        var rgba = new byte[w * h * 4];
        float cx = w / 2f, top0 = h * (1 - heightScale) / 2f + h * 0.08f, height = h * 0.84f * heightScale;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float v = (y - top0) / height;
                float dx = MathF.Abs(x - cx) / (w * 0.25f * widthScale);
                bool body = v is >= 0 and < 0.5f && dx * dx + (v - 0.25f) * (v - 0.25f) / 0.0625f < 1;
                bool leg = v is >= 0.45f and < 1f && dx < 0.6f;
                var c = body ? top : leg ? legs : backdrop;
                int o = 4 * (y * w + x);
                // A little texture on the figure, as cloth has.
                int n = body || leg ? (x * 7 + y * 13) % 9 - 4 : 0;
                rgba[o] = (byte)Math.Clamp(c.Item1 + n, 0, 255);
                rgba[o + 1] = (byte)Math.Clamp(c.Item2 + n, 0, 255);
                rgba[o + 2] = (byte)Math.Clamp(c.Item3 + n, 0, 255);
                rgba[o + 3] = transparent && !(body || leg) ? (byte)0 : (byte)255;
            }
        }
        PngWriter.WriteRgba(path, w, h, rgba);
    }

    private static readonly (byte, byte, byte) Light = (210, 210, 214), RenderBackdrop = (124, 130, 142);
    private static readonly (byte, byte, byte) Navy = (40, 48, 90), Charcoal = (50, 50, 54), Red = (170, 40, 40);

    private dynamic Compare(string reference, Action<string, int, int> render)
        => ReferenceComparer.Compare(reference, Path.Combine(_dir, "out.png"), 240, render);

    private static T Get<T>(object o, string name) => (T)o.GetType().GetProperty(name)!.GetValue(o)!;

    [Fact]
    public void TheSameFigureScoresHighEvenAtAnotherScale()
    {
        string reference = Path.Combine(_dir, "ref.png");
        Figure(reference, 200, 300, Light, Navy, Charcoal);
        object result = Compare(reference, (path, w, h) => Figure(path, w, h, RenderBackdrop, Navy, Charcoal, heightScale: 0.8f, widthScale: 0.8f));

        var silhouette = Get<object>(result, "silhouette");
        Assert.True(Get<float>(silhouette, "iou") > 0.9f, $"iou {Get<float>(silhouette, "iou")}");
        Assert.True(Get<float>(silhouette, "iouSameFrame") < Get<float>(silhouette, "iou"));
        var palette = ((System.Collections.IEnumerable)Get<object>(result, "palette")).Cast<object>().ToList();
        Assert.NotEmpty(palette);
        Assert.All(palette, p => Assert.True(Get<float>(p, "deltaE") < 6f, $"{Get<string>(p, "region")}: {Get<float>(p, "deltaE")}"));
        Assert.True(File.Exists(Get<string>(result, "sideBySidePath")));
        Assert.True(File.Exists(Get<string>(result, "renderPath")));
    }

    [Fact]
    public void ANarrowerFigureAndOtherColoursScoreLow()
    {
        string reference = Path.Combine(_dir, "ref.png");
        Figure(reference, 200, 300, Light, Navy, Charcoal);
        object result = Compare(reference, (path, w, h) => Figure(path, w, h, RenderBackdrop, Red, Charcoal, widthScale: 0.6f));

        var silhouette = Get<object>(result, "silhouette");
        Assert.InRange(Get<float>(silhouette, "iou"), 0.3f, 0.8f);
        var ratios = ((System.Collections.IEnumerable)Get<object>(silhouette, "widthRatios")).Cast<object>().ToList();
        Assert.All(ratios, r => Assert.True(Get<float>(r, "renderOverReference") < 0.8f));
        var palette = ((System.Collections.IEnumerable)Get<object>(result, "palette")).Cast<object>().ToList();
        var chest = palette.First(p => Get<string>(p, "region") == "shoulders_chest");
        Assert.True(Get<float>(chest, "deltaE") > 25f);
        var legs = palette.First(p => Get<string>(p, "region") == "legs");
        Assert.True(Get<float>(legs, "deltaE") < 6f);
    }

    [Fact]
    public void ATransparentReferenceIsCutOutByItsAlpha()
    {
        string reference = Path.Combine(_dir, "ref.png");
        Figure(reference, 200, 300, (0, 0, 0), Navy, Charcoal, transparent: true);
        var image = RgbaImage.Load(reference);
        var mask = ReferenceComparer.Segment(image);
        Assert.Contains("alpha", Get<string>(Compare(reference, (p, w, h) => Figure(p, w, h, RenderBackdrop, Navy, Charcoal)).silhouette, "segmentation"));
        float coverage = mask.Count(m => m) / (float)mask.Length;
        Assert.InRange(coverage, 0.2f, 0.45f);
    }

    [Fact]
    public void APngRoundTripsWithItsAlpha()
    {
        string path = Path.Combine(_dir, "rt.png");
        var rgba = new byte[3 * 2 * 4];
        for (int i = 0; i < rgba.Length; i++)
            rgba[i] = (byte)(i * 11);
        PngWriter.WriteRgba(path, 3, 2, rgba);
        var image = RgbaImage.Load(path);
        Assert.Equal(3, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(rgba, image.Rgba);
    }
}
