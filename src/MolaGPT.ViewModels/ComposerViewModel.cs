using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Attachments;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools;
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
/// active or the desktop host can prepare the Agent runtime on demand.
///
/// Tracks composer toolbar state for reasoning, network tools, webpage
/// reading, and attachments. Visibility is derived from the selected model's
/// advertised capabilities.
/// </summary>
public sealed partial class ComposerViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComposerPlaceholderVisible))]
    private string _text = string.Empty;
    [ObservableProperty] private bool _isSending;

    /// <summary>Reads straight through to the settings toggle rather than keeping
    /// a local copy: a second field here silently drifted from the settings page,
    /// so flipping "按 Enter 直接发送消息" never reached the input box. Defaults to
    /// true when no settings VM is wired (design-time / tests).</summary>
    public bool EnterToSend => _settings?.EnterToSend ?? true;

    /// <summary>Raised right after a user turn is committed to the transcript, so
    /// the chat view can re-take bottom-follow even if the user had scrolled up.</summary>
    public event Action? MessageSubmitted;

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

    /// <summary>Image generation mode. MolaGPT account mode uses the proxy
    /// image flow; BYOK image work is handled by the separate workbench.</summary>
    [ObservableProperty] private bool _isImageGenerationMode;

    [ObservableProperty] private string _imageAspectRatio = "1:1";
    [ObservableProperty] private string _imageStyle = string.Empty;

    public ObservableCollection<Attachment> Attachments { get; } = new();

    public Func<string, CancellationToken, Task<string?>>? ConversationCompletedAsync { get; set; }

    /// <summary>Generates a title for the first successful turn of a local
    /// BYOK/Work conversation. The desktop host supplies the persistence service.</summary>
    public Func<string, string?, string?, CancellationToken, Task<string?>>? LocalConversationTitleAsync { get; set; }

    private Func<Task<bool>>? _ensureAgentRuntimeAsync;
    public Func<Task<bool>>? EnsureAgentRuntimeAsync
    {
        get => _ensureAgentRuntimeAsync;
        set
        {
            _ensureAgentRuntimeAsync = value;
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    private readonly ChatViewModel _chat;
    private readonly BackgroundStreamService? _backgroundStreams;
    private readonly SettingsViewModel? _settings;
    private readonly PersonaListViewModel? _personas;
    private readonly SkillsViewModel? _skills;
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
        MolaGPT.Storage.AttachmentStore? attachmentStore,
        SkillsViewModel? skills = null)
    {
        _chat = chat;
        _backgroundStreams = backgroundStreams;
        _settings = settings;
        _personas = personas;
        _attachmentStore = attachmentStore;
        _skills = skills;
        _chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChatViewModel.ConversationId))
                PruneOrphanedArtifactContexts();
            if (e.PropertyName is nameof(ChatViewModel.ActiveProvider) or nameof(ChatViewModel.ActiveModel))
            {
                SendCommand.NotifyCanExecuteChanged();
                RetryCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(IsThinkingVisible));
                OnPropertyChanged(nameof(IsReasoningEffortVisible));
                OnPropertyChanged(nameof(IsAttachVisible));
                OnPropertyChanged(nameof(CanAcceptImageAttachments));
                OnPropertyChanged(nameof(CanAcceptFileAttachments));
                OnPropertyChanged(nameof(CanProcessOpaqueFiles));
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
                if (!IsImageGenerationAvailable && IsImageGenerationMode)
                    IsImageGenerationMode = false;

                ActiveThinkingKind = _chat.ActiveModel?.ThinkingConfig?.Kind
                    ?? MolaGPT.Core.Models.ThinkingParamKindInference.InferFromModelId(_chat.ActiveModel?.Id);

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
                if (e.PropertyName is nameof(SettingsViewModel.EnterToSend))
                    OnPropertyChanged(nameof(EnterToSend));

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
                    or nameof(SettingsViewModel.LocalToolPermissionMode)
                    or nameof(SettingsViewModel.PythonToolAllowedImports)
                    or nameof(SettingsViewModel.PythonToolDeniedImports)
                    or nameof(SettingsViewModel.PythonToolAllowedPathPrefixes)
                    or nameof(SettingsViewModel.PythonToolDeniedPathPrefixes))
                {
                    OnPropertyChanged(nameof(IsPythonToolVisible));
                    OnPropertyChanged(nameof(CanProcessOpaqueFiles));
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

    /// <summary>Show the thinking control when the user has enabled thinking and
    /// the model exposes either effort or budget controls.</summary>
    public bool IsReasoningEffortVisible => EnableThinking
        && (_chat.ActiveModel?.SupportsReasoningEffort == true || IsBudgetSliderVisible);

    /// <summary>Attach button is always shown; text/document attachments are
    /// validated at send time, while images also require vision support.</summary>
    public bool IsAttachVisible => true;
    public bool CanAcceptImageAttachments =>
        _chat.ActiveProvider?.Kind == ProviderKind.MolaGptProxy
        || _chat.ActiveModel?.SupportsVision == true
        || _settings?.IsVisionProxyAvailableFor(_chat.ActiveProvider?.Kind, _chat.ActiveModel) == true;
    /// <summary>附件按钮整体是否可用。文本、PDF、Office 文档在任何模式下都能直接
    /// 抽成文字注入上下文，因此恒为真；只有既抽不出文字、又没有 Python 工具可以
    /// 处理的二进制文件才在入口逐个拦截，见 <see cref="CanProcessOpaqueFiles"/>。</summary>
    public bool CanAcceptFileAttachments => true;

    /// <summary>BYOK 下能否接收抽不出文字的二进制文件（压缩包、可执行文件、
    /// 音视频等）——只有 Python 工具可用时它们才有意义。MolaGPT 代理模式走沙箱
    /// 上传，不受此限制。</summary>
    public bool CanProcessOpaqueFiles =>
        _chat.ActiveProvider?.Kind == ProviderKind.MolaGptProxy
        || CanUseByokPythonTool;
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

    private bool CanUseByokFileTools =>
        _chat.ActiveProvider?.Kind != ProviderKind.MolaGptProxy
        && _chat.ActiveModel?.SupportsToolCalling == true
        && _settings?.FileToolsEnabled == true;

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
        "ultra" => "Ultra",
        // Empty/null: blank so the button doesn't lie about an unset value.
        null or "" => string.Empty,
        // Unknown value: surface it verbatim instead of pretending it's "中".
        var other => other
    };

    /// <summary>Label for the thinking control in the composer toolbar. Budget
    /// models must not present their token count as a qualitative effort level.</summary>
    public string ReasoningControlLabel => IsBudgetSliderVisible
        ? $"预算: {ThinkingBudgetTokens}"
        : $"强度: {ReasoningEffortLabel}";

    public string ReasoningControlTitle => IsBudgetSliderVisible ? "推理预算" : "推理强度";

    public string ReasoningControlToolTip => IsBudgetSliderVisible ? "调整推理预算" : "调整推理强度";

    public IReadOnlyList<string> AvailableEffortLevels
    {
        get
        {
            var model = _chat.ActiveModel;
            var kind = EffectiveThinkingKind;
            var resolved = MolaGPT.Core.Models.ThinkingEffortLevels.Resolve(model?.ThinkingConfig, kind);
            // OpenAI 模板历史上带 none（关）；若模型未自定义档位，保留兼容。
            if (kind == MolaGPT.Core.Models.ThinkingParamKind.OpenAiReasoningEffort
                && (model?.ThinkingConfig?.EffortLevels is null or { Length: 0 }))
            {
                return new[] { "none" }.Concat(resolved).ToArray();
            }
            return resolved;
        }
    }

    public bool IsEffortComboVisible => EffectiveThinkingKind is not (
        MolaGPT.Core.Models.ThinkingParamKind.AnthropicBudget or
        MolaGPT.Core.Models.ThinkingParamKind.GeminiBudget or
        MolaGPT.Core.Models.ThinkingParamKind.QwenThinkingBudget);

    public bool IsBudgetSliderVisible => EffectiveThinkingKind is
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
        OnPropertyChanged(nameof(IsReasoningEffortVisible));
        OnPropertyChanged(nameof(ReasoningControlLabel));
        OnPropertyChanged(nameof(ReasoningControlTitle));
        OnPropertyChanged(nameof(ReasoningControlToolTip));
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
        if (_chat.ActiveProvider is null || _chat.ActiveModel is null)
        {
            if (EnsureAgentRuntimeAsync is null || !await EnsureAgentRuntimeAsync()) return;
        }
        if (_chat.ActiveProvider is null || _chat.ActiveModel is null) return;
        if (HasUnsupportedImages(Attachments, _chat.ActiveProvider, _chat.ActiveModel))
            return;
        var isMolaGptImageGenerationSend =
            _chat.ActiveProvider.Kind == ProviderKind.MolaGptProxy && IsImageGenerationMode;
        var generateLocalTitleOnCompletion =
            _chat.IsEmpty && _chat.ActiveProvider.Kind != ProviderKind.MolaGptProxy;
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
        // Re-take bottom-follow now that a new turn exists, so a user who had
        // scrolled up still sees their message and the incoming reply.
        MessageSubmitted?.Invoke();
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
                assistantMsg.AppendDelta($"\n\n> **附件上传失败**：{ex.Message}");
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

        // BYOK file attachments: extract their text up front and drop a copy in
        // the per-conversation Python workspace. The text reaches the model
        // inline (so a weak or tool-less model still sees the content) while the
        // original stays reachable by path for tables, page operations and
        // embedded images. The chips the user sees are unchanged — only the
        // model-visible payload differs.
        if (provider.Kind != ProviderKind.MolaGptProxy
            && outgoingAttachments.Any(a => a.Kind == AttachmentKind.File))
        {
            assistantMsg.SetPendingStatus("处理附件", "提取文档文本");
            var pending = outgoingAttachments;
            try
            {
                // Parsing a large PDF takes about a second; off the UI thread so
                // the message bubble the user just posted stays responsive.
                outgoingAttachments = await Task.Run(
                    () => PrepareByokFileAttachments(pending, conversationId, cts.Token), cts.Token);

                if (userMsg is not null)
                {
                    // Re-chip so the workspace/sidecar paths persist with the
                    // message and later turns reuse them instead of copying the
                    // file again.
                    userMsg.Attachments = BuildAttachmentChips(outgoingAttachments);
                    _chat.UpdatePersistedMessage(userMsg);
                }
                // Uploaded files now live in the working directory — reflect them
                // in the artifact panel right away.
                _chat.RefreshArtifacts();
            }
            catch (OperationCanceledException)
            {
                // User pressed stop while the documents were being parsed.
                assistantMsg.WasStopped = true;
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
            catch (Exception ex)
            {
                // Per-file failures are already absorbed inside the preparation
                // step, so reaching here means something systemic. Send the text
                // anyway rather than losing the user's message, and say what was
                // lost instead of silently dropping the attachments.
                assistantMsg.AppendDelta($"\n\n> **附件处理失败**：{ex.Message}（本轮仅发送文字）");
                assistantMsg.FlushPendingDelta();
                outgoingAttachments = outgoingAttachments
                    .Where(a => a.Kind != AttachmentKind.File)
                    .ToList();
            }
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
            ModelId = model.Id,
            ProviderId = provider.Id,
            ProviderKind = provider.Kind,
            AssistantMessage = assistantMsg,
            Cts = cts,
            StreamTask = Task.CompletedTask,
            SessionId = req.SessionId,
            GenerateTitleOnCompletion = generateLocalTitleOnCompletion
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
            // Marks the bubble as stopped rather than merely empty, so it keeps its
            // action bar (retry) and says why there is nothing there.
            assistantMsg.WasStopped = true;
        }
        catch (MolaGptAuthExpiredException ex)
        {
            assistantMsg.AppendDelta($"\n\n> {ex.Message}");
            try
            {
                if (MolaGptProviderIds.IsMolaGptAccount(provider.Id) && _chat.ActiveProvider?.Id == provider.Id)
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
            assistantMsg.AppendDelta($"\n\n> **错误**：{ex.Message}");
            ClassifyActionableError(assistantMsg, ex);
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
                task.AssistantMessage.AppendDelta($"\n\n> **恢复失败**：{ex.Message}");
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

        if (trackingTask is not null && !cts.IsCancellationRequested)
            trackingTask.CompletedSuccessfully = true;
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
            ["deepResearch"] = false,
            ["permissionMode"] = _settings?.LocalToolPermissionMode ?? ToolPermissionMode.Approval,
            ["imageGenerationPermissionMode"] = _settings?.ImageGenerationPermissionMode ?? ToolPermissionMode.Approval,
            ["visionPermissionMode"] = _settings?.VisionPermissionMode ?? ToolPermissionMode.Approval,
            ["mcpPermissionMode"] = _settings?.McpPermissionMode ?? ToolPermissionMode.Approval
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
            // The skill folders of whatever skills are switched on this turn. The
            // model gets a catalogue of these in its system prompt, so every tool
            // that might follow it there has to be able to — one list, so Python
            // and the file tools cannot end up disagreeing about which skills are
            // readable.
            var skillRoots = _skills is { HasEnabledSkills: true }
                ? _skills.AllowedReadRoots()
                : Array.Empty<string>();

            if (CanUseByokPythonTool)
            {
                var pythonOptions = _settings!.BuildPythonExecutionOptions() with { Enabled = true };
                // Let the Python tool read enabled skills' SKILL.md / scripts
                // without tripping path approval.
                if (skillRoots.Count > 0)
                {
                    pythonOptions = pythonOptions with
                    {
                        AllowedPathPrefixes = string.Join(",",
                            new[] { pythonOptions.AllowedPathPrefixes }
                                .Concat(skillRoots)
                                .Where(s => !string.IsNullOrWhiteSpace(s)))
                    };
                }
                enabledTools["python"] = pythonOptions;
            }
            if (CanUseByokFileTools)
            {
                // Read-only file tools (read_file / glob_files / grep_files),
                // default-allowed. They honor the same deny-list as the Python
                // tool so blocked paths stay blocked across tools.
                enabledTools["fileTools"] = true;
                var denied = _settings?.PythonToolDeniedPathPrefixes;
                if (!string.IsNullOrWhiteSpace(denied))
                    enabledTools["fileToolsDeniedPaths"] = denied;

                // Same skill folders, so "读一下 pdf 技能" does not raise an approval
                // dialog for a file the app itself just told the model to read.
                if (skillRoots.Count > 0)
                    enabledTools["fileToolsReadableRoots"] = string.Join(",", skillRoots);
            }
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
    /// so multi-turn follow-ups still carry earlier images <em>and files</em>.
    /// Bytes are re-read from the local <see cref="MolaGPT.Storage.AttachmentStore"/>
    /// by <see cref="AttachmentChip.LocalName"/>; in-memory
    /// <see cref="AttachmentChip.Bytes"/> (the just-sent turn) is preferred to skip
    /// a disk round-trip.
    ///
    /// An attachment whose bytes are gone is rebuilt as an explicitly unavailable
    /// one rather than dropped: dropping it would leave the user staring at a chip
    /// the model never received, and would renumber every later <c>[图片#N]</c>.
    /// Returns null when the message has nothing to rehydrate (e.g. MolaGPT-account
    /// images, which travel via ContentPartsJson instead).
    /// </summary>
    private IReadOnlyList<Attachment>? BuildHistoryAttachments(MessageViewModel message)
    {
        if (message.Attachments is null || message.Attachments.Count == 0) return null;

        var rebuilt = new List<Attachment>();
        var restatedChips = new List<AttachmentChip>(message.Attachments.Count);
        var chipsChanged = false;

        foreach (var originalChip in message.Attachments)
        {
            var chip = originalChip;
            var bytes = chip.Bytes;
            if (bytes is not { Length: > 0 } && _attachmentStore is not null)
                bytes = _attachmentStore.Load(chip.LocalName);

            var available = bytes is { Length: > 0 };
            void MarkUnavailable()
            {
                if (chip.IsUnavailable) return;
                chip = chip with { IsUnavailable = true };
                chipsChanged = true;
            }

            if (chip.IsImage)
            {
                var mime = string.IsNullOrWhiteSpace(chip.MimeType) ? "image/png" : chip.MimeType!;
                if (available)
                {
                    rebuilt.Add(new Attachment(AttachmentKind.Image, mime, bytes!, FileName: chip.FileName));
                }
                else if (!string.IsNullOrEmpty(chip.LocalName))
                {
                    // Had a local copy once, so this is a real loss worth
                    // reporting. A legacy chip that never had one is skipped.
                    MarkUnavailable();
                    rebuilt.Add(new Attachment(
                        AttachmentKind.Image, mime, Array.Empty<byte>(), FileName: chip.FileName,
                        UnavailableReason: "本地副本已丢失，无法重新发送这张图片。"));
                }
            }
            else if (string.IsNullOrEmpty(chip.LocalName) && !available)
            {
                // Legacy file chip: nothing was ever stored to rebuild from.
            }
            else
            {
                var fileMime = string.IsNullOrWhiteSpace(chip.MimeType) ? "application/octet-stream" : chip.MimeType!;
                if (available)
                {
                    // Extraction is memoised on the content hash, so re-feeding the
                    // same document every turn costs a lookup, not a re-parse.
                    var extraction = DocumentTextExtractor.Extract(bytes, fileMime, chip.FileName);
                    rebuilt.Add(new Attachment(
                        AttachmentKind.File, fileMime, bytes!, FileName: chip.FileName,
                        WorkspaceRelativePath: chip.WorkspacePath,
                        Text: new AttachmentText(
                            extraction.Text, extraction.PageCount, extraction.Note, chip.ExtractedTextPath)));
                }
                else
                {
                    MarkUnavailable();
                    rebuilt.Add(new Attachment(
                        AttachmentKind.File, fileMime, Array.Empty<byte>(), FileName: chip.FileName,
                        WorkspaceRelativePath: chip.WorkspacePath,
                        UnavailableReason: "本地副本已丢失，无法重新读取该文件内容。"));
                }
            }

            restatedChips.Add(chip);
        }

        // Reassigning the collection is what refreshes the bubble; mutating the
        // records in place would leave the UI showing an attachment the model was
        // just told it cannot see.
        if (chipsChanged) message.Attachments = restatedChips;

        return rebuilt.Count == 0 ? null : rebuilt;
    }

    private IReadOnlyList<AttachmentChip>? BuildAttachmentChips(IReadOnlyList<Attachment> attachments)
    {
        if (attachments.Count == 0) return null;
        return attachments
            .Select(attachment =>
            {
                var isImage = attachment.Kind == AttachmentKind.Image;
                var hasBytes = attachment.Bytes is { Length: > 0 };

                // BYOK attachments are content-addressed into the local
                // AttachmentStore so they survive app restart and can be re-fed
                // on later turns. MolaGPT-account uploads already live on the
                // server (RemoteUrl for images, SandboxPath for files) and are
                // never rehydrated locally, so storing them would be dead weight.
                string? localName = null;
                if (hasBytes
                    && string.IsNullOrWhiteSpace(attachment.RemoteUrl)
                    && string.IsNullOrWhiteSpace(attachment.SandboxPath)
                    && _attachmentStore is not null)
                {
                    localName = _attachmentStore.Save(attachment.Bytes, attachment.MimeType, attachment.FileName);
                }

                return new AttachmentChip(
                    attachment.DisplayName,
                    isImage ? "图片" : AttachmentMime.ChipLabel(attachment.FileName),
                    string.IsNullOrWhiteSpace(attachment.RemoteUrl) ? null : attachment.RemoteUrl)
                {
                    // Keep image bytes in memory so the user can re-open the
                    // preview right after sending (no disk round-trip). On reload
                    // the preview falls back to LocalName → AttachmentStore, or
                    // ThumbnailUrl for MolaGPT-account images.
                    Bytes = isImage && hasBytes ? attachment.Bytes : null,
                    LocalName = localName,
                    MimeType = attachment.MimeType,
                    Kind = attachment.Kind,
                    WorkspacePath = attachment.WorkspaceRelativePath,
                    ExtractedTextPath = attachment.Text?.TextFileRelativePath,
                    IsUnavailable = attachment.IsUnavailable
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

        // BYOK: everything travels in the content parts — images as base64, files
        // as extracted text plus a workspace path.
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

    /// <summary>
    /// BYOK file routing. Every file gets the same treatment: its text is
    /// extracted for inlining, and a copy lands in the conversation's Python
    /// workspace so <c>read_file</c> / <c>execute_python_code</c> can reach the
    /// original by name. Neither step is allowed to fail the send — a failed copy
    /// still leaves the extracted text, and a failed extraction still leaves the
    /// path plus a model-visible note.
    ///
    /// Image attachments pass through untouched.
    /// </summary>
    private static List<Attachment> PrepareByokFileAttachments(
        IReadOnlyList<Attachment> attachments,
        string conversationId,
        CancellationToken ct)
    {
        var result = new List<Attachment>(attachments.Count);
        foreach (var attachment in attachments)
        {
            result.Add(attachment.Kind == AttachmentKind.File
                ? PrepareByokFile(attachment, conversationId, ct)
                : attachment);
        }
        return result;
    }

    private static Attachment PrepareByokFile(Attachment attachment, string conversationId, CancellationToken ct)
    {
        var name = attachment.DisplayName;
        var extraction = DocumentTextExtractor.Extract(attachment.Bytes, attachment.MimeType, name);

        string? workspacePath = null;
        try
        {
            workspacePath = PythonExecutionTool.CopyAttachmentToSession(conversationId, name, attachment.Bytes, ct);
        }
        catch (Exception)
        {
            // The extracted text still reaches the model; it just loses the
            // ability to open the original with a tool.
        }

        return attachment with
        {
            WorkspaceRelativePath = workspacePath,
            Text = new AttachmentText(
                extraction.Text,
                extraction.PageCount,
                extraction.Note,
                TryWriteExtractedTextSidecar(attachment, extraction, conversationId, workspacePath, ct))
        };
    }

    /// <summary>
    /// Writes the full extracted text next to the original when it is too large
    /// to inline, so the model has something <c>read_file</c> can actually page
    /// through — a truncated PDF or DOCX is unreadable through the original,
    /// which is binary. Plain-text sources need no sidecar: their own path works.
    /// </summary>
    private static string? TryWriteExtractedTextSidecar(
        Attachment attachment,
        DocumentExtraction extraction,
        string conversationId,
        string? workspacePath,
        CancellationToken ct)
    {
        if (workspacePath is null || !extraction.HasText) return null;
        if (extraction.TotalChars <= AttachedFilePrompt.DefaultInlineCharsPerFile) return null;
        if (AttachmentMime.ClassifyDocument(attachment.MimeType, attachment.FileName, attachment.Bytes)
            == AttachmentDocumentKind.Text) return null;

        try
        {
            var sidecarName = Path.GetFileNameWithoutExtension(workspacePath) + ".extracted.txt";
            return PythonExecutionTool.CopyAttachmentToSession(
                conversationId,
                sidecarName,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(extraction.Text!),
                ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string AppendHiddenSystemHint(string text, string hint)
    {
        if (string.IsNullOrWhiteSpace(text)) return hint;
        return text.TrimEnd() + "\n\n" + hint;
    }

    /// <summary>Wraps a hint in the delimiter MessageViewModel strips before
    /// display, so it reaches the model without showing up in the user's bubble.
    /// Used by the MolaGPT-account image and sandbox paths; BYOK file content
    /// travels as its own content part and needs no wrapper.</summary>
    private static string BuildHiddenSystemHint(string hint) => $"{SystemHintDelimiter}{hint}{SystemHintDelimiter}";

    private const string SystemHintDelimiter = "✝";

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

        // Appended after whatever the user configured: the environment block says
        // how this machine's workspace behaves, the skill catalog says what is
        // available in it. Both must reach the model even when there is no
        // persona / conversation / model prompt, so they are folded in after
        // interpolation rather than gated behind the merged-prompt early return.
        var appendices = new[] { BuildPythonEnvironmentHint(), BuildSkillCatalogHint() }
            .Where(hint => !string.IsNullOrWhiteSpace(hint))
            .Select(hint => hint!)
            .ToArray();

        if (string.IsNullOrWhiteSpace(merged))
            return appendices.Length == 0 ? null : string.Join("\n\n", appendices);

        var vars = new PromptVariables
        {
            Now = DateTimeOffset.Now,
            ModelDisplayName = _chat.ActiveModel?.DisplayName,
            ModelId = _chat.ActiveModel?.Id,
            ProviderDisplayName = _chat.ActiveProvider?.DisplayName,
            Username = _settings?.MolaGptUsername
        };
        var interpolated = SystemPromptInterpolator.Interpolate(merged, vars);
        return appendices.Aggregate(interpolated, AppendHiddenSystemHint);
    }

    /// <summary>
    /// The few facts about the local Python workspace a model cannot guess and
    /// otherwise burns turns rediscovering: where its files live, how images get
    /// shown, what pip does, and what raises an approval dialog.
    ///
    /// Deliberately four lines. The environment itself is kept honest — real user
    /// folders resolve normally, <c>~</c> means what it says — so anything a model
    /// would already assume correctly is left out rather than restated here.
    /// </summary>
    private string? BuildPythonEnvironmentHint()
    {
        if (!CanUseByokPythonTool || _settings is null) return null;

        var options = _settings.BuildPythonExecutionOptions();
        var lines = new List<string>
        {
            "本对话有专属工作目录，你的代码在其中运行，文件跨轮次保留——用相对路径读写，上一轮生成的文件直接按原名复用。",
            "生成的图表存为 PNG/JPG 放在工作目录，按 display_instructions 给的相对路径展示，不要编造 URL 或绝对路径。",
            "pip 装的包只在本对话有效，它们的命令行工具已在 PATH 上，按名字直接调用。"
        };

        // Nothing prompts under full access, and no path scope applies there, so
        // stating either would be a lie that also discourages the model from
        // acting.
        if (options.PermissionMode != PythonPermissionMode.FullAccess)
        {
            lines.Add("默认只有工作目录可写，全盘可读。要写到别处（含桌面、文档、下载），在 paths 参数里声明该文件夹，用户确认一次后长期有效；未声明就写会直接报错并列出已批准范围，此时向用户说明需要哪个位置、为什么，不要反复重试。");
            lines.Add("删除移动文件、装包、起子进程需要用户确认，合并成一次执行并在 description 里说清意图。");
        }

        if (!options.AllowNetwork)
            lines.Add("网络默认关闭，不要依赖下载文件或抓取网页。");

        return "<运行环境>\n" + string.Join("\n", lines) + "\n</运行环境>";
    }

    /// <summary>
    /// Tier-1 skill catalog injected into the system prompt. Only meaningful for
    /// BYOK chats with the Python tool enabled, since skills execute through it.
    /// </summary>
    private string? BuildSkillCatalogHint()
    {
        if (_skills is null) return null;
        if (!CanUseByokPythonTool) return null;
        return _skills.BuildCatalogForPrompt(canUseReadTool: CanUseByokFileTools);
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

        var cts = new CancellationTokenSource();
        _cts = cts;
        _activeAssistantMsg = assistantMsg;

        var conversationId = _chat.ConversationId ?? string.Empty;
        var sessionId = Guid.NewGuid().ToString("N");

        // A retry runs through the same machinery as a first send, and for the same
        // reasons: it is what publishes the completion notification, what lets the
        // user switch conversations without stranding the stream, and what tells
        // cloud sync the conversation moved. Doing it by hand here is how a retry
        // ended up finishing in silence.
        var streamContext = new BackgroundStreamTask
        {
            ConversationId = conversationId,
            ConversationTitle = _chat.ConversationTitle,
            ModelLabel = assistantMsg.ModelLabel,
            ModelId = activeModel.Id,
            ProviderId = activeProvider.Id,
            ProviderKind = activeProvider.Kind,
            AssistantMessage = assistantMsg,
            Cts = cts,
            StreamTask = Task.CompletedTask,
            SessionId = sessionId,
            IsRegeneration = true
        };
        _activeTask = streamContext;
        var wasCancelled = false;

        try
        {
            // Providers that keep the transcript themselves have to be told this is
            // a do-over. Pruning the message list below does nothing for them: they
            // read only the newest turn and answer from their own history, which
            // still holds the attempt being replaced.
            //
            // Deliberately before the stream rather than after a successful one: if
            // the retry then fails, the provider has forgotten a turn the UI still
            // shows, which is recoverable. The other order leaves the old attempt
            // in the model's context, which is the bug.
            if (activeProvider is IStatefulHistoryProvider stateful)
                await stateful.ForgetLastTurnAsync(conversationId, cts.Token);

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
                SessionId: sessionId,
                UseThinking: EnableThinking,
                ReasoningEffort: IsReasoningEffortVisible ? ReasoningEffort : null,
                ExtraBody: extras,
                ThinkingBudgetTokens: EnableThinking ? ThinkingBudgetTokens : null,
                ThinkingParamKind: thinkingKind);

            var streamTask = RunStreamLoopAsync(activeProvider, req, assistantMsg, cts, streamContext);
            streamContext.StreamTask = streamTask;
            _activeStreamTask = streamTask;
            await streamTask;
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
            // Same as the first-send path: a stopped retry keeps its action bar
            // and says why it is empty, instead of becoming a blank version.
            assistantMsg.WasStopped = true;
        }
        catch (Exception ex)
        {
            assistantMsg.AppendDelta($"\n\n> **错误**：{ex.Message}");
            ClassifyActionableError(assistantMsg, ex);
        }
        finally
        {
            // Ahead of CompleteStreamContext, which persists: the stored meta and
            // the version switcher both read RetryAttempts, and capturing an
            // attempt means capturing the text — so the deltas have to be flushed
            // and the artifact links resolved before the snapshot is taken, or the
            // saved version keeps the links this attempt showed on screen only
            // until it finished. (CompleteStreamContext rewrites again; the second
            // pass sees absolute URLs and leaves them alone.)
            assistantMsg.StopPending();
            assistantMsg.FlushPendingDelta();
            assistantMsg.IsStreaming = false;
            assistantMsg.StopThinking();
            RewritePythonArtifactMarkdownLinks(assistantMsg);
            assistantMsg.CommitRetryAttempt();

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

    /// <summary>Map a failed turn to a one-tap recovery when we recognize the
    /// cause, so the error banner can offer a fix instead of a dead end.
    /// Balance/402 → model selector.</summary>
    private static void ClassifyActionableError(MessageViewModel assistantMsg, Exception ex)
    {
        var message = ex.Message ?? string.Empty;

        if (message.Contains("402", StringComparison.Ordinal)
            || message.Contains("Insufficient Balance", StringComparison.OrdinalIgnoreCase)
            || message.Contains("余额", StringComparison.Ordinal))
        {
            assistantMsg.SetActionableError(
                MessageErrorAction.SwitchModel,
                "当前模型不可用或余额不足，换一个模型再试。");
        }
    }

    private bool CanSend()
    {
        var providerReady = _chat.ActiveProvider is not null && _chat.ActiveModel is not null;
        return !IsSending
               && (!string.IsNullOrWhiteSpace(Text) || Attachments.Count > 0)
               && (!(IsImageGenerationAvailable && IsImageGenerationMode) || !string.IsNullOrWhiteSpace(Text))
               && (providerReady || EnsureAgentRuntimeAsync is not null)
               && (!providerReady || !HasUnsupportedImages(Attachments, _chat.ActiveProvider, _chat.ActiveModel));
    }

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
                RememberGeneratedImageContext(assistantMsg);
            }
            if (string.Equals(tool.Status, "completed", StringComparison.OrdinalIgnoreCase)
                && string.Equals(tool.Name, PythonExecutionTool.ToolName, StringComparison.Ordinal))
            {
                RememberPythonArtifactContext(assistantMsg, tool.ResultPreviewJson);
                RewritePythonArtifactMarkdownLinks(assistantMsg);
                // A python run may have produced new files; refresh the
                // session-level artifact panel so they appear immediately.
                _chat.RefreshArtifacts();
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

        // Collect image references robustly. The tool-result *preview* fed here can
        // be truncated by the provider (~1600 chars) — which corrupts the JSON and
        // drops the images[] tail when a long revised_prompt precedes it, so a plain
        // JsonDocument.Parse silently yields nothing. We parse when intact (for
        // file_name / mime_type) and always also raw-scan for local_name / image_path
        // so the reference survives truncation.
        var refs = ExtractGeneratedImageRefs(resultJson);
        if (refs.Count == 0) return;

        var existing = new HashSet<string>(
            (assistantMsg.Attachments ?? Array.Empty<AttachmentChip>())
                .Select(c => c.LocalName)
                .Where(n => !string.IsNullOrEmpty(n))!,
            StringComparer.Ordinal);

        List<AttachmentChip>? added = null;
        foreach (var (localName, fileName, mime) in refs)
        {
            if (string.IsNullOrEmpty(localName) || !existing.Add(localName)) continue;

            var bytes = _attachmentStore.Load(localName);
            if (bytes is not { Length: > 0 }) continue;

            added ??= new List<AttachmentChip>();
            added.Add(new AttachmentChip(fileName ?? localName, "图片")
            {
                Bytes = bytes,
                LocalName = localName,
                MimeType = mime ?? "image/png",
                Kind = AttachmentKind.Image
            });
        }

        if (added is not { Count: > 0 }) return;

        var merged = new List<AttachmentChip>(assistantMsg.Attachments ?? Array.Empty<AttachmentChip>());
        merged.AddRange(added);
        assistantMsg.Attachments = merged;
    }

    /// <summary>Pull generated-image references out of a (possibly truncated) tool
    /// result. Tries a structured parse first for full metadata, then raw-scans for
    /// <c>local_name</c> / <c>image_path</c> values so a truncated preview can't
    /// hide the reference.</summary>
    private static List<(string LocalName, string? FileName, string? Mime)> ExtractGeneratedImageRefs(string resultJson)
    {
        var refs = new List<(string, string?, string?)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
            {
                foreach (var img in images.EnumerateArray())
                {
                    if (img.ValueKind != JsonValueKind.Object) continue;
                    var localName = ReadJsonString(img, "local_name");
                    if (string.IsNullOrEmpty(localName) || !seen.Add(localName!)) continue;
                    refs.Add((localName!, ReadJsonString(img, "file_name"), ReadJsonString(img, "mime_type")));
                }
            }
        }
        catch (JsonException)
        {
            // Truncated/invalid preview — the raw scan below recovers the reference.
        }

        foreach (System.Text.RegularExpressions.Match m in GeneratedImageRefRegex().Matches(resultJson))
        {
            var localName = m.Groups["name"].Value;
            if (string.IsNullOrEmpty(localName) || !seen.Add(localName)) continue;
            refs.Add((localName, null, null));
        }

        return refs;
    }

    [System.Text.RegularExpressions.GeneratedRegex("\"(?:local_name|image_path)\"\\s*:\\s*\"(?<name>[^\"]+)\"")]
    private static partial System.Text.RegularExpressions.Regex GeneratedImageRefRegex();


    /// <summary>Register a markdown-link rewrite context for BYOK generate_image
    /// results so an inline ![](generated-image-1.png) resolves to the real local
    /// attachment file (mirrors the python-artifact link rewrite). Built from the
    /// chips just attached by <see cref="AttachGeneratedImages"/>.</summary>
    private void RememberGeneratedImageContext(MessageViewModel assistantMsg)
    {
        var context = PythonArtifactMarkdownRewriter.CreateAttachmentContext(assistantMsg.Attachments);
        if (context is null) return;

        if (!_pythonArtifactContexts.TryGetValue(assistantMsg, out var contexts))
        {
            contexts = new List<PythonArtifactMarkdownRewriter.ArtifactContext>();
            _pythonArtifactContexts[assistantMsg] = contexts;
        }
        contexts.Add(context);
    }

    /// <summary>
    /// Drop artifact contexts whose message no longer belongs to any live stream.
    /// Streams normally remove their own entry when they finish; this safety net
    /// (run on conversation switch) catches entries stranded by a stream that
    /// never terminated or whose completion path threw before the removal.
    /// Anything foreground-active, background-registered, or still streaming is
    /// left alone.
    /// </summary>
    private void PruneOrphanedArtifactContexts()
    {
        if (_pythonArtifactContexts.Count == 0) return;

        List<MessageViewModel>? stale = null;
        var background = _backgroundStreams?.ActiveTasks;
        foreach (var key in _pythonArtifactContexts.Keys)
        {
            if (key.IsStreaming) continue;
            if (ReferenceEquals(key, _activeAssistantMsg)) continue;
            if (background is not null && background.Any(t => ReferenceEquals(t.AssistantMessage, key))) continue;
            (stale ??= new List<MessageViewModel>()).Add(key);
        }
        if (stale is null) return;
        foreach (var key in stale)
            _pythonArtifactContexts.Remove(key);
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

        // A regeneration's bubble is already a row; finalizing it the normal way
        // would insert a second one.
        if (streamContext.IsRegeneration)
            _chat.CompleteRetriedAssistantMessage(streamContext.ConversationId, streamContext.AssistantMessage);
        else
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
        else if (streamContext.GenerateTitleOnCompletion
                 && streamContext.CompletedSuccessfully
                 && streamContext.TryBeginTitleGeneration())
            _ = GenerateLocalConversationTitleAsync(streamContext);
    }

    private async Task GenerateLocalConversationTitleAsync(BackgroundStreamTask streamContext)
    {
        if (LocalConversationTitleAsync is null) return;

        try
        {
            var title = await LocalConversationTitleAsync(
                streamContext.ConversationId,
                streamContext.ProviderId,
                streamContext.ModelId,
                CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(title))
                _chat.ApplyExternalConversationTitle(streamContext.ConversationId, title);
        }
        catch
        {
            // Automatic titles are best-effort and must never interrupt chat.
        }
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
        OnPropertyChanged(nameof(ReasoningControlLabel));
    }

    partial void OnThinkingBudgetTokensChanged(int value)
    {
        OnPropertyChanged(nameof(ReasoningControlLabel));
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

        var kind = EffectiveThinkingKind;

        return kind == MolaGPT.Core.Models.ThinkingParamKind.None ? null : kind;
    }

    private MolaGPT.Core.Models.ThinkingParamKind EffectiveThinkingKind =>
        ActiveThinkingKind != MolaGPT.Core.Models.ThinkingParamKind.None
            ? ActiveThinkingKind
            : _chat.ActiveModel?.ThinkingConfig?.Kind
              ?? MolaGPT.Core.Models.ThinkingParamKindInference.InferFromModelId(_chat.ActiveModel?.Id);

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
