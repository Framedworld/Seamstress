using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Seamstress.Core;
using Seamstress.Core.Editing;
using Seamstress.Core.Simulation;
using Seamstress.Mcp;

namespace Seamstress.App;

/// <summary>
/// The live link: an agent's MCP server hands its tool calls to this window, which runs them on the project the user
/// has open. The user watches the work happen in the 2D and 3D views; each call is one undo step ("Agent: …"); a
/// simulation runs live, as if the user had pressed Simulate. Calls arrive on the link's own threads and are run
/// on the window's thread by <see cref="Tick"/>.
/// </summary>
public partial class AppController : ISessionHost
{
    // Tools that only look: they leave no undo step.
    private static readonly HashSet<string> LookingTools = new()
    {
        "get_project_summary", "get_avatar_landmarks", "locate_on_piece", "inspect_fit", "fit_map", "render_preview",
        "list_body_profiles", "inspect_metahuman_dna", "export_garment", "save_project", "save_body_profile",
    };

    private readonly ConcurrentQueue<Action> _agentWork = new();
    private LiveLinkServer? _liveLink;
    private GarmentSession? _agentSession;
    private McpToolRegistry? _agentTools;

    /// <summary>Starts listening for an agent's tool calls (not in automated runs, which must not be driven from outside).</summary>
    private void StartLiveLink()
    {
        if (StartupAutomated)
            return;
        try
        {
            _agentSession = new GarmentSession(this);
            _agentTools = new McpToolRegistry(_agentSession);
            _liveLink = new LiveLinkServer(HandleAgentCall);
            _liveLink.Start();
            AppLog.Write($"Live link for agents listening on 127.0.0.1:{_liveLink.Port}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            AppLog.Write($"The live link for agents could not start: {e.Message}");
            _liveLink = null;
        }
    }

    private void StopLiveLink()
    {
        _liveLink?.Dispose();
        _liveLink = null;
    }

    /// <summary>Runs <paramref name="work"/> on the window's thread at the next tick and hands back its result.</summary>
    private Task<T> OnWindowThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _agentWork.Enqueue(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        return done.Task;
    }

    /// <summary>Called by <see cref="Tick"/>: the agent's work that has arrived since the last tick.</summary>
    private void RunAgentWork()
    {
        while (_agentWork.TryDequeue(out var work))
            work();
    }

    private Task<object> HandleAgentCall(string tool, JsonElement args)
    {
        if (tool == LiveLink.Hello)
        {
            return OnWindowThread<object>(() => new
            {
                window = WindowTitle,
                pid = Environment.ProcessId,
                project = _workspace.FilePath,
                unsavedChanges = Dirty,
            });
        }
        if (tool == "simulate")
            return LiveSimulate(args);
        return OnWindowThread(() => RunAgentTool(tool, args));
    }

    private object RunAgentTool(string tool, JsonElement args)
    {
        string label = tool.Replace('_', ' ');
        StatusText = Lang.F("Agent: {0}", label);
        if (LookingTools.Contains(tool) || tool is "new_project" or "open_project")
            return _agentTools!.Dispatch(tool, args);
        if (_simulation.IsRunning)
            StopSimulation();
        _workspace.History.BeginGroup(Lang.F("Agent: {0}", label));
        try
        {
            var result = _agentTools!.Dispatch(tool, args);
            _workspace.History.EndGroup(keep: true);
            ViewportRevision++;
            return result;
        }
        catch
        {
            // A tool that fails half way leaves nothing behind in the user's project.
            _workspace.History.EndGroup(keep: false);
            throw;
        }
    }

    /// <summary>
    /// "simulate" from an agent: the window's own simulation runs for the frames asked for, so the user watches the
    /// cloth drape; the agent gets the same report a headless run gives.
    /// </summary>
    private async Task<object> LiveSimulate(JsonElement args)
    {
        string? garmentId = Text(args, "garmentId");
        int frames = Math.Clamp(Whole(args, "frames", 90), 1, 5000);
        bool all = Flag(args, "allGarments", false);
        var simulated = new List<string>();

        long start = await OnWindowThread(() =>
        {
            if (_simulation.IsRunning)
                StopSimulation();
            if (garmentId != null)
            {
                if (_workspace.Document.FindGarment(garmentId) == null)
                    throw new ArgumentException($"Garment '{garmentId}' not found.");
                _workspace.SetActiveGarment(garmentId);
            }
            var settings = _workspace.Document.Simulation.Clone();
            settings.SelfCollision = Flag(args, "selfCollision", true);
            settings.Substeps = Math.Clamp(Whole(args, "substeps", 12), 1, 64);
            _ = ((ISessionHost)this).Avatar; // the avatar has to be ready before the cloth can drape on it
            string? problem = _simulation.Start(all, Lang.T("Simulate (agent)"), settings);
            if (problem != null)
                throw new InvalidOperationException(problem);
            simulated.AddRange(_simulation.SimulatedGarments);
            StatusText = Lang.F("Agent: {0}", Lang.T("simulating"));
            Notify(nameof(Simulating));
            Notify(nameof(CanUndo));
            return _simulation.Frame;
        });

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromMinutes(10))
        {
            await Task.Delay(30);
            // The user may stop it, or start editing (which stops it): the agent gets what was done until then.
            if (await OnWindowThread(() => !_simulation.IsRunning || _simulation.Frame - start >= frames))
                break;
        }

        return await OnWindowThread<object>(() =>
        {
            long ran = Math.Max(0, _simulation.Frame - start);
            float peak = _simulation.PeakSpeed, gap = _simulation.SeamGap;
            string backend = _simulation.BackendName;
            int substeps = _simulation.EffectiveSubsteps, particles = _simulation.ParticleCount;
            double ms = _simulation.StepMilliseconds;
            bool stoppedByUser = !_simulation.IsRunning;
            if (_simulation.IsRunning)
                StopSimulation();
            var doc = _workspace.Document;
            var openSeams = simulated.Select(doc.FindGarment).OfType<Seamstress.Core.Model.Garment>()
                .SelectMany(g => SeamReport.OpenSeams(doc, _workspace.MeshFor, g)).Take(12).ToList();
            return new
            {
                backend,
                frames = ran,
                substeps,
                particles,
                msPerFrame = Math.Round(ms, 2),
                seamGapMetres = MathF.Round(gap, 4),
                peakSpeedMetresPerSec = MathF.Round(peak, 3),
                settled = peak < 0.08f && gap < 0.015f,
                simulatedGarments = simulated.Select(id => doc.FindGarment(id)?.Name).ToList(),
                openSeams,
                advice = stoppedByUser && ran < frames
                    ? "The user stopped the simulation (or edited the project) before the frames ran out."
                    : gap > 0.015f && peak > 0.3f
                        ? "The cloth was still moving and seams were still closing when the frames ran out: simulate again before judging the open seams."
                        : null,
                note = "Ran live in the user's window: everything sewn onto the garment simulated with it, and the run is one undo step there.",
                fit = _agentSession!.InspectFit(garmentId, includeSeams: false),
            };
        });
    }

