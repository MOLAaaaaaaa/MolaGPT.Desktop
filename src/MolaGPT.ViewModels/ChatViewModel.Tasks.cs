using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Tasks;
using MolaGPT.Core.Chat.Tools.PythonExecution;
using MolaGPT.Core.Models;
using MolaGPT.Storage;

namespace MolaGPT.ViewModels;

/// <summary>A running background task of the conversation on screen.</summary>
public sealed partial class TaskChipViewModel : ObservableObject
{
    private readonly AgentTask _task;
    private readonly Action<string> _stop;
    private readonly Action<string>? _inspect;

    public TaskChipViewModel(AgentTask task, Action<string> stop, Action<string>? inspect)
    {
        _task = task;
        _stop = stop;
        _inspect = inspect;
    }

    public string Id => _task.Id;
    public string Label => _task.Label;
    public string KindLabel => _task.Kind == AgentTaskKinds.Agent ? "子 Agent" : "Python";
    public bool CanInspect => _task.AgentId is not null;
    public string ElapsedText => TaskTools.FormatElapsed(_task.Elapsed);

    public void Tick() => OnPropertyChanged(nameof(ElapsedText));

    [RelayCommand]
    private void Stop() => _stop(_task.Id);

    [RelayCommand]
    private void Inspect()
    {
        if (_task.AgentId is { } agentId) _inspect?.Invoke(agentId);
    }
}

/// <summary>A conversation branched off into one of its own.</summary>
/// <param name="Draft">The text of the user message the branch was taken at, for
/// the input box — the branch ends just before it.</param>
public sealed record ConversationBranch(string Id, string Title, string? ProviderId, string? PersonaLabel, string? Draft);

public sealed partial class ChatViewModel
{
    private AgentTaskRegistry? _tasks;
    private Action<Action>? _post;
    private Timer? _taskClock;

    public AgentTaskRegistry? Tasks => _tasks;

    /// <summary>The conversation's tasks that are still running — state, drawn as a
    /// chip in the composer, not announced.</summary>
    public ObservableCollection<TaskChipViewModel> ConversationTasks { get; } = new();
    public ObservableCollection<SubagentViewModel> Subagents { get; } = new();

    public int RunningTaskCount => ConversationTasks.Count;
    public bool HasRunningTasks => ConversationTasks.Count > 0;
    public int SubagentCount => Subagents.Count;
    public bool HasSubagents => Subagents.Count > 0;

    /// <summary>Every task change in every conversation, on the UI thread.</summary>
    public event Action<AgentTask>? TaskChanged;

    public event Action<string>? AgentMessageQueued;
    public event Action<string>? SubagentInspectRequested;

