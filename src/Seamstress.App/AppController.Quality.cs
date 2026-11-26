using Seamstress.Core;
using Seamstress.Core.Model;

namespace Seamstress.App;

/// <summary>
/// Two ways to make big outfits cheaper to work on: Draft quality (a coarse working mesh, refined to each garment's
/// own particle distance at the end) and deactivating garments (left out of the simulation entirely).
/// </summary>
public partial class AppController
{
    /// <summary>True at Draft quality, false at Final.</summary>
    public bool DraftQuality => _workspace.Quality == SimulationQuality.Draft;

    /// <summary>The particle distance Draft meshes with, in centimetres.</summary>
    public double DraftParticleDistance => _workspace.Document.Simulation.DraftParticleDistance;

    /// <summary>Switches to Draft (true) or refines to Final (false). A running simulation carries on at the new quality.</summary>
    public void SetDraftQuality(bool draft)
    {
        var quality = draft ? SimulationQuality.Draft : SimulationQuality.Final;
        if (_workspace.Quality == quality)
            return;
        bool wasRunning = _simulation.IsRunning;
        if (wasRunning)
            StopSimulation();
        var change = _workspace.SetQuality(quality);
        string distance = FormatLength(_workspace.Document.Simulation.DraftParticleDistance);
        StatusText = draft
            ? Lang.F("Draft quality: garments are meshed no finer than {0}, so they simulate faster. Drapes and pins were carried over. Refine to Final when the outfit is done.", distance)
            : change.Creased > 0
                ? Lang.T("Refined to Final: drapes and pins were carried over. Creases came from the coarser Draft mesh and are softer; set them again where fine folds matter.")
                : Lang.T("Refined to Final: each garment uses its own particle distance again. Drapes and pins were carried over.");
        AppLog.Write($"Quality {quality}: {change.Pieces} pieces re-meshed, {change.Draped} drapes, {change.Pinned} pinned, {change.Creased} creased.");
        Notify(nameof(DraftQuality));
        if (wasRunning)
            StartSimulation();
    }

    public void RefineToFinal() => SetDraftQuality(false);

    /// <summary>Sets how coarse Draft meshes are (cm). At Draft the garments are meshed again, keeping their shape.</summary>
    public void SetDraftParticleDistance(double centimetres)
    {
        if (_simulation.IsRunning)
            StopSimulation();
        _workspace.SetDraftParticleDistance((float)centimetres);
        Notify(nameof(DraftParticleDistance));
    }

    /// <summary>
    /// Deactivates a garment (left out of the simulation entirely; other cloth passes through it, and it is drawn faded)
    /// or switches it back on.
    /// </summary>
    public void SetDeactivated(string garmentId, bool value)
    {
        if (_simulation.IsRunning)
            StopSimulation();
        _workspace.SetDeactivated(garmentId, value);
        var name = _workspace.Document.FindGarment(garmentId)?.Name ?? "";
        StatusText = value
            ? Lang.F("“{0}” is deactivated: it is not simulated and nothing collides with it, so other cloth passes through it.", name)
            : Lang.F("“{0}” is active again: it is simulated and collided with as usual.", name);
    }

    private string FormatLength(float centimetres)
        => Imperial
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0.##} in", centimetres / 2.54f)
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0.#} cm", centimetres);
}
