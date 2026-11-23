using Seamstress.Core;

namespace Seamstress.App;

/// <summary>
/// What automated runs ("--screenshot", "--smoke") need from the interface: where the camera starts, and a way to say
/// how far the run got, so that the watchdog in <see cref="Program"/> can end a run that stalls with a reason.
/// </summary>
public partial class AppController
{
    /// <summary>Set once the screenshot was written (or the run was told to fail); the watchdog stops waiting.</summary>
    internal static volatile bool AutomationFinished;

    /// <summary>Frames the window has drawn, counted only in automated runs; read by the watchdog for its report.</summary>
    internal static volatile int FramesDrawn;

    /// <summary>The last thing the automated run did, in words, for the watchdog's report.</summary>
    internal static volatile string AutomationStage = "waiting for the capture timer";

    /// <summary>True for "--screenshot" and "--smoke" runs.</summary>
    public bool StartupAutomated => Startup.Smoke || Startup.Screenshot != null;

    /// <summary>"--camera": [yaw, pitch] or [yaw, pitch, distance] or all six numbers as a JSON array; empty when not given.</summary>
    public string StartupCamera => Startup.Camera;

    /// <summary>"--drop": the URLs of files to treat as dropped on the window, separated by "|"; empty when none.</summary>
    public string StartupDrops => string.Join("|", Startup.Drops);

    /// <summary>Called by the window for every frame of an automated run.</summary>
    public void FrameDrawn() => FramesDrawn = FramesDrawn + 1;

    /// <summary>Notes how far an automated run got.</summary>
    public void AutomationStep(string step) => AutomationStage = step;

    // "--view livesim": how long the live simulation and the 3D view's updates take, measured in the real window.
    private readonly System.Diagnostics.Stopwatch _benchClock = new();
    private double _benchSyncMs;
    private int _benchSyncs;
    private int _benchFrames;
    private double _benchStepMs;

    /// <summary>Starts simulating the active garment and starts counting.</summary>
    public void BenchmarkStart()
    {
        if (!_simulation.IsRunning)
            StartSimulation();
        AppLog.Write("livesim: started");
        _benchSyncMs = 0;
        _benchSyncs = 0;
        _benchFrames = 0;
        _benchStepMs = 0;
        _benchArmed = true;
    }

    private bool _benchArmed;

    private void BenchmarkCountFrame()
    {
        if (!_benchArmed)
            return;
        if (!_benchClock.IsRunning)
        {
            // Counting starts with the first frame the simulation delivers (the avatar may still be getting ready).
            _benchClock.Restart();
            return;
        }
        _benchFrames++;
        _benchStepMs += _simulation.StepMilliseconds;
    }

    private void BenchmarkCountSync(double ms)
    {
        if (!_benchClock.IsRunning)
            return;
        _benchSyncs++;
        _benchSyncMs += ms;
    }

    /// <summary>Writes what was measured to the log and standard error.</summary>
    public void BenchmarkReport()
    {
        if (!_benchClock.IsRunning)
            return;
        double seconds = _benchClock.Elapsed.TotalSeconds;
        _benchClock.Stop();
        _benchArmed = false;
        string line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "livesim: {0:N0} particles, {1:0.0} frames shown/s, solver {2:0.0} ms/step, 3D view update {3:0.0} ms ({4} updates in {5:0.0} s)",
            _simulation.ParticleCount, _benchFrames / seconds, _benchFrames > 0 ? _benchStepMs / _benchFrames : 0,
            _benchSyncs > 0 ? _benchSyncMs / _benchSyncs : 0, _benchSyncs, seconds);
        AppLog.Write(line);
        Console.Error.WriteLine(line);
    }

    /// <summary>The automated run did what it was started for; the window closes next.</summary>
    public void AutomationDone()
    {
        AutomationStage = "finished";
        AutomationFinished = true;
    }

    /// <summary>Ends an automated run that could not do its job: one line on standard error and a non-zero exit code.</summary>
    public void AutomationFailed(string reason)
    {
        AutomationFinished = true;
        Console.Error.WriteLine("error: " + reason);
        AppLog.Write("Automated run failed: " + reason);
        Program.ExitNow(Program.ExitFailed);
    }
}
