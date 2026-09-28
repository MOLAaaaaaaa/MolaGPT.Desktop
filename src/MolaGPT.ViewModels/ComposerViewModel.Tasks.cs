using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Tasks;
using MolaGPT.Core.Models;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.ViewModels;

/// <summary>A message typed while the turn runs, waiting for the agent to take it in.</summary>
public sealed class PendingInjectionViewModel
{
    public PendingInjectionViewModel(string text, bool followUp)
    {
        Text = text;
        IsFollowUp = followUp;
    }

    public string Text { get; }
    public bool IsFollowUp { get; }
    public string ModeLabel => IsFollowUp ? "排队" : "插队";

    public string Preview
    {
        get
        {
            var line = Text.Replace("\r", "").Replace('\n', ' ').Trim();
            return line.Length <= 80 ? line : line[..80] + "…";
        }
    }
}

/// <summary>
/// Messages into a running turn, and background tasks reporting back.
///
/// Both land in the transcript the same way: Pi takes in a user-role message mid-run,
/// and the reply splits there — the part before is one answer, the message is a turn
/// of its own, and what follows answers it. Keeping the transcript and Pi's history
/// in step this way is what lets retry, branching and history rebuilds keep working.
/// </summary>
public sealed partial class ComposerViewModel
{
    public ObservableCollection<PendingInjectionViewModel> PendingInjections { get; } = new();
    public bool HasPendingInjections => PendingInjections.Count > 0;

    /// <summary>Whether the stream on screen accepts messages mid-turn: a fresh
    /// answer (not a retry or 续写, whose bubble is being rewritten), in a Work chat
    /// that is not 氛围模式.</summary>
    private bool _injectable;

    private bool CanInject =>
        IsSending
        && _injectable
        && _activeTask is not null
        && _chat.ActiveProvider is PiWorkProvider
        && !_chat.IsAtmosphereMode
        && Attachments.Count == 0
        && ArtifactReferences.Count == 0;

    /// <summary>Show the running-turn send action beside 停止.</summary>
    public bool IsInjectAvailable => CanInject;
    public bool QueueDuringTaskByDefault => _settings?.RunningTaskSendModeIndex == 1;
    public string RunningSendHint => EnterToSend
        ? QueueDuringTaskByDefault ? "Enter 排队 · Alt+Enter 插队" : "Enter 插队 · Alt+Enter 排队"
        : QueueDuringTaskByDefault ? "Ctrl+Enter 排队 · Alt+Enter 插队" : "Ctrl+Enter 插队 · Alt+Enter 排队";
    public string DefaultInjectionTooltip => QueueDuringTaskByDefault
        ? "排队：本轮结束后送达" : "插队：当前步骤完成后送达";

    private void NotifyInjectState()
    {
        OnPropertyChanged(nameof(IsInjectAvailable));
        SendCommand.NotifyCanExecuteChanged();
        SendDuringTaskCommand.NotifyCanExecuteChanged();
        SteerCommand.NotifyCanExecuteChanged();
        QueueCommand.NotifyCanExecuteChanged();
    }

    private bool CanInjectText() => CanInject && !string.IsNullOrWhiteSpace(Text);

    /// <summary>Deliver after the current tool calls, before the next model call.</summary>
    [RelayCommand(CanExecute = nameof(CanInjectText))]
    public void Steer() => Inject(followUp: false);

    /// <summary>Deliver once the agent would otherwise stop.</summary>
    [RelayCommand(CanExecute = nameof(CanInjectText))]
    public void Queue() => Inject(followUp: true);

    [RelayCommand(CanExecute = nameof(CanInjectText))]
    public void SendDuringTask() => Inject(followUp: QueueDuringTaskByDefault);

    [RelayCommand]
    private void WithdrawInjection(PendingInjectionViewModel? item)
    {
        if (item is null || _activeTask is not { } stream) return;
        if (TemplateProvider(stream.ConversationId) is not { } agent) return;

        // Pi's queue has no "remove one": empty it and put the rest back, in order.
        var keep = PendingInjections.Where(p => !ReferenceEquals(p, item)).ToList();
        if (!agent.TryClearQueue(stream.ConversationId)) return;
        foreach (var pending in keep)
            agent.TryEnqueue(stream.ConversationId, pending.Text, pending.IsFollowUp);
        // Internal messages may already have left Pi's queue. Keep their receipt
        // tracking; any not consumed are offered again after this turn ends.
        PendingInjections.Remove(item);
        Text = string.IsNullOrWhiteSpace(Text) ? item.Text : item.Text + "\n\n" + Text;
        FocusRequested?.Invoke();
    }