    /// <param name="post">Runs an action on the UI thread. The registry raises its
    /// events wherever a task happened to end.</param>
    public void AttachTaskRegistry(AgentTaskRegistry registry, Action<Action> post)
    {
        _tasks = registry;
        _post = post;
        registry.Changed += task => post(() => OnTaskChanged(task));
        registry.AgentActivityChanged += (conversationId, agentId, activity) => post(() =>
        {
            if (conversationId != ConversationId) return;
            var agent = Subagents.FirstOrDefault(item => item.Id == agentId);
            if (agent is null)
            {
                RefreshSubagents();
                agent = Subagents.FirstOrDefault(item => item.Id == agentId);
            }
            agent?.ApplyActivity(activity);
            agent?.RefreshProgress();
        });
        registry.ParentMessageQueued += conversationId => post(() => AgentMessageQueued?.Invoke(conversationId));
        ConversationTasks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(RunningTaskCount));
            OnPropertyChanged(nameof(HasRunningTasks));
        };
        Subagents.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SubagentCount));
            OnPropertyChanged(nameof(HasSubagents));
        };
    }

    private void OnTaskChanged(AgentTask task)
    {
        if (!task.IsRunning)
            _messageRepo?.UpdateBackgroundTaskState(
                task.ConversationId, task.Id, TaskTools.StatusName(task.Status));
        if (task.ConversationId == ConversationId)
        {
            RefreshConversationTasks();
            if (!task.IsRunning && task.Kind is AgentTaskKinds.Agent or AgentTaskKinds.Python)
                RefreshArtifacts(autoOpenNewFiles: true);
        }
        TaskChanged?.Invoke(task);
    }

    /// <summary>Rebuild the running-task chip for the conversation on screen and bring
    /// its tool cards in line with the registry.</summary>
    public void RefreshConversationTasks()
    {
        ConversationTasks.Clear();
        RefreshSubagents();
        if (_tasks is not null && !string.IsNullOrEmpty(ConversationId))
        {
            foreach (var task in _tasks.List(ConversationId!).Where(t => t.IsRunning))
                ConversationTasks.Add(new TaskChipViewModel(task, StopTask, id => SubagentInspectRequested?.Invoke(id)));
        }
        ApplyTaskStates();
        UpdateTaskClock();
    }

    private void RefreshSubagents()
    {
        var agents = _tasks is not null && !string.IsNullOrEmpty(ConversationId)
            ? _tasks.ListAgents(ConversationId!) : [];
        foreach (var item in Subagents.Where(item => agents.All(agent => agent.Id != item.Id)).ToArray())
        {
            item.Dispose();
            Subagents.Remove(item);
        }
        foreach (var agent in agents)
        {
            var item = Subagents.FirstOrDefault(existing => existing.Id == agent.Id);
            if (item is null)
            {
                item = new SubagentViewModel(agent, StopTask);
                item.UpdateTask(agent.LatestTaskId is { } newTaskId
                    ? _tasks!.Find(ConversationId!, newTaskId) : null);
                foreach (var activity in _tasks!.ListAgentActivities(ConversationId!, agent.Id))
                    item.ApplyActivity(activity);
                Subagents.Add(item);
            }
            else
                item.UpdateTask(agent.LatestTaskId is { } taskId ? _tasks!.Find(ConversationId!, taskId) : null);
        }
    }

    private void RestoreSubagents(string conversationId, IReadOnlyList<PreparedMessage> messages, string? providerId)
    {
        if (_tasks is null) return;
        var latest = new Dictionary<string, (StoredSubagent Agent, DateTimeOffset StartedAt)>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role != ChatMessage.RoleAssistant || message.ToolCalls is null) continue;
            foreach (var tool in message.ToolCalls)
            {
                if (SubagentTool.ReadStored(tool) is { } agent)
                    latest[agent.AgentId] = (agent, DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedAt));
            }
        }
        foreach (var (agent, startedAt) in latest.Values)
            _tasks.RestoreAgent(conversationId, agent, providerId ?? string.Empty, startedAt);
    }

    /// <summary>A card that started a task shows the task, not the call — the call
    /// finished the moment the task began.</summary>
    public void ApplyTaskStates()
    {
        if (_tasks is null || string.IsNullOrEmpty(ConversationId)) return;
        foreach (var message in Messages)
        {
            if (SyncMessageTaskStates(message, ConversationId!))
                UpdatePersistedMessage(message, ConversationId);
        }
    }

    private bool SyncMessageTaskStates(MessageViewModel message, string conversationId)
    {
        if (_tasks is null) return false;
        var changed = false;
        foreach (var tool in message.ToolCalls)
        {
            if (tool.BackgroundTaskId is not { } id) continue;
            var state = ResolveTaskState(conversationId, id, tool.TaskState);
            if (tool.TaskState == state) continue;
            tool.TaskState = state;
            changed = true;
        }

        if (message.RetryAttempts is { } attempts)
        {
            var retryChanged = false;
            var updated = attempts.Select(attempt =>
            {
                if (attempt.ToolCalls is null) return attempt;
                var tools = attempt.ToolCalls.Select(tool =>
                {
                    var id = BackgroundTaskId(tool);
                    if (id is null) return tool;
                    var state = ResolveTaskState(conversationId, id, tool.TaskState);
                    if (tool.BackgroundTaskId == id && tool.TaskState == state) return tool;
                    changed = true;
                    retryChanged = true;
                    return tool with { BackgroundTaskId = id, TaskState = state };
                }).ToArray();
                return attempt with { ToolCalls = tools };
            }).ToArray();
            if (retryChanged) message.RetryAttempts = updated;
        }
        return changed;
    }

    private string? ResolveTaskState(string conversationId, string id, string? storedState) =>
        _tasks?.Find(conversationId, id) is { } task
            ? TaskTools.StatusName(task.Status)
            : storedState is null or "running" ? "interrupted" : storedState;

    private static string? BackgroundTaskId(ToolCallDelta tool)
    {
        if (tool.BackgroundTaskId is { } id) return id;
        if (tool.ResultPreviewJson is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(tool.ResultPreviewJson);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("background", out var background)
                   && background.ValueKind == JsonValueKind.True
                   && root.TryGetProperty("task_id", out var taskId)
                   && taskId.ValueKind == JsonValueKind.String
                ? taskId.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    public void StopTask(string taskId)
    {
        if (_tasks is null || string.IsNullOrEmpty(ConversationId)) return;
        _tasks.Stop(ConversationId!, taskId);
    }

    private void UpdateTaskClock()
    {
        if (ConversationTasks.Count == 0)
        {
            _taskClock?.Dispose();
            _taskClock = null;
            return;
        }
        _taskClock ??= new Timer(_ =>
        {
            void Tick()
            {
                foreach (var chip in ConversationTasks) chip.Tick();
                foreach (var agent in Subagents) agent.Tick();
            }
            _post?.Invoke(Tick);
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Whether a conversation still exists and is not in the bin.</summary>
    public bool ConversationIsLive(string conversationId) =>
        _conversationRepo?.Get(conversationId) is { DeletedAt: null };

    public string ConversationTitleOf(string conversationId) =>
        ConversationId == conversationId
            ? ConversationTitle
            : _conversationRepo?.Get(conversationId)?.Title is { Length: > 0 } title ? title : "新对话";

    // ---- messages into a conversation that may not be on screen -----------------

    /// <summary>
    /// Write a message into <paramref name="conversationId"/>, whether or not it is
    /// the conversation on screen: a background turn can take in a queued message,
    /// or be woken by a notification, while the user is reading something else.
    /// </summary>
    public MessageViewModel AppendMessageTo(string conversationId, string role, string content, string? parentId)
    {
        var vm = new MessageViewModel(role, content, DateTimeOffset.UtcNow) { ParentMessageId = parentId };
        vm.VersionSelected += OnMessageVersionSelected;
        if (ConversationId == conversationId) Messages.Add(vm);
        PersistMessage(vm, conversationId);
        TouchConversation(conversationId);
        return vm;
    }

    /// <summary>A new, still-streaming reply after <paramref name="parent"/>, carrying
    /// over the labels of the reply it continues.</summary>
    public MessageViewModel BeginAssistantMessageFor(string conversationId, MessageViewModel parent, MessageViewModel labels)
    {
        var vm = new MessageViewModel(ChatMessage.RoleAssistant, string.Empty, DateTimeOffset.UtcNow)
        {
            ParentMessageId = parent.MessageId,
            IsStreaming = true,
            ModelLabel = labels.ModelLabel,
            ProviderLabel = labels.ProviderLabel,
            PersonaId = labels.PersonaId,
            PersonaName = labels.PersonaName,
            PersonaAvatar = labels.PersonaAvatar,
            AutoCollapseThinkingOnComplete = AutoCollapseThinking
        };
        vm.StartPending(false);
        vm.VersionSelected += OnMessageVersionSelected;
        if (ConversationId == conversationId) Messages.Add(vm);
        return vm;
    }

    // ---- branching into a new conversation ---------------------------------------

    /// <summary>
    /// Take the conversation up to <paramref name="message"/> into a conversation of
    /// its own, 「分支 · 原标题」: messages, working directory and agent transcript
    /// all copied, so the two go on independently.
    ///
    /// At an answer, the branch includes it and can be continued at once. At a user
    /// message, the branch stops just before it and hands its text back for the input
    /// box — the point of branching there is to say it differently.
    /// </summary>
    public async Task<ConversationBranch?> BranchToNewConversationAsync(MessageViewModel message, CancellationToken ct = default)
    {
        var sourceId = ConversationId;
        if (!CanEditHistory || string.IsNullOrEmpty(sourceId) || message.MessageId is null
            || _conversationRepo is null || _messageRepo is null)
            return null;
        var source = _conversationRepo.Get(sourceId!);
        if (source is null) return null;

        var history = _messageRepo.List(sourceId!);
        var index = history.ToList().FindIndex(row => row.Id == message.MessageId);
        if (index < 0) throw new InvalidOperationException("找不到分支消息。");

        var atUser = message.Role == ChatMessage.RoleUser && !message.IsTaskNotification;
        var kept = history.Take(atUser ? index : index + 1).ToList();
        if (kept.Count == 0 && !atUser) return null;

        var branchId = ComposerViewModel.CreateWebCompatibleConversationId();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var baseTitle = string.IsNullOrWhiteSpace(source.Title) ? "无标题对话" : source.Title.Trim();
        var title = baseTitle.StartsWith("分支 · ", StringComparison.Ordinal) ? baseTitle : "分支 · " + baseTitle;
        var row = new ConversationRow
        {
            Id = branchId,
            Title = title,
            ModelId = source.ModelId,
            ProviderId = source.ProviderId,
            CreatedAt = now,
            UpdatedAt = now,
            SystemPrompt = source.SystemPrompt,
            PersonaId = source.PersonaId,
            SystemPromptMode = source.SystemPromptMode,
            RoleContextJson = source.RoleContextJson
        };
        _conversationRepo.CreateWithMessages(row, kept
            .Select(item => new MessageRow(
                Guid.NewGuid().ToString("N"), branchId, item.Role, item.Content, item.Meta, item.CreatedAt, null))
            .ToList());

        await Task.Run(() => CopyWorkspace(sourceId!, branchId), ct).ConfigureAwait(true);

        if (_providers.GetById(source.ProviderId ?? string.Empty) is PiWorkProvider agent)
        {
            var userTurns = kept.Count(item => item.Role == ChatMessage.RoleUser);
            // An edit the agent has not seen yet means its transcript and the
            // messages disagree; -1 makes the copy fall back to the messages.
            var sourceUserTurns = RoleContext.NeedsHistorySync
                ? -1
                : history.Count(item => item.Role == ChatMessage.RoleUser);
            var fallback = kept
                .Where(item => item.Role is ChatMessage.RoleUser or ChatMessage.RoleAssistant)
                .Select(item => new ChatMessage(item.Role, item.Content))
                .ToList();
            try
            {
                await agent.BranchSessionAsync(
                    sourceId!, branchId, userTurns, sourceUserTurns, fallback,
                    source.ModelId ?? ActiveModel?.Id ?? agent.Models.FirstOrDefault()?.Id ?? string.Empty,
                    ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The messages are already there; the agent rebuilds its history
                // from them on the first turn it cannot find a transcript for.
                DiagnosticBranchError = ex.Message;
            }
        }

        return new ConversationBranch(
            branchId,
            title,
            row.ProviderId,
            _personas?.Find(row.PersonaId)?.Name,
            atUser ? message.Content : null);
    }

    /// <summary>Why the last branch fell back to rebuilding the agent's history, if
    /// it did. Diagnostic only.</summary>
    public string? DiagnosticBranchError { get; private set; }

    /// <summary>Scratch the branch does not need: caches and the source's own task
    /// scaffolding. Everything else — outputs, uploads, installed packages — comes
    /// along, so the branch starts where the source was.</summary>
    private static readonly HashSet<string> BranchSkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tasks", ".uv-cache", ".pip-cache", "__pycache__", ".matplotlib"
    };

    private static void CopyWorkspace(string sourceConversationId, string targetConversationId)
    {
        var source = PythonExecutionTool.GetSessionDirectory(sourceConversationId);
        if (!Directory.Exists(source)) return;
        var target = PythonExecutionTool.GetSessionDirectory(targetConversationId);
        CopyDirectory(source, target);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            try { File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false); }
            catch (IOException) { /* in use or already there; the rest still copies */ }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (BranchSkippedDirectories.Contains(name)) continue;
            CopyDirectory(directory, Path.Combine(target, name));
        }
    }
}
