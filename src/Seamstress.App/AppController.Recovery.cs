using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Seamstress.Core;
using Seamstress.Core.Editing;
using Seamstress.Core.IO;
using Seamstress.Core.Model;

namespace Seamstress.App;

/// <summary>
/// Autosave and crash recovery. While the open document has unsaved changes, a recovery copy of it is written to the
/// application data folder every few minutes and after each simulation: never to the user's own file, and without
/// touching the undo history. Saving or closing properly removes the copy, so one that is found at the next start
/// is work a crash would have lost, and the user is asked whether to restore it.
/// </summary>
public partial class AppController
{
    private RecoveryStore _recovery = new();
    private readonly AutosaveSettings _autosave = AutosaveSettings.Load();
    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..8];
    private readonly HashSet<string> _recoveryKeys = new();
    private readonly Stopwatch _sinceRecoveryCopy = Stopwatch.StartNew();
    private readonly Stopwatch _sinceDocumentChange = Stopwatch.StartNew();

    /// <summary>
    /// A pause this long after the last change also writes the copy, without waiting for the interval. The check runs
    /// on the interface's timer, which stands still while the monitor is asleep (see Program.cs on
    /// QT_D3D_NO_VBLANK_THREAD), so the copy has to be on disk before an unattended PC gets that far: a display
    /// goes to sleep after a minute without input at the earliest.
    /// </summary>
    private const double RecoveryPauseSeconds = 30;
    private Task? _recoveryTask;
    private long _documentChanges, _copiedChanges = -1;
    private bool _wasSimulating;
    private bool _recoveryWrites = true;
    private bool _recoveryPending;

    private void InitRecovery()
    {
        _workspace.History.Changed += () =>
        {
            _documentChanges++;
            _sinceDocumentChange.Restart();
        };
        bool offer = !StartupAutomated;
        if (Startup.Smoke)
        {
            // The automated check keeps its recovery copies in its own folder, starts with none, asks for them
            // itself, and does not depend on the interval chosen in Preferences (nothing is saved from here).
            string folder = Path.Combine(Path.GetDirectoryName(Startup.Screenshot ?? Path.Combine(Path.GetTempPath(), "smoke.png"))!, "smoke-recovery");
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            _recovery = new RecoveryStore(folder);
            _autosave.Minutes = AutosaveSettings.DefaultMinutes;
        }
        else if (Startup.Screenshot != null)
        {
            // A capture neither leaves copies behind nor stops to ask, unless it is the question being captured.
            _recoveryWrites = false;
            offer = Startup.View.Contains("recovery") || Startup.Recover;
        }
        if (!offer)
            return;
        try
        {
            var copies = _recovery.Find();
            if (copies.Count == 0)
                return;
            AppLog.Write($"Recovery copies found: {string.Join(", ", copies.Select(c => c.Key))}");
            if (Startup.Recover)
                RestoreRecovery(copies[0].Key);
            else
                _recoveryPending = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Could not look for recovery copies: {e.Message}");
        }
    }

    /// <summary>True at startup when unsaved work of an earlier session was found; the window then asks about it.</summary>
    public bool RecoveryPending => _recoveryPending;

    /// <summary>Minutes between recovery copies; 0 when they are turned off.</summary>
    public int AutosaveMinutes => _autosave.Minutes;

    /// <summary>The folder recovery copies are kept in, for Preferences.</summary>
    public string RecoveryFolder => _recovery.Folder;

    /// <summary>Stores how often recovery copies are written (0 = never), from Preferences.</summary>
    public void SetAutosaveMinutes(int minutes)
    {
        minutes = Math.Clamp(minutes, 0, AutosaveSettings.MaxMinutes);
        if (minutes == _autosave.Minutes)
            return;
        _autosave.Minutes = minutes;
        try
        {
            _autosave.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = Lang.F("Could not save preferences: {0}", e.Message);
            AppLog.Write($"Could not save the autosave preference: {e.Message}");
        }
        if (minutes == 0)
            ForgetRecovery();
        Notify(nameof(AutosaveMinutes));
    }

    /// <summary>Called from <see cref="Tick"/>: writes a recovery copy when one is due, and drops it when it is not needed any more.</summary>
    private void RecoveryTick()
    {
        bool simulating = _simulation.IsRunning;
        bool simulationEnded = _wasSimulating && !simulating;
        _wasSimulating = simulating;
        if (!_recoveryWrites || _autosave.Minutes <= 0)
            return;
        if (!_workspace.History.IsDirty)
        {
            // Saved, or undone back to the saved state: the project file is all there is to recover.
            if (_recoveryKeys.Count > 0 && !_workspace.History.InTransaction)
                ForgetRecovery();
            return;
        }
        if (_documentChanges == _copiedChanges && !simulationEnded)
            return;
        // Due when the interval has come round (so a long stretch of work is never further behind than that), when
        // the work has paused for a moment, and when a simulation has just ended.
        bool due = _sinceRecoveryCopy.Elapsed.TotalMinutes >= _autosave.Minutes
            || _sinceDocumentChange.Elapsed.TotalSeconds >= RecoveryPauseSeconds
            || simulationEnded;
        if (due && _recoveryTask is not { IsCompleted: false })
            WriteRecovery(wait: false);
    }

    /// <summary>
    /// Writes the recovery copy of the open document now. The document is cloned here, on the interface's thread
    /// (cheap: the simulated shapes are shared, not copied), and written on another, so a large project does not
    /// make the interface stutter.
    /// </summary>
    private bool WriteRecovery(bool wait)
    {
        if (_recoveryTask is { IsCompleted: false } running)
        {
            if (!wait)
                return false;
            running.Wait(TimeSpan.FromSeconds(10));
        }
        var snapshot = _workspace.Document.Clone();
        string? original = _workspace.FilePath;
        string key = RecoveryStore.KeyFor(original, _sessionId);
        var store = _recovery;
        _copiedChanges = _documentChanges;
        _sinceRecoveryCopy.Restart();
        _recoveryKeys.Add(key);
        _recoveryTask = Task.Run(() =>
        {
            try
            {
                store.Write(key, snapshot, original);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"Could not write the recovery copy: {e.Message}");
            }
        });
        if (wait)
            _recoveryTask.Wait(TimeSpan.FromSeconds(10));
        return true;
    }

    /// <summary>Writes the recovery copy now and waits for it; used by the automated check.</summary>
    public bool WriteRecoveryCopy() => WriteRecovery(wait: true) && File.Exists(_recovery.DocumentFile(RecoveryStore.KeyFor(_workspace.FilePath, _sessionId)));

    /// <summary>Removes this session's recovery copies: the document was saved, closed or replaced on purpose.</summary>
    private void ForgetRecovery()
    {
        if (_recoveryTask is { IsCompleted: false } running)
            running.Wait(TimeSpan.FromSeconds(10));
        foreach (string key in _recoveryKeys)
            _recovery.Delete(key);
        _recoveryKeys.Clear();
        _copiedChanges = -1;
        _sinceRecoveryCopy.Restart();
    }

    /// <summary>The recovery copies that can be restored, newest first, as [{key, name, file, saved}].</summary>
    public string RecoveryCopies()
    {
        List<RecoveryCopy> copies;
        try
        {
            copies = _recovery.Find();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            copies = new List<RecoveryCopy>();
        }
        return JsonSerializer.Serialize(copies.Select(c => new
        {
            key = c.Key,
            name = string.IsNullOrEmpty(c.Name) ? Lang.T("Untitled") : c.Name,
            file = c.OriginalPath ?? "",
            saved = c.SavedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
        }));
    }

    /// <summary>
    /// Opens a recovery copy as unsaved changes to the project it belongs to. The copy stays on disk until the
    /// restored work is saved, so a second crash does not lose it.
    /// </summary>
    public bool RestoreRecovery(string key)
    {
        _recoveryPending = false;
        RecoveryCopy? copy = null;
        try
        {
            copy = _recovery.Find().FirstOrDefault(c => c.Key == key);
            if (copy == null)
                return false;
            if (_simulation.IsRunning)
                StopSimulation();
            Document recovered = _recovery.Load(copy);
            // A copy of a project that was moved or deleted since is restored as a new, unsaved document.
            _workspace.Restore(recovered, copy.OriginalPath);
            // From here on this session owns the copy: it is replaced by the next one and removed on saving.
            _recoveryKeys.Add(copy.Key);
            _copiedChanges = _documentChanges;
            _sinceRecoveryCopy.Restart();
            StatusText = Lang.F("Restored the unsaved work on “{0}” from {1}. Save to keep it.", DisplayName(copy),
                copy.SavedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            AppLog.Write($"Restored the recovery copy {copy.Key} ({copy.OriginalPath ?? "never saved"}).");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            Fail(Lang.F("Could not restore the recovery copy of “{0}”.", copy == null ? key : DisplayName(copy)), e);
            return false;
        }
    }

    private static string DisplayName(RecoveryCopy copy)
        => copy.OriginalPath != null ? Path.GetFileName(copy.OriginalPath) : string.IsNullOrEmpty(copy.Name) ? Lang.T("Untitled") : copy.Name;

    /// <summary>Deletes a recovery copy the user does not want.</summary>
    public void DiscardRecovery(string key)
    {
        _recoveryPending = false;
        _recovery.Delete(key);
        _recoveryKeys.Remove(key);
        AppLog.Write($"Discarded the recovery copy {key}.");
    }
}
