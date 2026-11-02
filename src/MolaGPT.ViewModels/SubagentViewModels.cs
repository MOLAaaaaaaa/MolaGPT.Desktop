using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using MolaGPT.Core.Chat;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Chat.Tasks;

namespace MolaGPT.ViewModels;

public sealed partial class SubagentViewModel : ObservableObject, IDisposable
{
    private readonly AgentHandle? _agent;
    private readonly string? _serverId;
    private JsonObject? _server;
    private string? _serverReport;
    private readonly Action<string>? _stop;
    private AgentTask? _task;

    public SubagentViewModel(AgentHandle agent, Action<string> stop)
    {
        _agent = agent;
        _stop = stop;
    }

    public SubagentViewModel(string id, JsonObject agent)
    {
        _serverId = id;
        UpdateServer(agent);
    }

    public bool IsServer => _serverId is not null;
    public bool CanStop => !IsServer && IsRunning;
    public string Id => _serverId ?? _agent!.Id;
    public string Label => _server is { } agent ? ServerSubagents.Text(agent, "label") : _agent!.Label;
    public string Model => IsServer ? string.Empty : _agent!.ModelId;
    public string Subtitle => IsServer ? ElapsedText + (_server!["tools"]?.GetValue<int>() is > 0 ? $" · 工具调用 {_server["tools"]} 次" : "") : Model;
    public bool IsRunning => _server is { } agent ? ServerSubagents.Text(agent, "status") == "running" : _task?.IsRunning == true;
    public bool IsCompleted => _server is { } agent ? ServerSubagents.Text(agent, "status") == "completed" : _task?.Status == AgentTaskStatus.Completed;
    public bool IsFailed => _server is { } agent ? ServerSubagents.Text(agent, "status") is "failed" or "timeout" or "rejected" : _task?.Status == AgentTaskStatus.Failed;
    public string StatusText => _server is { } agent ? ServerSubagents.Status(agent) : _task?.Status switch
    {
        AgentTaskStatus.Running => "运行中", AgentTaskStatus.Completed => "已完成",
        AgentTaskStatus.Failed => "失败", AgentTaskStatus.Cancelled => "已停止",
        AgentTaskStatus.Interrupted => "已中断", _ => "等待中"
    };
    public string ElapsedText => _server is { } agent
        ? TaskTools.FormatElapsed(TimeSpan.FromMilliseconds(Math.Max(0,
            (agent["ended_at"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) - agent["started_at"]!.GetValue<long>())))
        : _task is null || _task.IsRestored ? string.Empty : TaskTools.FormatElapsed(_task.Elapsed);
    public string? FailureText => _server is { } agent ? ServerSubagents.Text(agent, "error") : _task?.Status == AgentTaskStatus.Failed ? _task.Report : null;
    public bool HasFailure => !string.IsNullOrWhiteSpace(FailureText);
    public string CurrentAction => !IsRunning ? string.Empty : _server is { } agent ? ServerSubagents.Text(agent, "step") : _agent!.RecentOutput.Replace('\n', ' ').Trim();
    public ObservableCollection<SubagentActivityViewModel> Activity { get; } = new();
    public bool HasActivity => Activity.Count > 0;

    public void UpdateServer(JsonObject agent)
    {
        _server = agent;
        var report = ServerSubagents.Report(agent);
        var task = ServerSubagents.Text(agent, "task");
        var signature = task + "\n" + report;
        if (_serverReport != signature)
        {
            _serverReport = signature;
            foreach (var activity in Activity) activity.Message?.Dispose();
            Activity.Clear();
            if (task.Length > 0) Activity.Add(new SubagentActivityViewModel(new AgentActivity("task", "task", "任务", "completed", task, null, 1), false));
            if (report.Length > 0) Activity.Add(new SubagentActivityViewModel(new AgentActivity("report", "text", "报告", "completed", report, null, 1), false));
        }
        foreach (var name in new[] { nameof(Label), nameof(Subtitle), nameof(IsRunning), nameof(IsCompleted), nameof(IsFailed), nameof(StatusText), nameof(ElapsedText), nameof(FailureText), nameof(HasFailure), nameof(CurrentAction), nameof(HasActivity) })
            OnPropertyChanged(name);
    }

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
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(FailureText));
        OnPropertyChanged(nameof(HasFailure));
        OnPropertyChanged(nameof(CurrentAction));
    }

    public void RefreshProgress() => OnPropertyChanged(nameof(CurrentAction));
    public void Tick()
    {
        OnPropertyChanged(nameof(ElapsedText));
        if (IsServer) OnPropertyChanged(nameof(Subtitle));
    }

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
        if (IsRunning && _task is not null) _stop?.Invoke(_task.Id);
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
