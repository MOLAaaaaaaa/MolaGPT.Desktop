using System.Collections.Concurrent;
using System.Text;
using MolaGPT.Core.Chat;

namespace MolaGPT.Core.Chat.Tasks;

public enum AgentTaskStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}

/// <summary>Who started a task: the conversation it belongs to, and the turn (the
/// request's <c>SessionId</c>) that launched it, so stopping that turn can stop it.</summary>
public sealed record AgentTaskOwner(string ConversationId, string? TurnId);

/// <summary>What a finished task hands back: whether it succeeded, and the report the
/// model receives in its notification.</summary>
public sealed record AgentTaskOutcome(bool Success, string Report);

public sealed record SubagentRuntimeOptions(
    string? ProviderId = null,
    string? ModelId = null,
    int MaxPerConversation = 4);

public sealed record AgentMessage(string Id, string ConversationId, string AgentId, string Text);

public sealed record AgentActivity(string Id, string Kind, string Label, string Status,
    string Text, IReadOnlyList<ToolCallDelta>? ToolDeltas, long Revision, bool IsDelta = false);

internal sealed class AgentActivityState(string id, string kind, string label, string status)
{
    private readonly StringBuilder _text = new();

    public string Id { get; } = id;
    public string Kind { get; } = kind;
    public string Label { get; set; } = label;
    public string Status { get; set; } = status;
    public List<ToolCallDelta> ToolDeltas { get; } = [];
    public long Revision { get; private set; }

    public void Append(string text)
    {
        _text.Append(text);
        Revision++;
    }

    public void Update() => Revision++;

    public AgentActivity Snapshot() => new(Id, Kind, Label, Status, _text.ToString(),
        ToolDeltas.Count > 0 ? ToolDeltas.ToArray() : null, Revision);
}

public sealed class AgentHandle(string id, string conversationId, string label, string providerId, string modelId)
{
    public string Id { get; } = id;
    public string ConversationId { get; } = conversationId;
    public string Label { get; } = label;
    public string ProviderId { get; } = providerId;
    public string ModelId { get; } = modelId;
    public DateTimeOffset RegisteredAt { get; internal set; } = DateTimeOffset.Now;
    public string? LatestTaskId { get; internal set; }
    public string RecentOutput { get; internal set; } = string.Empty;
    public long Version { get; internal set; }
    internal List<AgentActivityState> Activities { get; } = [];
}

/// <summary>
/// One piece of work running outside the turn that started it — a Python run sent
/// to the background, or a sub-agent.
///
/// Mutable state is written by the registry only; everyone else reads.
/// </summary>
public sealed class AgentTask
{
    internal AgentTask(string id, AgentTaskOwner owner, string kind, string label, Func<string>? tail, TimeSpan alreadyRunning, string? agentId)
    {
        Id = id;
        ConversationId = owner.ConversationId;
        TurnId = owner.TurnId;
        Kind = kind;
        Label = label;
        AgentId = agentId;
        StartedAt = DateTimeOffset.Now - alreadyRunning;
        _tail = tail;
    }

    private readonly Func<string>? _tail;
    internal readonly CancellationTokenSource Cts = new();

    public string Id { get; }
    public string ConversationId { get; }
    public string? TurnId { get; }

    /// <summary><see cref="AgentTaskKinds"/>.</summary>
    public string Kind { get; }
    public string Label { get; }
    public string? AgentId { get; }
    public DateTimeOffset StartedAt { get; internal set; }

    public AgentTaskStatus Status { get; internal set; } = AgentTaskStatus.Running;
    public DateTimeOffset? EndedAt { get; internal set; }

    /// <summary>The report the model is given when it hears about the task. Set when
    /// the task ends.</summary>
    public string? Report { get; internal set; }
    public bool IsRestored { get; internal set; }

    /// <summary>Whether the end of this task has reached the model — as a
    /// notification, or in a later turn's summary.</summary>
    public bool Delivered { get; internal set; }

    public bool IsRunning => Status == AgentTaskStatus.Running;

