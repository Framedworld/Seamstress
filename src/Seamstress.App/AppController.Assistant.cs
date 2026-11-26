using System.Text.Json;
using Seamstress.Assistant;
using Seamstress.Core;

namespace Seamstress.App;

/// <summary>
/// The assistant panel: the user types what they want ("make the jacket baggier", "add a cargo pocket to the left
/// leg") and Claude does it on the open project with the agent tools, through the same path as the live link: every
/// tool call is one undo step "Agent: …", and a simulation drapes live. The conversation runs on a background task;
/// its tool calls and progress are handed to the window's thread by <see cref="Tick"/>.
/// </summary>
public partial class AppController
{
    private sealed class ChatItem
    {
        public string Kind = "";      // user, assistant, tool, note, error
        public string Text = "";
        public string Detail = "";
        public bool Running;
        public bool Failed;
        public List<string> Pictures = new();
    }

    private readonly List<ChatItem> _chat = new();
    private readonly List<string> _assistantAttachments = new();
    private AssistantConversation? _conversation;
    private CancellationTokenSource? _assistantCancel;
    private IAssistantModel? _scriptedModel;
    private AnthropicAssistantModel? _anthropic;
    private string _anthropicKey = "";
    private int _assistantRevision;
    private bool _assistantBusy;
    private bool _assistantVisible;
    private int _assistantPictures;

    private static string AssistantFolder => Path.Combine(Path.GetTempPath(), $"seamstress-assistant-{Environment.ProcessId}");

    /// <summary>Goes up whenever the chat, the attachments or the busy state change; the panel reads them again.</summary>
    public int AssistantRevision { get => _assistantRevision; private set { _assistantRevision = value; Notify(); } }

    public bool AssistantBusy { get => _assistantBusy; private set { _assistantBusy = value; Notify(); AssistantRevision++; } }

    /// <summary>Whether the panel is shown (View ▸ Assistant).</summary>
    public bool AssistantVisible
    {
        get => _assistantVisible;
        set
        {
            if (_assistantVisible == value)
                return;
            _assistantVisible = value;
            Notify();
        }
    }

