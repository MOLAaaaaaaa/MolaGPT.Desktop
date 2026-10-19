using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Attachments;
using MolaGPT.Core.Chat.Tasks;
using MolaGPT.Core.Models;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.ViewModels;

public enum RemoteSendStatus
{
    /// <summary>A new turn started with the message.</summary>
    Started,

    /// <summary>The conversation was already answering; the message went into that turn.</summary>
    Steered
}

/// <param name="MessageId">The user message the turn answers, when the turn wrote one.</param>
/// <param name="Completion">Ends with the turn this message started or joined.</param>
public sealed record RemoteSendOutcome(RemoteSendStatus Status, string? MessageId, Task Completion);

/// <summary>A message from a phone that the desktop will not run, and why.</summary>
public sealed class RemoteSendRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Turns asked for from a phone.
///
/// A phone can talk to any conversation, not only the one on screen, so the request
/// is built from that conversation's own model, persona, prompt and switches. While
/// it is being built, <see cref="_subject"/> points the composer's request-building
/// members at the stored conversation; with it unset they read the screen as always.
/// </summary>
public sealed partial class ComposerViewModel
{
    /// <summary>Set only for the duration of building a request for a conversation
    /// that is not on screen. Everything here runs on the UI thread, so no binding
    /// reads the composer while it is set.</summary>
    private StoredConversation? _subject;

    private IChatProvider? SubjectProvider => _subject is { } s ? s.Provider : _chat.ActiveProvider;
    private ProviderModel? SubjectModel => _subject is { } s ? s.Model : _chat.ActiveModel;
    private PersonaItemViewModel? SubjectPersona => _subject is { } s ? s.Persona : _chat.ActivePersona;
    private bool SubjectAtmosphere => _subject?.IsAtmosphereMode ?? _chat.IsAtmosphereMode;
    private string? SubjectConversationId => _subject is { } s ? s.Row.Id : _chat.ConversationId;
    private string? SubjectConversationSystemPrompt => _subject is { } s ? s.Row.SystemPrompt : _chat.ConversationSystemPrompt;
    private string? SubjectSystemPromptMode => _subject is { } s ? s.SystemPromptMode : _chat.SystemPromptMode;
    private ConversationRoleContext SubjectRoleContext => _subject?.RoleContext ?? _chat.RoleContext;

    /// <summary>The 网络访问 switch as the conversation last left it, the way the
    /// composer restores it on opening the conversation.</summary>
    private bool SubjectNetwork => _subject is { } s
        ? Merged(s.RoleContext.EnableNetwork, s.RoleContext.EnableWebFetch)
          ?? Merged(s.Persona?.DefaultEnableNetwork, s.Persona?.DefaultEnableWebFetch)
          ?? true
        : EnableNetwork;

    /// <summary>Whether <paramref name="conversationId"/> has a turn running, on
    /// screen or in the background.</summary>
    public bool IsConversationBusy(string conversationId) => StreamFor(conversationId) is not null;

    /// <summary>Stop the turn running in <paramref name="conversationId"/>, and the
    /// background work it started, as 停止 does for the conversation on screen.</summary>
    public bool StopConversation(string conversationId)
    {
        if (StreamFor(conversationId) is not { } stream) return false;
        if (ReferenceEquals(stream, _activeTask))
        {
            Stop();
            return true;
        }
        if (stream.SessionId is { } turn) _chat.Tasks?.StopTurn(conversationId, turn);
        stream.Cts.Cancel();
        return true;
    }

    /// <summary>Read the composer's request-building members for <paramref name="stored"/>
    /// rather than the screen, unless it is the conversation on screen.</summary>
    private T ForSubject<T>(StoredConversation stored, bool attached, Func<T> read)
    {
        _subject = attached ? null : stored;
        try { return read(); }
        finally { _subject = null; }
    }

    /// <summary>The attach button's rules for the subject conversation: images need
    /// vision or the vision proxy; files text cannot be pulled from need Python.</summary>
    private (bool Images, bool OpaqueFiles) SubjectAttachmentRules() =>
        (SubjectModel?.SupportsVision == true
         || _settings?.IsVisionProxyAvailableFor(SubjectProvider?.Kind, SubjectModel) == true,
         CanUseByokPythonTool);