    public TimeSpan Elapsed => (EndedAt ?? DateTimeOffset.Now) - StartedAt;

    /// <summary>The most recent output, for status queries while the task runs.</summary>
    public string Tail() => _tail?.Invoke() ?? string.Empty;
}

public static class AgentTaskKinds
{
    public const string Python = "python";
    public const string Agent = "agent";
}

/// <summary>
/// Every background task in the app, keyed by conversation.
///
/// Tasks are not bound to the turn or the Pi sidecar lease that started them: a
/// lease ends with its turn, and the work it launched is meant to outlive it.
/// Running tasks do not survive an app restart; sub-agent handles restored from
/// conversation history are terminal snapshots, never live work.
/// </summary>
public sealed class AgentTaskRegistry
{
    public const int MaxRunningPerConversation = 4;

    private readonly ConcurrentDictionary<string, AgentTask> _tasks = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly HashSet<AgentTaskReservation> _reservations = [];
    private readonly HashSet<string> _agentReservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentHandle> _agents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AgentMessage>> _parentMessages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AgentMessage>> _agentMessages = new(StringComparer.Ordinal);
    private readonly Func<SubagentRuntimeOptions> _options;
    private TaskCompletionSource _changeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AgentTaskRegistry(Func<SubagentRuntimeOptions>? options = null) =>
        _options = options ?? (() => new SubagentRuntimeOptions());

    public event Action<string>? ParentMessageQueued;
    public event Action<string>? AgentMessageQueued;
    public event Action<string, string, AgentActivity>? AgentActivityChanged;

    /// <summary>Raised when a task starts and when it ends, on whichever thread did
    /// it. Subscribers marshal to their own thread.</summary>
    public event Action<AgentTask>? Changed;

    /// <summary>Why a new task cannot start right now, or null when it can.</summary>
    public string? CheckCapacity(string conversationId, string kind)
    {
        lock (_gate) return CapacityErrorLocked(conversationId, kind);
    }

    /// <summary>
    /// Reserve capacity before starting work. The reservation counts against the
    /// same limits as a running task and can later be transferred to the task.
    /// </summary>
    public bool TryReserve(
        AgentTaskOwner owner,
        string kind,
        out AgentTaskReservation? reservation,
        out string? error)
    {
        lock (_gate)
        {
            error = CapacityErrorLocked(owner.ConversationId, kind);
            if (error is not null)
            {
                reservation = null;
                return false;
            }

            reservation = new AgentTaskReservation(this, owner, kind);
            _reservations.Add(reservation);
            return true;
        }
    }

    public bool TryReserveAgentTurn(
        AgentTaskOwner owner,
        string agentId,
        out AgentTaskReservation? reservation,
        out string? error)
    {
        lock (_gate)
        {
            if (!_agents.TryGetValue(Key(owner.ConversationId, agentId), out var agent))
                error = "本对话没有这个子 Agent。";
            else if (_agentReservations.Contains(Key(owner.ConversationId, agentId))
                     || agent.LatestTaskId is { } taskId && Find(owner.ConversationId, taskId)?.IsRunning == true)
                error = "这个子 Agent 正在运行。";
            else
                error = CapacityErrorLocked(owner.ConversationId, AgentTaskKinds.Agent);
            if (error is not null)
            {
                reservation = null;
                return false;
            }
            reservation = new AgentTaskReservation(this, owner, AgentTaskKinds.Agent, agentId);
            _reservations.Add(reservation);
            _agentReservations.Add(Key(owner.ConversationId, agentId));
            return true;
        }
    }

