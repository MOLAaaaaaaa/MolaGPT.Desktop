using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Chat.Tools.PythonExecution;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Models;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.ViewModels;

/// <summary>
/// Bottom composer view model. Owns the in-flight CancellationTokenSource so
/// the Stop button can abort a streaming generation. Send is enabled only when
/// (a) there's text, (b) we're not already sending, (c) a provider+model is
/// active.
///
/// Tracks composer toolbar state for reasoning, network tools, webpage
/// reading, and attachments. Visibility is derived from the selected model's
/// advertised capabilities.
/// </summary>
public sealed partial class ComposerViewModel : ObservableObject
{
    private const string SystemHintDelimiter = "✝";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComposerPlaceholderVisible))]
    private string _text = string.Empty;
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private bool _enterToSend = true;

    /// <summary>True when the user has tapped the lightbulb button on a
    /// reasoning-capable model. Becomes <c>use_thinking</c> in the request body.</summary>
    [ObservableProperty] private bool _enableThinking;

    /// <summary>"low" / "medium" / "high". Becomes <c>reasoning_effort</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasoningEffortLabel))]
    private string _reasoningEffort = "medium";

    /// <summary>Budget tokens for Anthropic/Gemini/Qwen thinking modes.</summary>
    [ObservableProperty] private int _thinkingBudgetTokens = 10000;

    /// <summary>The thinking parameter kind of the currently active model.</summary>
    [ObservableProperty] private MolaGPT.Core.Models.ThinkingParamKind _activeThinkingKind = MolaGPT.Core.Models.ThinkingParamKind.None;

    /// <summary>True when the user has tapped the globe button. Becomes
    /// <c>enabled_tools.network</c>.</summary>
    [ObservableProperty] private bool _enableNetwork;

    /// <summary>True when web_fetch / webpage reading is enabled. BYOK 使用
    /// 工具名 <c>web_fetch</c>；wire 上仍以 <c>enabled_tools.steelBrowser</c>
    /// 与代理后端通信（向前兼容）。</summary>
    [ObservableProperty] private bool _enableWebFetch;

    /// <summary>True when the current BYOK turn may expose the local Python
    /// execution tool to the model.</summary>
    [ObservableProperty] private bool _enablePythonTool;

    /// <summary>Image generation mode. MolaGPT account mode uses the proxy
    /// image flow; BYOK image work is handled by the separate workbench.</summary>
    [ObservableProperty] private bool _isImageGenerationMode;

    [ObservableProperty] private string _imageAspectRatio = "1:1";
    [ObservableProperty] private string _imageStyle = string.Empty;

    public ObservableCollection<Attachment> Attachments { get; } = new();

    public Func<string, CancellationToken, Task<string?>>? ConversationCompletedAsync { get; set; }

    private readonly ChatViewModel _chat;
    private readonly BackgroundStreamService? _backgroundStreams;
    private readonly SettingsViewModel? _settings;
    private readonly PersonaListViewModel? _personas;
    private readonly MolaGPT.Storage.AttachmentStore? _attachmentStore;
    private readonly Dictionary<MessageViewModel, List<PythonArtifactMarkdownRewriter.ArtifactContext>> _pythonArtifactContexts = new();
    private CancellationTokenSource? _cts;
    private Task? _activeStreamTask;
    private MessageViewModel? _activeAssistantMsg;
    private BackgroundStreamTask? _activeTask;

    /// <summary>Exposed to XAML so the composer can bind directly to chat state
    /// (active persona, conversation prompt, model labels) without going through
    /// the Main view model. ComposerView.DataContext is this VM.</summary>
    public ChatViewModel Chat => _chat;

    /// <summary>Exposed to XAML so the PersonaPicker popup can render the full
    /// list. Null when no persona registry is wired (e.g. design-time data).</summary>
    public PersonaListViewModel? Personas => _personas;

    /// <summary>True iff persona / system-prompt controls should be visible;
    /// BYOK provider active. MolaGptProxy mode hides them entirely so the
    /// client doesn't override server-side prompts (chator.php has its own).</summary>
    public bool IsPersonaPickerVisible =>
        _chat.ActiveProvider is not null && _chat.ActiveProvider.Kind != ProviderKind.MolaGptProxy;

    public ComposerViewModel(ChatViewModel chat, BackgroundStreamService? backgroundStreams = null, SettingsViewModel? settings = null)
        : this(chat, backgroundStreams, settings, null, null) { }

    public ComposerViewModel(
        ChatViewModel chat,
        BackgroundStreamService? backgroundStreams,
        SettingsViewModel? settings,
        PersonaListViewModel? personas)
        : this(chat, backgroundStreams, settings, personas, null) { }

    public ComposerViewModel(
        ChatViewModel chat,
        BackgroundStreamService? backgroundStreams,
        SettingsViewModel? settings,
        PersonaListViewModel? personas,
        MolaGPT.Storage.AttachmentStore? attachmentStore)
    {
        _chat = chat;
        _backgroundStreams = backgroundStreams;
        _settings = settings;
        _personas = personas;
        _attachmentStore = attachmentStore;
        _chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChatViewModel.ActiveProvider) or nameof(ChatViewModel.ActiveModel))
            {
                SendCommand.NotifyCanExecuteChanged();
                RetryCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(IsThinkingVisible));
                OnPropertyChanged(nameof(IsReasoningEffortVisible));
                OnPropertyChanged(nameof(IsAttachVisible));
                OnPropertyChanged(nameof(CanAcceptImageAttachments));
                OnPropertyChanged(nameof(CanAcceptFileAttachments));
                OnPropertyChanged(nameof(AreNetworkToolsEnabled));
                OnPropertyChanged(nameof(IsPythonToolVisible));
                OnPropertyChanged(nameof(IsPersonaPickerVisible));
                OnPropertyChanged(nameof(IsImageGenerationAvailable));
                OnPropertyChanged(nameof(IsImageOptionsVisible));

                if (!IsThinkingVisible && EnableThinking) EnableThinking = false;
                if (!AreNetworkToolsEnabled)
                {
                    EnableNetwork = false;
                    EnableWebFetch = false;
                }
                if (!IsPythonToolVisible && EnablePythonTool)
                    EnablePythonTool = false;
                if (!IsImageGenerationAvailable && IsImageGenerationMode)
                    IsImageGenerationMode = false;

                ActiveThinkingKind = _chat.ActiveModel?.ThinkingConfig?.Kind
                    ?? MolaGPT.Core.Models.ThinkingParamKind.None;

                // Normalize ReasoningEffort BEFORE notifying AvailableEffortLevels so
                // the ComboBox doesn't reverse-write null when the previous value
                // (e.g. "medium") isn't in the new model's level set (e.g. DeepSeek
                // exposes only ["high","max"]). Order: model default → keep current
                // if still valid → fall back to first available level.
                var newLevels = AvailableEffortLevels;
                var modelDefault = _chat.ActiveModel?.ThinkingConfig?.DefaultEffort;
                if (!string.IsNullOrEmpty(modelDefault) && newLevels.Contains(modelDefault))
                    ReasoningEffort = modelDefault!;
                else if (!newLevels.Contains(ReasoningEffort))
                    ReasoningEffort = newLevels.FirstOrDefault() ?? "medium";

                // Always refresh budget/effort bounds — two models may share the
                // same ThinkingParamKind but differ in budget range or default effort.
                OnPropertyChanged(nameof(BudgetMin));
                OnPropertyChanged(nameof(BudgetMax));
                OnPropertyChanged(nameof(AvailableEffortLevels));

                if (_chat.ActiveModel?.ThinkingConfig?.DefaultBudget is { } defBudget)
                    ThinkingBudgetTokens = defBudget;
            }
        };
        if (_settings is not null)
        {
            _settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(SettingsViewModel.ImageGenerationEnabled)
                    or nameof(SettingsViewModel.ImageGenerationProviderId)
                    or nameof(SettingsViewModel.ImageGenerationModelId)
                    or nameof(SettingsViewModel.ImageGenerationBaseUrl)
                    or nameof(SettingsViewModel.ImageGenerationApiKey)
                    or nameof(SettingsViewModel.ImageGenerationModel)
                    or nameof(SettingsViewModel.IsImageGenerationConfigured))
                {
                    OnPropertyChanged(nameof(IsImageGenerationAvailable));
                    OnPropertyChanged(nameof(IsImageOptionsVisible));
                    SendCommand.NotifyCanExecuteChanged();
                    if (!IsImageGenerationAvailable && IsImageGenerationMode)
                        IsImageGenerationMode = false;
                }
                if (e.PropertyName is nameof(SettingsViewModel.PythonToolEnabled)
                    or nameof(SettingsViewModel.PythonToolExecutablePath)
                    or nameof(SettingsViewModel.PythonToolTimeoutSeconds)
                    or nameof(SettingsViewModel.PythonToolMaxOutputCharacters)
                    or nameof(SettingsViewModel.PythonToolAllowNetwork)
                    or nameof(SettingsViewModel.PythonToolPermissionMode)
                    or nameof(SettingsViewModel.PythonToolAllowedImports)
                    or nameof(SettingsViewModel.PythonToolDeniedImports)
                    or nameof(SettingsViewModel.PythonToolAllowedPathPrefixes)
                    or nameof(SettingsViewModel.PythonToolDeniedPathPrefixes))
                {
                    OnPropertyChanged(nameof(IsPythonToolVisible));
                    if (!IsPythonToolVisible && EnablePythonTool)
                        EnablePythonTool = false;
                }
            };
        }
        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            SendCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Show "推理" toggle iff the active model explicitly reports
    /// SupportsThinking. MolaGPT account models get this from
    /// model_config_public.php; BYOK models get it from user settings.</summary>
    public bool IsThinkingVisible => _chat.ActiveModel?.SupportsThinking == true;

    /// <summary>Show "推理强度" iff the active model supports effort control
    /// and the user has enabled thinking.</summary>
    public bool IsReasoningEffortVisible => EnableThinking && _chat.ActiveModel?.SupportsReasoningEffort == true;

    /// <summary>Attach button is always shown; text/document attachments are
    /// validated at send time, while images also require vision support.</summary>
    public bool IsAttachVisible => true;
    public bool CanAcceptImageAttachments =>
        _chat.ActiveProvider?.Kind == ProviderKind.MolaGptProxy
        || _chat.ActiveModel?.SupportsVision == true
        || _settings?.IsVisionProxyAvailableFor(_chat.ActiveProvider?.Kind, _chat.ActiveModel) == true;
    /// <summary>非图片附件目前仅 MolaGPT 代理模式支持（走沙箱上传）。BYOK
    /// 直连官方端点时，PDF/DOCX 等二进制文件无法解析，文本类文件也只是
    /// inline 截断 —— 在 UI 入口直接拦截，避免用户误以为模型读到了文件内容。</summary>
    public bool CanAcceptFileAttachments =>
        _chat.ActiveProvider?.Kind == ProviderKind.MolaGptProxy;
    public bool AreNetworkToolsEnabled =>
        _chat.ActiveProvider?.Kind == ProviderKind.MolaGptProxy || _chat.ActiveModel?.SupportsToolCalling == true;
    public bool IsPythonToolVisible => CanUseByokPythonTool;
    // The in-composer image button / aspect-ratio / style options exist only for
    // MolaGPT-account mode. BYOK chats can still call the configured image
    // generation service as a model tool when enabled in settings.
    public bool IsImageGenerationAvailable =>
        _chat.ActiveProvider?.Kind == ProviderKind.MolaGptProxy;
    public bool IsImageOptionsVisible =>
        IsImageGenerationAvailable
        && IsImageGenerationMode;
    public string ComposerPlaceholder => IsImageGenerationMode
        ? "描述你想要的画面；如有参考图，可在左侧上传..."
        : "输入消息...";
    public bool IsComposerPlaceholderVisible => string.IsNullOrEmpty(Text);

    public bool HasAttachments => Attachments.Count > 0;

    private bool CanUseByokImageGenerationTool =>
        _chat.ActiveProvider?.Kind != ProviderKind.MolaGptProxy
        && _chat.ActiveModel?.SupportsToolCalling == true
        && _settings?.IsImageGenerationConfigured == true;

    private bool CanUseByokPythonTool =>
        _chat.ActiveProvider?.Kind != ProviderKind.MolaGptProxy
        && _chat.ActiveModel?.SupportsToolCalling == true
        && _settings?.PythonToolEnabled == true;

    public IReadOnlyList<ImageGenerationOption> ImageAspectRatioOptions { get; } =
    [
        new("1:1", "1:1"),
        new("16:9", "16:9"),
        new("9:16", "9:16"),
        new("4:3", "4:3"),
        new("3:4", "3:4"),
        new("21:9", "21:9")
    ];

    public IReadOnlyList<ImageGenerationOption> ImageStyleOptions { get; } =
    [
        new("默认", ""),
        new("写实", "photorealistic"),
        new("动漫", "anime"),
        new("油画", "oil painting"),
        new("水彩", "watercolor"),
        new("3D", "3D render"),
        new("像素", "pixel art"),
        new("极简", "minimalist")
    ];

    /// <summary>Display label for the current effort, "低 / 中 / 高".</summary>
    public string ReasoningEffortLabel => ReasoningEffort switch
    {
        "none" => "无",
        "minimal" => "极低",
        "low" => "低",
        "medium" => "中",
        "high" => "高",
        "xhigh" => "极高",
        "max" => "最大",
        // Empty/null: blank so the button doesn't lie about an unset value.
        null or "" => string.Empty,
        // Unknown value: surface it verbatim instead of pretending it's "中".
        var other => other
    };

    public IReadOnlyList<string> AvailableEffortLevels => ActiveThinkingKind switch
    {
        MolaGPT.Core.Models.ThinkingParamKind.OpenAiReasoningEffort =>
            new[] { "none", "minimal", "low", "medium", "high", "xhigh" },
        MolaGPT.Core.Models.ThinkingParamKind.AnthropicAdaptive =>
            new[] { "low", "medium", "high", "xhigh", "max" },
        MolaGPT.Core.Models.ThinkingParamKind.DeepSeekV4 =>
            new[] { "high", "max" },
        MolaGPT.Core.Models.ThinkingParamKind.GeminiThinkingLevel =>
            new[] { "minimal", "low", "medium", "high" },
        _ => new[] { "low", "medium", "high" }
    };

    public bool IsEffortComboVisible => ActiveThinkingKind is not (
        MolaGPT.Core.Models.ThinkingParamKind.AnthropicBudget or
        MolaGPT.Core.Models.ThinkingParamKind.GeminiBudget or
        MolaGPT.Core.Models.ThinkingParamKind.QwenThinkingBudget);

    public bool IsBudgetSliderVisible => ActiveThinkingKind is
        MolaGPT.Core.Models.ThinkingParamKind.AnthropicBudget or
        MolaGPT.Core.Models.ThinkingParamKind.GeminiBudget or
        MolaGPT.Core.Models.ThinkingParamKind.QwenThinkingBudget;

    public int BudgetMin => _chat.ActiveModel?.ThinkingConfig?.MinBudget ?? 0;
    public int BudgetMax => _chat.ActiveModel?.ThinkingConfig?.MaxBudget ?? 32768;

    /// <summary>
    /// Hint chip click handler — fills the composer with the canned prompt and
    /// (optionally) auto-sends so the user sees streaming start.
    /// </summary>
    [RelayCommand]
    public void ApplyHint(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return;
        Text = hint;
    }

    /// <summary>Cycle the reasoning effort low → medium → high → low.</summary>
    [RelayCommand]
    public void CycleReasoningEffort()
    {
        ReasoningEffort = ReasoningEffort switch
        {
            "low" => "medium",
            "medium" => "high",
            _ => "low"
        };
    }

    partial void OnActiveThinkingKindChanged(MolaGPT.Core.Models.ThinkingParamKind value)
    {
        OnPropertyChanged(nameof(AvailableEffortLevels));
        OnPropertyChanged(nameof(IsEffortComboVisible));
        OnPropertyChanged(nameof(IsBudgetSliderVisible));
        OnPropertyChanged(nameof(BudgetMin));
        OnPropertyChanged(nameof(BudgetMax));
    }

    [RelayCommand]
    public void ToggleImageGenerationMode()
    {
        if (!IsImageGenerationAvailable)
            return;

        IsImageGenerationMode = !IsImageGenerationMode;
    }

    [RelayCommand]
    public void RemoveAttachment(Attachment? a)
    {
        if (a is null) return;
        Attachments.Remove(a);
    }

    [RelayCommand]
    public void ClearAttachments() => Attachments.Clear();

    [RelayCommand(CanExecute = nameof(CanSend))]
    public async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(Text) && Attachments.Count == 0) return;
        if (_chat.ActiveProvider is null || _chat.ActiveModel is null) return;
        if (HasUnsupportedImages(Attachments, _chat.ActiveProvider, _chat.ActiveModel))
            return;
        var isMolaGptImageGenerationSend =
            _chat.ActiveProvider.Kind == ProviderKind.MolaGptProxy && IsImageGenerationMode;
        if (isMolaGptImageGenerationSend && string.IsNullOrWhiteSpace(Text))
            return;

        if (string.IsNullOrEmpty(_chat.ConversationId))
            _chat.ConversationId = CreateWebCompatibleConversationId();

        var userText = Text;
        var queuedAttachments = Attachments.ToList();
        Text = string.Empty;
        _chat.AppendUserMessage(userText, BuildAttachmentChips(queuedAttachments));
        var userMsg = _chat.Messages.LastOrDefault(m => m.Role == ChatMessage.RoleUser);
        var assistantMsg = _chat.BeginAssistantMessage();
        IsSending = true;
        _chat.IsStreaming = true;
        Attachments.Clear();

        var cts = new CancellationTokenSource();
        _cts = cts;
        _activeAssistantMsg = assistantMsg;

        var provider = _chat.ActiveProvider;
        var model = _chat.ActiveModel;
        var conversationId = _chat.ConversationId!;
        var conversationTitle = _chat.ConversationTitle;
        var outgoingUserText = userText;
        var outgoingAttachments = queuedAttachments;

        if (queuedAttachments.Count > 0
            && provider is MolaGptProxyProvider proxyForUploads)
        {
            try
            {
                assistantMsg.SetPendingStatus("上传附件", "同步到会话沙箱");
                var prepared = await proxyForUploads.PrepareAttachmentsAsync(
                    queuedAttachments,
                    conversationId,
                    model.SupportsVision || isMolaGptImageGenerationSend,
                    cts.Token);
                outgoingAttachments = prepared.Attachments.ToList();
                if (isMolaGptImageGenerationSend)
                {
                    outgoingUserText = BuildImageGenerationPrompt(userText, outgoingAttachments, prepared.SystemHint);
                }
                else if (!string.IsNullOrWhiteSpace(prepared.SystemHint))
                {
                    outgoingUserText = AppendHiddenSystemHint(userText, prepared.SystemHint!);
                }

                if (userMsg is not null)
                {
                    userMsg.Content = outgoingUserText;
                    userMsg.Attachments = BuildAttachmentChips(outgoingAttachments);
                    userMsg.ContentPartsJson = model.SupportsVision
                        ? BuildOpenAiContentPartsJson(outgoingUserText, outgoingAttachments)
                        : null;
                    _chat.UpdatePersistedMessage(userMsg);
                }
            }
            catch (Exception ex)
            {
                assistantMsg.AppendDelta($"\n\n> ❌ **附件上传失败**: {ex.Message}");
                assistantMsg.FlushPendingDelta();
                assistantMsg.IsStreaming = false;
                assistantMsg.StopThinking();
                _chat.FinalizeAssistantMessage(conversationId, assistantMsg);
                IsSending = false;
                _chat.IsStreaming = false;
                _activeStreamTask = null;
                _activeAssistantMsg = null;
                _activeTask = null;
                _cts = null;
                cts.Dispose();
                return;
            }
        }
        else if (isMolaGptImageGenerationSend)
        {
            outgoingUserText = BuildImageGenerationPrompt(userText, outgoingAttachments, null);
            if (userMsg is not null)
            {
                userMsg.Content = outgoingUserText;
                _chat.UpdatePersistedMessage(userMsg);
            }
        }
        else if (userMsg is not null && outgoingAttachments.Any(a => a.Kind == AttachmentKind.Image && !string.IsNullOrWhiteSpace(a.RemoteUrl)))
        {
            userMsg.ContentPartsJson = BuildOpenAiContentPartsJson(outgoingUserText, outgoingAttachments);
            _chat.UpdatePersistedMessage(userMsg);
        }
        var requestAttachments = BuildRequestAttachments(provider, model, outgoingAttachments);

        // BYOK history images are re-fed from the local store so multi-turn
        // follow-ups can still see earlier pictures. MolaGPT-account mode keeps
        // images in ContentPartsJson (durable RemoteUrl), so we don't backfill
        // raw bytes there.
        var backfillHistory = provider.Kind != ProviderKind.MolaGptProxy;

        var msgs = _chat.Messages
            .Where(m => !m.IsStreaming || m == assistantMsg)
            .Where(m => m != assistantMsg)
            .Select(m => new ChatMessage(
                m.Role,
                ReferenceEquals(m, userMsg) ? outgoingUserText : BuildContentForHistory(m),
                Attachments: ReferenceEquals(m, userMsg)
                    ? (requestAttachments.Count > 0 ? requestAttachments : null)
                    : (backfillHistory && m.Role == ChatMessage.RoleUser ? BuildHistoryAttachments(m) : null),
                ReasoningContent: m.Role == ChatMessage.RoleAssistant ? m.Thinking : null))
            .ToList();

        var systemPrompt = ResolveSystemPrompt();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            msgs.Insert(0, new ChatMessage("system", systemPrompt));

        var extras = BuildExtras();
        var thinkingKind = ResolveActiveThinkingParamKind();

        var req = new ChatRequest(
            ModelId: model.Id,
            Messages: msgs,
            ConversationId: conversationId,
            SessionId: Guid.NewGuid().ToString("N"),
            UseThinking: EnableThinking,
            ReasoningEffort: IsReasoningEffortVisible ? ReasoningEffort : null,
            ExtraBody: extras,
            ThinkingBudgetTokens: EnableThinking ? ThinkingBudgetTokens : null,
            ThinkingParamKind: thinkingKind);

        var streamContext = new BackgroundStreamTask
        {
            ConversationId = conversationId,
            ConversationTitle = conversationTitle,
            ModelLabel = assistantMsg.ModelLabel,
            ProviderId = provider.Id,
            ProviderKind = provider.Kind,
            AssistantMessage = assistantMsg,
            Cts = cts,
            StreamTask = Task.CompletedTask,
            SessionId = req.SessionId
        };
        _activeTask = streamContext;

        var streamTask = RunStreamLoopAsync(provider, req, assistantMsg, cts, streamContext);
        streamContext.StreamTask = streamTask;
        _activeStreamTask = streamTask;
        var wasCancelled = false;

        try
        {
            await streamContext.StreamTask;
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }
        catch (MolaGptAuthExpiredException ex)
        {
            assistantMsg.AppendDelta($"\n\n> ⚠️ {ex.Message}");
            try
            {
                if (provider.Id == "molagpt-proxy" && _chat.ActiveProvider?.Id == provider.Id)
                {
                    _chat.ActiveProvider = null;
                    _chat.ActiveModel = null;
                    _chat.TryAutoPickActive();
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            assistantMsg.AppendDelta($"\n\n> ❌ **错误**: {ex.Message}");
        }
        finally
        {
            CompleteStreamContext(streamContext, publishNotification: !wasCancelled);
            if (ReferenceEquals(_activeTask, streamContext))
            {
                IsSending = false;
                _chat.IsStreaming = false;
                _activeStreamTask = null;
                _activeAssistantMsg = null;
                _activeTask = null;
                _cts = null;
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Detach the current active stream to background so the user can switch
    /// conversations without interrupting generation.
    /// </summary>
    public bool DetachToBackground()
    {
        if (_backgroundStreams is null || _activeTask is null)
            return false;
        if (string.IsNullOrEmpty(_activeTask.ConversationId))
            return false;

        _activeTask.IsDetached = true;
        _chat.DetachTransientMessage(_activeTask.AssistantMessage);

        if (_activeTask.ProviderKind == ProviderKind.MolaGptProxy
            && _chat.ActiveProvider is MolaGptProxyProvider proxyProvider)
            _activeTask.ApiUrl = proxyProvider.LastResolvedApiUrl;

        _backgroundStreams.Register(_activeTask);

        _cts = null;
        _activeStreamTask = null;
        _activeAssistantMsg = null;
        _activeTask = null;
        IsSending = false;
        _chat.IsStreaming = false;

        return true;
    }

    /// <summary>
    /// Re-attach a background stream that was previously detached. Called when
    /// the user switches back to a conversation with an active background task.
    /// </summary>
    public async Task ReattachFromBackgroundAsync(string conversationId)
    {
        if (_backgroundStreams is null) return;
        var task = _backgroundStreams.GetTask(conversationId);
        if (task is null) return;

        _backgroundStreams.StopPolling(task);
        _backgroundStreams.Detach(conversationId);
        task.IsDetached = false;

        if (task.IsCompleted)
        {
            _chat.AttachTransientMessage(task.AssistantMessage);
            task.AssistantMessage.FinishStreaming();
            CompleteStreamContext(task, publishNotification: false);
            return;
        }

        _chat.AttachTransientMessage(task.AssistantMessage);

        if (!task.StreamTask.IsCompleted && !task.Cts.IsCancellationRequested)
        {
            _activeAssistantMsg = task.AssistantMessage;
            _cts = task.Cts;
            _activeStreamTask = task.StreamTask;
            _activeTask = task;
            IsSending = true;
            _chat.IsStreaming = true;
            return;
        }

        if (task.ProviderKind == ProviderKind.MolaGptProxy
            && _chat.ActiveProvider is MolaGptProxyProvider proxyProvider
            && !string.IsNullOrEmpty(task.SessionId))
        {
            var status = await proxyProvider.CheckStreamStatusAsync(task.SessionId!, CancellationToken.None);

            if (status is null || status.Status == "completed")
            {
                var data = await proxyProvider.FetchCompletedStreamAsync(task.SessionId!, CancellationToken.None);
                if (data is not null)
                {
                    task.AssistantMessage.ReplaceContent(data.Text);
                    if (data.Sources is { Count: > 0 })
                        task.AssistantMessage.Sources = data.Sources;
                }
                task.AssistantMessage.FinishStreaming();
                CompleteStreamContext(task, publishNotification: false);
                return;
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _activeAssistantMsg = task.AssistantMessage;
            _activeTask = task;
            task.Cts = cts;
            IsSending = true;
            _chat.IsStreaming = true;

            var resumeTask = RunResumeStreamLoopAsync(
                proxyProvider, task.SessionId!, task.ReceivedChunkCount,
                task.ApiUrl ?? "api/auth/chatAuto.php",
                task.AssistantMessage, cts, task);
            _activeStreamTask = resumeTask;
            task.StreamTask = resumeTask;

            try
            {
                await resumeTask;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                task.AssistantMessage.AppendDelta($"\n\n> ❌ **恢复错误**: {ex.Message}");
            }
            finally
            {
                CompleteStreamContext(task, publishNotification: true);
                if (ReferenceEquals(_activeTask, task))
                {
                    IsSending = false;
                    _chat.IsStreaming = false;
                    _activeStreamTask = null;
                    _activeAssistantMsg = null;
                    _activeTask = null;
                    _cts = null;
                }
                cts.Dispose();
            }
            return;
        }

        _activeAssistantMsg = task.AssistantMessage;
        _cts = task.Cts;
        _activeStreamTask = task.StreamTask;
        _activeTask = task;
        IsSending = true;
        _chat.IsStreaming = true;
    }

    private async Task RunStreamLoopAsync(
        IChatProvider provider,
        ChatRequest req,
        MessageViewModel assistantMsg,
        CancellationTokenSource cts,
        BackgroundStreamTask? trackingTask = null)
    {
        await foreach (var chunk in provider.StreamChatAsync(req, cts.Token).WithCancellation(cts.Token))
        {
            ApplyStreamChunk(assistantMsg, chunk);
            if (trackingTask is not null && chunk.RawJson is not null)
                trackingTask.ReceivedChunkCount++;
            if (chunk.FinishReason is not null) break;
        }
    }

    private async Task RunResumeStreamLoopAsync(
        MolaGptProxyProvider provider,
        string sessionId,
        int offset,
        string apiUrl,
        MessageViewModel assistantMsg,
        CancellationTokenSource cts,
        BackgroundStreamTask? trackingTask = null)
    {
        await foreach (var chunk in provider.ResumeStreamAsync(sessionId, offset, apiUrl, cts.Token).WithCancellation(cts.Token))
        {
            ApplyStreamChunk(assistantMsg, chunk);
            if (trackingTask is not null && chunk.RawJson is not null)
                trackingTask.ReceivedChunkCount++;
            if (chunk.FinishReason is not null) break;
        }
    }

    private Dictionary<string, object> BuildExtras()
    {
        var enabledTools = new Dictionary<string, object?>
        {
            ["network"] = EnableNetwork,
            ["steelBrowser"] = EnableWebFetch,
            ["code"] = true,
            ["deepResearch"] = false
        };

        if (_chat.ActiveProvider?.Kind != ProviderKind.MolaGptProxy)
        {
            enabledTools["searchProvider"] = _settings?.WebSearchProvider;
            enabledTools["searchApiKey"] = _settings?.WebSearchApiKey;
            enabledTools["searchBaseUrl"] = _settings?.WebSearchBaseUrl;
            enabledTools["searchMaxResults"] = _settings?.WebSearchMaxResults ?? 6;
            enabledTools["webPageMaxCharacters"] = _settings?.WebPageMaxCharacters ?? 12000;
            enabledTools["mcpServers"] = _settings?.BuildMcpServerOptions() ?? Array.Empty<MolaGPT.Core.Chat.LocalTools.McpServerOptions>();
            enabledTools["vision"] = _settings?.BuildVisionProxyOptions();
            if (CanUseByokImageGenerationTool)
                enabledTools["image_generation"] = _settings!.BuildImageGenerationOptions();
            if (CanUseByokPythonTool && EnablePythonTool)
                enabledTools["python"] = _settings!.BuildPythonExecutionOptions() with { Enabled = true };
        }

        var extras = new Dictionary<string, object>
        {
            ["enabled_tools"] = enabledTools
        };

        if (_settings is not null && !_settings.TracksEnabled)
            extras["privacy_mode"] = true;

        return extras;
    }

    private static object BuildContentForHistory(MessageViewModel message)
    {
        if (!string.IsNullOrWhiteSpace(message.ContentPartsJson))
        {
            try
            {
                if (JsonNode.Parse(message.ContentPartsJson!) is JsonNode parts)
                    return parts;
            }
            catch (JsonException) { }
        }

        return message.Content;
    }

    /// <summary>
    /// Rebuild the wire <see cref="Attachment"/> list for a history user message
    /// so multi-turn follow-ups still carry earlier images. BYOK image bytes are
    /// re-read from the local <see cref="MolaGPT.Storage.AttachmentStore"/> by
    /// <see cref="AttachmentChip.LocalName"/>; in-memory <see cref="AttachmentChip.Bytes"/>
    /// (the just-sent turn) is preferred to skip a disk round-trip. Returns null
    /// when the message has no rehydratable image (e.g. MolaGPT-account images,
    /// which travel via ContentPartsJson instead).
    /// </summary>
    private IReadOnlyList<Attachment>? BuildHistoryAttachments(MessageViewModel message)
    {
        if (message.Attachments is null || message.Attachments.Count == 0) return null;
        var rebuilt = new List<Attachment>();
        foreach (var chip in message.Attachments)
        {
            if (!chip.IsImage) continue;
            var bytes = chip.Bytes;
            if (bytes is not { Length: > 0 } && _attachmentStore is not null)
                bytes = _attachmentStore.Load(chip.LocalName);
            if (bytes is not { Length: > 0 }) continue;
            rebuilt.Add(new Attachment(
                AttachmentKind.Image,
                string.IsNullOrWhiteSpace(chip.MimeType) ? "image/png" : chip.MimeType!,
                bytes,
                FileName: chip.FileName));
        }
        return rebuilt.Count == 0 ? null : rebuilt;
    }

    private IReadOnlyList<AttachmentChip>? BuildAttachmentChips(IReadOnlyList<Attachment> attachments)
    {
        if (attachments.Count == 0) return null;
        return attachments
            .Select(attachment =>
            {
                var isImage = attachment.Kind == AttachmentKind.Image && attachment.Bytes is { Length: > 0 };
                // BYOK images (no server RemoteUrl) are content-addressed into
                // the local AttachmentStore so they survive app restart and can
                // be re-fed to the vision proxy on later turns. MolaGPT-account
                // images already have a durable RemoteUrl/ThumbnailUrl.
                string? localName = null;
                if (isImage && string.IsNullOrWhiteSpace(attachment.RemoteUrl) && _attachmentStore is not null)
                    localName = _attachmentStore.Save(attachment.Bytes, attachment.MimeType, attachment.FileName);

                return new AttachmentChip(
                    string.IsNullOrWhiteSpace(attachment.FileName) ? "附件" : attachment.FileName!,
                    attachment.Kind == AttachmentKind.Image ? "图片" : LabelForFile(attachment),
                    string.IsNullOrWhiteSpace(attachment.RemoteUrl) ? null : attachment.RemoteUrl)
                {
                    // Keep image bytes in memory so the user can re-open the
                    // preview right after sending (no disk round-trip). On reload
                    // the preview falls back to LocalName → AttachmentStore, or
                    // ThumbnailUrl for MolaGPT-account images.
                    Bytes = isImage ? attachment.Bytes : null,
                    LocalName = localName,
                    MimeType = isImage ? attachment.MimeType : null
                };
            })
            .ToList();
    }

    private static IReadOnlyList<Attachment> BuildRequestAttachments(
        IChatProvider provider,
        ProviderModel model,
        IReadOnlyList<Attachment> attachments)
    {
        if (attachments.Count == 0) return Array.Empty<Attachment>();
        if (provider.Kind != ProviderKind.MolaGptProxy)
            return attachments;

        if (!model.SupportsVision)
            return Array.Empty<Attachment>();

        return attachments
            .Where(attachment => attachment.Kind == AttachmentKind.Image
                                 && !string.IsNullOrWhiteSpace(attachment.RemoteUrl))
            .ToList();
    }

    private static string? BuildOpenAiContentPartsJson(string text, IReadOnlyList<Attachment> attachments)
    {
        var images = attachments
            .Where(a => a.Kind == AttachmentKind.Image && !string.IsNullOrWhiteSpace(a.RemoteUrl))
            .ToList();
        if (images.Count == 0) return null;

        var parts = new JsonArray();
        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text
            });
        }

        foreach (var image in images)
        {
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = image.RemoteUrl
                }
            });
        }

        return parts.ToJsonString();
    }

    private static string LabelForFile(Attachment attachment)
    {
        var name = attachment.FileName ?? string.Empty;
        var ext = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        return string.IsNullOrWhiteSpace(ext) ? "文件" : ext;
    }

    private static string AppendHiddenSystemHint(string text, string hint)
    {
        if (string.IsNullOrWhiteSpace(text)) return hint;
        return text.TrimEnd() + "\n\n" + hint;
    }

    private static string BuildHiddenSystemHint(string hint) =>
        $"{SystemHintDelimiter}{hint}{SystemHintDelimiter}";

    private string? ResolveSystemPrompt()
    {
        if (_chat.ActiveProvider?.Kind == ProviderKind.MolaGptProxy)
            return null;

        // Four-layer resolution (highest priority first):
        //   1. Conversation-level override          — _chat.ConversationSystemPrompt
        //   2. Active persona's system prompt       — _chat.ActivePersonaSystemPrompt
        //   3. Model-level default (legacy fallback)— _chat.ActiveModelSystemPrompt
        //   4. None                                  — return null
        //
        // When the conversation override is set together with a persona, the
        // user can choose to "append" the override after the persona prompt
        // instead of replacing it (default: replace).
        var conversationPrompt = _chat.ConversationSystemPrompt;
        var personaPrompt = _chat.ActivePersonaSystemPrompt;

        string? merged;
        if (!string.IsNullOrWhiteSpace(personaPrompt) || !string.IsNullOrWhiteSpace(conversationPrompt))
        {
            merged = SystemPromptInterpolator.Combine(personaPrompt, conversationPrompt, _chat.SystemPromptMode);
        }
        else
        {
            // Neither persona nor conversation prompt — fall back to the
            // legacy per-model default for backwards compatibility with the
            // pre-persona ProviderModelEntry.SystemPrompt field.
            var modelPrompt = _chat.ActiveModelSystemPrompt;
            merged = string.IsNullOrWhiteSpace(modelPrompt) ? null : modelPrompt;
        }

        if (string.IsNullOrWhiteSpace(merged)) return null;

        var vars = new PromptVariables
        {
            Now = DateTimeOffset.Now,
            ModelDisplayName = _chat.ActiveModel?.DisplayName,
            ModelId = _chat.ActiveModel?.Id,
            ProviderDisplayName = _chat.ActiveProvider?.DisplayName,
            Username = _settings?.MolaGptUsername
        };
        return SystemPromptInterpolator.Interpolate(merged, vars);
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    public void Stop()
    {
        _cts?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    public async Task RetryAsync(MessageViewModel? assistantMsg)
    {
        var activeProvider = _chat.ActiveProvider;
        var activeModel = _chat.ActiveModel;
        if (assistantMsg is null || activeProvider is null || activeModel is null) return;
        var index = _chat.Messages.IndexOf(assistantMsg);
        if (index <= 0 || !assistantMsg.IsLatestAssistant) return;

        var previousUser = _chat.Messages
            .Take(index)
            .LastOrDefault(m => m.Role == ChatMessage.RoleUser);
        if (previousUser is null) return;

        assistantMsg.BeginRetryAttempt();
        // Sync the assistant bubble's model/provider labels to whatever is
        // active *now*, not whatever produced the previous attempt — the
        // floating model name above the message must reflect the live model
        // during the retry stream and freeze on that value when committed.
        assistantMsg.ModelLabel = activeModel.DisplayName;
        assistantMsg.ProviderLabel = activeProvider.DisplayName;
        assistantMsg.IsStreaming = true;
        assistantMsg.StartPending(IsRoutesModel(activeModel));
        IsSending = true;
        _chat.IsStreaming = true;
        _cts = new CancellationTokenSource();

        try
        {
            var backfillHistory = activeProvider.Kind != ProviderKind.MolaGptProxy;
            var msgs = _chat.Messages
                .Take(index)
                .Select(m => new ChatMessage(
                    m.Role,
                    BuildContentForHistory(m),
                    Attachments: backfillHistory && m.Role == ChatMessage.RoleUser
                        ? BuildHistoryAttachments(m)
                        : null,
                    ReasoningContent: m.Role == ChatMessage.RoleAssistant ? m.Thinking : null))
                .ToList();

            var systemPrompt = ResolveSystemPrompt();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                msgs.Insert(0, new ChatMessage("system", systemPrompt));

            var extras = BuildExtras();
            var thinkingKind = ResolveActiveThinkingParamKind();

            var req = new ChatRequest(
                ModelId: activeModel.Id,
                Messages: msgs,
                ConversationId: _chat.ConversationId,
                SessionId: Guid.NewGuid().ToString("N"),
                UseThinking: EnableThinking,
                ReasoningEffort: IsReasoningEffortVisible ? ReasoningEffort : null,
                ExtraBody: extras,
                ThinkingBudgetTokens: EnableThinking ? ThinkingBudgetTokens : null,
                ThinkingParamKind: thinkingKind);

            await foreach (var chunk in activeProvider.StreamChatAsync(req, _cts.Token).WithCancellation(_cts.Token))
            {
                ApplyStreamChunk(assistantMsg, chunk);
                if (chunk.FinishReason is not null) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            assistantMsg.AppendDelta($"\n\n> ❌ **错误**: {ex.Message}");
        }
        finally
        {
            assistantMsg.StopPending();
            assistantMsg.FlushPendingDelta();
            RewritePythonArtifactMarkdownLinks(assistantMsg);
            _pythonArtifactContexts.Remove(assistantMsg);
            assistantMsg.IsStreaming = false;
            assistantMsg.StopThinking();
            assistantMsg.CommitRetryAttempt();
            _chat.UpdatePersistedMessage(assistantMsg);
            IsSending = false;
            _chat.IsStreaming = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanSend() =>
        !IsSending &&
        (!string.IsNullOrWhiteSpace(Text) || Attachments.Count > 0) &&
        (!(IsImageGenerationAvailable && IsImageGenerationMode) || !string.IsNullOrWhiteSpace(Text)) &&
        _chat.ActiveProvider is not null &&
        _chat.ActiveModel is not null &&
        !HasUnsupportedImages(Attachments, _chat.ActiveProvider, _chat.ActiveModel);

    private bool CanStop() => IsSending;
    private bool CanRetry(MessageViewModel? message) =>
        !IsSending
        && message is not null
        && message.Role == ChatMessage.RoleAssistant
        && message.IsLatestAssistant
        && !message.IsStreaming
        && _chat.ActiveProvider is not null
        && _chat.ActiveModel is not null;

    private bool HasUnsupportedImages(
        IEnumerable<Attachment> attachments,
        IChatProvider? provider,
        ProviderModel? model)
    {
        if (!attachments.Any(a => a.Kind == AttachmentKind.Image)) return false;
        if (provider?.Kind == ProviderKind.MolaGptProxy) return false;
        return model?.SupportsVision != true
               && _settings?.IsVisionProxyAvailableFor(provider?.Kind, model) != true;
    }

    private static bool IsRoutesModel(ProviderModel? model)
    {
        if (model is null) return false;
        return string.Equals(model.Id, "autoLLM", StringComparison.OrdinalIgnoreCase)
            || model.DisplayName.Contains("MolaGPT Routes", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyStreamChunk(MessageViewModel assistantMsg, ChatChunk chunk)
    {
        if (chunk.Pending is { } pending)
            assistantMsg.SetPendingStatus(pending.Label, pending.Detail, pending.IsRoutes);
        if (chunk.Tool is { } tool)
        {
            assistantMsg.FlushPendingDelta();
            assistantMsg.ApplyToolDelta(tool);
            if (string.Equals(tool.Status, "completed", StringComparison.OrdinalIgnoreCase)
                && string.Equals(tool.Name, ImageGenerationTool.ToolName, StringComparison.Ordinal))
            {
                AttachGeneratedImages(assistantMsg, tool.ResultPreviewJson);
            }
            if (string.Equals(tool.Status, "completed", StringComparison.OrdinalIgnoreCase)
                && string.Equals(tool.Name, PythonExecutionTool.ToolName, StringComparison.Ordinal))
            {
                RememberPythonArtifactContext(assistantMsg, tool.ResultPreviewJson);
                RewritePythonArtifactMarkdownLinks(assistantMsg);
            }
        }
        if (chunk.Sources is { Count: > 0 })
            assistantMsg.Sources = chunk.Sources;
        if (chunk.Usage is not null)
            assistantMsg.Usage = chunk.Usage;
        if (chunk.DeltaText is { Length: > 0 } t)
        {
            t = RewritePythonArtifactMarkdownLinks(t, assistantMsg);
            assistantMsg.AppendDelta(t);
        }
        if (chunk.DeltaThinking is { Length: > 0 } th)
            assistantMsg.AppendThinking(th);
    }

    /// <summary>
    /// Render images produced by the BYOK <c>generate_image</c> tool. The tool
    /// saves bytes to the local <see cref="MolaGPT.Storage.AttachmentStore"/> and
    /// returns JSON carrying each image's <c>local_name</c>; here we re-read those
    /// bytes and attach them to the assistant message so they show inline (and
    /// persist via message meta). Dedupes by LocalName because a tool call can be
    /// re-applied (running→completed, display-block rebuilds).
    /// </summary>
    private void AttachGeneratedImages(MessageViewModel assistantMsg, string? resultJson)
    {
        if (_attachmentStore is null || string.IsNullOrWhiteSpace(resultJson)) return;

        List<AttachmentChip>? added = null;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!(root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)) return;
            if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return;

            var existing = new HashSet<string>(
                (assistantMsg.Attachments ?? Array.Empty<AttachmentChip>())
                    .Select(c => c.LocalName)
                    .Where(n => !string.IsNullOrEmpty(n))!,
                StringComparer.Ordinal);

            foreach (var img in images.EnumerateArray())
            {
                if (img.ValueKind != JsonValueKind.Object) continue;
                var localName = ReadJsonString(img, "local_name");
                if (string.IsNullOrEmpty(localName) || !existing.Add(localName!)) continue;

                var bytes = _attachmentStore.Load(localName);
                if (bytes is not { Length: > 0 }) continue;

                added ??= new List<AttachmentChip>();
                added.Add(new AttachmentChip(
                    ReadJsonString(img, "file_name") ?? localName!,
                    "图片")
                {
                    Bytes = bytes,
                    LocalName = localName,
                    MimeType = ReadJsonString(img, "mime_type") ?? "image/png"
                });
            }
        }
        catch (JsonException)
        {
            return;
        }

        if (added is not { Count: > 0 }) return;

        var merged = new List<AttachmentChip>(assistantMsg.Attachments ?? Array.Empty<AttachmentChip>());
        merged.AddRange(added);
        assistantMsg.Attachments = merged;
    }

    private void RememberPythonArtifactContext(MessageViewModel assistantMsg, string? resultJson)
    {
        var context = PythonArtifactMarkdownRewriter.CreateContext(resultJson);
        if (context is null)
            return;

        if (!_pythonArtifactContexts.TryGetValue(assistantMsg, out var contexts))
        {
            contexts = new List<PythonArtifactMarkdownRewriter.ArtifactContext>();
            _pythonArtifactContexts[assistantMsg] = contexts;
        }
        contexts.Add(context);
    }

    private string RewritePythonArtifactMarkdownLinks(string text, MessageViewModel assistantMsg) =>
        _pythonArtifactContexts.TryGetValue(assistantMsg, out var contexts)
            ? PythonArtifactMarkdownRewriter.Rewrite(text, contexts)
            : text;

    private void RewritePythonArtifactMarkdownLinks(MessageViewModel assistantMsg)
    {
        if (!_pythonArtifactContexts.TryGetValue(assistantMsg, out var contexts))
            return;

        var rewritten = PythonArtifactMarkdownRewriter.Rewrite(assistantMsg.Content, contexts);
        if (!string.Equals(rewritten, assistantMsg.Content, StringComparison.Ordinal))
            assistantMsg.ReplaceContent(rewritten);
    }

    private static string? ReadJsonString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void CompleteStreamContext(BackgroundStreamTask streamContext, bool publishNotification)
    {
        RewritePythonArtifactMarkdownLinks(streamContext.AssistantMessage);
        _pythonArtifactContexts.Remove(streamContext.AssistantMessage);
        _chat.FinalizeAssistantMessage(streamContext.ConversationId, streamContext.AssistantMessage);

        if (publishNotification)
        {
            if (streamContext.IsDetached)
                _backgroundStreams?.Complete(streamContext);
            else
                _backgroundStreams?.PublishCompletion(
                    streamContext.ConversationId,
                    streamContext.ConversationTitle,
                    streamContext.ModelLabel);
        }
        else if (streamContext.IsDetached)
        {
            _backgroundStreams?.Detach(streamContext.ConversationId);
        }

        if (streamContext.ProviderKind == ProviderKind.MolaGptProxy)
            _ = CompleteConversationTurnAsync(streamContext.ConversationId);
    }

    private async Task CompleteConversationTurnAsync(string conversationId)
    {
        if (ConversationCompletedAsync is null) return;

        try
        {
            var title = await ConversationCompletedAsync(conversationId, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(title))
                _chat.ApplyExternalConversationTitle(conversationId, title);
        }
        catch
        {
            // Background sync/title generation should never break the composer.
        }
    }

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsComposerPlaceholderVisible));
        SendCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsSendingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
    }

    partial void OnEnableThinkingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsReasoningEffortVisible));
    }

    partial void OnReasoningEffortChanged(string value)
    {
        OnPropertyChanged(nameof(ReasoningEffortLabel));
    }

    partial void OnIsImageGenerationModeChanged(bool value)
    {
        if (!value)
        {
            ImageAspectRatio = "1:1";
            ImageStyle = string.Empty;
        }

        OnPropertyChanged(nameof(IsImageOptionsVisible));
        OnPropertyChanged(nameof(ComposerPlaceholder));
        SendCommand.NotifyCanExecuteChanged();
    }

    private MolaGPT.Core.Models.ThinkingParamKind? ResolveActiveThinkingParamKind()
    {
        if (!IsThinkingVisible) return null;

        var kind = ActiveThinkingKind;
        if (kind == MolaGPT.Core.Models.ThinkingParamKind.None)
            kind = MolaGPT.Core.Models.ThinkingParamKindInference.InferFromModelId(_chat.ActiveModel?.Id);

        return kind == MolaGPT.Core.Models.ThinkingParamKind.None ? null : kind;
    }

    private static string CreateWebCompatibleConversationId()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        Span<char> suffix = stackalloc char[9];
        var random = Random.Shared;
        for (int i = 0; i < suffix.Length; i++)
            suffix[i] = alphabet[random.Next(alphabet.Length)];
        return $"chat_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{new string(suffix)}";
    }

    private string BuildImageGenerationPrompt(
        string userText,
        IReadOnlyList<Attachment> preparedAttachments,
        string? sandboxHint)
    {
        var referenceImageUrl = preparedAttachments
            .Where(a => a.Kind == AttachmentKind.Image)
            .Select(a => a.RemoteUrl)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

        if (!string.IsNullOrWhiteSpace(referenceImageUrl))
        {
            return AppendHiddenSystemHint(
                userText,
                BuildHiddenSystemHint($"[重要提示: 用户已上传参考图片，公网访问地址为: {referenceImageUrl}。若需编辑此图片，请调用 image_generation_and_editing 工具时使用 action=\"edit\" 并将此 URL 作为 image_url 参数传递。]"));
        }

        var prompt = string.IsNullOrWhiteSpace(sandboxHint)
            ? userText
            : AppendHiddenSystemHint(userText, sandboxHint!);

        var ratioHint = !string.IsNullOrWhiteSpace(ImageAspectRatio) && ImageAspectRatio != "1:1"
            ? $"，必须使用 aspect_ratio=\"{ImageAspectRatio}\""
            : string.Empty;
        var styleHint = !string.IsNullOrWhiteSpace(ImageStyle)
            ? $"，必须使用 style=\"{ImageStyle}\""
            : string.Empty;

        return AppendHiddenSystemHint(
            prompt,
            BuildHiddenSystemHint($"[提示：可以使用 image_generation_and_editing 工具创建图片。工具支持 action=\"generate\"（生成新图片）和 action=\"edit\"（编辑现有图片）。生成时可指定 style（风格）和 aspect_ratio（宽高比）{ratioHint}{styleHint}。]"));
    }
}

public sealed record ImageGenerationOption(string Label, string Value);