    /// <summary>
    /// Run <paramref name="text"/> in <paramref name="conversationId"/> as the user's
    /// next message. Into the running turn when the conversation is answering;
    /// otherwise a turn of its own, on screen if the conversation is open, in the
    /// background if not. Throws <see cref="RemoteSendRefusedException"/> when the
    /// conversation cannot take a message from a phone.
    /// </summary>
    /// <param name="attachments">Files the phone uploaded, already through the same
    /// intake as the attach button.</param>
    public RemoteSendOutcome SendFromRemote(
        string conversationId, string text, IReadOnlyList<Attachment>? attachments = null) =>
        SendFromRemote(conversationId, text, attachments, firstTurn: false);

    /// <param name="firstTurn">The conversation's first message: its title is generated
    /// once the answer is in, as for a conversation started on the desktop.</param>
    private RemoteSendOutcome SendFromRemote(
        string conversationId, string text, IReadOnlyList<Attachment>? attachments, bool firstTurn)
    {
        text = text.Trim();
        attachments ??= [];
        if (text.Length == 0 && attachments.Count == 0) throw new RemoteSendRefusedException("empty", "消息不能为空");

        if (StreamFor(conversationId) is { } running)
        {
            if (attachments.Count > 0)
                throw new RemoteSendRefusedException("busy", "正在回答，附件请在回答结束后发送");
            return SteerFromRemote(running, text);
        }

        var stored = _chat.ReadStoredConversation(conversationId)
                     ?? throw new RemoteSendRefusedException("not_found", "对话不存在或模型已移除");
        var attached = _chat.ConversationId == conversationId;
        if (attached && _chat.IsConversationLoading)
            throw new RemoteSendRefusedException("busy", "正在加载对话，请稍后重试");
        // Sending from the desktop is under way but has not reached the model yet
        // (attachments still uploading): there is no turn to join.
        if (attached && IsSending)
            throw new RemoteSendRefusedException("busy", "正在发送消息，请稍后重试");
        var provider = attached ? _chat.ActiveProvider : stored.Provider;
        if (provider is not PiWorkProvider)
            throw new RemoteSendRefusedException("unsupported", "此对话不支持手机端操作");
        if (attached ? _chat.IsAtmosphereMode : stored.IsAtmosphereMode)
            throw new RemoteSendRefusedException("read_only", "角色扮演对话仅支持在电脑端继续");

        // The model may have changed since the files were uploaded.
        var copyImages = false;
        if (attachments.Count > 0)
        {
            var rules = ForSubject(stored, attached, SubjectAttachmentRules);
            if (!rules.Images && attachments.Any(a => a.Kind == AttachmentKind.Image))
                throw new RemoteSendRefusedException("unsupported", "当前模型不支持图片，请切换到支持视觉的模型");
            if (!rules.OpaqueFiles && attachments.Any(a => a.Kind == AttachmentKind.File
                    && AttachmentMime.ClassifyDocument(a.MimeType, a.FileName, a.Bytes) == AttachmentDocumentKind.Opaque))
                throw new RemoteSendRefusedException("unsupported", "此类文件需在电脑上开启 Python 工具才能处理");
            copyImages = ForSubject(stored, attached, () => CanUseByokPythonTool || CanUseByokFileTools);
        }

        var template = BuildRemoteTemplate(stored, attached);
        return StartRemoteTurn(conversationId, text, attachments, copyImages, template, stored, attached, firstTurn);
    }

