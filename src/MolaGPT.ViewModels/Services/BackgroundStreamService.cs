using System.Collections.Concurrent;
using MolaGPT.Core.Chat;

namespace MolaGPT.ViewModels.Services;

public sealed class BackgroundStreamTask
{
    private int _titleGenerationStarted;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _finalizerClaimed;

    public string ConversationId { get; init; } = default!;
    public string ConversationTitle { get; init; } = "新对话";
    public string? ModelLabel { get; init; }
    public string? ModelId { get; init; }
    public string? ProviderId { get; init; }
    public ProviderKind ProviderKind { get; init; } = ProviderKind.Custom;

    /// <summary>The bubble the stream is writing into. Replaced mid-stream when the
    /// agent takes in a message queued into the run: the reply splits there.</summary>
    public MessageViewModel AssistantMessage { get; set; } = default!;

    /// <summary>Tool calls in flight right now. While there are any, the agent is in
    /// its tool loop and a message queued into the run lands before its next model
    /// call; with none, the reply may already be the final answer.</summary>
    internal HashSet<string> RunningTools { get; } = new(StringComparer.Ordinal);

    /// <summary>Messages sent into the run that it has not taken in yet. Kept here
    /// rather than on the composer, so they outlive a switch to another conversation.</summary>
    internal List<PendingInjectionViewModel> Injections { get; } = new();

    /// <summary>Started by a background task's notification rather than by the user.</summary>
    public bool IsWakeTurn { get; init; }

    /// <summary>Started by a message sent from a paired phone.</summary>
    public bool IsRemoteTurn { get; init; }
    public CancellationTokenSource Cts { get; init; } = default!;
    public bool IsDetached { get; set; }
    public Task Completion => _completion.Task;
    public bool IsInFlight => !Completion.IsCompleted;
    internal bool IsFinalizing => Volatile.Read(ref _finalizerClaimed) != 0 && IsInFlight;
    internal bool IsReadingStream { get; set; }
    internal bool AcceptsMessages => IsReadingStream && !IsFinalizing && IsInFlight && !Cts.IsCancellationRequested;
    internal bool TryBeginFinalization() => Interlocked.CompareExchange(ref _finalizerClaimed, 1, 0) == 0;
    internal void Finish() => _completion.SetResult();
    public bool GenerateTitleOnCompletion { get; init; }
    public bool CompletedSuccessfully { get; set; }
    internal bool HasUnsavedChanges { get; set; } = true;
    internal int ResponsePostProcessingStartIndex { get; init; }
    internal bool ResponsePostProcessingApplied { get; set; }

    /// <summary>
    /// This stream is replacing an answer that already exists rather than adding a
    /// new one. The assistant message is therefore already in the store, so it must
    /// be updated on completion — persisting it the normal way would insert a
    /// second copy of the same bubble.
    /// </summary>
    public bool IsRegeneration { get; init; }
    public bool IsContinuation { get; init; }
    public IReadOnlyDictionary<string, MolaGPT.Core.Models.LoreActivationState>? RoleStates { get; set; }
    public string? RoleRevision { get; set; }

    public string? SessionId { get; init; }
    public bool TryBeginTitleGeneration() =>
        Interlocked.Exchange(ref _titleGenerationStarted, 1) == 0;
}

public sealed class BackgroundStreamCompletedEventArgs : EventArgs
{
    public string ConversationId { get; init; } = default!;
    public string ConversationTitle { get; init; } = "新对话";
    public string? ModelLabel { get; init; }
}

/// <summary>
/// A turn that ended on an error rather than an answer. Separate from
/// <see cref="BackgroundStreamCompletedEventArgs"/> because the two must not
/// share an exit: a failed turn was announcing itself as 「回复已完成」 while the
/// bubble sat empty, which is precisely the case where the user needs to be told
/// what went wrong.
/// </summary>
public sealed class BackgroundStreamFailedEventArgs : EventArgs
{
    public string ConversationId { get; init; } = default!;
    public string ConversationTitle { get; init; } = "新对话";
    public string? ModelLabel { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
}

public sealed class BackgroundStreamService
{
    private readonly ConcurrentDictionary<string, BackgroundStreamTask> _tasks = new();

    public event EventHandler<BackgroundStreamCompletedEventArgs>? TaskCompleted;
    public event EventHandler<BackgroundStreamFailedEventArgs>? TaskFailed;
    public event EventHandler<string>? TaskRegistered;
    /// <summary>A task that finished without announcing itself, e.g. one a phone
    /// asked for, which is told by the phone instead.</summary>
    public event EventHandler<string>? TaskEnded;

    public bool HasTask(string conversationId) => GetTask(conversationId) is not null;
    public int ActiveTaskCount => _tasks.Values.Count(task => task.IsInFlight);

    public BackgroundStreamTask? GetTask(string conversationId) =>
        _tasks.TryGetValue(conversationId, out var task) && task.IsInFlight ? task : null;

    public IReadOnlyCollection<BackgroundStreamTask> ActiveTasks => _tasks.Values.Where(task => task.IsInFlight).ToList();

    public void Register(BackgroundStreamTask task)
    {
        if (!_tasks.TryAdd(task.ConversationId, task))
            throw new InvalidOperationException("此对话仍有任务在途。");
        TaskRegistered?.Invoke(this, task.ConversationId);
    }

    public bool TryReattach(BackgroundStreamTask task)
    {
        if (!Remove(task)) return false;
        task.IsDetached = false;
        TaskEnded?.Invoke(this, task.ConversationId);
        return true;
    }

    private bool Remove(BackgroundStreamTask task) =>
        _tasks.TryRemove(new KeyValuePair<string, BackgroundStreamTask>(task.ConversationId, task));

    public void StopAll()
    {
        foreach (var task in ActiveTasks)
            task.Cts.Cancel();
    }

    /// <summary>Finish <paramref name="task"/> with no notification; whoever shows it
    /// running still hears that it stopped.</summary>
    public void EndQuietly(BackgroundStreamTask task)
    {
        if (Remove(task)) TaskEnded?.Invoke(this, task.ConversationId);
    }

    public void Complete(BackgroundStreamTask task)
    {
        if (Remove(task)) PublishCompletion(task.ConversationId, task.ConversationTitle, task.ModelLabel);
    }

    public void Fail(BackgroundStreamTask task, string errorMessage)
    {
        if (Remove(task)) PublishFailure(task.ConversationId, task.ConversationTitle, task.ModelLabel, errorMessage);
    }

    public void PublishCompletion(string conversationId, string conversationTitle, string? modelLabel)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return;
        TaskCompleted?.Invoke(this, new BackgroundStreamCompletedEventArgs
        {
            ConversationId = conversationId,
            ConversationTitle = conversationTitle,
            ModelLabel = modelLabel
        });
    }

    public void PublishFailure(
        string conversationId,
        string conversationTitle,
        string? modelLabel,
        string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return;
        TaskFailed?.Invoke(this, new BackgroundStreamFailedEventArgs
        {
            ConversationId = conversationId,
            ConversationTitle = conversationTitle,
            ModelLabel = modelLabel,
            ErrorMessage = errorMessage
        });
    }
}
