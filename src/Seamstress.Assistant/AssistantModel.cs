using System.Text.Json;
using System.Text.Json.Nodes;

namespace Seamstress.Assistant;

// The conversation as the assistant keeps it, independent of any client library: what is sent to the model and what
// comes back. AnthropicAssistantModel turns it into Messages API requests; tests answer it with a script.

public enum AssistantRole
{
    User,
    Assistant,
}

public abstract record AssistantBlock;

public sealed record TextPart(string Text) : AssistantBlock;

/// <summary>A picture, base64 encoded: "image/png", "image/jpeg", "image/gif" or "image/webp".</summary>
public sealed record ImagePart(string MediaType, string Base64) : AssistantBlock
{
    public static ImagePart FromFile(string path)
    {
        string type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".png" => "image/png",
            _ => throw new ArgumentException("Pictures can be PNG, JPEG, GIF or WebP."),
        };
        return new ImagePart(type, Convert.ToBase64String(File.ReadAllBytes(path)));
    }
}

public sealed record ToolUsePart(string Id, string Name, JsonElement Input) : AssistantBlock;

/// <summary>What a tool call gave back; <paramref name="Image"/> lets the model see a rendered preview.</summary>
public sealed record ToolResultPart(string ToolUseId, string Content, bool IsError, ImagePart? Image = null) : AssistantBlock;

/// <summary>The model's reasoning, passed back unchanged on later requests (the signature is checked).</summary>
public sealed record ThinkingPart(string Thinking, string Signature) : AssistantBlock;

public sealed record RedactedThinkingPart(string Data) : AssistantBlock;

/// <summary>Where a request moved to another model after a refusal; kept where it appeared.</summary>
public sealed record FallbackPart(JsonElement Raw) : AssistantBlock;

public sealed record AssistantMessage(AssistantRole Role, IReadOnlyList<AssistantBlock> Content);

public sealed record AssistantToolDefinition(string Name, string Description, JsonObject InputSchema);

public sealed record AssistantRequest(
    string Model,
    string System,
    IReadOnlyList<AssistantToolDefinition> Tools,
    IReadOnlyList<AssistantMessage> Messages,
    int MaxTokens);

/// <summary>
/// One answer from the model. <paramref name="StopReason"/> is the API's: "end_turn", "tool_use", "max_tokens",
/// "pause_turn", "refusal" or "stop_sequence".
/// </summary>
public sealed record AssistantReply(IReadOnlyList<AssistantBlock> Content, string StopReason, string? ServedBy = null);

/// <summary>Sends a conversation to Claude. The app uses the Messages API; tests use a script.</summary>
public interface IAssistantModel
{
    Task<AssistantReply> SendAsync(AssistantRequest request, CancellationToken cancel);
}

/// <summary>A failure the user should read as it is (a refused key, no network, an overloaded service).</summary>
public sealed class AssistantModelException : Exception
{
    public AssistantModelException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>What running a tool gave back: JSON text, whether it failed, and optionally a picture.</summary>
public sealed record ToolOutcome(string Json, bool IsError, ImagePart? Image = null);

/// <summary>Runs the model's tool calls on the open project.</summary>
public interface IAssistantToolHost
{
    Task<ToolOutcome> RunAsync(string tool, JsonElement input, CancellationToken cancel);
}