    /// <summary>
    /// Start <paramref name="conversationId"/> with <paramref name="text"/> as its first
    /// message: a conversation a phone drafted, with the model, persona and switches it
    /// picked. Created only together with its first message, so a refused message
    /// leaves no empty conversation behind.
    /// </summary>
    public RemoteSendOutcome CreateFromRemote(
        string conversationId,
        string? providerId,
        string? modelId,
        string? personaId,
        bool? network,
        bool? thinking,
        string? effort,
        string text,
        IReadOnlyList<Attachment>? attachments = null)
    {
        if (_chat.ConversationExists(conversationId))
            throw new RemoteSendRefusedException("conflict", "对话已存在");
        var draft = _chat.DraftConversation(conversationId, providerId, modelId, personaId)
                    ?? throw new RemoteSendRefusedException("not_found", "模型不可用");
        if (draft.IsAtmosphereMode)
            throw new RemoteSendRefusedException("read_only", "角色扮演对话仅支持在电脑端开始");
        text = text.Trim();
        if (text.Length == 0 && attachments is not { Count: > 0 })
            throw new RemoteSendRefusedException("empty", "消息不能为空");

        var context = draft.RoleContext;
        if (network is { } on)
        {
            context.EnableNetwork = on;
            context.EnableWebFetch = on;
        }
        if (thinking is { } wanted) context.EnableThinking = wanted;
        if (!string.IsNullOrEmpty(effort) && ReasoningFor(draft.Model, context, draft.Persona).Levels.Contains(effort))
            context.ReasoningEffort = effort;

        _chat.CreateStoredConversation(draft, text.Length > 0 ? ChatViewModel.GenerateTitle(text) : "无标题对话");
        try
        {
            return SendFromRemote(conversationId, text, attachments, firstTurn: true);
        }
        catch (RemoteSendRefusedException)
        {
            _chat.DiscardStoredConversation(conversationId);
            throw;
        }
    }

    private RemoteSendOutcome SteerFromRemote(BackgroundStreamTask stream, string text)
    {
        if (stream.IsRegeneration || stream.IsContinuation)
            throw new RemoteSendRefusedException("busy", "正在重新生成，请稍后重试");
        if (TemplateProvider(stream.ConversationId) is not { } agent)
            throw new RemoteSendRefusedException("busy", "当前回答暂不支持补充消息");

        var item = new PendingInjectionViewModel(text, followUp: false);
        stream.Injections.Add(item);
        if (ReferenceEquals(stream, _activeTask)) PendingInjections.Add(item);
        // Like a message typed on the desktop: a refusal means the turn is settling,
        // and what is still waiting when it ends goes out as the next message.
        agent.TryEnqueue(stream.ConversationId, text, followUp: false);
        return new RemoteSendOutcome(RemoteSendStatus.Steered, null, stream.StreamTask);
    }

    /// <summary>The request the conversation's next turn is sent with. On screen
    /// the live state is the truth; elsewhere, what the conversation stored.</summary>
    private TurnTemplate BuildRemoteTemplate(StoredConversation stored, bool attached)
    {
        _subject = attached ? null : stored;
        try
        {
            var provider = SubjectProvider!;
            var model = SubjectModel!;
            var messages = new List<ChatMessage>();
            if (ResolveSystemPrompt() is { Length: > 0 } systemPrompt)
                messages.Add(new ChatMessage(ChatMessage.RoleSystem, systemPrompt));

            var thinking = attached
                ? (IsThinkingEnabled, IsReasoningEffortVisible ? ReasoningEffort : null,
                    IsThinkingEnabled ? ThinkingBudgetTokens : (int?)null, ResolveActiveThinkingParamKind())
                : StoredThinking(stored);

            var profile = SubjectPersona?.Profile;
            var maxTokens = profile?.MaxTokens is { } requested && model.MaxOutputTokens is > 0 and var limit
                ? Math.Min(requested, limit)
                : profile?.MaxTokens;
            var request = new ChatRequest(
                ModelId: model.Id,
                Messages: messages,
                ConversationId: stored.Row.Id,
                UseThinking: thinking.Item1,
                ReasoningEffort: thinking.Item2,
                ExtraBody: BuildExtras(),
                ThinkingBudgetTokens: thinking.Item3,
                ThinkingParamKind: thinking.Item4,
                Temperature: model.SupportsTemperature ? profile?.Temperature : null,
                TopP: model.SupportsTopP ? profile?.TopP : null,
                MaxTokens: maxTokens);

            var labels = new MessageViewModel(ChatMessage.RoleAssistant, string.Empty, DateTimeOffset.UtcNow)
            {
                ModelLabel = model.DisplayName,
                ProviderLabel = provider.DisplayName,
                PersonaId = SubjectPersona?.Id,
                PersonaName = SubjectPersona?.Name,
                PersonaAvatar = SubjectPersona?.Avatar
            };
            return new TurnTemplate(provider, model, request, labels);
        }
        finally
        {
            _subject = null;
        }
    }