    /// <summary>
    /// Transfer a reservation to a task and start running it. The reservation is
    /// removed and the task is added under the same lock, so capacity never opens
    /// between admission and registration.
    /// </summary>
    /// <param name="alreadyRunning">How long the work has been going before it became
    /// a task — a foreground run that timed out into the background.</param>
    public AgentTask Start(
        AgentTaskReservation reservation,
        string label,
        Func<AgentTask, CancellationToken, Task<AgentTaskOutcome>> run,
        Func<string>? tail = null,
        TimeSpan alreadyRunning = default,
        string? agentId = null)
    {
        AgentTask task;
        lock (_gate)
        {
            if (!_reservations.Remove(reservation) || !reservation.TryConsume(this))
                throw new InvalidOperationException("后台任务容量预留已释放或已使用。");
            if (reservation.AgentId is { } reservedAgentId)
                _agentReservations.Remove(Key(reservation.Owner.ConversationId, reservedAgentId));

            task = new AgentTask(
                NewId(reservation.Owner.ConversationId, reservation.Kind),
                reservation.Owner,
                reservation.Kind,
                label,
                tail,
                alreadyRunning,
                agentId);
            _tasks[Key(task.ConversationId, task.Id)] = task;
            if (agentId is not null && _agents.TryGetValue(Key(task.ConversationId, agentId), out var agent))
            {
                agent.LatestTaskId = task.Id;
                agent.Version++;
            }
            SignalLocked();
        }

        Changed?.Invoke(task);
        _ = RunAsync(task, run);
        return task;
    }

    internal void ReleaseReservation(AgentTaskReservation reservation)
    {
        lock (_gate)
        {
            _reservations.Remove(reservation);
            if (reservation.AgentId is { } agentId)
                _agentReservations.Remove(Key(reservation.Owner.ConversationId, agentId));
        }
    }

    private string? CapacityErrorLocked(string conversationId, string kind)
    {
        var runningInConversation = 0;
        var runningAgentsInConversation = 0;
        foreach (var task in _tasks.Values)
        {
            if (!task.IsRunning) continue;
            if (task.ConversationId == conversationId) runningInConversation++;
            if (task.ConversationId == conversationId && task.Kind == AgentTaskKinds.Agent)
                runningAgentsInConversation++;
        }
        foreach (var reservation in _reservations)
        {
            if (reservation.Owner.ConversationId == conversationId) runningInConversation++;
            if (reservation.Owner.ConversationId == conversationId && reservation.Kind == AgentTaskKinds.Agent)
                runningAgentsInConversation++;
        }

        if (runningInConversation >= MaxRunningPerConversation)
            return $"本对话已有 {MaxRunningPerConversation} 个任务在运行。";
        var limits = _options();
        var perConversation = Math.Clamp(limits.MaxPerConversation, 1, MaxRunningPerConversation);
        if (kind == AgentTaskKinds.Agent && runningAgentsInConversation >= perConversation)
            return $"本对话已有 {perConversation} 个子 Agent 在运行。";
        return null;
    }

    private async Task RunAsync(AgentTask task, Func<AgentTask, CancellationToken, Task<AgentTaskOutcome>> run)
    {
        AgentTaskStatus status;
        string report;
        try
        {
            var outcome = await run(task, task.Cts.Token).ConfigureAwait(false);
            status = task.Cts.IsCancellationRequested
                ? AgentTaskStatus.Cancelled
                : outcome.Success ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;
            report = outcome.Report;
        }
        catch (OperationCanceledException) when (task.Cts.IsCancellationRequested)
        {
            status = AgentTaskStatus.Cancelled;
            report = "任务已停止。";
        }
        catch (Exception ex)
        {
            status = AgentTaskStatus.Failed;
            report = "任务出错：" + ex.Message;
        }

        End(task, status, report);
    }

    private void End(AgentTask task, AgentTaskStatus status, string report)
    {
        lock (_gate)
        {
            if (!task.IsRunning) return;
            task.Status = status;
            task.Report = report;
            task.EndedAt = DateTimeOffset.Now;
            if (task.AgentId is { } agentId && _agents.TryGetValue(Key(task.ConversationId, agentId), out var agent))
                agent.Version++;
            SignalLocked();
        }
        Changed?.Invoke(task);
    }

    public IReadOnlyList<AgentTask> List(string conversationId) =>
        _tasks.Values
            .Where(t => t.ConversationId == conversationId)
            .OrderBy(t => t.StartedAt)
            .ToList();