    private void Inject(bool followUp)
    {
        var text = Text.Trim();
        if (text.Length == 0 || !CanInject || _activeTask is not { } stream) return;
        // The provider running this turn, which the picker may no longer show.
        if (TemplateProvider(stream.ConversationId) is not { } agent) return;

        Text = string.Empty;
        PendingInjections.Add(new PendingInjectionViewModel(text, followUp));
        // A refusal means the turn is settling. The chip stays, and whatever is
        // still waiting when the stream ends is sent as the next message.
        agent.TryEnqueue(stream.ConversationId, text, followUp);
    }

    /// <summary>Mirror Pi's queue. Notifications ride the same queue but are not the
    /// user's, so they get no chip.</summary>
    private void ApplyQueueState(BackgroundStreamTask stream, QueueStateDelta queue)
    {
        if (!ReferenceEquals(stream, _activeTask)) return;
        PendingInjections.Clear();
        foreach (var text in queue.Steering.Where(IsUserText))
            PendingInjections.Add(new PendingInjectionViewModel(text, followUp: false));
        foreach (var text in queue.FollowUp.Where(IsUserText))
            PendingInjections.Add(new PendingInjectionViewModel(text, followUp: true));
    }

    private static bool IsUserText(string text) =>
        !text.TrimStart().StartsWith(TaskTools.NotificationOpenTag, StringComparison.Ordinal)
        && !text.TrimStart().StartsWith("<agent-message>", StringComparison.Ordinal);

    /// <summary>
    /// The agent took in a queued message: close the reply so far, write the message
    /// as a turn of its own, and carry on in a new reply to it.
    /// </summary>
    private MessageViewModel SplitAtInjection(BackgroundStreamTask stream, MessageViewModel current, string text)
    {
        // A rewritten bubble cannot be split without losing its versions; nothing
        // is queued into those turns, so this only guards against the unexpected.
        if (stream.IsRegeneration || stream.IsContinuation) return current;

        var conversationId = stream.ConversationId;
        current.FlushPendingDelta();
        RewritePythonArtifactMarkdownLinks(current);
        _chat.FinalizeAssistantMessage(conversationId, current);

        var injected = _chat.AppendMessageTo(conversationId, ChatMessage.RoleUser, text, current.MessageId);
        if (!IsUserText(text) && _chat.Tasks is { } tasks)
        {
            var received = _queuedAgentMessages
                .Where(m => m.ConversationId == conversationId
                            && text.Contains($"<message-id>{m.Id}</message-id>", StringComparison.Ordinal))
                .ToList();
            tasks.AcknowledgeParentMessages(conversationId, received.Select(m => m.Id));
            _queuedAgentMessages.ExceptWith(received);
            foreach (var task in _queuedTaskNotices.Where(t => t.ConversationId == conversationId).ToList())
            {
                if (!text.Contains($"<task-id>{task.Id}</task-id>", StringComparison.Ordinal)) continue;
                tasks.MarkDelivered(task);
                _queuedTaskNotices.Remove(task);
            }
        }
        var next = _chat.BeginAssistantMessageFor(conversationId, injected, current);
        next.MarkRequestStarted();

        stream.AssistantMessage = next;
        stream.RunningTools.Clear();
        if (ReferenceEquals(_activeTask, stream))
        {
            _activeAssistantMsg = next;
            var match = PendingInjections.FirstOrDefault(p => p.Text == text.Trim());
            if (match is not null) PendingInjections.Remove(match);
            MessageSubmitted?.Invoke();
        }
        return next;
    }

    private static void TrackTool(BackgroundStreamTask stream, ToolCallDelta tool)
    {
        if (tool.Status is "preparing" or "running") stream.RunningTools.Add(tool.Id);
        else stream.RunningTools.Remove(tool.Id);
    }

