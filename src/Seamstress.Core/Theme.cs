using System.Text.Json;

namespace Seamstress.Core;

/// <summary>
/// The colours of the interface. A theme is a set of named roles: first the Qt palette roles that every control,
/// panel and menu follows, then the colours of the two view canvases. Four presets ship with the application; a
/// custom theme is a small JSON file using the same role names.
/// </summary>
public static class Theme
{
    /// <summary>The preset theme keys, in the order the Preferences page lists them.</summary>
    public static readonly string[] Presets = ["default", "light", "hcdark", "hclight"];

    /// <summary>Every colour role, palette roles first, then the view colours.</summary>
    public static readonly string[] Roles =
    [
        "window", "windowText", "base", "alternateBase", "text", "button", "buttonText", "highlight",
        "highlightedText", "mid", "dark", "light", "midlight", "shadow", "placeholderText", "toolTipBase",
        "toolTipText", "brightText", "link",
        "viewerBg", "gridMinor", "gridMajor", "gridAxis", "checkerA", "checkerB", "checkerEdge", "ghostFill",
        "ghostStroke", "edgeActive", "edgeIdle", "labelActive", "labelDim", "pointFill", "overlayText",
        "annotation", "annotationDeep", "annotationFill", "warningText", "dangerText", "infoText",
    ];

    // The dark studio scheme: deep charcoal surfaces, crisp slate dividers, cobalt accent and warm amber highlights.
    private static readonly Dictionary<string, string> DefaultColors = new()
    {
        ["window"] = "#1b1e25", ["windowText"] = "#e8ecf4", ["base"] = "#161920", ["alternateBase"] = "#20242d",
        ["text"] = "#e8ecf4", ["button"] = "#232731", ["buttonText"] = "#e8ecf4", ["highlight"] = "#3b82f6",
        ["highlightedText"] = "#ffffff", ["mid"] = "#343b4d", ["dark"] = "#15171c", ["light"] = "#4a5368",
        ["midlight"] = "#2b303c", ["shadow"] = "#0d0f13", ["placeholderText"] = "#8e97a8", ["toolTipBase"] = "#232731",
        ["toolTipText"] = "#e8ecf4", ["brightText"] = "#ffffff", ["link"] = "#60a5fa",
        ["viewerBg"] = "#15171c", ["gridMinor"] = "#212530", ["gridMajor"] = "#2d3343", ["gridAxis"] = "#4a556d",
        ["checkerA"] = "#252a35", ["checkerB"] = "#343b4a", ["checkerEdge"] = "#6b768e",
        ["ghostFill"] = "#7d8ba3", ["ghostStroke"] = "#9aa8c0",
        ["edgeActive"] = "#e8ecf4", ["edgeIdle"] = "#7e889b", ["labelActive"] = "#f1f5fb", ["labelDim"] = "#929cae",
        ["pointFill"] = "#f1f5fb", ["overlayText"] = "#aeb6c7",
        ["annotation"] = "#f59e0b", ["annotationDeep"] = "#d97706", ["annotationFill"] = "#fde68a",
        ["warningText"] = "#fbbf24", ["dangerText"] = "#f87171", ["infoText"] = "#10b981",
    };

    private static readonly Dictionary<string, string> LightColors = new()
    {
        ["window"] = "#f0f1f3", ["windowText"] = "#1c1e21", ["base"] = "#ffffff", ["alternateBase"] = "#e9ebee",
        ["text"] = "#1c1e21", ["button"] = "#e3e5e9", ["buttonText"] = "#1c1e21", ["highlight"] = "#2f6fbd",
        ["highlightedText"] = "#ffffff", ["mid"] = "#9ba1a9", ["dark"] = "#c3c7cd", ["light"] = "#ffffff",
        ["midlight"] = "#eceef1", ["shadow"] = "#b9bec5", ["placeholderText"] = "#6b7078", ["toolTipBase"] = "#ffffff",
        ["toolTipText"] = "#1c1e21", ["brightText"] = "#ffffff", ["link"] = "#1f5fa8",
        ["viewerBg"] = "#e9ebee", ["gridMinor"] = "#dfe1e5", ["gridMajor"] = "#cdd0d5", ["gridAxis"] = "#a7adb5",
        ["checkerA"] = "#d9dce1", ["checkerB"] = "#c4c8cf", ["checkerEdge"] = "#6b7078",
        ["ghostFill"] = "#4b5563", ["ghostStroke"] = "#64748b",
        ["edgeActive"] = "#1c1e21", ["edgeIdle"] = "#71767e", ["labelActive"] = "#1c1e21", ["labelDim"] = "#5c6169",
        ["pointFill"] = "#ffffff", ["overlayText"] = "#4a4e55",
        ["annotation"] = "#a16207", ["annotationDeep"] = "#7a4e00", ["annotationFill"] = "#ffe58a",
        ["warningText"] = "#b45309", ["dangerText"] = "#c0392b", ["infoText"] = "#1f7a3d",
    };