    public AgentTask? Find(string conversationId, string id) =>
        _tasks.TryGetValue(Key(conversationId, id.Trim()), out var task) ? task : null;

    public int RunningCount(string conversationId) =>
        _tasks.Values.Count(t => t.ConversationId == conversationId && t.IsRunning);

    public bool Stop(string conversationId, string id)
    {
        var task = Find(conversationId, id);
        if (task is not { IsRunning: true }) return false;
        Cancel(task);
        return true;
    }

    /// <summary>Stop what one turn started. Stopping a turn stops its tasks: the
    /// user pressing 停止 means "no more of this", not "no more of the part I can see".</summary>
    public int StopTurn(string conversationId, string turnId)
    {
        var victims = _tasks.Values
            .Where(t => t.IsRunning && t.ConversationId == conversationId && t.TurnId == turnId)
            .ToList();
        foreach (var task in victims) Cancel(task);
        return victims.Count;
    }

    public int StopConversation(string conversationId)
    {
        var victims = _tasks.Values.Where(t => t.IsRunning && t.ConversationId == conversationId).ToList();
        foreach (var task in victims) Cancel(task);
        return victims.Count;
    }

    public void StopAll()
    {
        foreach (var task in _tasks.Values.Where(t => t.IsRunning).ToList()) Cancel(task);
    }

    /// <summary>A stopped task never wakes the model — whoever stopped it knows. It is
    /// still undelivered, so the next turn's summary tells the model it is gone,
    /// unless the model stopped it itself and the tool result already said so.</summary>
    private static void Cancel(AgentTask task)
    {
        try { task.Cts.Cancel(); }
        catch (AggregateException) { /* the task reports its own failure */ }
    }

    /// <summary>Finished tasks whose outcome the model has not seen yet.</summary>
    public IReadOnlyList<AgentTask> Undelivered(string conversationId) =>
        _tasks.Values
            .Where(t => t.ConversationId == conversationId && !t.IsRunning && !t.Delivered)
            .OrderBy(t => t.EndedAt)
            .ToList();

    public void MarkDelivered(AgentTask task) => task.Delivered = true;

    public AgentHandle RegisterAgent(string conversationId, string label, string providerId, string modelId)
    {
        lock (_gate)
        {
            var id = "agent-" + Guid.NewGuid().ToString("N")[..12];
            var agent = new AgentHandle(id, conversationId, label, providerId, modelId);
            _agents.Add(Key(conversationId, id), agent);
            SignalLocked();
            return agent;
        }
    }

