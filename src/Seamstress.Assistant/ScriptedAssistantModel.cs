namespace Seamstress.Assistant;

/// <summary>
/// A stand-in for Claude that answers from a script: each step looks at the conversation so far (for example the
/// last tool result) and gives the next reply. Used by the tests and by the app's smoke test, which have no key.
/// </summary>
public sealed class ScriptedAssistantModel : IAssistantModel
{
    private readonly Queue<Func<AssistantRequest, AssistantReply>> _steps;

    public ScriptedAssistantModel(params Func<AssistantRequest, AssistantReply>[] steps)
    {
        _steps = new Queue<Func<AssistantRequest, AssistantReply>>(steps);
    }

    /// <summary>Every request the conversation sent, in order.</summary>
    public List<AssistantRequest> Requests { get; } = new();

    public Task<AssistantReply> SendAsync(AssistantRequest request, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        Requests.Add(request);
        if (_steps.Count == 0)
            return Task.FromResult(new AssistantReply(new[] { new TextPart("(the script has ended)") }, "end_turn"));
        return Task.FromResult(_steps.Dequeue()(request));
    }

    /// <summary>The text of the last tool result in a request: what the script reads ids from.</summary>
    public static string LastToolResult(AssistantRequest request)
        => request.Messages.LastOrDefault(m => m.Role == AssistantRole.User)?.Content.OfType<ToolResultPart>().LastOrDefault()?.Content ?? "";

    public static AssistantReply Call(string id, string tool, object input, string? say = null)
    {
        var blocks = new List<AssistantBlock>();
        if (say != null)
            blocks.Add(new TextPart(say));
        blocks.Add(new ToolUsePart(id, tool, System.Text.Json.JsonSerializer.SerializeToElement(input)));
        return new AssistantReply(blocks, "tool_use");
    }

    public static AssistantReply Say(string text) => new(new[] { new TextPart(text) }, "end_turn");
}
