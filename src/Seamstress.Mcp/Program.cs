using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Seamstress.Mcp;

/// <summary>
/// Model Context Protocol (MCP) stdio JSON-RPC 2.0 server for Seamstress.
/// Allows AI agents to draft 2D patterns, sew edges, arrange pieces in 3D, run CUDA-accelerated cloth simulation,
/// inspect fit, render previews, and export garments.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static int Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var session = new GarmentSession();
        var tools = new McpToolRegistry(session);

        Console.Error.WriteLine($"[Seamstress.Mcp] Started. Compute backend: {session.ComputeBackend}");
        if (BuildInfo.StaleWarning() is { } staleAtStart)
            Console.Error.WriteLine($"[Seamstress.Mcp] WARNING: {staleAtStart}");

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonNode? id = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idEl))
                    id = JsonNode.Parse(idEl.GetRawText());

                string method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() ?? "" : "";
                var @params = root.TryGetProperty("params", out var pEl) ? pEl : default;

                // Notifications (no id) require no response
                if (id == null && method.StartsWith("notifications/", StringComparison.Ordinal))
                    continue;

                JsonNode result = method switch
                {
                    "initialize" => new JsonObject
                    {
                        ["protocolVersion"] = "2024-11-05",
                        ["capabilities"] = new JsonObject
                        {
                            ["tools"] = new JsonObject { ["listChanged"] = false },
                        },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "seamstress-mcp",
                            ["version"] = $"0.1.0+{BuildInfo.BuiltUtc:yyyyMMdd.HHmm}",
                        },
                        ["instructions"] = (BuildInfo.StaleWarning() is { } stale ? "WARNING: " + stale + " Tell the user. " : "")
                            + "Seamstress drafts 2D pattern pieces, sews them and drapes them on an avatar. Start with load_avatar "
                            + "(it returns tape measurements), draft with add_piece naming the edges, sew with sew_named, then simulate "
                            + "and read openSeams. Use add_patch, add_tube and add_hardware for pockets, sleeves and snaps. Give each "
                            + "garment worn over another a bodyOffsetMm instead of a large thickness. When the Seamstress window is "
                            + "open, the tools work on the project the user has open there (they watch, and each call is one undo "
                            + "step); live_link says which, and get_project_summary shows the window under liveLink.",
                    },
                    "ping" => new JsonObject(),
                    "tools/list" => new JsonObject
                    {
                        ["tools"] = WithLiveLinkTool(tools.GetToolDefinitions()),
                    },
                    "tools/call" => HandleToolCall(tools, @params),
                    _ => throw new KeyNotFoundException($"Method not found: {method}"),
                };

                if (id != null)
                    WriteResponse(id, result);
            }
            catch (KeyNotFoundException ex)
            {
                if (id != null)
                    WriteError(id, -32601, ex.Message);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Seamstress.Mcp] Error: {ex}");
                if (id != null)
                    WriteError(id, -32603, ex.Message);
            }
        }

        return 0;
    }

    // ---------------------------------------------------------------- live link to the Seamstress window

    // "undecided" until the first tool call, then "live" (working in the window) or "headless" (a project of its own),
    // and "lost" when the window it was working in closed. Settled once, so an agent never ends up in another project
    // half way through without being told.
    private static string _mode = "undecided";
    private static LiveLinkClient? _link;

    private static void DecideMode()
    {
        if (_mode != "undecided")
            return;
        // Scripted builds that want a project of their own set SEAMSTRESS_LIVE=0.
        if (Environment.GetEnvironmentVariable("SEAMSTRESS_LIVE") == "0")
        {
            _mode = "headless";
            return;
        }
        _link = LiveLinkClient.TryConnect();
        _mode = _link != null ? "live" : "headless";
        Console.Error.WriteLine(_link != null
            ? $"[Seamstress.Mcp] Working in the open Seamstress window: {_link.Window?.ToJsonString()}"
            : "[Seamstress.Mcp] No Seamstress window is open: working headless.");
    }

    private static object LiveLinkTool(JsonElement args)
    {
        string action = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("action", out var a) ? a.GetString() ?? "status" : "status";
        switch (action)
        {
            case "connect":
                _link?.Dispose();
                _link = LiveLinkClient.TryConnect();
                if (_link == null)
                    throw new InvalidOperationException("No Seamstress window is open (or it does not accept the link). Ask the user to open Seamstress, or use action \"headless\".");
                _mode = "live";
                break;
            case "headless":
                _link?.Dispose();
                _link = null;
                _mode = "headless";
                break;
            case "status":
                DecideMode();
                break;
            default:
                throw new ArgumentException("action is status, connect or headless.");
        }
        return new
        {
            mode = _mode,
            window = _link?.Window,
            note = _mode switch
            {
                "live" => "Tools run in the user's open Seamstress window, on the project they have open: they see every change, and each call is one undo step there. Opening or starting a project is refused while the window has unsaved changes.",
                "lost" => "The window this session was working in has closed. Use connect once it is open again, or headless to carry on in a project of your own.",
                _ => "Tools run on a project of this server's own, not shown anywhere; save it and open it in Seamstress to see it.",
            },
        };
    }

    private static JsonArray WithLiveLinkTool(JsonArray definitions)
    {
        definitions.Add(new JsonObject
        {
            ["name"] = "live_link",
            ["description"] = "Whether the tools work in the user's open Seamstress window (live: they watch, each call is one undo step there) or on a headless project of this server's own. The choice is made at the first tool call: live when the window is open. action: status (default), connect (to the window, e.g. after it was opened), headless (a project of this server's own from now on).",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject { ["type"] = "string", ["description"] = "status, connect or headless." },
                },
            },
        });
        return definitions;
    }

    private static JsonNode HandleToolCall(McpToolRegistry tools, JsonElement @params)
    {
        string toolName = @params.GetProperty("name").GetString() ?? "";
        var args = @params.TryGetProperty("arguments", out var aEl) ? aEl : default;
        try
        {
            string json;
            if (toolName == "live_link")
            {
                json = JsonSerializer.Serialize(LiveLinkTool(args), JsonOpts);
            }
            else
            {
                DecideMode();
                if (_mode == "lost")
                    throw new InvalidOperationException("The Seamstress window this session was working in has closed. Call live_link with action \"connect\" once it is open again, or \"headless\" to carry on in a project of your own.");
                json = _link != null ? CallWindow(toolName, args) : JsonSerializer.Serialize(tools.Dispatch(toolName, args), JsonOpts);
            }
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = json },
                },
                ["isError"] = false,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Seamstress.Mcp] Tool '{toolName}' failed:\n{ex}");
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = $"Error in {toolName}: {ex.Message}" },
                },
                ["isError"] = true,
            };
        }
    }

    /// <summary>Runs a tool in the window; the project summary also says which window it is.</summary>
    private static string CallWindow(string toolName, JsonElement args)
    {
        try
        {
            string json = _link!.Call(toolName, args);
            if (toolName == "get_project_summary" && JsonNode.Parse(json) is JsonObject summary)
            {
                summary["liveLink"] = new JsonObject { ["mode"] = "live", ["window"] = _link.Window?.DeepClone() };
                json = summary.ToJsonString(JsonOpts);
            }
            return json;
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            _link?.Dispose();
            _link = null;
            _mode = "lost";
            throw new InvalidOperationException("The Seamstress window closed the link (it was probably closed). Nothing was done. Call live_link with action \"connect\" once it is open again, or \"headless\" to carry on in a project of your own.");
        }
    }

    private static void WriteResponse(JsonNode id, JsonNode result)
    {
        var resp = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result,
        };
        Console.WriteLine(resp.ToJsonString(JsonOpts));
        Console.Out.Flush();
    }

    private static void WriteError(JsonNode id, int code, string message)
    {
        var resp = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
        Console.WriteLine(resp.ToJsonString(JsonOpts));
        Console.Out.Flush();
    }
}
