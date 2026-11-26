using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Seamstress.Core.Editing;
using Seamstress.Core.Simulation;
using Seamstress.Mcp;

namespace Seamstress.Tests;

/// <summary>The live link that lets an agent's MCP server work in the open Seamstress window.</summary>
public class LiveLinkTests
{
    private static string TempDiscovery() => Path.Combine(Path.GetTempPath(), $"live-link-{Guid.NewGuid():N}.json");

    [Fact]
    public void AClientFindsTheWindowAndRunsToolsThere()
    {
        string discovery = TempDiscovery();
        var calls = new List<string>();
        using var server = new LiveLinkServer((tool, args) =>
        {
            calls.Add(tool);
            if (tool == LiveLink.Hello)
                return Task.FromResult<object>(new { window = "Test — Seamstress" });
            if (tool == "fails")
                throw new InvalidOperationException("Piece 'x' not found.");
            return Task.FromResult<object>(new { echoed = args.GetProperty("value").GetInt32() * 2 });
        }, discovery);
        server.Start();

        using var client = LiveLinkClient.TryConnect(discovery);
        Assert.NotNull(client);
        Assert.Equal("Test — Seamstress", client!.Window?["window"]?.GetValue<string>());

        using var args = JsonDocument.Parse("{\"value\": 21}");
        Assert.Equal(42, JsonDocument.Parse(client.Call("double", args.RootElement)).RootElement.GetProperty("echoed").GetInt32());

        var error = Assert.Throws<LiveLinkException>(() => client.Call("fails", default));
        Assert.Contains("not found", error.Message);
        Assert.Equal(new[] { LiveLink.Hello, "double", "fails" }, calls);
    }

    [Fact]
    public void ACallerWithoutTheKeyGetsNothing()
    {
        string discovery = TempDiscovery();
        bool ran = false;
        using var server = new LiveLinkServer((_, _) => { ran = true; return Task.FromResult<object>(new { }); }, discovery);
        server.Start();

        using var socket = new TcpClient();
        socket.Connect(IPAddress.Loopback, server.Port);
        using var stream = socket.GetStream();
        var request = Encoding.UTF8.GetBytes("{\"token\":\"guess\",\"tool\":\"new_project\",\"arguments\":{}}\n");
        stream.Write(request);
        stream.ReadTimeout = 3000;
        int read;
        try
        {
            read = stream.Read(new byte[64]);
        }
        catch (IOException)
        {
            read = 0;
        }
        Assert.Equal(0, read); // closed without an answer
        Assert.False(ran);
    }

    [Fact]
    public void NoWindowMeansNoLink()
    {
        Assert.Null(LiveLinkClient.TryConnect(TempDiscovery()));

        // A file left behind by a window that has gone (a process number that is not running).
        string stale = TempDiscovery();
        File.WriteAllText(stale, "{\"port\":1,\"pid\":2147483000,\"token\":\"x\"}");
        try
        {
            Assert.Null(LiveLinkClient.TryConnect(stale));
        }
        finally
        {
            File.Delete(stale);
        }
    }

    [Fact]
    public void ClosingTheWindowRemovesItsOwnFileOnly()
    {
        string discovery = TempDiscovery();
        var server = new LiveLinkServer((_, _) => Task.FromResult<object>(new { }), discovery);
        server.Start();
        Assert.True(File.Exists(discovery));
        server.Dispose();
        Assert.False(File.Exists(discovery));
    }

    [Fact]
    public void AGroupIsOneUndoStepAndCanBeTakenBack()
    {
        var ws = new Workspace();
        ws.History.BeginGroup("Agent: add things");
        ws.AddTemplate("Skirt");                 // records its own step outside a group
        ws.AddTemplate("T-Shirt");
        ws.History.EndGroup(keep: true);
        Assert.Equal(2, ws.Document.Garments.Count);
        Assert.Equal("Agent: add things", ws.History.UndoName);
        ws.History.Undo();
        Assert.Empty(ws.Document.Garments);

        ws.History.BeginGroup("Agent: fails half way");
        ws.AddTemplate("Skirt");
        ws.History.EndGroup(keep: false);
        Assert.Empty(ws.Document.Garments);
    }

    private sealed class FakeWindow : ISessionHost
    {
        public Workspace Workspace { get; } = new();
        public Avatar? Avatar { get; set; } = Avatar.CreateMannequin();
        public List<string> Opened { get; } = new();
        public void Open(string path) => Opened.Add(path);
        public void New() => Opened.Add("(new)");
    }

    [Fact]
    public void ToolsWorkOnTheWindowsProject()
    {
        var window = new FakeWindow();
        var session = new GarmentSession(window);
        var tools = new McpToolRegistry(session);

        using var args = JsonDocument.Parse("{\"template\":\"Skirt\"}");
        tools.Dispatch("add_template_garment", args.RootElement);
        Assert.Single(window.Workspace.Document.Garments);

        using var open = JsonDocument.Parse("{\"path\":\"elsewhere.garment.json\"}");
        tools.Dispatch("new_project", default);
        Assert.Equal("(new)", window.Opened.Last()); // the window decides, not the session
        session.OpenProject("elsewhere.garment.json");
        Assert.Equal("elsewhere.garment.json", window.Opened.Last());
    }
}