    public void RestoreAgent(string conversationId, StoredSubagent stored, string providerId, DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            if (_agents.ContainsKey(Key(conversationId, stored.AgentId))) return;
            var agent = new AgentHandle(stored.AgentId, conversationId, stored.Label,
                stored.ProviderId ?? providerId, stored.ModelId)
            {
                RegisteredAt = startedAt,
                LatestTaskId = stored.TaskId
            };
            if (!string.IsNullOrWhiteSpace(stored.Task))
            {
                var activity = new AgentActivityState(Guid.NewGuid().ToString("N"), "task", "任务", "completed");
                activity.Append(stored.Task);
                agent.Activities.Add(activity);
            }
            var task = new AgentTask(stored.TaskId, new AgentTaskOwner(conversationId, null),
                AgentTaskKinds.Agent, stored.Label, null, default, stored.AgentId)
            {
                StartedAt = startedAt,
                EndedAt = DateTimeOffset.Now,
                Status = stored.Status,
                Report = stored.Status == AgentTaskStatus.Interrupted
                    ? "应用退出后，先前的子 Agent 任务已中断，当前没有运行中的任务。"
                    : stored.Report,
                Delivered = stored.Status is not (AgentTaskStatus.Interrupted or AgentTaskStatus.Cancelled),
                IsRestored = true
            };
            _agents.Add(Key(conversationId, agent.Id), agent);
            _tasks[Key(conversationId, task.Id)] = task;
            SignalLocked();
        }
    }

    public AgentHandle? FindAgent(string conversationId, string agentId)
    {
        lock (_gate)
            return _agents.GetValueOrDefault(Key(conversationId, agentId.Trim()));
    }

    public IReadOnlyList<AgentHandle> ListAgents(string conversationId)
    {
        lock (_gate)
            return _agents.Values.Where(a => a.ConversationId == conversationId)
                .OrderBy(a => a.RegisteredAt).ToList();
    }

    public IReadOnlyList<AgentActivity> ListAgentActivities(string conversationId, string agentId)
    {
        lock (_gate)
            return _agents.TryGetValue(Key(conversationId, agentId), out var agent)
                ? agent.Activities.Select(activity => activity.Snapshot()).ToArray() : [];
    }

    public void BeginAgentTurn(string conversationId, string agentId, string task)
    {
        AgentActivity activity;
        lock (_gate)
        {
            if (!_agents.TryGetValue(Key(conversationId, agentId), out var agent)) return;
            var entry = new AgentActivityState(Guid.NewGuid().ToString("N"), "task", "任务", "started");
            entry.Append(task);
            agent.Activities.Add(entry);
            agent.RecentOutput = string.Empty;
            activity = entry.Snapshot();
        }
        AgentActivityChanged?.Invoke(conversationId, agentId, activity);
    }

    public void AppendAgentText(string conversationId, string agentId, string text, bool thinking = false)
    {
        AgentActivity activity;
        lock (_gate)
        {
            if (!_agents.TryGetValue(Key(conversationId, agentId), out var agent)) return;
            var entry = agent.Activities.LastOrDefault();
            var kind = thinking ? "thinking" : "text";
            if (entry?.Kind != kind)
            {
                entry = new AgentActivityState(Guid.NewGuid().ToString("N"), kind,
                    thinking ? "思考" : "输出", "streaming");
                agent.Activities.Add(entry);
                if (!thinking) agent.RecentOutput = string.Empty;
            }
            entry.Append(text);
            activity = new AgentActivity(entry.Id, entry.Kind, entry.Label, entry.Status,
                text, null, entry.Revision, IsDelta: true);
            if (!thinking)
            {
                var recent = agent.RecentOutput + text;
                agent.RecentOutput = recent.Length <= 2000 ? recent : recent[^2000..];
            }
        }
        AgentActivityChanged?.Invoke(conversationId, agentId, activity);
    }

    public void UpdateAgentTool(string conversationId, string agentId, ToolCallDelta tool)
    {
        AgentActivity? activity = null;
        lock (_gate)
        {
            if (!_agents.TryGetValue(Key(conversationId, agentId), out var agent)) return;
            var entry = agent.Activities.LastOrDefault(item => item.Kind == "tool" && item.Id == tool.Id);
            if (entry is null)
            {
                entry = new AgentActivityState(tool.Id, "tool", tool.Label ?? tool.Name, tool.Status);
                agent.Activities.Add(entry);
            }
            entry.Label = tool.Label ?? tool.Name;
            entry.Status = tool.Status;
            // Preparing deltas contain the entire still-growing JSON argument string.
            // Keep only the latest preview and wait for the complete running delta
            // before asking the UI to parse arguments.
            var preview = tool.Status == "preparing";
            var display = preview ? tool with { ArgumentsJson = null, Summary = null } : tool;
            var previous = entry.ToolDeltas.LastOrDefault();
            var notify = !preview || previous is null
                || previous.Name != display.Name || previous.Label != display.Label;
            if (preview && previous?.Status == "preparing")
                entry.ToolDeltas[^1] = display;
            else
                entry.ToolDeltas.Add(display);
            entry.Update();
            if (tool.Status is "preparing" or "running")
                agent.RecentOutput = "正在调用 " + entry.Label;
            if (notify)
                activity = new AgentActivity(entry.Id, entry.Kind, entry.Label, entry.Status,
                    string.Empty, [display], entry.Revision, IsDelta: true);
        }
        if (activity is not null) AgentActivityChanged?.Invoke(conversationId, agentId, activity);
    }

    public bool QueueToParent(string conversationId, string fromAgentId, string text)
    {
        lock (_gate)
        {
            if (!_agents.ContainsKey(Key(conversationId, fromAgentId))) return false;
            AddMessageLocked(_parentMessages, conversationId,
                new AgentMessage(Guid.NewGuid().ToString("N"), conversationId, fromAgentId, text));
            SignalLocked();
        }
        ParentMessageQueued?.Invoke(conversationId);
        return true;
    }

    public bool QueueToAgent(string conversationId, string targetAgentId, string fromAgentId, string text)
    {
        lock (_gate)
        {
            if (!_agents.ContainsKey(Key(conversationId, targetAgentId))) return false;
            if (fromAgentId != "parent" && !_agents.ContainsKey(Key(conversationId, fromAgentId))) return false;
            AddMessageLocked(_agentMessages, Key(conversationId, targetAgentId),
                new AgentMessage(Guid.NewGuid().ToString("N"), conversationId, fromAgentId, text));
            SignalLocked();
        }
        AgentMessageQueued?.Invoke(targetAgentId);
        return true;
    }

    public IReadOnlyList<AgentMessage> PeekParentMessages(string conversationId)
    {
        lock (_gate)
            return _parentMessages.TryGetValue(conversationId, out var messages) ? messages.ToArray() : [];
    }

    public IReadOnlyList<AgentMessage> PeekAgentMessages(string conversationId, string agentId)
    {
        lock (_gate)
            return _agentMessages.TryGetValue(Key(conversationId, agentId), out var messages) ? messages.ToArray() : [];
    }

    public void AcknowledgeParentMessages(string conversationId, IEnumerable<string> ids)
    {
        lock (_gate)
            if (_parentMessages.TryGetValue(conversationId, out var messages))
                messages.RemoveAll(m => ids.Contains(m.Id));
    }

    public void AcknowledgeAgentMessages(string conversationId, string agentId, IEnumerable<string> ids)
    {
        lock (_gate)
            if (_agentMessages.TryGetValue(Key(conversationId, agentId), out var messages))
                messages.RemoveAll(m => ids.Contains(m.Id));
    }

    public Task CaptureChangeSignal()
    {
        lock (_gate) return _changeSignal.Task;
    }

    public static async Task<bool> WaitForChangeAsync(Task signal, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await signal.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static void AddMessageLocked(Dictionary<string, List<AgentMessage>> mailbox, string key, AgentMessage message)
    {
        if (!mailbox.TryGetValue(key, out var messages)) mailbox[key] = messages = [];
        messages.Add(message);
    }

    private void SignalLocked()
    {
        var previous = _changeSignal;
        _changeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    private string NewId(string conversationId, string kind)
    {
        var prefix = kind == AgentTaskKinds.Agent ? "agent" : "py";
        while (true)
        {
            var id = $"{prefix}-{Random.Shared.NextInt64():x16}";
            if (!_tasks.ContainsKey(Key(conversationId, id))) return id;
        }
    }

    private static string Key(string conversationId, string id) => conversationId + "\n" + id;
}

/// <summary>A capacity reservation that can be transferred to a task or released.</summary>
public sealed class AgentTaskReservation : IDisposable
{
    private AgentTaskRegistry? _registry;

    internal AgentTaskReservation(AgentTaskRegistry registry, AgentTaskOwner owner, string kind, string? agentId = null)
    {
        _registry = registry;
        Owner = owner;
        Kind = kind;
        AgentId = agentId;
    }

    internal AgentTaskOwner Owner { get; }
    internal string Kind { get; }
    internal string? AgentId { get; }

    internal bool TryConsume(AgentTaskRegistry registry) =>
        ReferenceEquals(Interlocked.CompareExchange(ref _registry, null, registry), registry);

    public void Dispose() => Interlocked.Exchange(ref _registry, null)?.ReleaseReservation(this);
}
