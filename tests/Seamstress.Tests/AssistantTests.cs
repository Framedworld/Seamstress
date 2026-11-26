using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Beta.Messages;
using Seamstress.Assistant;
using Seamstress.Core.Editing;
using Seamstress.Core.IO;
using Seamstress.Core.Simulation;
using Seamstress.Mcp;

namespace Seamstress.Tests;

/// <summary>The in-app assistant: its conversation with Claude (scripted here, no key needed) and the tools it runs.</summary>
public class AssistantTests
{
    private sealed class Window : ISessionHost
    {
        public Workspace Workspace { get; } = new();
        public Avatar? Avatar { get; set; } = Avatar.CreateMannequin();
        public void Open(string path) => throw new InvalidOperationException("not in these tests");
        public void New() => throw new InvalidOperationException("not in these tests");
    }

    private static string Folder() => Path.Combine(Path.GetTempPath(), $"assistant-test-{Guid.NewGuid():N}");

    /// <summary>The window's project, the tools as the panel runs them (one undo step per call) and a conversation.</summary>
    private static (Window Window, AssistantConversation Conversation, List<AssistantEvent> Events) Setup(IAssistantModel model,
        AssistantToolHost.Runner? runner = null, string? folder = null)
    {
        var window = new Window();
        var tools = new UndoableTools(window.Workspace, new McpToolRegistry(new GarmentSession(window)));
        runner ??= (tool, input, _) => Task.FromResult(tools.Run(tool, input));
        var host = new AssistantToolHost(runner, folder ?? Folder());
        var conversation = new AssistantConversation(model, host, AssistantPrompt.Tools(tools.Registry), AssistantPrompt.System);
        var events = new List<AssistantEvent>();
        conversation.Progress += events.Add;
        return (window, conversation, events);
    }

    private static List<AssistantBlock> Message(Workspace ws, string text)
        => new() { new TextPart(AssistantPrompt.Context(ws, "Mannequin")), new TextPart(text) };

    /// <summary>The front left piece of the trousers in a template tool's result.</summary>
    private static string FrontLeft(string result)
    {
        var json = JsonNode.Parse(result)!;
        return json["garments"]![0]!["pieces"]!.AsArray()
            .First(p => p!["name"]!.GetValue<string>().StartsWith("Front L", StringComparison.Ordinal))!["id"]!.GetValue<string>();
    }

