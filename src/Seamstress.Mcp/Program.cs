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
                            + "garment worn over another a bodyOffsetMm instead of a large thickness.",
                    },
                    "ping" => new JsonObject(),
                    "tools/list" => new JsonObject
                    {
                        ["tools"] = tools.GetToolDefinitions(),
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

    private static JsonNode HandleToolCall(McpToolRegistry tools, JsonElement @params)
    {
        string toolName = @params.GetProperty("name").GetString() ?? "";
        var args = @params.TryGetProperty("arguments", out var aEl) ? aEl : default;
        try
        {
            object payload = tools.Dispatch(toolName, args);
            string json = JsonSerializer.Serialize(payload, JsonOpts);
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
