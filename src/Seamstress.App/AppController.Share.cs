using System.Globalization;
using System.Text;
using Seamstress.Core;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;

namespace Seamstress.App;

/// <summary>
/// What artists pass around: a picture of the 3D view and the flat pattern as SVG. Also the surface stitches (a
/// patch pocket's edge sewn onto a line inside another piece) for the 2D view to draw.
/// </summary>
public partial class AppController
{
    // ---------------------------------------------------------------- picture of the 3D view

    /// <summary>The file a picture of the 3D view is written to: the chosen one, as a path, ending in ".png".</summary>
    public string ImagePath(string fileUrl)
    {
        string path = LocalPath(fileUrl);
        return path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? path : path + ".png";
    }

    /// <summary>Called by the 3D view once it has written (or failed to write) a picture of itself.</summary>
    public void ImageSaved(string path, bool saved)
    {
        if (saved && File.Exists(path))
        {
            // The size is read back from the file, so the message says what was really written.
            var size = ImageSize.TryRead(path) ?? (0, 0);
            StatusText = Lang.F("Saved the 3D view as {0} ({1} × {2} pixels)", path, size.Width, size.Height);
            AppLog.Write($"Saved the 3D view as {path} ({size.Width} x {size.Height})");
        }
        else
        {
            Fail(Lang.F("Could not save the image {0}.", Path.GetFileName(path)));
        }
    }

    /// <summary>"width x height" of an image file in pixels, or an empty text when it cannot be read; for the automated check.</summary>
    public string ImageDimensions(string fileUrl)
    {
        string path = LocalPath(fileUrl);
        return File.Exists(path) && ImageSize.TryRead(path) is { } size ? $"{size.Width}x{size.Height}" : "";
    }

    // ---------------------------------------------------------------- pattern as SVG

    /// <summary>Writes the pattern pieces of the visible garments as a 1:1 SVG drawing, in centimetres.</summary>
    public bool ExportPatternSvg(string fileUrl, bool grid, bool seamGuides)
    {
        string path = WithExtension(LocalPath(fileUrl), ".svg");
        AppLog.Write($"Exporting the pattern as SVG to {path} (grid: {grid}; seam guides: {seamGuides})");
        try
        {
            int pieces = _workspace.Document.Garments.Where(g => g.Visible).Sum(g => g.Pieces.Count);
            if (pieces == 0)
                throw new InvalidOperationException(Lang.T("There is nothing to export: no visible garment has any pieces."));
            SvgPatternExporter.Export(_workspace.Document, path, grid, seamGuides);
            StatusText = Lang.F("Exported the pattern ({0} pieces) to {1}", pieces, path);
            AppLog.Write($"Exported the pattern ({pieces} pieces) to {path}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Fail(Lang.T("Export failed."), e);
            return false;
        }
    }

    // ---------------------------------------------------------------- surface stitches in the 2D view

    private static readonly string[] SurfaceStitchColors = { "#F59E0B", "#22D3EE", "#A3E635", "#F472B6", "#C084FC", "#FB923C" };

    /// <summary>
    /// The surface stitches of the visible pieces for the 2D view, in pattern centimetres: for each, the patch edge
    /// that is sewn ("edge", a polyline) and the line inside the host piece it is sewn onto ("line", x0 y0 x1 y1).
    /// Empty in UV mode, where pieces are not at their pattern positions.
    /// </summary>
    public string SurfaceStitches()
    {
        var sb = new StringBuilder("[");
        if (_editor.UvMode)
            return sb.Append(']').ToString();
        var inv = CultureInfo.InvariantCulture;
        var doc = _workspace.Document;
        int index = 0;
        bool first = true;
        foreach (var garment in doc.Garments)
        {
            foreach (var stitch in garment.SurfaceStitches)
            {
                string color = SurfaceStitchColors[index++ % SurfaceStitchColors.Length];
                var source = garment.FindPiece(stitch.SourcePieceId);
                // The host may belong to another garment: a pocket in its own garment sewn onto the trousers.
                var host = doc.FindPiece(stitch.TargetPieceId);
                var hostGarment = doc.GarmentOfPiece(stitch.TargetPieceId);
                if (source == null || host == null || hostGarment == null || stitch.SourceEdge < 0 || stitch.SourceEdge >= source.EdgeCount)
                    continue;
                if (!Seamstress.Core.Editing.Workspace.IsEffectivelyVisible(garment, source)
                    || !Seamstress.Core.Editing.Workspace.IsEffectivelyVisible(hostGarment, host))
                    continue;
                if (!first)
                    sb.Append(',');
                first = false;
                sb.Append("{\"id\":").Append(Q(stitch.Id)).Append(",\"color\":").Append(Q(color));
                sb.Append(",\"source\":").Append(Q(source.Name)).Append(",\"host\":").Append(Q(host.Name));
                sb.Append(",\"active\":").Append(B(garment.Id == _workspace.ActiveGarmentId || hostGarment.Id == _workspace.ActiveGarmentId));
                sb.Append(",\"edge\":[");
                bool firstPoint = true;
                foreach (var p in Outline.FlattenEdge(source, stitch.SourceEdge))
                {
                    if (!firstPoint)
                        sb.Append(',');
                    firstPoint = false;
                    sb.Append(inv, $"{p.X + source.Origin.X:0.###},{p.Y + source.Origin.Y:0.###}");
                }
                var a = stitch.TargetStart + host.Origin;
                var b = stitch.TargetEnd + host.Origin;
                sb.Append(inv, $"],\"line\":[{a.X:0.###},{a.Y:0.###},{b.X:0.###},{b.Y:0.###}]}}");
            }
        }
        return sb.Append(']').ToString();
    }
}