    private static readonly Dictionary<string, string> HighContrastDarkColors = new()
    {
        ["window"] = "#000000", ["windowText"] = "#ffffff", ["base"] = "#000000", ["alternateBase"] = "#141414",
        ["text"] = "#ffffff", ["button"] = "#1f1f1f", ["buttonText"] = "#ffffff", ["highlight"] = "#3aa0ff",
        ["highlightedText"] = "#000000", ["mid"] = "#9aa0a8", ["dark"] = "#000000", ["light"] = "#6a6f78",
        ["midlight"] = "#33383f", ["shadow"] = "#000000", ["placeholderText"] = "#c6cad0", ["toolTipBase"] = "#000000",
        ["toolTipText"] = "#ffffff", ["brightText"] = "#ffffff", ["link"] = "#6cc0ff",
        ["viewerBg"] = "#000000", ["gridMinor"] = "#2e3138", ["gridMajor"] = "#454a52", ["gridAxis"] = "#9aa0a8",
        ["checkerA"] = "#1c1f24", ["checkerB"] = "#33373e", ["checkerEdge"] = "#c6cad0",
        ["ghostFill"] = "#c6ccd6", ["ghostStroke"] = "#e6eaf0",
        ["edgeActive"] = "#ffffff", ["edgeIdle"] = "#b8bec6", ["labelActive"] = "#ffffff", ["labelDim"] = "#b8bec6",
        ["pointFill"] = "#ffffff", ["overlayText"] = "#ffffff",
        ["annotation"] = "#ffe066", ["annotationDeep"] = "#d9b53d", ["annotationFill"] = "#ffe58a",
        ["warningText"] = "#ffb469", ["dangerText"] = "#ff8f86", ["infoText"] = "#8ce89c",
    };

    private static readonly Dictionary<string, string> HighContrastLightColors = new()
    {
        ["window"] = "#ffffff", ["windowText"] = "#000000", ["base"] = "#ffffff", ["alternateBase"] = "#f2f2f2",
        ["text"] = "#000000", ["button"] = "#e6e6e6", ["buttonText"] = "#000000", ["highlight"] = "#0a4fb5",
        ["highlightedText"] = "#ffffff", ["mid"] = "#5f5f5f", ["dark"] = "#000000", ["light"] = "#ffffff",
        ["midlight"] = "#d9d9d9", ["shadow"] = "#000000", ["placeholderText"] = "#4a4a4a", ["toolTipBase"] = "#ffffff",
        ["toolTipText"] = "#000000", ["brightText"] = "#ffffff", ["link"] = "#003ea8",
        ["viewerBg"] = "#ffffff", ["gridMinor"] = "#e0e0e0", ["gridMajor"] = "#c2c2c2", ["gridAxis"] = "#5f5f5f",
        ["checkerA"] = "#d4d4d4", ["checkerB"] = "#bcbcbc", ["checkerEdge"] = "#333333",
        ["ghostFill"] = "#333333", ["ghostStroke"] = "#1a1a1a",
        ["edgeActive"] = "#000000", ["edgeIdle"] = "#555555", ["labelActive"] = "#000000", ["labelDim"] = "#4a4a4a",
        ["pointFill"] = "#ffffff", ["overlayText"] = "#333333",
        ["annotation"] = "#7a4e00", ["annotationDeep"] = "#5c3a00", ["annotationFill"] = "#ffe58a",
        ["warningText"] = "#8a4b00", ["dangerText"] = "#aa1f1f", ["infoText"] = "#0f6b2f",
    };

