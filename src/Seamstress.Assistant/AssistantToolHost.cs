using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Seamstress.Assistant;

/// <summary>
/// Runs the assistant's tool calls through a runner the caller provides (the window's live-link path in the app,
/// <see cref="Seamstress.Mcp.UndoableTools"/> directly in tests). It refuses the tools the panel leaves out, writes
/// previews into its own folder and hands the picture back so Claude can look at it.
/// </summary>
public sealed class AssistantToolHost : IAssistantToolHost
{
    public delegate Task<object> Runner(string tool, JsonElement input, CancellationToken cancel);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly Runner _run;
    private readonly string _pictureFolder;
    private int _pictures;

    public AssistantToolHost(Runner run, string pictureFolder)
    {
        _run = run;
        _pictureFolder = pictureFolder;
    }

    public async Task<ToolOutcome> RunAsync(string tool, JsonElement input, CancellationToken cancel)
    {
        if (AssistantPrompt.LeftOut.Contains(tool))
            return new ToolOutcome($"'{tool}' is not available in the assistant panel: the user opens, saves and exports files from the menus.", IsError: true);

        string? png = null;
        if (tool == "render_preview")
        {
            Directory.CreateDirectory(_pictureFolder);
            png = Path.Combine(_pictureFolder, $"preview-{Interlocked.Increment(ref _pictures)}.png");
            var args = input.ValueKind == JsonValueKind.Object ? JsonNode.Parse(input.GetRawText())!.AsObject() : new JsonObject();
            args.Remove("svgPath");
            args["pngPath"] = png;
            input = JsonSerializer.SerializeToElement(args);
        }

        object result = await _run(tool, input, cancel).ConfigureAwait(false);
        string json = JsonSerializer.Serialize(result, Json);
        ImagePart? image = null;
        if (png != null && File.Exists(png))
        {
            image = ImagePart.FromFile(png);
            json = """{"ok": true, "picture": "attached below"}""";
        }
        return new ToolOutcome(json, IsError: false, image);
    }
}
