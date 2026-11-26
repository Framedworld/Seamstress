using System.Text.Json;

namespace Seamstress.Assistant;

/// <summary>Something the panel shows while a request runs.</summary>
public abstract record AssistantEvent;

/// <summary>The request went to Claude and the panel waits for its answer.</summary>
public sealed record AssistantWaiting : AssistantEvent;

/// <summary>Claude wrote something to the user.</summary>
public sealed record AssistantSaid(string Text) : AssistantEvent;

public sealed record AssistantToolStarted(string Tool, string Input) : AssistantEvent;

public sealed record AssistantToolFinished(string Tool, string Summary, bool IsError) : AssistantEvent;

/// <summary>How a request ended.</summary>
public enum AssistantEnding
{
    Done,
    Stopped,
    Refused,
    OutOfRoom,
    TooManySteps,
    Failed,
}

/// <summary>
/// A conversation with Claude about the open project: each request goes to the model with the agent tools; every
/// tool call it makes is run by the host and its result sent back, until the model answers without a tool call.
/// The history stays valid for the next request even when one is stopped half way: every tool call gets a result.
/// </summary>
public sealed class AssistantConversation
{
    /// <summary>The most tool calls the model gets back to back in one request before it is stopped.</summary>
    public const int MaxRounds = 60;

    /// <summary>Tool results longer than this are cut (a full project summary of a big outfit is huge).</summary>
    public const int MaxResultCharacters = 40_000;

    private readonly IAssistantToolHost _host;
    private readonly IReadOnlyList<AssistantToolDefinition> _tools;
    private readonly string _system;
    private readonly List<AssistantMessage> _messages = new();

    public AssistantConversation(IAssistantModel model, IAssistantToolHost host, IReadOnlyList<AssistantToolDefinition> tools, string system)
    {
        Model = model;
        _host = host;
        _tools = tools;
        _system = system;
    }

    public IReadOnlyList<AssistantMessage> Messages => _messages;

    /// <summary>Who answers; it can change between requests (a new key) and the conversation carries on.</summary>
    public IAssistantModel Model { get; set; }

    public int MaxTokens { get; set; } = 16_000;

    public event Action<AssistantEvent>? Progress;

    public void Clear() => _messages.Clear();

    /// <summary>
    /// Sends the user's message (text, pictures, the project context) and lets Claude work until it has answered.
    /// Stopping through <paramref name="cancel"/> ends it after the tool that is running.
    /// </summary>
    public async Task<AssistantEnding> SendAsync(IReadOnlyList<AssistantBlock> userContent, string model, CancellationToken cancel)
    {
        AddUser(userContent);
        try
        {
            for (int round = 0; round < MaxRounds; round++)
            {
                cancel.ThrowIfCancellationRequested();
                Progress?.Invoke(new AssistantWaiting());
                var reply = await Model.SendAsync(new AssistantRequest(model, _system, _tools, _messages.ToList(), MaxTokens), cancel)
                    .ConfigureAwait(false);
                if (reply.StopReason == "refusal")
                {
                    // The refused message is not kept, so the next one starts clean (tool results have to stay: they
                    // answer the calls before them).
                    if (_messages[^1].Role == AssistantRole.User && !_messages[^1].Content.OfType<ToolResultPart>().Any())
                        _messages.RemoveAt(_messages.Count - 1);
                    return AssistantEnding.Refused;
                }
                if (reply.Content.Count > 0)
                    _messages.Add(new AssistantMessage(AssistantRole.Assistant, reply.Content));
                foreach (var text in reply.Content.OfType<TextPart>())
                {
                    if (!string.IsNullOrWhiteSpace(text.Text))
                        Progress?.Invoke(new AssistantSaid(text.Text.Trim()));
                }

                var uses = reply.Content.OfType<ToolUsePart>().ToList();
                if (uses.Count == 0)
                {
                    if (reply.StopReason == "pause_turn")
                        continue;
                    return reply.StopReason == "max_tokens" ? AssistantEnding.OutOfRoom : AssistantEnding.Done;
                }

                var results = new List<AssistantBlock>();
                foreach (var use in uses)
                    results.Add(await RunToolAsync(use, cancel).ConfigureAwait(false));
                _messages.Add(new AssistantMessage(AssistantRole.User, results));
                if (cancel.IsCancellationRequested)
                    return AssistantEnding.Stopped;
            }
            return AssistantEnding.TooManySteps;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return AssistantEnding.Stopped;
        }
    }