    [Fact]
    public async Task AScriptedExchangeAddsTrousersThenAPocketAsTwoUndoSteps()
    {
        string? pieceSeen = null;
        var model = new ScriptedAssistantModel(
            _ => ScriptedAssistantModel.Call("call_1", "add_template_garment", new { template = "Fitted Trousers" }, "Cutting the trousers."),
            request =>
            {
                pieceSeen = FrontLeft(ScriptedAssistantModel.LastToolResult(request));
                return ScriptedAssistantModel.Call("call_2", "add_patch",
                    new { hostPieceId = pieceSeen, kind = "pocket", name = "Cargo pocket", x = 4, y = 40, widthCm = 14, heightCm = 16, simulate = false });
            },
            _ => ScriptedAssistantModel.Say("Done: trousers with a cargo pocket on the left leg."));
        var (window, conversation, events) = Setup(model);
        var ws = window.Workspace;

        var ending = await conversation.SendAsync(Message(ws, "Trousers with a cargo pocket on the left leg"), AssistantPrompt.DefaultModel, CancellationToken.None);

        Assert.Equal(AssistantEnding.Done, ending);
        Assert.NotNull(pieceSeen);
        Assert.Contains(ws.Document.Garments, g => g.Pieces.Any(p => p.Name == "Cargo pocket"));

        // Two tool calls, two undo steps, named after the tools; undoing both leaves the project as it was.
        Assert.Equal(UndoableTools.UndoName("add_patch"), ws.History.UndoName);
        ws.History.Undo();
        Assert.DoesNotContain(ws.Document.Garments, g => g.Pieces.Any(p => p.Name == "Cargo pocket"));
        Assert.NotEmpty(ws.Document.Garments);
        Assert.Equal(UndoableTools.UndoName("add_template_garment"), ws.History.UndoName);
        ws.History.Undo();
        Assert.Empty(ws.Document.Garments);
        Assert.False(ws.History.CanUndo);

        // What went to the model: the system prompt, the tools without the file ones, the project context, and each
        // tool's result answering its call.
        Assert.Equal(3, model.Requests.Count);
        var first = model.Requests[0];
        Assert.Equal(AssistantPrompt.DefaultModel, first.Model);
        Assert.Contains("Templates first", first.System);
        Assert.Contains(first.Tools, t => t.Name == "add_template_garment");
        Assert.DoesNotContain(first.Tools, t => AssistantPrompt.LeftOut.Contains(t.Name));
        Assert.Contains("<project>", ((TextPart)first.Messages[0].Content[0]).Text);
        var last = model.Requests[2].Messages;
        Assert.Equal(5, last.Count); // user, call, result, call, result
        Assert.Equal(new[] { AssistantRole.User, AssistantRole.Assistant, AssistantRole.User, AssistantRole.Assistant, AssistantRole.User },
            last.Select(m => m.Role));
        var result = Assert.IsType<ToolResultPart>(Assert.Single(last[4].Content));
        Assert.Equal("call_2", result.ToolUseId);
        Assert.False(result.IsError);

        // The panel was told what ran.
        Assert.Equal(new[] { "add_template_garment", "add_patch" }, events.OfType<AssistantToolStarted>().Select(e => e.Tool));
        Assert.All(events.OfType<AssistantToolFinished>(), e => Assert.False(e.IsError));
        Assert.Contains(events.OfType<AssistantSaid>(), e => e.Text.StartsWith("Done", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedToolIsReportedAndLeavesNothingBehind()
    {
        var model = new ScriptedAssistantModel(
            _ => ScriptedAssistantModel.Call("a", "add_patch", new { hostPieceId = "no-such-piece", x = 0, y = 0, widthCm = 5, heightCm = 5 }),
            _ => ScriptedAssistantModel.Say("That piece does not exist."));
        var (window, conversation, events) = Setup(model);

        Assert.Equal(AssistantEnding.Done, await conversation.SendAsync(Message(window.Workspace, "pocket"), AssistantPrompt.DefaultModel, default));

        var result = (ToolResultPart)model.Requests[1].Messages[^1].Content[0];
        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content);
        Assert.False(window.Workspace.History.CanUndo);
        Assert.True(events.OfType<AssistantToolFinished>().Single().IsError);
    }

    [Fact]
    public async Task FileToolsAreNotOfferedAndRefusedIfAskedFor()
    {
        var model = new ScriptedAssistantModel(
            _ => ScriptedAssistantModel.Call("a", "save_project", new { path = "C:/somewhere/else.garment.json" }),
            _ => ScriptedAssistantModel.Say("ok"));
        bool ran = false;
        var (window, conversation, _) = Setup(model, (_, _, _) => { ran = true; return Task.FromResult<object>(new { }); });

        await conversation.SendAsync(Message(window.Workspace, "save it"), AssistantPrompt.DefaultModel, default);

        Assert.False(ran);
        Assert.True(((ToolResultPart)model.Requests[1].Messages[^1].Content[0]).IsError);
        Assert.DoesNotContain(model.Requests[0].Tools, t => t.Name is "save_project" or "open_project" or "export_garment");
        var preview = model.Requests[0].Tools.Single(t => t.Name == "render_preview");
        Assert.False(preview.InputSchema["properties"]!.AsObject().ContainsKey("pngPath"));
    }

    [Fact]
    public async Task StoppingLeavesAConversationThatCanCarryOn()
    {
        using var cancel = new CancellationTokenSource();
        var model = new ScriptedAssistantModel(
            _ =>
            {
                cancel.Cancel(); // the user presses Stop while Claude answers with two tool calls
                return new AssistantReply(new AssistantBlock[]
                {
                    new ToolUsePart("x", "add_template_garment", JsonSerializer.SerializeToElement(new { template = "Skirt" })),
                    new ToolUsePart("y", "add_template_garment", JsonSerializer.SerializeToElement(new { template = "Dress" })),
                }, "tool_use");
            },
            _ => ScriptedAssistantModel.Say("Carrying on."));
        var (window, conversation, _) = Setup(model);

        Assert.Equal(AssistantEnding.Stopped, await conversation.SendAsync(Message(window.Workspace, "skirt and dress"), AssistantPrompt.DefaultModel, cancel.Token));
        Assert.Empty(window.Workspace.Document.Garments);
        var answers = conversation.Messages[^1].Content.OfType<ToolResultPart>().ToList();
        Assert.Equal(new[] { "x", "y" }, answers.Select(a => a.ToolUseId)); // every call has its answer
        Assert.All(answers, a => Assert.True(a.IsError));

        // The next message joins the tool results in one user turn, so the roles still alternate.
        Assert.Equal(AssistantEnding.Done, await conversation.SendAsync(new[] { new TextPart("go on") }, AssistantPrompt.DefaultModel, default));
        var roles = model.Requests[^1].Messages.Select(m => m.Role).ToList();
        Assert.Equal(new[] { AssistantRole.User, AssistantRole.Assistant, AssistantRole.User }, roles);
    }

    [Fact]
    public async Task APreviewIsWrittenInTheAssistantsFolderAndShownToClaude()
    {
        string folder = Folder();
        string? asked = null;
        var model = new ScriptedAssistantModel(
            _ => ScriptedAssistantModel.Call("p", "render_preview", new { views = "front", pngPath = "C:/Windows/evil.png" }),
            _ => ScriptedAssistantModel.Say("Looks good."));
        var (window, conversation, _) = Setup(model, (tool, input, _) =>
        {
            asked = input.GetProperty("pngPath").GetString();
            PngWriter.WriteRgba(asked!, 1, 1, new byte[] { 255, 0, 0, 255 });
            return Task.FromResult<object>(new { ok = true, pngPath = asked });
        }, folder);
        try
        {
            await conversation.SendAsync(Message(window.Workspace, "show me"), AssistantPrompt.DefaultModel, default);
            Assert.StartsWith(folder, asked);
            var result = (ToolResultPart)model.Requests[1].Messages[^1].Content[0];
            Assert.NotNull(result.Image);
            Assert.Equal("image/png", result.Image!.MediaType);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ARefusedMessageIsNotKept()
    {
        var model = new ScriptedAssistantModel(
            _ => new AssistantReply(Array.Empty<AssistantBlock>(), "refusal"),
            _ => ScriptedAssistantModel.Say("Hello."));
        var (window, conversation, _) = Setup(model);
        Assert.Equal(AssistantEnding.Refused, await conversation.SendAsync(new[] { new TextPart("something odd") }, AssistantPrompt.DefaultModel, default));
        Assert.Empty(conversation.Messages);
        await conversation.SendAsync(new[] { new TextPart("hi") }, AssistantPrompt.DefaultModel, default);
        Assert.Equal("hi", ((TextPart)model.Requests[1].Messages.Single().Content.Single()).Text);
    }

    [Fact]
    public void TheContextNamesGarmentsAndTheSelection()
    {
        var ws = new Workspace();
        var garment = ws.AddTemplate("Skirt");
        ws.SetActiveGarment(garment.Id);
        string context = AssistantPrompt.Context(ws, "Gus");
        Assert.Contains("avatar: Gus", context);
        Assert.Contains(garment.Id, context);
        Assert.Contains("selected garment", context);
    }

    // ---------------------------------------------------------------- the Messages API request, without a key

    private static AssistantRequest SampleRequest(McpToolRegistry registry)
    {
        var image = new ImagePart("image/png", Convert.ToBase64String(new byte[] { 1, 2, 3 }));
        return new AssistantRequest(AssistantPrompt.StrongModel, AssistantPrompt.System, AssistantPrompt.Tools(registry), new[]
        {
            new AssistantMessage(AssistantRole.User, new AssistantBlock[] { new TextPart("<project>…</project>"), image, new TextPart("like this") }),
            new AssistantMessage(AssistantRole.Assistant, new AssistantBlock[]
            {
                new ThinkingPart("", "sig-123"),
                new ToolUsePart("toolu_1", "render_preview", JsonSerializer.SerializeToElement(new { views = "front" })),
            }),
            new AssistantMessage(AssistantRole.User, new AssistantBlock[] { new ToolResultPart("toolu_1", "{\"ok\":true}", false, image) }),
        }, 16_000);
    }

    [Fact]
    public void TheRequestHasTheShapeTheMessagesApiExpects()
    {
        var registry = new McpToolRegistry(new GarmentSession(new Window()));
        var parameters = AnthropicAssistantModel.Parameters(SampleRequest(registry));
        var body = JsonNode.Parse(JsonSerializer.Serialize(parameters.RawBodyData))!;

        Assert.Equal("claude-opus-5-5", body["model"]!.GetValue<string>());
        Assert.Equal("default", body["fallbacks"]!.GetValue<string>());
        Assert.Equal("medium", body["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("ephemeral", body["cache_control"]!["type"]!.GetValue<string>());
        var tools = body["tools"]!.AsArray();
        Assert.Equal(AssistantPrompt.Tools(registry).Count, tools.Count);
        var template = tools.First(t => t!["name"]!.GetValue<string>() == "add_template_garment")!;
        Assert.Equal("object", template["input_schema"]!["type"]!.GetValue<string>());
        Assert.Contains("template", template["input_schema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));

        var messages = body["messages"]!.AsArray();
        Assert.Equal(new[] { "user", "assistant", "user" }, messages.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("image", messages[0]!["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("base64", messages[0]!["content"]![1]!["source"]!["type"]!.GetValue<string>());
        Assert.Equal("sig-123", messages[1]!["content"]![0]!["signature"]!.GetValue<string>());
        Assert.Equal("toolu_1", messages[1]!["content"]![1]!["id"]!.GetValue<string>());
        var result = messages[2]!["content"]![0]!;
        Assert.Equal("tool_result", result["type"]!.GetValue<string>());
        Assert.Equal("image", result["content"]![1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void AnAnswerFromTheApiBecomesTheConversationsBlocks()
    {
        const string json = """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5-5","stop_reason":"tool_use","stop_sequence":null,
             "usage":{"input_tokens":10,"output_tokens":5},
             "content":[
               {"type":"thinking","thinking":"","signature":"abc"},
               {"type":"text","text":"Adding a pocket."},
               {"type":"tool_use","id":"toolu_9","name":"add_patch","input":{"hostPieceId":"p1","kind":"pocket"}}
             ]}
            """;
        var message = JsonSerializer.Deserialize<BetaMessage>(json)!;
        var reply = AnthropicAssistantModel.Reply(message);

        Assert.Equal("tool_use", reply.StopReason);
        Assert.Equal("abc", Assert.IsType<ThinkingPart>(reply.Content[0]).Signature);
        Assert.Equal("Adding a pocket.", Assert.IsType<TextPart>(reply.Content[1]).Text);
        var use = Assert.IsType<ToolUsePart>(reply.Content[2]);
        Assert.Equal("add_patch", use.Name);
        Assert.Equal("p1", use.Input.GetProperty("hostPieceId").GetString());
    }
}
