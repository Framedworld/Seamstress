using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.Simulation;

/// <summary>
/// Starts and stops cloth simulation for a workspace. Only the active garment is simulated unless other garments opt
/// in ("simulate with others") or the caller asks for all of them; a whole run becomes a single undo step.
/// </summary>
public sealed class SimulationController : IDisposable
{
    private readonly Workspace _workspace;
    private readonly Func<Avatar?> _avatar;
    private SimulationRunner? _runner;
    private bool _stopping;
    private readonly HashSet<string> _garments = new();

    public SimulationController(Workspace workspace, Func<Avatar?> avatar)
    {
        _workspace = workspace;
        _avatar = avatar;
        // Any other edit (including undo) ends the run first, so the solver never works on a stale document.
        _workspace.History.BeforeChange += () =>
        {
            if (IsRunning && !_stopping)
                Stop();
        };
    }

    public bool IsRunning => _runner != null;
    public IReadOnlyCollection<string> SimulatedGarments => _garments;
    public int ParticleCount => _runner?.Solver.ParticleCount ?? 0;
    public double StepMilliseconds => _runner?.StepMilliseconds ?? 0;
    public long Frame => _runner?.Frame ?? 0;

    /// <summary>The fastest cloth of the last frame (m/s) and the widest seam still open (m).</summary>
    public float PeakSpeed => _runner?.Solver.PeakSpeed ?? 0;
    public float SeamGap => _runner?.Solver.SeamGap ?? 0;
    public string BackendName => _runner?.Solver.BackendName ?? "";
    public int EffectiveSubsteps => _runner?.Solver.EffectiveSubsteps ?? 0;

    /// <summary>The moving body of the running simulation, if an animation is playing.</summary>
    public IBodyMotion? Motion => _runner?.Motion;

    /// <summary>The garments a run started now would simulate.</summary>
    public List<Garment> GarmentsToSimulate(bool all)
    {
        var doc = _workspace.Document;
        var active = _workspace.ActiveGarment;
        bool Movable(Garment g) => g.Visible && !g.Frozen && g.Pieces.Count > 0;
        var simulated = doc.Garments.Where(g => Movable(g) && (all || g == active || g.SimulateWithOthers)).ToList();
        // What is sewn onto a simulated garment comes with it, as a pocket comes with the jacket it is sewn to (and a
        // flap with its pocket): left behind as an obstacle, trim stayed where it was while the garment moved away.
        bool added;
        do
        {
            added = false;
            foreach (var g in doc.Garments)
            {
                if (simulated.Contains(g) || !Movable(g))
                    continue;
                if (g.SurfaceStitches.Any(s => simulated.Any(h => h.FindPiece(s.TargetPieceId) != null)))
                {
                    simulated.Add(g);
                    added = true;
                }
            }
        } while (added);
        return simulated;
    }

    /// <summary>Starts simulating. Returns a message when there is nothing to simulate.</summary>
    /// <param name="undoName">Name of the undo step for this run; null names it after the garments.</param>
    /// <param name="settings">Settings for this run only (an agent's own substeps or self-collision); null uses the project's.</param>
    /// <param name="motion">A moving body (an animation) the cloth collides with instead of the still avatar.</param>
    /// <param name="collider">The moving body's collision grid, which the solver is created with.</param>
    public string? Start(bool allGarments, string? undoName = null, SimulationSettings? settings = null,
        IBodyMotion? motion = null, SdfGrid? collider = null)
    {
        if (IsRunning)
            return null;
        var doc = _workspace.Document;
        var simulated = GarmentsToSimulate(allGarments);
        if (simulated.Count == 0)
            return _workspace.ActiveGarment is { Frozen: true } frozen
                ? Lang.F("“{0}” is frozen. Unfreeze it in the layers panel to simulate it.", frozen.Name)
                : Lang.T("There is no visible garment to simulate. Add a garment or select one in the layers panel.");

        _stopping = true; // Begin raises BeforeChange; this run has not started yet
        _workspace.History.Begin(undoName ?? (simulated.Count == 1 ? Lang.F("Simulate {0}", simulated[0].Name) : Lang.T("Simulate garments")));
        _stopping = false;

        // Hard parts are lined up with the cloth they sit on, so they can be carried along as it moves.
        Seamstress.Core.Editing.Attachments.Prepare(doc, _workspace.MeshFor);
        var active = simulated.Select(g => _workspace.MeshFor(g)).ToList();
        var obstacles = doc.Garments.Where(g => g.Visible && !simulated.Contains(g)).Select(g => _workspace.MeshFor(g)).ToList();
        var solver = ClothSolver.Create(active, obstacles, motion != null ? collider : _avatar()?.Sdf, settings ?? doc.Simulation.Clone());
        AppLog.Write($"Simulating {solver.ParticleCount} particles on {solver.BackendName}.");
        _garments.Clear();
        foreach (var g in simulated)
            _garments.Add(g.Id);
        _runner = new SimulationRunner(solver) { Motion = motion };
        _runner.Start();
        return null;
    }

    /// <summary>Copies the newest simulated frame into the document. Returns true when something changed.</summary>
    public bool Pump(bool force = false)
    {
        var runner = _runner;
        if (runner == null)
            return false;
        var frame = runner.TakeFrame(force);
        if (frame == null)
            return false;
        var moved = new HashSet<string>();
        foreach (var cloth in runner.Solver.Pieces)
        {
            if (cloth.Simulated && frame.TryGetValue(cloth.Piece.Id, out var drape))
            {
                cloth.Piece.Drape = drape;
                cloth.Piece.DrapeKey = cloth.Mesh.Key;
                moved.Add(cloth.Piece.Id);
            }
        }
        Seamstress.Core.Editing.Attachments.Follow(_workspace.Document, _workspace.MeshFor, moved);
        return true;
    }

    /// <summary>Takes hold of the simulated cloth nearest to a point in the 3D view (metres). Needs a running simulation.</summary>
    public void BeginPinch(System.Numerics.Vector3 point, float radius = 0.05f) => _runner?.Solver.BeginPinch(point, radius);

    /// <summary>Moves the held cloth towards a new position.</summary>
    public void MovePinch(System.Numerics.Vector3 target) => _runner?.Solver.MovePinch(target);

    public void EndPinch() => _runner?.Solver.EndPinch();

    /// <summary>Advances a started run by exactly <paramref name="frames"/> frames without real-time pacing.</summary>
    public void RunFrames(int frames) => _runner?.RunFrames(frames);

    public string? Stop()
    {
        var runner = _runner;
        if (runner == null)
            return null;
        _stopping = true;
        try
        {
            runner.Stop();
            Pump(force: true);
            runner.Dispose(); // frees the solver's GPU buffers now instead of whenever the garbage collector gets to them
            _runner = null;
            _garments.Clear();
            _workspace.History.Commit();
            return runner.Error;
        }
        finally
        {
            _stopping = false;
        }
    }

    public void Dispose() => _runner?.Dispose();
}
