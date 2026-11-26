using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Seamstress.Mcp;

/// <summary>
/// The live link between an agent's MCP server and the Seamstress window. The window listens on a local port and
/// writes where (with a random key) to a file in the user's own folder; the MCP server finds it there and hands each
/// tool call to the window, which runs it on the project the user has open, so they watch the work happen and can
/// undo it. Requests and answers are single lines of JSON.
/// </summary>
public static class LiveLink
{
    /// <summary>Where a running window says how to reach it.</summary>
    public static string DefaultDiscoveryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Seamstress", "live-link.json");

    /// <summary>The handshake a client sends first: answered with what the window has open.</summary>
    public const string Hello = "__hello";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    internal sealed record Discovery(int Port, int Pid, string Token);

    internal static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>A tool call the window could not run; the message is for the agent.</summary>
public sealed class LiveLinkException : Exception
{
    public LiveLinkException(string message) : base(message) { }
}

/// <summary>The window's side: accepts tool calls on 127.0.0.1 and answers each with what the handler returns.</summary>
public sealed class LiveLinkServer : IDisposable
{
    private readonly Func<string, JsonElement, Task<object>> _handler;
    private readonly string _discoveryPath;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    /// <param name="handler">Runs one tool call (name, arguments) and returns the object to send back as JSON.</param>
    public LiveLinkServer(Func<string, JsonElement, Task<object>> handler, string? discoveryPath = null)
    {
        _handler = handler;
        _discoveryPath = discoveryPath ?? LiveLink.DefaultDiscoveryPath;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        Directory.CreateDirectory(Path.GetDirectoryName(_discoveryPath)!);
        var discovery = new LiveLink.Discovery(Port, Environment.ProcessId, _token);
        File.WriteAllText(_discoveryPath, JsonSerializer.Serialize(discovery, LiveLink.Json));
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        while (!_stop.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            if (line == null)
                return;
            JsonObject answer;
            try
            {
                using var request = JsonDocument.Parse(line);
                var root = request.RootElement;
                if (!root.TryGetProperty("token", out var token) || token.GetString() != _token)
                {
                    // A caller without the key from the user's own folder gets nothing, not even an answer.
                    return;
                }
                string tool = root.GetProperty("tool").GetString() ?? "";
                var args = root.TryGetProperty("arguments", out var a) ? a.Clone() : default;
                object result = await _handler(tool, args);
                answer = new JsonObject { ["ok"] = true, ["result"] = JsonSerializer.SerializeToNode(result, LiveLink.Json) };
            }
            catch (Exception e)
            {
                answer = new JsonObject { ["ok"] = false, ["error"] = e.GetBaseException().Message };
            }
            try
            {
                await writer.WriteLineAsync(answer.ToJsonString());
            }
            catch (IOException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            // Only our own file: another window may have started since and written its own.
            if (File.Exists(_discoveryPath) && File.ReadAllText(_discoveryPath).Contains(_token, StringComparison.Ordinal))
                File.Delete(_discoveryPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The MCP server's side: one connection to the window, one tool call at a time.</summary>
public sealed class LiveLinkClient : IDisposable
{
    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly string _token;

    private LiveLinkClient(TcpClient client, string token)
    {
        _client = client;
        _token = token;
        var stream = client.GetStream();
        _reader = new StreamReader(stream, new UTF8Encoding(false));
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>What the window said it has open when the link was made.</summary>
    public JsonNode? Window { get; private set; }

    /// <summary>Connects to the running window, or returns null when there is none.</summary>
    public static LiveLinkClient? TryConnect(string? discoveryPath = null)
    {
        string path = discoveryPath ?? LiveLink.DefaultDiscoveryPath;
        try
        {
            if (!File.Exists(path))
                return null;
            var discovery = JsonSerializer.Deserialize<LiveLink.Discovery>(File.ReadAllText(path), LiveLink.Json);
            if (discovery == null || !LiveLink.IsAlive(discovery.Pid))
                return null;
            var client = new TcpClient();
            if (!client.ConnectAsync(IPAddress.Loopback, discovery.Port).Wait(2000))
            {
                client.Dispose();
                return null;
            }
            var link = new LiveLinkClient(client, discovery.Token);
            link.Window = JsonNode.Parse(link.Call(LiveLink.Hello, default));
            return link;
        }
        catch (Exception e) when (e is IOException or SocketException or JsonException or AggregateException or LiveLinkException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Runs a tool in the window and returns its result as JSON text. Throws LiveLinkException when the tool failed there.</summary>
    public string Call(string tool, JsonElement arguments)
    {
        var request = new JsonObject
        {
            ["token"] = _token,
            ["tool"] = tool,
            ["arguments"] = arguments.ValueKind == JsonValueKind.Undefined ? new JsonObject() : JsonNode.Parse(arguments.GetRawText()),
        };
        _writer.WriteLine(request.ToJsonString());
        string line = _reader.ReadLine() ?? throw new IOException("The Seamstress window closed the link.");
        var answer = JsonNode.Parse(line)!;
        if (answer["ok"]?.GetValue<bool>() != true)
            throw new LiveLinkException(answer["error"]?.GetValue<string>() ?? "The window could not run the tool.");
        return answer["result"]?.ToJsonString() ?? "null";
    }

    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
        _client.Dispose();
    }
}
