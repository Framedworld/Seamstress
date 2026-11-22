using System.Diagnostics;
using System.Runtime.InteropServices;
using Qt.Quick;
using Seamstress.Core;

namespace Seamstress.App;

/// <summary>Options taken from the command line before the user interface starts.</summary>
internal sealed class StartupOptions
{
    public string? File { get; set; }
    public List<string> Templates { get; } = new();
    public int SimulateFrames { get; set; }
    public string? Tool { get; set; }
    public bool UvMode { get; set; }
    public bool SelectFirst { get; set; }
    public string? Screenshot { get; set; }
    public string View { get; set; } = "";
    public string? Avatar { get; set; }
    public bool Smoke { get; set; }
    public bool Quads { get; set; }
    public float? Gender { get; set; }
    public string? Units { get; set; }
    /// <summary>"--lang": the interface language for this run, without changing the saved one.</summary>
    public string? Language { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? SelectPiece { get; set; }
    /// <summary>"--camera yaw,pitch,distance,targetX,targetY,targetZ": where the 3D camera starts, as a JSON array.</summary>
    public string Camera { get; set; } = "";
    /// <summary>"--timeout": seconds an automated run ("--screenshot", "--smoke") may take before it is stopped.</summary>
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>"--recover": restore a recovery file found at startup without asking (for automated checks).</summary>
    public bool Recover { get; set; }
    /// <summary>"--drop": files handed to the window as if they had been dragged onto it (for automated checks).</summary>
    public List<string> Drops { get; } = new();
    public bool AllowLaunch { get; set; } = true;
    /// <summary>Which renderer is in use, in English; the interface shows it translated.</summary>
    public string RendererDescription { get; set; } = "GPU";

    private static string CrashMarker => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "gpu-startup.marker");

    /// <summary>
    /// Left behind while the GPU renderer starts and removed once it has drawn a frame. Finding it on the next launch
    /// means the GPU path crashed or hung last time, so that launch falls back to the CPU renderer.
    /// </summary>
    public static bool PreviousGpuStartFailed => System.IO.File.Exists(CrashMarker);