    private async Task<ToolResultPart> RunToolAsync(ToolUsePart use, CancellationToken cancel)
    {
        if (cancel.IsCancellationRequested)
            return new ToolResultPart(use.Id, "Not run: the user stopped the request.", IsError: true);
        Progress?.Invoke(new AssistantToolStarted(use.Name, Compact(use.Input)));
        ToolOutcome outcome;
        try
        {
            outcome = await _host.RunAsync(use.Name, use.Input, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = new ToolOutcome("Stopped by the user while it ran.", IsError: true);
        }
        catch (Exception e)
        {
            outcome = new ToolOutcome(e.Message, IsError: true);
        }
        Progress?.Invoke(new AssistantToolFinished(use.Name, ToolSummary.Describe(outcome), outcome.IsError));
        string text = outcome.Json.Length > MaxResultCharacters
            ? outcome.Json[..MaxResultCharacters] + $"\n…(cut: the full result was {outcome.Json.Length} characters)"
            : outcome.Json;
        return new ToolResultPart(use.Id, text, outcome.IsError, outcome.Image);
    }

    /// <summary>A new user message; after a stopped request the last message may be the user's already, so it joins it.</summary>
    private void AddUser(IReadOnlyList<AssistantBlock> content)
    {
        if (_messages.Count > 0 && _messages[^1].Role == AssistantRole.User)
        {
            var joined = _messages[^1].Content.Concat(content).ToList();
            _messages[^1] = new AssistantMessage(AssistantRole.User, joined);
        }
        else
            _messages.Add(new AssistantMessage(AssistantRole.User, content));
    }

    private static string Compact(JsonElement input)
    {
        string text = input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText();
        return text.Length > 160 ? text[..157] + "…" : text;
    }
}

/// <summary>A line about a tool's result for the panel: the error, or a few telling values.</summary>
public static class ToolSummary
{
    private static readonly string[] Telling =
    {
        "name", "garment", "piece", "pieceId", "frames", "settled", "openSeams", "pieces", "seams", "penetratedVertices",
        "pngPath", "fitWarning", "advice", "movedPieces",
    };

    public static string Describe(ToolOutcome outcome)
    {
        if (outcome.IsError)
            return Shorten(outcome.Json, 200);
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(outcome.Json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Shorten(outcome.Json, 120);
        }
        if (root.ValueKind != JsonValueKind.Object)
            return Shorten(outcome.Json, 120);

        var parts = new List<string>();
        var props = root.EnumerateObject().ToList();
        foreach (var p in props.Where(p => Telling.Contains(p.Name)).Concat(props.Where(p => !Telling.Contains(p.Name))))
        {
            string? value = p.Value.ValueKind switch
            {
                JsonValueKind.String => Shorten(p.Value.GetString() ?? "", 40),
                JsonValueKind.Number => p.Value.GetRawText(),
                JsonValueKind.True => "yes",
                JsonValueKind.False => "no",
                JsonValueKind.Array => p.Value.GetArrayLength().ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
            if (value == null || p.Name is "ok" or "note" or "server")
                continue;
            parts.Add($"{p.Name}: {value}");
            if (parts.Count == 4)
                break;
        }
        return parts.Count == 0 ? "ok" : string.Join(", ", parts);
    }

    private static string Shorten(string text, int max)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length > max ? text[..(max - 1)] + "…" : text;
    }
}
