using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Seamstress.Core;

namespace Seamstress.Assistant;

/// <summary>
/// Claude through the official Anthropic SDK (the Messages API). Uses the beta endpoint for server-side refusal
/// fallbacks: a request a safety classifier declines is answered by the model Anthropic recommends instead. The key is
/// held by the client only; it is never logged or written anywhere by this class.
/// </summary>
public sealed class AnthropicAssistantModel : IAssistantModel
{
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    private readonly AnthropicClient _client;

    public AnthropicAssistantModel(string apiKey)
    {
        _client = new AnthropicClient { ApiKey = apiKey };
    }

    public async Task<AssistantReply> SendAsync(AssistantRequest request, CancellationToken cancel)
    {
        var parameters = Parameters(request);

        BetaMessage response;
        try
        {
            response = await _client.Beta.Messages.Create(parameters, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AnthropicUnauthorizedException e)
        {
            throw new AssistantModelException(Lang.T("The Anthropic API refused the key. Check it in Preferences ▸ Assistant."), e);
        }
        catch (AnthropicRateLimitException e)
        {
            throw new AssistantModelException(Lang.T("The Anthropic API is busy for this key (rate limit). Wait a minute and try again."), e);
        }
        catch (AnthropicApiException e)
        {
            throw new AssistantModelException(Lang.F("The Anthropic API answered with an error: {0}", e.Message), e);
        }
        catch (HttpRequestException e)
        {
            throw new AssistantModelException(Lang.F("Could not reach the Anthropic API: {0}", e.Message), e);
        }
        return Reply(response);
    }

    /// <summary>The request as the SDK sends it (public so tests can check its shape without a key).</summary>
    public static MessageCreateParams Parameters(AssistantRequest request)
        => new()
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            Betas = [FallbackBeta],
            Fallbacks = new Default(),
            // Medium effort suits a run of tool calls; both offered models think adaptively by default.
            OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
            // Caches the tools, the system prompt and the conversation so far: each round re-sends all of them.
            CacheControl = new BetaCacheControlEphemeral(),
            System = request.System,
            Tools = request.Tools.Select(ToTool).ToList(),
            Messages = request.Messages.Select(ToMessage).ToList(),
        };

    /// <summary>The model's answer as the conversation keeps it.</summary>
    public static AssistantReply Reply(BetaMessage response)
    {
        var content = new List<AssistantBlock>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text))
                content.Add(new TextPart(text.Text));
            else if (block.TryPickThinking(out var thinking))
                content.Add(new ThinkingPart(thinking.Thinking, thinking.Signature));
            else if (block.TryPickRedactedThinking(out var redacted))
                content.Add(new RedactedThinkingPart(redacted.Data));
            else if (block.TryPickToolUse(out var use))
                content.Add(new ToolUsePart(use.ID, use.Name, JsonSerializer.SerializeToElement(use.Input)));
            else if (block.TryPickFallback(out var fallback))
                content.Add(new FallbackPart(JsonSerializer.SerializeToElement(fallback)));
        }
        return new AssistantReply(content, response.StopReason?.Raw() ?? "end_turn", response.Model.Raw());
    }

    private static BetaToolUnion ToTool(AssistantToolDefinition tool)
    {
        var properties = new Dictionary<string, JsonElement>();
        if (tool.InputSchema["properties"] is System.Text.Json.Nodes.JsonObject props)
        {
            foreach (var (name, value) in props)
                properties[name] = JsonSerializer.SerializeToElement(value);
        }
        var required = tool.InputSchema["required"] is System.Text.Json.Nodes.JsonArray req
            ? req.Select(r => r!.GetValue<string>()).ToList()
            : new List<string>();
        return new BetaTool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new InputSchema { Properties = properties, Required = required },
        };
    }

    private static BetaMessageParam ToMessage(AssistantMessage message)
    {
        var blocks = new List<BetaContentBlockParam>();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextPart text:
                    blocks.Add(new BetaTextBlockParam { Text = text.Text });
                    break;
                case ImagePart image:
                    blocks.Add(Image(image));
                    break;
                case ThinkingPart thinking:
                    blocks.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                    break;
                case RedactedThinkingPart redacted:
                    blocks.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                    break;
                case ToolUsePart use:
                    blocks.Add(new BetaToolUseBlockParam
                    {
                        ID = use.Id,
                        Name = use.Name,
                        Input = use.Input.ValueKind == JsonValueKind.Object
                            ? use.Input.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                            : new Dictionary<string, JsonElement>(),
                    });
                    break;
                case ToolResultPart result:
                    blocks.Add(ToolResult(result));
                    break;
                case FallbackPart fallback:
                    blocks.Add(JsonSerializer.Deserialize<BetaFallbackBlockParam>(fallback.Raw.GetRawText())!);
                    break;
            }
        }
        return message.Role == AssistantRole.User
            ? new BetaUserMessageParam { Content = blocks }
            : new BetaAssistantMessageParam { Content = blocks };
    }

    private static BetaImageBlockParam Image(ImagePart image) => new()
    {
        Source = new BetaBase64ImageSource
        {
            Data = image.Base64,
            MediaType = image.MediaType switch
            {
                "image/jpeg" => MediaType.ImageJpeg,
                "image/gif" => MediaType.ImageGif,
                "image/webp" => MediaType.ImageWebP,
                _ => MediaType.ImagePng,
            },
        },
    };

    private static BetaToolResultBlockParam ToolResult(ToolResultPart result)
    {
        if (result.Image == null)
            return new BetaToolResultBlockParam { ToolUseID = result.ToolUseId, Content = result.Content, IsError = result.IsError };
        var parts = new List<Block>
        {
            new BetaTextBlockParam { Text = result.Content },
            Image(result.Image),
        };
        return new BetaToolResultBlockParam { ToolUseID = result.ToolUseId, Content = parts, IsError = result.IsError };
    }
}
