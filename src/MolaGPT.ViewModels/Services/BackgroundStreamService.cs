using System.Collections.Concurrent;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;

namespace MolaGPT.ViewModels.Services;

public sealed class BackgroundStreamTask
{
    public string ConversationId { get; init; } = default!;
    public string ConversationTitle { get; init; } = "新对话";
    public string? ModelLabel { get; init; }
    public string? ProviderId { get; init; }
    public ProviderKind ProviderKind { get; init; } = ProviderKind.Custom;
    public MessageViewModel AssistantMessage { get; init; } = default!;
    public CancellationTokenSource Cts { get; set; } = default!;
    public Task StreamTask { get; set; } = default!;
    public bool IsDetached { get; set; }
    public bool IsCompleted { get; set; }

    public string? SessionId { get; init; }
    public string? ApiUrl { get; set; }
    public int ReceivedChunkCount { get; set; }
    internal CancellationTokenSource? PollCts { get; set; }
}

public sealed class BackgroundStreamCompletedEventArgs : EventArgs
{
    public string ConversationId { get; init; } = default!;
    public string ConversationTitle { get; init; } = "新对话";
    public string? ModelLabel { get; init; }
}

public sealed class BackgroundStreamService
{
    private readonly ConcurrentDictionary<string, BackgroundStreamTask> _tasks = new();

    public event EventHandler<BackgroundStreamCompletedEventArgs>? TaskCompleted;
    public event EventHandler<string>? TaskRegistered;

    public bool HasTask(string conversationId) => _tasks.ContainsKey(conversationId);

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
                    task.IsCompleted = true;
                    _tasks.TryRemove(task.ConversationId, out _);
                    PublishCompletion(task.ConversationId, task.ConversationTitle, task.ModelLabel);
                    break;
                }

                if (status.Status == "completed")
                {
                    var data = await provider.FetchCompletedStreamAsync(task.SessionId!, ct);
                    if (data is not null)
                        task.AssistantMessage.ReplaceContent(data.Text);

                    task.IsCompleted = true;
                    _tasks.TryRemove(task.ConversationId, out _);
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
}