    /// <summary>The reasoning settings the composer would show for a stored
    /// conversation: the same rules it applies on opening one.</summary>
    private static (bool, string?, int?, ThinkingParamKind?) StoredThinking(StoredConversation stored)
    {
        var model = stored.Model;
        var r = ReasoningFor(model, stored.RoleContext, stored.Persona);
        var effortVisible = r.Enabled && (model.SupportsReasoningEffort || r.Budget);
        ThinkingParamKind? paramKind = (!r.Toggle && !effortVisible) || r.Kind == ThinkingParamKind.None ? null : r.Kind;
        return (r.Enabled, effortVisible ? r.Effort : null,
            r.Enabled ? model.ThinkingConfig?.DefaultBudget ?? 10000 : null, paramKind);
    }

    /// <param name="Toggle">The on/off switch is offered.</param>
    /// <param name="Levels">Effort levels as the composer lists them, with the OpenAI
    /// "none" when the model has no levels of its own.</param>
    private sealed record Reasoning(
        bool Toggle, bool Enabled, bool Budget, IReadOnlyList<string> Levels, string Effort, ThinkingParamKind Kind);

    /// <summary>A conversation's reasoning as the composer derives it on opening:
    /// what the conversation stored, else the persona's default, else the model's.</summary>
    private static Reasoning ReasoningFor(ProviderModel model, ConversationRoleContext context, PersonaItemViewModel? persona)
    {
        var mandatory = model.ThinkingConfig?.Mandatory == true;
        var toggleVisible = model.SupportsThinking && !mandatory;
        var kind = model.ThinkingConfig?.Kind ?? ThinkingParamKindInference.InferFromModelId(model.Id);
        var budgetKind = kind is ThinkingParamKind.AnthropicBudget or ThinkingParamKind.GeminiBudget
            or ThinkingParamKind.QwenThinkingBudget;
        var wanted = context.EnableThinking ?? persona?.DefaultThinking ?? false;
        var enabled = mandatory || (toggleVisible ? wanted : model.SupportsReasoningEffort);

        IReadOnlyList<string> levels = ThinkingEffortLevels.Resolve(model.ThinkingConfig, kind);
        if (toggleVisible && kind == ThinkingParamKind.OpenAiReasoningEffort
            && model.ThinkingConfig?.EffortLevels is null or { Length: 0 })
            levels = ["none", .. levels];
        var effort = new[]
            {
                context.ReasoningEffort, persona?.DefaultReasoningEffort,
                model.ThinkingConfig?.DefaultEffort, "medium"
            }
            .FirstOrDefault(level => !string.IsNullOrEmpty(level) && levels.Contains(level))
            ?? levels.FirstOrDefault() ?? "medium";
        return new Reasoning(toggleVisible, enabled, budgetKind, levels, effort, kind);
    }

    private RemoteSendOutcome StartRemoteTurn(
        string conversationId,
        string text,
        IReadOnlyList<Attachment> attachments,
        bool copyImages,
        TurnTemplate template,
        StoredConversation stored,
        bool attached,
        bool firstTurn)
    {
        // Pi keeps its own copy of the conversation. When the desktop's copy was
        // edited since, or Pi has none, the turn carries the history over.
        var context = attached ? _chat.RoleContext : stored.RoleContext;
        var priorMessages = context.NeedsHistorySync
                            || (template.Provider is PiWorkProvider agent && !agent.HasSession(conversationId))
            ? _chat.ReadStoredMessages(conversationId)
            : [];
        IReadOnlyList<ChatMessage>? seed = priorMessages
            .Where(row => row.Role is ChatMessage.RoleUser or ChatMessage.RoleAssistant)
            .Select(row => new ChatMessage(row.Role, row.Content))
            .ToList();
        if (seed.Count == 0) seed = null;

        var summarizedTasks = new List<AgentTask>();
        var agentMessages = new List<AgentMessage>();
        var outgoing = AppendTaskSummary(conversationId, text, summarizedTasks, agentMessages);

        var user = _chat.AppendMessageTo(
            conversationId,
            ChatMessage.RoleUser,
            text,
            attached ? _chat.Messages.LastOrDefault(m => m.MessageId is not null)?.MessageId : null,
            BuildAttachmentChips(attachments));
        var completion = RunRemoteTurnAsync(
            conversationId, user, outgoing, attachments, copyImages, template, seed, context.HistoryRevision,
            summarizedTasks, agentMessages, attached, firstTurn);
        return new RemoteSendOutcome(RemoteSendStatus.Started, user.MessageId, completion);
    }

