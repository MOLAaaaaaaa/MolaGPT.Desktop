using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Chat.Tasks;

namespace MolaGPT.ViewModels;

public sealed partial class SubagentViewModel : ObservableObject, IDisposable
{
    private readonly AgentHandle _agent;
    private readonly Action<string> _stop;
    private AgentTask? _task;

    public SubagentViewModel(AgentHandle agent, Action<string> stop)
    {
        _agent = agent;
        _stop = stop;
    }

    public string Id => _agent.Id;
    public string Label => _agent.Label;
    public string Model => _agent.ModelId;
    public bool IsRunning => _task?.IsRunning == true;
    public bool IsCompleted => _task?.Status == AgentTaskStatus.Completed;
    public bool IsFailed => _task?.Status == AgentTaskStatus.Failed;
    public string StatusText => _task?.Status switch
    {
        AgentTaskStatus.Running => "运行中",
        AgentTaskStatus.Completed => "已完成",
        AgentTaskStatus.Failed => "失败",
        AgentTaskStatus.Cancelled => "已停止",
        AgentTaskStatus.Interrupted => "已中断",
        _ => "等待中"
    };
    public string ElapsedText => _task is null || _task.IsRestored ? string.Empty : TaskTools.FormatElapsed(_task.Elapsed);
    public string? FailureText => _task?.Status == AgentTaskStatus.Failed ? _task.Report : null;
    public bool HasFailure => !string.IsNullOrWhiteSpace(FailureText);
    public string CurrentAction => IsRunning ? _agent.RecentOutput.Replace('\n', ' ').Trim() : string.Empty;
    public ObservableCollection<SubagentActivityViewModel> Activity { get; } = new();
    public bool HasActivity => Activity.Count > 0;

    public void UpdateTask(AgentTask? task)
    {
        _task = task;
        if (IsRunning)
        {
            Activity.LastOrDefault()?.ResumeStreaming();
        }
        else Activity.LastOrDefault()?.FinishStreaming();
        foreach (var activity in Activity) activity.SetOwnerRunning(IsRunning);
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(FailureText));
        OnPropertyChanged(nameof(HasFailure));
        OnPropertyChanged(nameof(CurrentAction));
    }

    public void RefreshProgress() => OnPropertyChanged(nameof(CurrentAction));
    public void Tick() => OnPropertyChanged(nameof(ElapsedText));

    public void ApplyActivity(AgentActivity activity)
    {
        var existing = Activity.FirstOrDefault(item => item.Id == activity.Id);
        if (existing is not null)
        {
            existing.Apply(activity);
            return;
        }

        Activity.LastOrDefault()?.FlushBeforeNext();
        Activity.Add(new SubagentActivityViewModel(activity, IsRunning));
        OnPropertyChanged(nameof(HasActivity));
    }

    public void Dispose()
    {
        foreach (var activity in Activity) activity.Message?.Dispose();
        Activity.Clear();
    }

    [RelayCommand]
    private void Stop()
    {
        if (IsRunning && _task is not null) _stop(_task.Id);
    }
}

public sealed class SubagentActivityViewModel : ObservableObject
{
    private long _revision;

    public SubagentActivityViewModel(AgentActivity activity, bool running)
    {
        Id = activity.Id;
        IsText = activity.Kind == "text";
        IsThinking = activity.Kind == "thinking";
        IsTool = activity.Kind == "tool";
        IsTask = activity.Kind == "task";
        Label = activity.Label;
        TaskText = IsTask ? activity.Text : string.Empty;
        if (IsText || IsThinking)
        {
            Message = new MessageViewModel("assistant", activity.IsDelta ? string.Empty : activity.Text,
                DateTimeOffset.UtcNow) { IsStreaming = activity.IsDelta || running };
            if (activity.IsDelta)
            {
                Message.AppendDelta(activity.Text);
                Message.FlushPendingDelta();
            }
        }
        if (IsTool && activity.ToolDeltas is { Count: > 0 } tools)
        {
            Tool = new ToolCallViewModel(tools[0].Id, tools[0].Name);
            foreach (var tool in tools) Tool.Apply(tool);
        }
        _revision = activity.Revision;
        _ownerRunning = running;
    }

    public string Id { get; }
    public string Label { get; }
    public string TaskText { get; }
    public bool IsText { get; }
    public bool IsThinking { get; }
    public bool IsTool { get; }
    public bool IsTask { get; }
    public MessageViewModel? Message { get; }
    public ToolCallViewModel? Tool { get; }
    public string? RenderedMarkdown => Message?.IsStreaming == true ? null : Message?.Content;

    // A stopped sub-agent can leave its last call marked running.
    private bool _ownerRunning;
    public bool ShowsSpinner => _ownerRunning && Tool?.IsInFlight == true;

    public void SetOwnerRunning(bool running)
    {
        if (_ownerRunning == running) return;
        _ownerRunning = running;
        OnPropertyChanged(nameof(ShowsSpinner));
    }

    public void Apply(AgentActivity activity)
    {
        if (activity.Revision <= _revision) return;
        _revision = activity.Revision;
        if (Message is not null)
        {
            if (activity.IsDelta)
            {
                Message.AppendDelta(activity.Text);
                Message.FlushPendingDelta();
            }
            else Message.ReplaceContent(activity.Text);
            if (!Message.IsStreaming) OnPropertyChanged(nameof(RenderedMarkdown));
        }
        if (activity.ToolDeltas is { } tools)
        {
            foreach (var tool in tools) Tool?.Apply(tool);
            OnPropertyChanged(nameof(ShowsSpinner));
        }
    }

    public void FinishStreaming()
    {
        FlushBeforeNext();
    }

    public void FlushBeforeNext()
    {
        if (Message?.IsStreaming != true) return;
        Message.FlushPendingDelta();
        Message.IsStreaming = false;
        OnPropertyChanged(nameof(RenderedMarkdown));
    }

    public void ResumeStreaming()
    {
        if (Message is null || Message.IsStreaming) return;
        Message.IsStreaming = true;
        OnPropertyChanged(nameof(RenderedMarkdown));
    }
}
