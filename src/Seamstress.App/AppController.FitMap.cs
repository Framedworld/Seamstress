using Seamstress.Core;
using Seamstress.Core.Editing;
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
    /// <summary>
    /// What easing the active garment would change (<see cref="PatternEasing"/>), as the <c>ease_pattern</c> tool reports
    /// it (JSON); nothing changes.
    /// </summary>
    public string EasePlan(double targetPercent, double strength)
    {
        var garment = _workspace.ActiveGarment;
        if (garment == null)
            return "{\"garment\":\"\",\"changedPieces\":0,\"pieces\":[],\"seams\":[]}";
        var plan = PatternEasing.Plan(garment, _workspace.MeshFor(garment), EaseOptionsFor(targetPercent, strength));
        return System.Text.Json.JsonSerializer.Serialize(Seamstress.Mcp.EaseReport.Describe(plan, false));
    }

    /// <summary>
    /// Eases the active garment as one undo step and simulates it again, so the cloth relaxes into the new pattern.
    /// A running simulation is stopped first: easing measures the drape as it is.
    /// </summary>
    public void ApplyEase(double targetPercent, double strength)
    {
        if (_simulation.IsRunning)
            StopSimulation();
        var garment = _workspace.ActiveGarment;
        if (garment == null)
            return;
        var plan = PatternEasing.Plan(garment, _workspace.MeshFor(garment), EaseOptionsFor(targetPercent, strength));
        if (plan.ChangedPieces == 0)
        {
            StatusText = Lang.T("Nothing is stretched past the target.");
            return;
        }
        PatternEasing.Apply(_workspace, plan, Lang.T("Ease tight spots"));
        AppLog.Write($"Eased {plan.ChangedPieces} pieces of {garment.Name}");
        StartSimulation();
    }

    private static EaseOptions EaseOptionsFor(double targetPercent, double strength) => new()
    {
        TargetStrain = (float)Math.Clamp(targetPercent, 0, 30) / 100f,
        Strength = (float)Math.Clamp(strength, 0.05, 1),
    };

    public string FitMapSummary(string kind)
        => System.Text.Json.JsonSerializer.Serialize(Seamstress.Mcp.FitMapReport.Build(_workspace.Document, _workspace.MeshFor, _avatar,
            FitMaps.Parse(kind), null));
}
