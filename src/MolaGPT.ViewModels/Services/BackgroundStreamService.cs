using System.Collections.Concurrent;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;

namespace MolaGPT.ViewModels.Services;

public sealed class BackgroundStreamTask
{
    private int _titleGenerationStarted;

    public string ConversationId { get; init; } = default!;
    public string ConversationTitle { get; init; } = "新对话";
    public string? ModelLabel { get; init; }
    public string? ModelId { get; init; }
    public string? ProviderId { get; init; }
    public ProviderKind ProviderKind { get; init; } = ProviderKind.Custom;
    public MessageViewModel AssistantMessage { get; init; } = default!;
    public CancellationTokenSource Cts { get; set; } = default!;
    public Task StreamTask { get; set; } = default!;
    public bool IsDetached { get; set; }
    public bool IsCompleted { get; set; }
    public bool GenerateTitleOnCompletion { get; init; }
    public bool CompletedSuccessfully { get; set; }

    /// <summary>
    /// This stream is replacing an answer that already exists rather than adding a
    /// new one. The assistant message is therefore already in the store, so it must
    /// be updated on completion — persisting it the normal way would insert a
    /// second copy of the same bubble.
    /// </summary>
    public bool IsRegeneration { get; init; }

    public string? SessionId { get; init; }
    public string? ApiUrl { get; set; }
    public int ReceivedChunkCount { get; set; }
    public int MissedStatusPolls { get; set; }
    internal CancellationTokenSource? PollCts { get; set; }

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

    public bool HasTask(string conversationId) => _tasks.ContainsKey(conversationId);
    public int ActiveTaskCount => _tasks.Values.Count(task => !task.IsCompleted);

    public BackgroundStreamTask? GetTask(string conversationId) =>
        _tasks.TryGetValue(conversationId, out var task) ? task : null;

    public IReadOnlyCollection<BackgroundStreamTask> ActiveTasks => _tasks.Values.ToList();

    public void Register(BackgroundStreamTask task)
    {
        _tasks[task.ConversationId] = task;
        TaskRegistered?.Invoke(this, task.ConversationId);
    }

    public void RegisterWithPolling(BackgroundStreamTask task, MolaGptProxyProvider provider)
    {
        _tasks[task.ConversationId] = task;
        TaskRegistered?.Invoke(this, task.ConversationId);
        var pollCts = new CancellationTokenSource();
        task.PollCts = pollCts;
        _ = PollStreamStatusAsync(task, provider, pollCts.Token);
    }

    private async Task PollStreamStatusAsync(BackgroundStreamTask task, MolaGptProxyProvider provider, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(4000, ct);
                if (ct.IsCancellationRequested) break;

                var status = await provider.CheckStreamStatusAsync(task.SessionId!, ct);
                if (status is null)
                {
                    task.MissedStatusPolls++;
                    if (task.MissedStatusPolls < 3)
                        continue;

                    task.IsCompleted = true;
                    PublishCompletion(task.ConversationId, task.ConversationTitle, task.ModelLabel);
                    break;
                }
                task.MissedStatusPolls = 0;

                if (status.Status == "completed")
                {
                    var data = await provider.FetchCompletedStreamAsync(task.SessionId!, ct);
                    if (data is not null)
                    {
                        task.AssistantMessage.ReplaceContent(data.Text);
                        if (data.Sources is { Count: > 0 })
                            task.AssistantMessage.Sources = data.Sources;
                    }

                    task.IsCompleted = true;
                    PublishCompletion(task.ConversationId, task.ConversationTitle, task.ModelLabel);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    public void StopPolling(BackgroundStreamTask task)
    {
        task.PollCts?.Cancel();
        task.PollCts?.Dispose();
        task.PollCts = null;
    }

    public BackgroundStreamTask? Detach(string conversationId)
    {
        _tasks.TryRemove(conversationId, out var task);
        return task;
    }

    public void StopAll()
    {
        foreach (var task in _tasks.Values)
        {
            StopPolling(task);
            task.Cts.Cancel();
        }
    }

    public void Complete(BackgroundStreamTask task)
    {
        task.IsCompleted = true;
        _tasks.TryRemove(task.ConversationId, out _);
        PublishCompletion(task.ConversationId, task.ConversationTitle, task.ModelLabel);
    }

    public void Fail(BackgroundStreamTask task, string errorMessage)
    {
        task.IsCompleted = true;
        _tasks.TryRemove(task.ConversationId, out _);
        PublishFailure(task.ConversationId, task.ConversationTitle, task.ModelLabel, errorMessage);
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