    private static string? Text(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Whole(JsonElement args, string name, int fallback)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : fallback;

    private static bool Flag(JsonElement args, string name, bool fallback)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : fallback;

    // ---------------------------------------------------------------- ISessionHost: the project the tools work on

    Workspace ISessionHost.Workspace => _workspace;

    Avatar? ISessionHost.Avatar
    {
        get
        {
            // A tool needs the body now: finish loading it here rather than waiting for the timer.
            EnsureAvatar();
            if (_avatarTask != null)
            {
                try
                {
                    _avatarTask.Wait();
                }
                catch (AggregateException)
                {
                }
                FinishAvatar();
            }
            return _avatar;
        }
        set
        {
            _avatar = value;
            _avatarKey = AvatarKeyFor(_workspace.Document.Avatar);
            AvatarRevision++;
            ViewportRevision++;
        }
    }

    void ISessionHost.Open(string path)
    {
        if (Dirty)
            throw new InvalidOperationException("The Seamstress window has unsaved changes. Ask the user to save or discard them before another project is opened there.");
        if (!Open(path))
            throw new InvalidOperationException($"The window could not open {path}: {LastError}");
    }

    void ISessionHost.New()
    {
        if (Dirty)
            throw new InvalidOperationException("The Seamstress window has unsaved changes. Ask the user to save or discard them before a new project is started there.");
        NewDocument();
    }
}