    /// <summary>
    /// What was typed but never taken in, once the turn is over: back into the input
    /// box when the user stopped the turn, sent as the next message otherwise.
    /// </summary>
    private void FlushPendingInjections(BackgroundStreamTask stream, bool cancelled)
    {
        if (!ReferenceEquals(stream, _activeTask) || PendingInjections.Count == 0) return;
        var leftover = string.Join("\n\n", PendingInjections.Select(p => p.Text));
        PendingInjections.Clear();

        if (cancelled)
        {
            Text = string.IsNullOrWhiteSpace(Text) ? leftover : leftover + "\n\n" + Text;
            return;
        }
        _ = SendLeftoverAsync(leftover);
    }

    private async Task SendLeftoverAsync(string text)
    {
        // Let the finishing turn clear its sending state first.
        await Task.Yield();
        if (IsSending)
        {
            Text = string.IsNullOrWhiteSpace(Text) ? text : text + "\n\n" + Text;
            return;
        }
        var draft = Text;
        Text = text;
        // SendAsync reads and clears the input before its first await, so the draft
        // can go back straight away.
        var sending = SendAsync();
        Text = draft;
        await sending;
    }

    // ---- background tasks ----------------------------------------------------------

    /// <summary>The request a conversation's last turn was sent with. A wake-up turn
    /// reuses it — same system prompt, same tools, same model — so it reads the
    /// cached prefix, and so it does not depend on which conversation is on screen.</summary>
    private sealed record TurnTemplate(
        IChatProvider Provider,
        ProviderModel Model,
        ChatRequest Request,
        MessageViewModel Labels);

    private readonly Dictionary<string, TurnTemplate> _turnTemplates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deliveryScheduled = new(StringComparer.Ordinal);
    private readonly HashSet<AgentTask> _queuedTaskNotices = [];
    private readonly HashSet<AgentTask> _inflightTaskResults = [];
    private readonly HashSet<AgentMessage> _queuedAgentMessages = [];
    private readonly HashSet<AgentMessage> _inflightAgentMessages = [];
    private readonly HashSet<string> _deliverAfterStream = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _wakeLog = new(StringComparer.Ordinal);

    /// <summary>A model that keeps starting tasks from its own wake-up turns would
    /// never stop on its own. Past this, results wait for the user's next message.</summary>
    private const int MaxWakesPerHour = 6;

    private void RememberTurnTemplate(string conversationId, IChatProvider provider, ProviderModel model, ChatRequest request, MessageViewModel labels)
    {
        if (provider is not PiWorkProvider) return;
        // Only what a wake-up turn reuses: the whole request would keep every
        // attachment of the conversation's history alive for the session.
        var slim = request with
        {
            Messages = request.Messages.Where(m => m.Role == ChatMessage.RoleSystem).ToList(),
            HistorySeed = null
        };
        _turnTemplates[conversationId] = new TurnTemplate(provider, model, slim, labels);
    }

    private PiWorkProvider? TemplateProvider(string conversationId) =>
        _turnTemplates.TryGetValue(conversationId, out var template)
            ? template.Provider as PiWorkProvider
            : _chat.ActiveProvider as PiWorkProvider;

    /// <summary>
    /// Tasks the model has not heard the end of, appended to the message the user is
    /// sending: those still running, and results that did not wake it. At the end of
    /// the message, so the prefix cache is untouched.
    /// </summary>
    private string AppendTaskSummary(
        string conversationId, string text, List<AgentTask> summarizedTasks, List<AgentMessage> agentMessages)
    {
        if (_chat.Tasks is not { } tasks) return text;
        var running = tasks.List(conversationId).Where(t => t.IsRunning).ToList();
        var undelivered = tasks.Undelivered(conversationId);
        summarizedTasks.AddRange(undelivered);
        _inflightTaskResults.UnionWith(undelivered);
        if (TaskTools.BuildTurnSummary(running, undelivered) is { } summary)
            text = AppendHiddenSystemHint(text, summary);
        agentMessages.AddRange(PendingAgentMessages(conversationId));
        _inflightAgentMessages.UnionWith(agentMessages);
        return agentMessages.Count == 0 ? text : AppendHiddenSystemHint(text, FormatAgentMessages(agentMessages));
    }

