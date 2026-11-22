using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Seamstress.Core.IO;
using Seamstress.Core.Model;

namespace Seamstress.Core.Editing;

/// <summary>A recovery copy found on disk: unsaved work of a session that did not close properly.</summary>
/// <param name="Key">Identifies the copy inside the recovery folder.</param>
/// <param name="OriginalPath">The project the work belongs to, or null when it was never saved.</param>
/// <param name="Name">The document's name, for the question put to the user.</param>
/// <param name="SavedUtc">When the copy was written.</param>
/// <param name="ProcessId">The process that wrote it.</param>
public sealed record RecoveryCopy(string Key, string? OriginalPath, string Name, DateTime SavedUtc, int ProcessId);

/// <summary>
/// Recovery copies of open documents, kept in the per-user application data folder, never next to the user's files.
/// Each copy is an ordinary garment file ("&lt;key&gt;.garment.json", which can also be opened by hand) with a small
/// "&lt;key&gt;.info.json" beside it that says which project it belongs to. A copy is written while a document has
/// unsaved changes and removed when the document is saved or closed properly, so a copy that is still there at the
/// next start, and newer than its project, is work that was lost in a crash.
/// </summary>
public sealed class RecoveryStore
{
    private const string InfoSuffix = ".info.json";

    public string Folder { get; }

    public RecoveryStore(string? folder = null) => Folder = folder ?? DefaultFolder;

    /// <summary>
    /// "recovery" beside the settings file. The environment variable SEAMSTRESS_RECOVERY_FOLDER names another folder,
    /// which automated checks use to stay out of the user's own.
    /// </summary>
    public static string DefaultFolder
    {
        get
        {
            string? chosen = Environment.GetEnvironmentVariable("SEAMSTRESS_RECOVERY_FOLDER");
            return !string.IsNullOrWhiteSpace(chosen)
                ? Path.GetFullPath(chosen)
                : Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "recovery");
        }
    }

    /// <summary>
    /// The key of the copy for a document: the same for every session that has the same project open, so there is one
    /// copy per project, and one per session for a document that has no file yet.
    /// </summary>
    public static string KeyFor(string? originalPath, string sessionId)
    {
        if (originalPath == null)
            return "untitled-" + sessionId;
        string full = Path.GetFullPath(originalPath);
        if (OperatingSystem.IsWindows())
            full = full.ToLowerInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..12].ToLowerInvariant();
        string name = Path.GetFileName(originalPath);
        if (name.EndsWith(GarmentFile.Extension, StringComparison.OrdinalIgnoreCase))
            name = name[..^GarmentFile.Extension.Length];
        return ExportScene.SafeName(name) + "-" + hash;
    }

    public string DocumentFile(string key) => Path.Combine(Folder, key + GarmentFile.Extension);
    private string InfoFile(string key) => Path.Combine(Folder, key + InfoSuffix);

    /// <summary>
    /// Writes (or replaces) the copy for a document. Safe to call from a background thread with a document nobody
    /// else changes, such as a clone. The files are written under temporary names first, so a crash while writing
    /// leaves the previous copy intact.
    /// </summary>
    public void Write(string key, Document document, string? originalPath)
    {
        Directory.CreateDirectory(Folder);
        // No base folder: textures and reference images keep their full paths, since the copy lives elsewhere.
        string json = GarmentFile.Serialize(document);
        string info = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["original"] = originalPath,
            ["name"] = document.Name,
            ["saved"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["process"] = Environment.ProcessId,
        });
        Replace(DocumentFile(key), json);
        Replace(InfoFile(key), info);
    }

    private static void Replace(string file, string text)
    {
        string temporary = file + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, file, overwrite: true);
    }

    /// <summary>Removes a copy; nothing happens when there is none.</summary>
    public void Delete(string key)
    {
        foreach (string file in new[] { InfoFile(key), DocumentFile(key), InfoFile(key) + ".tmp", DocumentFile(key) + ".tmp" })
        {
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public Document Load(RecoveryCopy copy) => GarmentFile.Deserialize(File.ReadAllText(DocumentFile(copy.Key)));

    /// <summary>
    /// The copies worth offering, newest first: those newer than the project they belong to (or whose project was
    /// never saved or is gone) and not written by a Seamstress that is still running. Copies their project has
    /// overtaken are removed on the way.
    /// </summary>
    public List<RecoveryCopy> Find()
    {
        var found = new List<RecoveryCopy>();
        if (!Directory.Exists(Folder))
            return found;
        string[] infos;
        try
        {
            infos = Directory.GetFiles(Folder, "*" + InfoSuffix);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return found;
        }
        foreach (string infoFile in infos)
        {
            string key = Path.GetFileName(infoFile)[..^InfoSuffix.Length];
            if (!File.Exists(DocumentFile(key)) || Read(key, infoFile) is not { } copy)
                continue;
            if (copy.ProcessId != Environment.ProcessId && IsRunning(copy.ProcessId))
                continue; // another window still has this document open
            if (copy.OriginalPath != null && File.Exists(copy.OriginalPath)
                && File.GetLastWriteTimeUtc(copy.OriginalPath) >= copy.SavedUtc)
            {
                Delete(key); // saved after the copy was made: the project is the newer of the two
                continue;
            }
            found.Add(copy);
        }
        found.Sort((a, b) => b.SavedUtc.CompareTo(a.SavedUtc));
        return found;
    }

    private static RecoveryCopy? Read(string key, string infoFile)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(infoFile));
            var root = json.RootElement;
            string? original = root.TryGetProperty("original", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
            string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            int process = root.TryGetProperty("process", out var p) && p.TryGetInt32(out int id) ? id : 0;
            if (!root.TryGetProperty("saved", out var s) || s.ValueKind != JsonValueKind.String
                || !DateTime.TryParse(s.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var saved))
                saved = File.GetLastWriteTimeUtc(infoFile);
            return new RecoveryCopy(key, original, name, saved.ToUniversalTime(), process);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when the process that wrote a copy is still a running Seamstress.</summary>
    private static bool IsRunning(int processId)
    {
        if (processId <= 0)
            return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited && process.ProcessName.Contains("Seamstress", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// How often recovery copies are written. Kept in its own small file beside the settings ("autosave.json"), because
/// it was added after <see cref="AppSettings"/>; it can move there.
/// </summary>
public sealed class AutosaveSettings
{
    public const int DefaultMinutes = 3, MaxMinutes = 60;

    /// <summary>Minutes between recovery copies of a document with unsaved changes; 0 turns them off.</summary>
    public int Minutes { get; set; } = DefaultMinutes;

    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "autosave.json");

    public static AutosaveSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                if (json.RootElement.TryGetProperty("minutes", out var minutes) && minutes.TryGetInt32(out int value))
                    return new AutosaveSettings { Minutes = Math.Clamp(value, 0, MaxMinutes) };
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged file means the default interval, not a failed start.
        }
        return new AutosaveSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"minutes\": " + Math.Clamp(Minutes, 0, MaxMinutes).ToString(CultureInfo.InvariantCulture) + " }\n");
    }
}