    public static void WriteCrashMarker()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashMarker)!);
            System.IO.File.WriteAllText(CrashMarker, DateTime.Now.ToString("O"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void ClearCrashMarker()
    {
        try { System.IO.File.Delete(CrashMarker); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

public static class Program
{
    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetEnvUnix(string name, string value, int overwrite);

    // Qt reads its settings through the C runtime, which keeps its own copy of the environment on Windows.
    [DllImport("ucrtbase", EntryPoint = "_putenv_s")]
    private static extern int SetEnvWindows(string name, string value);

    /// <summary>Sets a variable in the native process environment, where Qt reads its configuration.</summary>
    private static void SetNativeEnvironment(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            if (OperatingSystem.IsWindows())
                SetEnvWindows(name, value);
            else
                SetEnvUnix(name, value, 1);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"Could not set {name}: {e.Message}");
            AppLog.Write($"Could not set {name}: {e.Message}");
        }
    }

    /// <summary>How long the interface has to draw its first frame before the launch counts as failed.</summary>
    private const int StartupTimeoutSeconds = 60;

    /// <summary>Exit codes of automated runs: the capture or check failed, or it did not finish in time.</summary>
    internal const int ExitFailed = 1, ExitTimedOut = 3;

    /// <summary>
    /// Ends the process with an exit code from any thread. Qt's own shutdown can wait on a render thread that is
    /// stuck, so a second thread ends the process outright if the orderly exit has not finished after a few seconds.
    /// </summary>
    internal static void ExitNow(int code)
    {
        try { Console.Error.Flush(); }
        catch (IOException) { }
        AppLog.Stop();
        var hard = new Thread(() =>
        {
            Thread.Sleep(3000);
            TerminateNow(code);
        })
        {
            IsBackground = true,
            Name = "exit watchdog",
        };
        hard.Start();
        Environment.Exit(code);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    /// <summary>Ends the process without running any shutdown code, keeping the exit code where the system allows it.</summary>
    private static void TerminateNow(int code)
    {
        if (OperatingSystem.IsWindows())
            TerminateProcess(GetCurrentProcess(), (uint)code);
        Process.GetCurrentProcess().Kill();
    }

    /// <summary>
    /// Stops an automated run ("--screenshot", "--smoke") that has not finished in time, with the reason on standard
    /// error, so that a script waiting for the picture gets an answer instead of a process that never ends. The
    /// capture is started by a timer in the interface, and those timers only advance while the window is being
    /// drawn: a run whose window stops rendering would otherwise wait for ever without a word.
    /// </summary>
    private static void WatchAutomation(StartupOptions options)
    {
        var started = Stopwatch.StartNew();
        var watchdog = new Thread(() =>
        {
            while (started.Elapsed.TotalSeconds < options.TimeoutSeconds && !AppController.AutomationFinished)
                Thread.Sleep(200);
            if (AppController.AutomationFinished)
            {
                // The job is done and the window has been asked to close. Qt's shutdown can itself wait for ever on
                // its render thread (seen about once in fifteen captures); this thread dies with the process when
                // the exit is orderly, and ends the process when it is not, so the caller is never left waiting.
                Thread.Sleep(8000);
                ExitNow(0);
                return;
            }
            string what = options.Screenshot != null
                ? $"the screenshot {options.Screenshot} was not written"
                : "the automated run did not finish";
            string stage = !AppController.InterfaceDrawn
                ? "the window never drew a frame"
                : $"the window drew {AppController.FramesDrawn} frame(s); last step: {AppController.AutomationStage}";
            Console.Error.WriteLine($"error: {what} within {options.TimeoutSeconds} seconds ({stage}). "
                + "Use --timeout <seconds> to allow more time.");
            ExitNow(ExitTimedOut);
        })
        {
            IsBackground = true,
            Name = "automation watchdog",
        };
        watchdog.Start();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr parent, string text, string caption, uint type);

    /// <summary>
    /// Closes a launch that never gets a window. The Qt bridge gives no result for a failed QML load: the engine
    /// writes the reason to the console, which a windowed app does not have, then waits as if all were well. The
    /// first frame is the only sign that the interface really came up, so without this the process would stay in
    /// the task manager with nothing on screen.
    /// </summary>
    private static void WatchStartup(bool automated, int limit)
    {
        var watchdog = new Thread(() =>
        {
            for (int quarter = 0; quarter < limit * 4; quarter++)
            {
                if (AppController.InterfaceDrawn)
                    return;
                Thread.Sleep(250);
            }
            if (AppController.InterfaceDrawn)
                return;
            string log = AppLog.FilePath ?? "(this run keeps no log file)";
            AppLog.Write($"No window after {limit} seconds: the interface did not load. The most "
                + "likely cause is a Qt QML module missing from this installation (the 3D viewport needs Qt Quick 3D).");
            AppLog.Stop();
            ReportStartupFailure($"The Seamstress window did not appear within {limit} seconds.\n\n"
                + "The interface could not be loaded. The most likely cause is a missing Qt Quick 3D module "
                + "(the 3D viewport needs it) or a graphics driver that did not start.", automated, log);
            if (automated)
                ExitNow(ExitFailed);
            Environment.Exit(1);
        })
        {
            IsBackground = true,
            Name = "startup watchdog",
        };
        watchdog.Start();
    }

    /// <summary>
    /// Says why a launch failed. A windowed program has no console, so the text goes into a message box (Windows)
    /// or to standard error, where automated runs read it.
    /// </summary>
    private static void ReportStartupFailure(string text, bool automated, string log)
    {
        if (automated)
        {
            Console.Error.WriteLine("error: " + text.Replace("\n\n", " ").Replace('\n', ' '));
            return;
        }
        string full = text + $"\n\nThe log has the details:\n{log}";
        if (OperatingSystem.IsWindows())
            MessageBoxW(IntPtr.Zero, full, "Seamstress", 0x00000010 /* MB_ICONERROR */);
        else
            Console.Error.WriteLine("error: " + full);
    }

    /// <summary>
    /// What is wrong with the files next to the program, or null when they are all there. An incomplete package, or
    /// one whose Qt libraries come from different versions, does not fail with a message: Qt aborts inside the QML
    /// engine, before there is a window to report anything.
    /// </summary>
    private static string? DeploymentProblem()
    {
        string root = AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows())
        {
            foreach (string file in new[] { "Qt6Core.dll", "Qt6Quick.dll", "Qt6Quick3D.dll" })
                if (!File.Exists(Path.Combine(root, file)))
                    return $"{file} is missing";

            // Two Qt versions next to each other is the other way a deployment goes wrong, and it aborts as well.
            string? expected = QtVersionOf(Path.Combine(root, "Qt6Core.dll"));
            if (expected != null)
            {
                foreach (string dll in Directory.EnumerateFiles(root, "Qt6*.dll"))
                {
                    string? actual = QtVersionOf(dll);
                    if (actual != null && actual != expected)
                        return $"{Path.GetFileName(dll)} is Qt {actual} but Qt6Core.dll is Qt {expected}";
                }
            }
        }
        else if (!File.Exists(Path.Combine(root, "lib", "libQt6Core.so.6")))
        {
            return "the Qt libraries are missing (lib/libQt6Core.so.6)";
        }

        if (!File.Exists(Path.Combine(root, "qml", "QtQuick3D", "qmldir")))
            return "the Qt Quick 3D QML module is missing (qml/QtQuick3D)";
        if (!File.Exists(Path.Combine(root, "Application", "Main.qml")))
            return "the application's QML files are missing (Application/Main.qml)";
        return null;
    }

    private static string? QtVersionOf(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool GpuLooksUsable()
    {
        if (!OperatingSystem.IsLinux())
            return true;
        try
        {
            // No DRM render node means there is no GPU driver to talk to (bare VM, container, broken driver).
            return Directory.Exists("/dev/dri") && Directory.GetFiles("/dev/dri", "renderD*").Length > 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>Writes the machine and startup details that make a launch log useful for a bug report.</summary>
    private static void LogStartup(string[] args)
    {
        AppLog.Write($"Seamstress {Version} starting");
        AppLog.Write($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
        AppLog.Write($"Runtime: .NET {Environment.Version}");
        AppLog.Write($"Arguments: {(args.Length == 0 ? "(none)" : string.Join(" ", args))}");
        AppLog.Write($"Settings: {AppSettings.DefaultPath}");
        AppLog.Write($"Log: {AppLog.FilePath ?? "(not written for this automated run)"}");
    }

    public static void Main(string[] args)
    {
        var options = new StartupOptions();
        string? forced = null;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--cpu": forced = "cpu"; break;
                case "--gpu": forced = "gpu"; break;
                case "--template": options.Templates.Add(Next()); break;
                case "--simulate": int.TryParse(Next(), out int frames); options.SimulateFrames = frames; break;
                case "--tool": options.Tool = Next(); break;
                case "--uv": options.UvMode = true; break;
                case "--select-first": options.SelectFirst = true; break;
                case "--view": options.View = Next(); break;
                case "--avatar": options.Avatar = Next(); break;
                case "--quads": options.Quads = true; break;
                case "--units": options.Units = Next(); break;
                case "--lang": options.Language = Next(); break;
                case "--gender":
                    if (float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g))
                        options.Gender = g;
                    break;
                case "--select": options.SelectPiece = Next(); break;
                case "--recover": options.Recover = true; break;
                case "--drop": options.Drops.Add(new Uri(Path.GetFullPath(Next())).AbsoluteUri); break;
                case "--camera":
                    {
                        // yaw,pitch[,distance[,targetX,targetY,targetZ]] in degrees and metres; what is left out
                        // keeps the view's usual value.
                        string text = Next();
                        var numbers = new List<float>();
                        bool valid = true;
                        foreach (string part in text.Split(','))
                        {
                            if (float.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float n)
                                && float.IsFinite(n))
                                numbers.Add(n);
                            else
                                valid = false;
                        }
                        if (!valid || numbers.Count is not (2 or 3 or 6))
                        {
                            Console.Error.WriteLine("error: --camera expects yaw,pitch,distance,targetX,targetY,targetZ "
                                + $"(degrees and metres; the distance and the target may be left out), not \"{text}\".");
                            Environment.Exit(2);
                        }
                        options.Camera = "[" + string.Join(",", numbers.Select(n => n.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]";
                        break;
                    }
                case "--timeout":
                    if (int.TryParse(Next(), out int seconds) && seconds > 0)
                        options.TimeoutSeconds = seconds;
                    break;
                case "--size":
                    {
                        var parts = Next().Split('x');
                        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                        {
                            options.Width = w;
                            options.Height = h;
                        }
                        break;
                    }
                case "--smoke": options.Smoke = true; options.AllowLaunch = false; break;
                case "--screenshot": options.Screenshot = Path.GetFullPath(Next()); options.AllowLaunch = false; break;
                case "--help":
                case "-h":
                    Console.WriteLine("Usage: Seamstress [file.garment.json] [--cpu | --gpu]\n"
                        + "  --cpu   render the 3D viewport on the CPU (software rasterizer)\n"
                        + "  --gpu   render on the GPU even if the last GPU start failed\n"
                        + "  --no-cuda  simulate cloth on the CPU even when an NVIDIA GPU is available\n"
                        + "  --lang  interface language for this run: " + string.Join(", ", Lang.Languages.Select(l => l.Code)) + "\n"
                        + "Automation:\n"
                        + "  --screenshot <png>  capture the window after 5 seconds, then quit (exit code 0 once the file is written)\n"
                        + "  --view <names>      what to capture or open: garment, front, side, back, threequarter, closeup, prefs, export...\n"
                        + "  --camera yaw,pitch,distance,targetX,targetY,targetZ\n"
                        + "                      where the 3D camera starts, in degrees and metres (yaw 0 front, 90 side, 180 back)\n"
                        + "  --size <W>x<H>      window size\n"
                        + "  --timeout <seconds> stop an automated run that has not finished by then (default 120; exit code 3)");
                    return;
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal) && System.IO.File.Exists(args[i]))
                        options.File = Path.GetFullPath(args[i]);
                    break;
            }
        }

        // The automated check reads English texts unless asked for another language.
        if (options.Smoke)
            options.Language ??= Lang.English;

        var settings = AppSettings.Load();
        bool automated = options.Smoke || options.Screenshot != null;
        // The log starts before Qt so that failures while the window is created are recorded too. Automated runs
        // report on standard error and do not leave session logs behind.
        if (!automated)
            AppLog.Start(AppSettings.LogsFolder);
        LogStartup(args);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("Unhandled exception:\n" + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write("Unobserved task exception:\n" + e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => AppLog.Stop();

        // After the log has started, so that it records which device the cloth solver got, or why it got none.
        if (args.Contains("--no-cuda"))
            AppLog.Write("Cloth solver: CPU (CUDA turned off with --no-cuda).");
        else if (!Seamstress.Cuda.CudaClothBackend.Register())
            AppLog.Write("Cloth solver: CPU.");

        // Decide between the GPU renderer and the CPU fallback before Qt creates its rendering context.
        string preference = forced ?? settings.Renderer;
        bool gpuUsable = GpuLooksUsable();
        bool previousGpuFailure = StartupOptions.PreviousGpuStartFailed;
        AppLog.Write($"Renderer preference: {preference}");
        AppLog.Write($"Previous GPU start failed: {previousGpuFailure}");
        if (OperatingSystem.IsLinux())
            AppLog.Write($"GPU driver found: {gpuUsable}");
        bool software;
        if (preference == "cpu")
        {
            software = true;
            options.RendererDescription = Lang.N("CPU (software rendering)");
        }
        else if (preference == "gpu")
        {
            software = false;
            options.RendererDescription = Lang.N("GPU");
        }
        else if (!gpuUsable)
        {
            software = true;
            options.RendererDescription = Lang.N("CPU (no GPU driver found)");
        }
        else if (previousGpuFailure)
        {
            software = true;
            options.RendererDescription = Lang.N("CPU (the GPU renderer failed to start last time)");
            options.ClearCrashMarker();
            AppLog.Write("The previous GPU start did not finish; falling back to the CPU renderer.");
        }
        else
        {
            software = false;
            options.RendererDescription = Lang.N("GPU");
        }
        AppLog.Write($"Renderer: {options.RendererDescription}");
        AppLog.Write($"Language: {options.Language ?? settings.Language}");

        if (software)
        {
            SetNativeEnvironment("QSG_RHI_PREFER_SOFTWARE_RENDERER", "1");
            SetNativeEnvironment("LIBGL_ALWAYS_SOFTWARE", "1");
            AppLog.Write("Software rendering variables set.");
        }
        else if (options.Screenshot == null)
        {
            StartupOptions.WriteCrashMarker();
            AppLog.Write("GPU startup marker written.");
        }
        if (automated && Environment.GetEnvironmentVariable("QT_D3D_NO_VBLANK_THREAD") == null)
        {
            // Qt (6.9 and later) paces a Direct3D window with a thread that waits for the monitor's vertical blank.
            // With the display asleep (an unattended PC), switched off or absent, that blank never comes: the window
            // draws one frame and then no more, and because the interface's timers advance with the frames, the
            // timer that takes the screenshot never fires. Automated runs must not depend on a monitor being awake.
            SetNativeEnvironment("QT_D3D_NO_VBLANK_THREAD", "1");
        }
        SetNativeEnvironment("QT_QUICK_CONTROLS_STYLE", "Fusion");
        // Qt's file dialogs need this to list local files when they run inside QML.
        SetNativeEnvironment("QML_XHR_ALLOW_FILE_READ", "1");

        // Refuse an installation that cannot work before Qt is asked to load it: in that case Qt aborts, and there
        // is no window left to say why.
        string? broken = DeploymentProblem();
        if (broken != null)
        {
            string log = AppLog.FilePath ?? "(this run keeps no log file)";
            AppLog.Write($"This installation is incomplete: {broken}");
            AppLog.Stop();
            ReportStartupFailure($"Seamstress cannot start from this folder:\n{broken}\n\n"
                + "Extract the whole archive again into a new folder (do not start the program from inside it) "
                + "and try again.", automated, log);
            Environment.Exit(1);
        }

        AppController.Startup = options;
        Qt.Application.DisplayName = "Seamstress";
        Qt.Application.SetWindowIcon("qrc:/assemblies/Seamstress/icons/seamstress.svg");
        // Started before the QML load, which reports nothing when the interface cannot be built.
        // "--simulate" runs its frames before the window exists, so such a run gets its whole time allowance to
        // show the first frame; any other automated run that has no window after a minute has failed to load.
        WatchStartup(automated, !automated ? StartupTimeoutSeconds
            : options.SimulateFrames > 0 ? Math.Max(StartupTimeoutSeconds, options.TimeoutSeconds)
            : Math.Min(StartupTimeoutSeconds, options.TimeoutSeconds));
        if (automated)
            WatchAutomation(options);
        Qml.LoadFromRootModule("Main");
        Qml.WaitForExit();
    }
}
