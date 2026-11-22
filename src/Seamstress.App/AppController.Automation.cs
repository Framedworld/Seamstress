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