    /// <summary>The key the assistant uses: the one in Preferences, else ANTHROPIC_API_KEY. Never logged.</summary>
    private string AssistantKey
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_settings.AssistantApiKey))
                return _settings.AssistantApiKey.Trim();
            return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")?.Trim() ?? "";
        }
    }

    /// <summary>True when the panel can talk to Claude (a key, or the smoke test's script).</summary>
    public bool AssistantReady => _scriptedModel != null || AssistantKey.Length > 0;

    /// <summary>True when the key comes from ANTHROPIC_API_KEY rather than Preferences.</summary>
    public bool AssistantKeyFromEnvironment
        => string.IsNullOrWhiteSpace(_settings.AssistantApiKey) && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

    /// <summary>True when a key is saved in Preferences (the field never shows it).</summary>
    public bool SettingAssistantKeySaved => !string.IsNullOrWhiteSpace(_settings.AssistantApiKey);

    public string SettingAssistantModel => AssistantPrompt.Models.Contains(_settings.AssistantModel) ? _settings.AssistantModel : AssistantPrompt.DefaultModel;

    /// <summary>The models offered in Preferences, as [{id, name}].</summary>
    public string AssistantModelsJson() => JsonSerializer.Serialize(new[]
    {
        new { id = AssistantPrompt.DefaultModel, name = Lang.T("Claude Sonnet 5.5 (faster, the default)") },
        new { id = AssistantPrompt.StrongModel, name = Lang.T("Claude Opus 5.5 (more capable, slower)") },
    });

    /// <summary>
    /// Saves the assistant's preferences. An empty key leaves the saved one as it is, unless <paramref name="forgetKey"/>
    /// removes it.
    /// </summary>
    public void SaveAssistantPreferences(string model, string apiKey, bool forgetKey)
    {
        _settings.AssistantModel = AssistantPrompt.Models.Contains(model) && model != AssistantPrompt.DefaultModel ? model : "";
        if (forgetKey)
            _settings.AssistantApiKey = "";
        else if (!string.IsNullOrWhiteSpace(apiKey))
            _settings.AssistantApiKey = apiKey.Trim();
        TrySaveSettings();
        Notify(nameof(SettingAssistantModel));
        Notify(nameof(SettingAssistantKeySaved));
        Notify(nameof(AssistantReady));
        Notify(nameof(AssistantKeyFromEnvironment));
        AssistantRevision++;
    }

    /// <summary>The chat so far, as [{kind, text, detail, running, failed, pictures}].</summary>
    public string AssistantItemsJson()
        => JsonSerializer.Serialize(_chat.Select(c => new
        {
            kind = c.Kind,
            text = c.Text,
            detail = c.Detail,
            running = c.Running,
            failed = c.Failed,
            pictures = c.Pictures.Select(ViewportSync.ToUrl).ToList(),
        }));

    /// <summary>Pictures waiting to go with the next message, as file URLs.</summary>
    public string AssistantAttachmentsJson() => JsonSerializer.Serialize(_assistantAttachments.Select(ViewportSync.ToUrl));

    /// <summary>A fresh file in the assistant's own folder, for the 3D view or a resized picture to be saved into.</summary>
    public string AssistantPicturePath(string name)
    {
        Directory.CreateDirectory(AssistantFolder);
        return Path.Combine(AssistantFolder, $"{name}-{Interlocked.Increment(ref _assistantPictures)}.png");
    }

    /// <summary>Adds a photo or sketch to the next message (the panel has already made it a modest PNG).</summary>
    public bool AssistantAttach(string urlOrPath)
    {
        string path = LocalPath(urlOrPath);
        if (!File.Exists(path))
            return false;
        try
        {
            _ = ImagePart.FromFile(path);
        }
        catch (ArgumentException)
        {
            StatusText = Lang.T("Pictures can be PNG, JPEG, GIF or WebP.");
            return false;
        }
        if (_assistantAttachments.Count >= 6)
            _assistantAttachments.RemoveAt(0);
        _assistantAttachments.Add(path);
        AssistantRevision++;
        return true;
    }

    public void AssistantRemoveAttachment(int index)
    {
        if (index >= 0 && index < _assistantAttachments.Count)
            _assistantAttachments.RemoveAt(index);
        AssistantRevision++;
    }

    /// <summary>Forgets the conversation (the project and its undo steps stay as they are).</summary>
    public void AssistantNewChat()
    {
        if (AssistantBusy)
            return;
        _conversation?.Clear();
        _chat.Clear();
        _assistantAttachments.Clear();
        AssistantRevision++;
    }

    /// <summary>
    /// Sends a message. <paramref name="viewPicture"/> is a picture of the 3D view the panel saved just before, or
    /// empty. Does nothing without a key: the panel explains how to add one.
    /// </summary>
    public void AssistantSend(string text, string viewPicture)
    {
        text = text.Trim();
        if (AssistantBusy || (text.Length == 0 && _assistantAttachments.Count == 0) || !AssistantReady)
            return;

        var content = new List<AssistantBlock> { new TextPart(AssistantPrompt.Context(_workspace, _avatar?.Name)) };
        var item = new ChatItem { Kind = "user", Text = text };
        foreach (string picture in _assistantAttachments)
        {
            try
            {
                content.Add(ImagePart.FromFile(picture));
                item.Pictures.Add(picture);
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
            {
                AppLog.Write($"Assistant: left out a picture that could not be read: {e.Message}");
            }
        }
        _assistantAttachments.Clear();
        string view = viewPicture.Length > 0 ? LocalPath(viewPicture) : "";
        if (view.Length > 0 && File.Exists(view))
        {
            content.Add(new TextPart("The user's 3D view as they see it now:"));
            content.Add(ImagePart.FromFile(view));
        }
        if (text.Length == 0)
            text = "Make a garment like this picture.";
        content.Add(new TextPart(text));
        _chat.Add(item);

        // One client per key: a key changed in Preferences takes effect with the next message.
        string key = AssistantKey;
        if (_scriptedModel == null && (_anthropic == null || _anthropicKey != key))
        {
            _anthropic = new AnthropicAssistantModel(key);
            _anthropicKey = key;
        }
        IAssistantModel model = _scriptedModel ?? _anthropic!;
        _conversation ??= NewConversation(model);
        _conversation.Model = model;

        var cancel = new CancellationTokenSource();
        _assistantCancel = cancel;
        AssistantBusy = true;
        string modelId = SettingAssistantModel;
        var conversation = _conversation;
        AppLog.Write($"Assistant: request sent to {modelId} ({content.Count(c => c is ImagePart)} picture(s))");
        _ = Task.Run(async () =>
        {
            AssistantEnding ending;
            string? problem = null;
            try
            {
                ending = await conversation.SendAsync(content, modelId, cancel.Token).ConfigureAwait(false);
            }
            catch (AssistantModelException e)
            {
                ending = AssistantEnding.Failed;
                problem = e.Message;
            }
            catch (Exception e)
            {
                ending = AssistantEnding.Failed;
                problem = e.Message;
                AppLog.Write($"Assistant: the request failed: {e.GetType().Name}: {e.Message}");
            }
            _agentWork.Enqueue(() => AssistantFinished(ending, problem, cancel));
        });
    }

    /// <summary>Stops the request: the tool that is running finishes (a simulation stops now), nothing more is sent.</summary>
    public void AssistantStop()
    {
        if (_assistantCancel is not { } cancel || cancel.IsCancellationRequested)
            return;
        cancel.Cancel();
        if (_simulation.IsRunning)
            StopSimulation();
        StatusText = Lang.T("Stopping the assistant…");
    }

    /// <summary>For the smoke test: answer from a script instead of Claude, so the panel can be driven without a key.</summary>
    public void AssistantUseScript(string script)
    {
        _scriptedModel = script switch
        {
            "skirt-pocket" => SkirtWithPocketScript(),
            _ => null,
        };
        Notify(nameof(AssistantReady));
    }

    // ---------------------------------------------------------------- the conversation

    private AssistantConversation NewConversation(IAssistantModel model)
    {
        var host = new AssistantToolHost(RunForAssistant, AssistantFolder);
        var conversation = new AssistantConversation(model, host, AssistantPrompt.Tools(AgentTools.Registry), AssistantPrompt.System);
        conversation.Progress += e => _agentWork.Enqueue(() => AssistantProgress(e));
        return conversation;
    }

    /// <summary>A tool call from the assistant, on the window's thread, exactly as the live link runs one.</summary>
    private Task<object> RunForAssistant(string tool, JsonElement input, CancellationToken cancel)
        => tool == "simulate" ? LiveSimulate(input) : OnWindowThread(() => RunAgentTool(tool, input));

    private void AssistantProgress(AssistantEvent e)
    {
        switch (e)
        {
            case AssistantWaiting:
                StatusText = Lang.T("The assistant is thinking…");
                break;
            case AssistantSaid said:
                _chat.Add(new ChatItem { Kind = "assistant", Text = said.Text });
                break;
            case AssistantToolStarted started:
                _chat.Add(new ChatItem { Kind = "tool", Text = started.Tool.Replace('_', ' '), Detail = started.Input, Running = true });
                break;
            case AssistantToolFinished finished:
                var item = _chat.LastOrDefault(c => c.Kind == "tool" && c.Running);
                if (item != null)
                {
                    item.Running = false;
                    item.Failed = finished.IsError;
                    item.Detail = finished.Summary;
                }
                break;
        }
        AssistantRevision++;
    }

    private void AssistantFinished(AssistantEnding ending, string? problem, CancellationTokenSource cancel)
    {
        if (_assistantCancel == cancel)
            _assistantCancel = null;
        cancel.Dispose();
        foreach (var running in _chat.Where(c => c.Running))
            running.Running = false;
        string? note = ending switch
        {
            AssistantEnding.Stopped => Lang.T("Stopped. Everything done so far can be undone step by step."),
            AssistantEnding.Refused => Lang.T("Claude declined this request. Try asking in a different way."),
            AssistantEnding.OutOfRoom => Lang.T("The answer ran out of room and was cut off. Ask it to carry on."),
            AssistantEnding.TooManySteps => Lang.T("The assistant stopped after many steps in a row. Ask it to carry on if it was not finished."),
            _ => null,
        };
        if (ending == AssistantEnding.Failed)
            AssistantFailed(problem ?? Lang.T("The request failed."));
        else if (note != null)
            _chat.Add(new ChatItem { Kind = "note", Text = note });
        StatusText = ending == AssistantEnding.Done ? Lang.T("The assistant has finished.") : StatusText;
        AppLog.Write($"Assistant: request ended ({ending})");
        AssistantBusy = false;
    }

    private void AssistantFailed(string message)
    {
        _chat.Add(new ChatItem { Kind = "error", Text = message });
        AssistantRevision++;
    }

    /// <summary>
    /// The smoke test's stand-in for Claude: a skirt from the templates, then a pocket on its front piece (read from
    /// the first tool's result), then a closing word.
    /// </summary>
    private static ScriptedAssistantModel SkirtWithPocketScript() => new(
        _ => ScriptedAssistantModel.Call("t1", "add_template_garment", new { template = "Skirt" }, "Adding a skirt."),
        request =>
        {
            using var result = JsonDocument.Parse(ScriptedAssistantModel.LastToolResult(request));
            string piece = FirstPieceId(result.RootElement);
            return ScriptedAssistantModel.Call("t2", "add_patch",
                new { hostPieceId = piece, kind = "pocket", x = 5, y = 20, widthCm = 14, heightCm = 15, simulate = false });
        },
        _ => ScriptedAssistantModel.Say("Added a skirt with a pocket."));

    private static string FirstPieceId(JsonElement result)
    {
        foreach (var garment in result.TryGetProperty("garments", out var g) && g.ValueKind == JsonValueKind.Array
                     ? g.EnumerateArray() : Enumerable.Empty<JsonElement>())
        {
            if (garment.TryGetProperty("pieces", out var pieces) && pieces.GetArrayLength() > 0)
                return pieces[0].GetProperty("id").GetString()!;
        }
        if (result.TryGetProperty("pieces", out var direct) && direct.GetArrayLength() > 0)
            return direct[0].GetProperty("id").GetString()!;
        throw new InvalidOperationException("The template's result named no pieces.");
    }
}