    private IReadOnlyList<AgentMessage> PendingAgentMessages(string conversationId) =>
        _chat.Tasks?.PeekParentMessages(conversationId)
            .Where(m => !_queuedAgentMessages.Contains(m) && !_inflightAgentMessages.Contains(m))
            .ToList() ?? [];

    private static string FormatAgentMessages(IEnumerable<AgentMessage> messages) =>
        string.Join("\n\n", messages.Select(m =>
            $"<agent-message>\n<message-id>{m.Id}</message-id>\n"
            + $"<agent-id>{System.Security.SecurityElement.Escape(m.AgentId)}</agent-id>\n"
            + $"<text>{System.Security.SecurityElement.Escape(m.Text)}</text>\n</agent-message>"));

    private void OnAgentMessageQueued(string conversationId)
    {
        var stream = StreamFor(conversationId);
        if (stream is null || stream.StreamTask.IsCompleted || stream.IsRegeneration || stream.IsContinuation)
            return;
        var messages = PendingAgentMessages(conversationId);
        if (messages.Count > 0 && TemplateProvider(conversationId) is { } agent
            && agent.TryEnqueue(conversationId, FormatAgentMessages(messages), followUp: false))
            _queuedAgentMessages.UnionWith(messages);
    }

    private void OnTaskChanged(AgentTask task)
    {
        if (task.IsRunning || task.Delivered) return;
        // A stopped task never wakes the model; whoever stopped it knows.
        if (task.Status is not (AgentTaskStatus.Completed or AgentTaskStatus.Failed)) return;
        if (_settings?.BackgroundTaskWakeEnabled == false) return;
        ScheduleDelivery(task.ConversationId, TimeSpan.FromSeconds(1.5));
    }

    /// <summary>Tasks that end together are told about together: a short wait
    /// collects them into one notification.</summary>
    private void ScheduleDelivery(string conversationId, TimeSpan delay)
    {
        if (!_deliveryScheduled.Add(conversationId)) return;
        _ = DeliverLaterAsync(conversationId, delay);
    }