    /// <summary>The colours of a preset theme. An unknown key gives the default theme.</summary>
    public static Dictionary<string, string> Preset(string key) => key switch
    {
        "light" => new Dictionary<string, string>(LightColors),
        "hcdark" => new Dictionary<string, string>(HighContrastDarkColors),
        "hclight" => new Dictionary<string, string>(HighContrastLightColors),
        _ => new Dictionary<string, string>(DefaultColors),
    };

    public static bool IsPreset(string key) => Presets.Contains(key);

    public static bool IsColor(string value) => ColorPattern.IsMatch(value.Trim());

    /// <summary>A colour from a theme file or a preset, with a fallback; never throws.</summary>
    public static string ColorOr(IReadOnlyDictionary<string, string> colors, string role, string fallback)
        => colors.TryGetValue(role, out var value) && IsColor(value) ? value.Trim() : fallback;

    /// <summary>The default value of a role, for use as a fallback.</summary>
    public static string DefaultColor(string role) => DefaultColors[role];

    /// <summary>Why a theme file could not be read; the interface translates these.</summary>
    public enum FileProblem
    {
        None,
        NotJson,
        NoColors,
        BadColor,
    }

    /// <summary>Reads a custom theme file. Roles the file leaves out take their default values; unknown names are
    /// ignored, so files written by a newer version still load. The name is taken from the file's "name".</summary>
    public static bool TryParse(string json, out string name, out Dictionary<string, string> colors, out FileProblem problem)
    {
        name = "";
        colors = new Dictionary<string, string>(DefaultColors);
        problem = FileProblem.None;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            problem = FileProblem.NotJson;
            return false;
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("colors", out var map)
                || map.ValueKind != JsonValueKind.Object)
            {
                problem = FileProblem.NoColors;
                return false;
            }
            if (document.RootElement.TryGetProperty("name", out var title) && title.ValueKind == JsonValueKind.String)
                name = title.GetString() ?? "";
            int found = 0;
            foreach (var entry in map.EnumerateObject())
            {
                if (!Roles.Contains(entry.Name))
                    continue;
                if (entry.Value.ValueKind != JsonValueKind.String || !IsColor(entry.Value.GetString() ?? ""))
                {
                    problem = FileProblem.BadColor;
                    return false;
                }
                colors[entry.Name] = entry.Value.GetString()!.Trim();
                found++;
            }
            if (found == 0)
            {
                problem = FileProblem.NoColors;
                return false;
            }
            return true;
        }
    }

    /// <summary>A theme as a file: {"name": …, "colors": {role: "#rrggbb", …}}.</summary>
    public static string ToJson(string name, IReadOnlyDictionary<string, string> colors)
    {
        var ordered = Roles.Where(colors.ContainsKey).ToDictionary(role => role, role => colors[role]);
        return JsonSerializer.Serialize(new { name, colors = ordered }, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>The files placed in the Themes folder: an example theme naming every role, and notes on the format.</summary>
    public static IEnumerable<string> ExampleFiles => typeof(Theme).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("Themes/", StringComparison.Ordinal)).Select(n => n["Themes/".Length..]);

    /// <summary>
    /// Copies the example files into <paramref name="folder"/>, creating it if needed. A file already there is left
    /// alone, so edits to it are kept; one that was deleted comes back. Returns the files written.
    /// </summary>
    public static List<string> InstallExamples(string folder)
    {
        var written = new List<string>();
        Directory.CreateDirectory(folder);
        foreach (string file in ExampleFiles)
        {
            string path = Path.Combine(folder, file);
            if (File.Exists(path))
                continue;
            using var source = typeof(Theme).Assembly.GetManifestResourceStream("Themes/" + file)!;
            using (var target = File.Create(path))
                source.CopyTo(target);
            written.Add(path);
        }
        return written;
    }

    private static readonly System.Text.RegularExpressions.Regex ColorPattern
        = new("^#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})$", System.Text.RegularExpressions.RegexOptions.Compiled);
}
