using System.Reflection;

namespace Seamstress.Mcp;

/// <summary>
/// When this server was built, and whether the source next to it has changed since: a server left running, or a
/// build that was never refreshed, silently lacks the newest tools, which is hard to notice from the client side.
/// </summary>
public static class BuildInfo
{
    public static DateTime BuiltUtc { get; } = File.GetLastWriteTimeUtc(Assembly.GetExecutingAssembly().Location);

    /// <summary>A sentence to show the user when the source is newer than this build, otherwise null.</summary>
    public static string? StaleWarning()
    {
        try
        {
            string? dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            while (dir != null && !File.Exists(Path.Combine(dir, "Seamstress.slnx")))
                dir = Path.GetDirectoryName(dir);
            if (dir == null)
                return null;
            DateTime newest = DateTime.MinValue;
            string newestFile = "";
            foreach (string project in new[] { "Seamstress.Core", "Seamstress.Cuda", "Seamstress.Agent", "Seamstress.Mcp" })
            {
                string root = Path.Combine(dir, "src", project);
                if (!Directory.Exists(root))
                    continue;
                foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(root, file);
                    if (rel.StartsWith("obj", StringComparison.Ordinal) || rel.StartsWith("bin", StringComparison.Ordinal))
                        continue;
                    var time = File.GetLastWriteTimeUtc(file);
                    if (time > newest)
                    {
                        newest = time;
                        newestFile = Path.Combine("src", project, rel);
                    }
                }
            }
            if (newest <= BuiltUtc.AddSeconds(90))
                return null;
            return $"This Seamstress MCP server was built {BuiltUtc.ToLocalTime():yyyy-MM-dd HH:mm}, but its source changed after that "
                 + $"({newestFile}, {newest.ToLocalTime():yyyy-MM-dd HH:mm}). Tools may be missing or out of date. "
                 + "Rebuild with: dotnet build src/Seamstress.Mcp -c Release, then restart the server.";
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static object Describe(int toolCount) => new
    {
        builtLocal = BuiltUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        tools = toolCount,
        staleWarning = StaleWarning(),
    };
}
