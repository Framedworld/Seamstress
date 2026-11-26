using Seamstress.Core.Simulation;

namespace Seamstress.App;

/// <summary>
/// Fit maps in the 3D view: the cloth coloured by stretch, pressure on the body, or distance from it, live while it
/// simulates (<see cref="FitMaps"/>, drawn by <see cref="FitMapPainter"/>).
/// </summary>
public partial class AppController
{
    /// <summary>"off", "stretch", "pressure" or "distance": what the 3D view colours the cloth by.</summary>
    public string FitMap
    {
        get => FitMaps.Name(_viewport.FitMap.Kind);
        set
        {
            var kind = FitMaps.Parse(value);
            if (kind == _viewport.FitMap.Kind)
                return;
            _viewport.FitMap.Kind = kind;
            Notify();
            ViewportRevision++;
        }
    }

    /// <summary>
    /// The numbers behind a map for every visible garment, as the <c>fit_map</c> tool gives them to agents (JSON):
    /// range, mean, how much is over and under its thresholds, and the worst places.
    /// </summary>
    public string FitMapSummary(string kind)
        => System.Text.Json.JsonSerializer.Serialize(Seamstress.Mcp.FitMapReport.Build(_workspace.Document, _workspace.MeshFor, _avatar,
            FitMaps.Parse(kind), null));
}
