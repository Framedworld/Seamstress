using System.Globalization;
using System.Numerics;
using System.Text;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.IO;

/// <summary>
/// Exports 2D pattern pieces (including quadratic Bezier curves, holes, labels, and color-coded seam guides)
/// to a 1:1 centimetre-scaled SVG suitable for printing, CAD import, or visual inspection.
/// </summary>
public static class SvgPatternExporter
{
    private static readonly string[] SeamPalette =
    {
        "#E05A47", "#3A86FF", "#2EC4B6", "#FFBE0B", "#9B5DE5", "#F15BB5", "#00BBF9", "#FB5607"
    };

    public static string BuildSvg(Document document, bool includeGrid = true, bool includeSeamGuides = true)
    {
        var pieces = document.Garments
            .Where(g => g.Visible)
            .SelectMany(g => g.Pieces.Select(p => (Garment: g, Piece: p)))
            .ToList();

        if (pieces.Count == 0)
            return "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20cm\" height=\"20cm\" viewBox=\"0 0 20 20\"></svg>";

        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var (_, piece) in pieces)
        {
            var (pmin, pmax) = Outline.Bounds(piece);
            min = Vector2.Min(min, pmin + piece.Origin);
            max = Vector2.Max(max, pmax + piece.Origin);
        }

        const float pad = 8f; // cm
        min -= new Vector2(pad);
        max += new Vector2(pad);
        float w = MathF.Max(10f, max.X - min.X);
        float h = MathF.Max(10f, max.Y - min.Y);

        // SVG Y goes down; pattern Y goes up -> flip Y via (max.Y - y).
        string Fx(float x) => (x - min.X).ToString("0.###", CultureInfo.InvariantCulture);
        string Fy(float y) => (max.Y - y).ToString("0.###", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" width="{w.ToString("0.##", CultureInfo.InvariantCulture)}cm" height="{h.ToString("0.##", CultureInfo.InvariantCulture)}cm" viewBox="0 0 {w.ToString("0.###", CultureInfo.InvariantCulture)} {h.ToString("0.###", CultureInfo.InvariantCulture)}">""");
        sb.AppendLine("  <rect width=\"100%\" height=\"100%\" fill=\"#181A1F\"/>");

        if (includeGrid)
        {
            sb.AppendLine("  <g stroke=\"#2A2E38\" stroke-width=\"0.12\">");
            for (float gx = MathF.Ceiling(min.X / 10f) * 10f; gx <= max.X; gx += 10f)
                sb.AppendLine($"    <line x1=\"{Fx(gx)}\" y1=\"0\" x2=\"{Fx(gx)}\" y2=\"{h.ToString("0.###", CultureInfo.InvariantCulture)}\"/>");
            for (float gy = MathF.Ceiling(min.Y / 10f) * 10f; gy <= max.Y; gy += 10f)
                sb.AppendLine($"    <line x1=\"0\" y1=\"{Fy(gy)}\" x2=\"{w.ToString("0.###", CultureInfo.InvariantCulture)}\" y2=\"{Fy(gy)}\"/>");
            sb.AppendLine("  </g>");
        }

        // Draw pieces
        foreach (var (garment, piece) in pieces)
        {
            var mat = document.FindMaterial(piece.MaterialId);
            string fill = mat?.Color ?? "#4F8FD9";
            sb.AppendLine($"  <g id=\"piece-{Escape(piece.Id)}\">");
            string path = BuildPathData(piece, Fx, Fy);
            sb.AppendLine($"    <path d=\"{path}\" fill=\"{Escape(fill)}\" fill-opacity=\"0.28\" fill-rule=\"evenodd\" stroke=\"#E6EAF2\" stroke-width=\"0.28\" stroke-linejoin=\"round\"/>");

            // Label at piece centre
            var (pmin, pmax) = Outline.Bounds(piece);
            var centre = (pmin + pmax) * 0.5f + piece.Origin;
            sb.AppendLine($"    <text x=\"{Fx(centre.X)}\" y=\"{Fy(centre.Y)}\" fill=\"#F0F4FA\" font-family=\"sans-serif\" font-size=\"2.4\" text-anchor=\"middle\" dominant-baseline=\"middle\">{Escape(piece.Name)}</text>");
            sb.AppendLine("  </g>");
        }

        // Draw color-coded seam edge overlays
        if (includeSeamGuides)
        {
            sb.AppendLine("  <g fill=\"none\" stroke-width=\"0.55\" stroke-linecap=\"round\">");
            int seamIdx = 0;
            foreach (var garment in document.Garments.Where(g => g.Visible))
            {
                foreach (var seam in garment.Seams)
                {
                    string color = SeamPalette[seamIdx++ % SeamPalette.Length];
                    AppendEdgePath(sb, garment.FindPiece(seam.A.PieceId), seam.A.Edge, color, Fx, Fy);
                    AppendEdgePath(sb, garment.FindPiece(seam.B.PieceId), seam.B.Edge, color, Fx, Fy);
                }
            }
            sb.AppendLine("  </g>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    public static void Export(Document document, string path, bool includeGrid = true, bool includeSeamGuides = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, BuildSvg(document, includeGrid, includeSeamGuides), Encoding.UTF8);
    }

    private static string BuildPathData(PatternPiece piece, Func<float, string> fx, Func<float, string> fy)
    {
        var sb = new StringBuilder();
        foreach (var (loop, _) in piece.Loops())
        {
            if (loop.Count < 3)
                continue;
            var p0 = loop[0].Position + piece.Origin;
            sb.Append($"M {fx(p0.X)} {fy(p0.Y)} ");
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                var bp = b.Position + piece.Origin;
                if (a.Curve is { } ctrl)
                {
                    var cp = ctrl + piece.Origin;
                    sb.Append($"Q {fx(cp.X)} {fy(cp.Y)} {fx(bp.X)} {fy(bp.Y)} ");
                }
                else
                {
                    sb.Append($"L {fx(bp.X)} {fy(bp.Y)} ");
                }
            }
            sb.Append("Z ");
        }
        return sb.ToString().TrimEnd();
    }

    private static void AppendEdgePath(StringBuilder sb, PatternPiece? piece, int edgeIndex, string color,
        Func<float, string> fx, Func<float, string> fy)
    {
        if (piece == null || edgeIndex < 0 || edgeIndex >= piece.EdgeCount)
            return;
        var a = piece.PointAt(edgeIndex);
        var b = piece.PointAt(piece.NextPoint(edgeIndex));
        var ap = a.Position + piece.Origin;
        var bp = b.Position + piece.Origin;
        if (a.Curve is { } ctrl)
        {
            var cp = ctrl + piece.Origin;
            sb.AppendLine($"    <path d=\"M {fx(ap.X)} {fy(ap.Y)} Q {fx(cp.X)} {fy(cp.Y)} {fx(bp.X)} {fy(bp.Y)}\" stroke=\"{color}\"/>");
        }
        else
        {
            sb.AppendLine($"    <path d=\"M {fx(ap.X)} {fy(ap.Y)} L {fx(bp.X)} {fy(bp.Y)}\" stroke=\"{color}\"/>");
        }
    }

    private static string Escape(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