    private async Task RunRemoteTurnAsync(
        string conversationId,
        MessageViewModel user,
        string outgoing,
        IReadOnlyList<Attachment> attachments,
        bool copyImages,
        TurnTemplate template,
        IReadOnlyList<ChatMessage>? seed,
        string? historyRevision,
        List<AgentTask> summarizedTasks,
        List<AgentMessage> agentMessages,
        bool attached,
        bool firstTurn)
    {
        var labels = template.Labels;
        var assistantMsg = _chat.BeginAssistantMessageFor(conversationId, user, labels);
        labels.Dispose();

        var cts = new CancellationTokenSource();
        var generationId = Guid.NewGuid().ToString("N");
        var streamContext = new BackgroundStreamTask
        {
            ConversationId = conversationId,
            ConversationTitle = _chat.ConversationTitleOf(conversationId),
            ModelLabel = assistantMsg.ModelLabel,
            ModelId = template.Model.Id,
            ProviderId = template.Provider.Id,
            ProviderKind = template.Provider.Kind,
            AssistantMessage = assistantMsg,
            Cts = cts,
            StreamTask = Task.CompletedTask,
            SessionId = generationId,
            IsRemoteTurn = true,
            IsDetached = !attached,
            GenerateTitleOnCompletion = firstTurn
        };

        if (attached)
        {
            MessageSubmitted?.Invoke();
            _cts = cts;
            _activeAssistantMsg = assistantMsg;
            _activeTask = streamContext;
            _injectable = true;
            IsSending = true;
            _chat.IsStreaming = true;
            NotifyInjectState();
        }
        else
        {
            _backgroundStreams?.Register(streamContext);
        }

        var wasCancelled = false;
        string? failureMessage = null;
        try
        {
            var prepared = await PrepareRemoteAttachmentsAsync(
                conversationId, user, assistantMsg, attachments, copyImages, attached, cts.Token);
            var requestAttachments = BuildRequestAttachments(template.Provider, template.Model, prepared);

            var messages = template.Request.Messages.ToList();
            messages.Add(new ChatMessage(
                ChatMessage.RoleUser, outgoing,
                Attachments: requestAttachments.Count > 0 ? requestAttachments : null));
            var request = template.Request with
            {
                Messages = messages,
                SessionId = generationId,
                HistorySeed = seed,
                HistoryRevision = seed is null ? null : historyRevision
            };
            RememberTurnTemplate(conversationId, template.Provider, template.Model, request, assistantMsg);

            var streamTask = RunStreamLoopAsync(template.Provider, request, assistantMsg, cts, streamContext);
            streamContext.StreamTask = streamTask;
            if (attached) _activeStreamTask = streamTask;
            await streamTask;
            if (streamContext.CompletedSuccessfully)
            {
                foreach (var task in summarizedTasks) _chat.Tasks?.MarkDelivered(task);
                _chat.Tasks?.AcknowledgeParentMessages(conversationId, agentMessages.Select(m => m.Id));
            }
            _chat.MarkHistorySynchronized(conversationId, request.HistoryRevision);
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
            streamContext.AssistantMessage.WasStopped = true;
        }
        catch (Exception ex)
        {
            failureMessage = ex.Message;
            streamContext.AssistantMessage.AppendDelta($"\n\n> **错误**：{ex.Message}");
            ClassifyActionableError(streamContext.AssistantMessage, ex);
        }
        finally
        {
            _inflightTaskResults.ExceptWith(summarizedTasks);
            _inflightAgentMessages.ExceptWith(agentMessages);
            FlushPendingInjections(streamContext, wasCancelled);
            // The phone that asked for this turn is told it finished; a desktop
            // notification on top of that would announce it twice.
            CompleteStreamContext(streamContext, publishNotification: false, failureMessage);
            if (!wasCancelled) _consolidator?.NoteTurnFinished();
            if (ReferenceEquals(_activeTask, streamContext))
            {
                IsSending = false;
                _chat.IsStreaming = false;
                _activeStreamTask = null;
                _activeAssistantMsg = null;
                _activeTask = null;
                _cts = null;
                _injectable = false;
                NotifyInjectState();
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// The attachment step of a desktop send: documents have their text extracted and,
    /// with images when a tool could open them, are copied into the conversation's
    /// workspace. The bubble is re-chipped so the paths persist with the message.
    /// </summary>
    private async Task<IReadOnlyList<Attachment>> PrepareRemoteAttachmentsAsync(
        string conversationId,
        MessageViewModel user,
        MessageViewModel assistantMsg,
        IReadOnlyList<Attachment> attachments,
        bool copyImages,
        bool attached,
        CancellationToken ct)
    {
        if (!attachments.Any(a => a.Kind == AttachmentKind.File || (copyImages && a.Kind == AttachmentKind.Image)))
            return attachments;

        assistantMsg.SetPendingStatus(
            "处理附件", attachments.Any(a => a.Kind == AttachmentKind.File) ? "提取文档文本" : "准备图片");
        try
        {
            var prepared = await Task.Run(
                () => PrepareByokFileAttachments(attachments, conversationId, copyImages, ct), ct);
            user.Attachments = BuildAttachmentChips(prepared);
            _chat.UpdatePersistedMessage(user, conversationId);
            if (attached) _chat.RefreshArtifacts();
            return prepared;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // As on the desktop: the text still goes, and the reply says what was lost.
            assistantMsg.AppendDelta($"\n\n> **附件处理失败**：{ex.Message}（本轮仅发送文字）");
            assistantMsg.FlushPendingDelta();
            return attachments.Where(a => a.Kind != AttachmentKind.File).ToList();
        }
    }

    // ---- settings a phone can change -------------------------------------------

    /// <summary>
    /// What a phone may adjust in <paramref name="conversationId"/>: its model, 网络访问
    /// and reasoning, as the desktop composer would offer them. Null when the conversation
    /// cannot be continued from a phone.
    /// </summary>
    public RemoteConversationSettings? DescribeRemoteSettings(string conversationId) =>
        _chat.ReadStoredConversation(conversationId) is { } stored
            ? DescribeStored(stored, attached: _chat.ConversationId == conversationId)
            : null;

    /// <summary>
    /// The settings a conversation a phone is drafting would start with: the model and
    /// persona it picked, and the switches as the composer sets them for a new
    /// conversation. Null when the model is not available here or the persona is a
    /// role-play one.
    /// </summary>
    public RemoteConversationSettings? DescribeNewRemoteConversation(string? providerId, string? modelId, string? personaId) =>
        _chat.DraftConversation(string.Empty, providerId, modelId, personaId) is { IsAtmosphereMode: false } draft
            ? DescribeStored(draft, attached: false)
            : null;

    private RemoteConversationSettings? DescribeStored(StoredConversation stored, bool attached)
    {
        var provider = attached ? _chat.ActiveProvider : stored.Provider;
        var model = attached ? _chat.ActiveModel : stored.Model;
        if (provider is not PiWorkProvider || model is null) return null;
        if (attached ? _chat.IsAtmosphereMode : stored.IsAtmosphereMode) return null;
        var persona = attached ? _chat.ActivePersona : stored.Persona;
        var context = attached ? _chat.RoleContext : stored.RoleContext;

        var r = ReasoningFor(model, context, persona);
        // Budget models are adjusted with a slider on the desktop; a phone gets the switch only.
        IReadOnlyList<string> levels = model.SupportsReasoningEffort && !r.Budget
            ? r.Levels.Where(level => level != "none").ToList()
            : [];
        var network = Merged(context.EnableNetwork, context.EnableWebFetch)
                      ?? Merged(persona?.DefaultEnableNetwork, persona?.DefaultEnableWebFetch)
                      ?? true;
        var rules = ForSubject(stored, attached, SubjectAttachmentRules);
        return new RemoteConversationSettings(
            provider, model,
            NetworkAvailable: model.SupportsToolCalling,
            NetworkOn: network,
            ThinkingToggle: r.Toggle,
            ThinkingOn: r.Enabled,
            EffortLevels: levels,
            Effort: levels.Contains(r.Effort) ? r.Effort : levels.FirstOrDefault(),
            AcceptsImages: rules.Images,
            AcceptsOpaqueFiles: rules.OpaqueFiles);
    }

    /// <summary>Models a phone may switch a Work or BYOK conversation to.</summary>
    public IReadOnlyList<(IChatProvider Provider, ProviderModel Model)> RemoteModelChoices() =>
        _chat.AgentModels().Where(choice => choice.Provider is PiWorkProvider).ToList();

    /// <summary>
    /// Apply a phone's change to <paramref name="conversationId"/>. On screen the change
    /// goes through the composer, so the desktop shows it at once; otherwise it is written
    /// to the stored conversation. A null argument leaves that setting as it is.
    /// </summary>
    public void ConfigureFromRemote(
        string conversationId, string? providerId, string? modelId, bool? network, bool? thinking, string? effort)
    {
        if (StreamFor(conversationId) is not null)
            throw new RemoteSendRefusedException("busy", "正在回答，请稍后再调整");
        var stored = _chat.ReadStoredConversation(conversationId)
                     ?? throw new RemoteSendRefusedException("not_found", "对话不存在或模型已移除");
        var attached = _chat.ConversationId == conversationId;
        if (attached && (_chat.IsConversationLoading || IsSending))
            throw new RemoteSendRefusedException("busy", "正在发送消息，请稍后重试");
        if ((attached ? _chat.ActiveProvider : stored.Provider) is not PiWorkProvider)
            throw new RemoteSendRefusedException("unsupported", "此对话不支持手机端操作");
        if (attached ? _chat.IsAtmosphereMode : stored.IsAtmosphereMode)
            throw new RemoteSendRefusedException("read_only", "角色扮演对话仅支持在电脑端继续");

        var target = (Provider: stored.Provider, Model: stored.Model);
        var switching = providerId is not null && modelId is not null;
        if (switching)
        {
            target = RemoteModelChoices().FirstOrDefault(c => c.Provider.Id == providerId && c.Model.Id == modelId);
            if (target.Provider is null) throw new RemoteSendRefusedException("not_found", "模型不可用");
        }

        if (attached)
        {
            if (switching)
            {
                if (!_chat.SetActiveByIds(providerId, modelId))
                    throw new RemoteSendRefusedException("unsupported", "此对话不能切换到该模型");
                _chat.SaveActiveModel();
            }
            if (network is { } on) EnableNetwork = on;
            if (thinking is { } wanted && IsThinkingVisible) EnableThinking = wanted;
            if (!string.IsNullOrEmpty(effort) && AvailableEffortLevels.Contains(effort)) ReasoningEffort = effort;
            return;
        }

        var context = stored.RoleContext;
        if (network is { } enable)
        {
            context.EnableNetwork = enable;
            context.EnableWebFetch = enable;
        }
        if (thinking is { } turnOn) context.EnableThinking = turnOn;
        if (!string.IsNullOrEmpty(effort) && ReasoningFor(target.Model, context, stored.Persona).Levels.Contains(effort))
            context.ReasoningEffort = effort;
        _chat.SaveStoredSettings(conversationId, target.Provider.Id, target.Model.Id, context);
    }
}

/// <summary>A conversation's adjustable settings as a phone sees them.</summary>
/// <param name="ThinkingToggle">Reasoning can be switched off; false for models that
/// always reason.</param>
/// <param name="EffortLevels">Empty when the phone offers the switch only.</param>
/// <param name="AcceptsImages">The model sees images, itself or through the vision proxy.</param>
/// <param name="AcceptsOpaqueFiles">Files no text can be pulled from are accepted, for
/// the Python tool. Text, PDF and Office documents always are.</param>
public sealed record RemoteConversationSettings(
    IChatProvider Provider,
    ProviderModel Model,
    bool NetworkAvailable,
    bool NetworkOn,
    bool ThinkingToggle,
    bool ThinkingOn,
    IReadOnlyList<string> EffortLevels,
    string? Effort,
    bool AcceptsImages,
    bool AcceptsOpaqueFiles);