    private async Task DeliverLaterAsync(string conversationId, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            _deliveryScheduled.Remove(conversationId);
            await DeliverAsync(conversationId);
        }
        catch (Exception)
        {
            // Delivery is best effort; an undelivered result rides the next message.
            _deliveryScheduled.Remove(conversationId);
        }
    }

    /// <summary>
    /// Tell the model its tasks ended.
    ///
    /// Into the running turn when it is between tool calls — it reads the result
    /// before its next step. Not into a turn already writing its answer: that answer
    /// is on screen, and appending to it would blur which turn said what, so the
    /// notification waits for the turn to end. With nothing running, a turn of its
    /// own wakes the model.
    /// </summary>
    private async Task DeliverAsync(string conversationId)
    {
        if (_chat.Tasks is not { } tasks) return;
        var ended = tasks.Undelivered(conversationId)
            .Where(t => (t.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed)
                        && !_queuedTaskNotices.Contains(t)
                        && !_inflightTaskResults.Contains(t))
            .ToList();
        if (ended.Count == 0) return;
        var running = tasks.List(conversationId).Where(t => t.IsRunning).ToList();
        var notification = TaskTools.BuildTurnSummary(running, ended)!;

        var stream = StreamFor(conversationId);
        if (stream is not null && !stream.StreamTask.IsCompleted)
        {
            if (!stream.IsRegeneration && !stream.IsContinuation && stream.RunningTools.Count > 0
                && TemplateProvider(conversationId) is { } agent
                && agent.TryEnqueue(conversationId, notification, followUp: false))
            {
                foreach (var task in ended) _queuedTaskNotices.Add(task);
                return;
            }
            _deliverAfterStream.Add(conversationId);
            return;
        }

        if (!_turnTemplates.TryGetValue(conversationId, out var template)) return;
        if (!_chat.ConversationIsLive(conversationId)) return;
        if (_chat.ConversationId == conversationId && (IsSending || _chat.RoleContext.NeedsHistorySync)) return;
        if (!TryConsumeWakeBudget(conversationId)) return;

        await RunWakeTurnAsync(conversationId, notification, template, ended);
    }

    private BackgroundStreamTask? StreamFor(string conversationId)
    {
        if (_activeTask is { } active && active.ConversationId == conversationId) return active;
        return _backgroundStreams?.GetTask(conversationId) is { IsCompleted: false } background ? background : null;
    }

    private bool TryConsumeWakeBudget(string conversationId)
    {
        var now = DateTimeOffset.Now;
        if (!_wakeLog.TryGetValue(conversationId, out var log))
            _wakeLog[conversationId] = log = new List<DateTimeOffset>();
        log.RemoveAll(time => now - time > TimeSpan.FromHours(1));
        if (log.Count >= MaxWakesPerHour) return false;
        log.Add(now);
        return true;
    }

    /// <summary>A turn ended; if it held back a notification, deliver it now.</summary>
    private void OnStreamCompletedForTasks(BackgroundStreamTask stream)
    {
        var conversationId = stream.ConversationId;
        _queuedTaskNotices.RemoveWhere(t => t.ConversationId == conversationId);
        _queuedAgentMessages.RemoveWhere(m => m.ConversationId == conversationId);
        var held = _deliverAfterStream.Remove(conversationId);
        if (stream.Cts.IsCancellationRequested || (stream.IsWakeTurn && !stream.CompletedSuccessfully)) return;
        var waiting = _settings?.BackgroundTaskWakeEnabled != false
                      && _chat.Tasks?.Undelivered(conversationId)
                          .Any(t => t.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed) == true;
        if (held || waiting) ScheduleDelivery(conversationId, TimeSpan.FromSeconds(0.8));
    }

    /// <summary>
    /// Run one turn whose prompt is the notification. On screen it streams like any
    /// answer; elsewhere it streams in the background, and its completion reaches the
    /// user through the same path every background answer does.
    /// </summary>
    private async Task RunWakeTurnAsync(
        string conversationId,
        string notification,
        TurnTemplate template,
        IReadOnlyList<AgentTask> deliveredTasks)
    {
        var attached = _chat.ConversationId == conversationId;
        if (attached && IsSending) return;

        var agentMessages = PendingAgentMessages(conversationId);
        if (agentMessages.Count > 0)
            notification = AppendHiddenSystemHint(notification, FormatAgentMessages(agentMessages));
        _inflightAgentMessages.UnionWith(agentMessages);

        var notice = _chat.AppendMessageTo(
            conversationId,
            ChatMessage.RoleUser,
            notification,
            attached ? _chat.Messages.LastOrDefault(m => m.MessageId is not null)?.MessageId : null);
        var assistantMsg = _chat.BeginAssistantMessageFor(conversationId, notice, template.Labels);

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
            IsWakeTurn = true,
            IsDetached = !attached
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

        var messages = template.Request.Messages
            .Where(m => m.Role == ChatMessage.RoleSystem)
            .ToList();
        messages.Add(new ChatMessage(ChatMessage.RoleUser, notification));
        var request = template.Request with
        {
            Messages = messages,
            ConversationId = conversationId,
            SessionId = generationId,
            HistorySeed = null,
            HistoryRevision = null,
            RolePrompt = null
        };
        _turnTemplates[conversationId] = template with { Request = request };

        _inflightTaskResults.UnionWith(deliveredTasks);
        var wasCancelled = false;
        string? failureMessage = null;
        try
        {
            var streamTask = RunStreamLoopAsync(template.Provider, request, assistantMsg, cts, streamContext);
            streamContext.StreamTask = streamTask;
            if (attached) _activeStreamTask = streamTask;
            await streamTask;
            if (streamContext.CompletedSuccessfully)
            {
                foreach (var task in deliveredTasks) _chat.Tasks?.MarkDelivered(task);
                _chat.Tasks?.AcknowledgeParentMessages(conversationId, agentMessages.Select(m => m.Id));
            }
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
        }
        finally
        {
            _inflightTaskResults.ExceptWith(deliveredTasks);
            _inflightAgentMessages.ExceptWith(agentMessages);
            FlushPendingInjections(streamContext, wasCancelled);
            CompleteStreamContext(streamContext, publishNotification: !wasCancelled, failureMessage);
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
}
