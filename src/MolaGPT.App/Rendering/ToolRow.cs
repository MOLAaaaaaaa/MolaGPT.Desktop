using System.Collections.ObjectModel;
using System.ComponentModel;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Rendering;

public sealed class ToolRow : TranscriptRow, INotifyPropertyChanged, IDisposable
{
    private IReadOnlyList<ToolCallViewModel> _tools = [];
    private bool _isExpanded;
    private bool _bodyLoaded;
    private bool _isArchived;
    private HeaderState? _header;

    private sealed record HeaderState(string Label, string Preview, string Count, string Status,
        bool Successful, bool Error, bool Background, bool Stop, string Glyph, bool Group, string ErrorDetail,
        bool Shimmer, bool Spinner);

    public ToolRow(MessageViewModel message, ToolCallViewModel tool, int segment)
        : base(message, $"{message.RowKey()}:tool:{tool.Id}")
    {
        message.PropertyChanged += OnMessageChanged;
        SyncTools([tool]);
    }

    public ToolCallViewModel Tool => _tools[0];
    public int CallCount => _tools.Count;
    internal IEnumerable<string> ToolIds => _tools.Select(tool => tool.Id);
    public bool IsGroup => _tools.Count > 1;
    public string DisplayLabel => Tool.DisplayLabel;
    public string HeaderArgPreview => Tool.HeaderArgPreview;
    public bool HasHeaderArgPreview => Tool.HasHeaderArgPreview;
    public string CountText => IsGroup ? $"+{_tools.Count - 1}" : string.Empty;
    public string IconGlyph => Tool.DisplayIconGlyph;
    public bool IsSuccessful => _tools.All(IsToolSuccessful);
    public bool IsError => _tools.Any(IsToolError);
    public bool IsBackgroundRunning => _tools.Any(tool => tool.IsBackgroundRunning);
    public bool CanStop => !IsGroup && Tool.IsBackgroundRunning;

    /// <summary>When the work went from running to done in front of the user, on
    /// the <see cref="FrameLoop.Now"/> clock; NaN for anything that arrived done.</summary>
    public double CompletedAt { get; private set; } = double.NaN;

    // A call still executing in the live turn. Gated on the message so a call left
    // "running" by an interrupted turn does not animate in the history forever.
    private bool IsInFlight => (Message.IsStreaming || Message.IsPending) && _tools.Any(tool => tool.IsInFlight);

    /// <summary>The ring means the work went to the background and the reply is
    /// not waiting on it; whatever the reply is waiting on sweeps its row instead.
    /// Never both on one header.</summary>
    public bool ShowsSpinner => IsBackgroundRunning;
    public bool IsShimmering => IsInFlight && !ShowsSpinner;
    public string ErrorDetail
    {
        get
        {
            var failed = _tools.FirstOrDefault(IsToolError);
            var detail = failed?.TaskCardOutput ?? failed?.Detail;
            if (string.IsNullOrWhiteSpace(detail)) return string.Empty;
            return detail[..Math.Min(detail.Length, 200)].Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }
    public bool HasErrorDetail => IsError && ErrorDetail.Length > 0;
    public ObservableCollection<ToolRow> GroupItems { get; } = new();
    public Action? OnUserExpanded { get; set; }
    public bool AnimateEntrance { get; set; }

    public string StatusText
    {
        get
        {
            if (IsError) return IsGroup ? $"{_tools.Count(IsToolError)} 失败" : "失败";
            if (IsBackgroundRunning) return "后台运行";
            if (IsSuccessful) return IsGroup ? $"{CallCount} 步完成" : Tool.StatusText;
            if (_tools.FirstOrDefault(tool => tool.IsReviewing) is { } reviewing) return reviewing.StatusText;
            if (IsGroup) return $"{_tools.Count(IsToolSuccessful)}/{CallCount}";
            if (Tool.BackgroundTaskId is not null) return Tool.TaskState switch
            {
                "cancelled" => "已停止",
                "interrupted" => "已中断",
                "stop_requested" => "停止中",
                _ => "状态未知"
            };
            return Tool.StatusText;
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Notify(nameof(IsExpanded));
            if (!value) return;
            OnUserExpanded?.Invoke();
            if (_bodyLoaded) return;
            _bodyLoaded = true;
            SyncGroupItems();
            Notify(nameof(BodyContent));
        }
    }

    public bool IsArchived
    {
        get => _isArchived;
        set
        {
            if (_isArchived == value) return;
            _isArchived = value;
            Notify(nameof(IsArchived));
        }
    }

    // Payload controls are created on first expansion, never on each streamed delta.
    public ToolRow? BodyContent => _bodyLoaded ? this : null;
    public bool IsArgumentsExpanded { get; set; }
    public bool IsResultExpanded { get; set; }

    public void SyncTools(IReadOnlyList<ToolCallViewModel> tools)
    {
        if (_tools.Count == tools.Count && _tools.SequenceEqual(tools)) return;
        var firstChanged = _tools.Count == 0 || !ReferenceEquals(_tools[0], tools[0]);
        var wasSingleExpanded = !IsGroup && _isExpanded && _bodyLoaded;
        foreach (var tool in _tools) tool.PropertyChanged -= OnToolChanged;
        _tools = tools.ToArray();
        foreach (var tool in _tools) tool.PropertyChanged += OnToolChanged;
        if (firstChanged) Notify(nameof(Tool));
        if (_bodyLoaded)
        {
            SyncGroupItems();
            if (wasSingleExpanded && IsGroup)
            {
                GroupItems[0].IsArgumentsExpanded = IsArgumentsExpanded;
                GroupItems[0].IsResultExpanded = IsResultExpanded;
                GroupItems[0].IsExpanded = true;
            }
        }
        Refresh();
        ToolActivity?.Invoke(this, EventArgs.Empty);
    }

    private void SyncGroupItems()
    {
        if (!IsGroup)
        {
            foreach (var child in GroupItems) child.Dispose();
            GroupItems.Clear();
            return;
        }
        for (var i = 0; i < _tools.Count; i++)
        {
            if (i < GroupItems.Count && ReferenceEquals(GroupItems[i].Tool, _tools[i])) continue;
            var child = new ToolRow(Message, _tools[i], i) { OnUserExpanded = () => OnUserExpanded?.Invoke() };
            if (i < GroupItems.Count)
            {
                GroupItems[i].Dispose();
                GroupItems[i] = child;
            }
            else GroupItems.Add(child);
        }
        while (GroupItems.Count > _tools.Count)
        {
            GroupItems[^1].Dispose();
            GroupItems.RemoveAt(GroupItems.Count - 1);
        }
    }

    private static bool IsToolSuccessful(ToolCallViewModel tool) => tool.IsCompleted
        && (tool.BackgroundTaskId is null || tool.TaskState == "completed");
    private static bool IsToolError(ToolCallViewModel tool) => tool.IsError
        || tool.TaskState is "failed" or "interrupted";

    private void OnToolChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ToolCallViewModel.Status) or nameof(ToolCallViewModel.ArgumentsJson)
            or nameof(ToolCallViewModel.ResultPreviewJson) or nameof(ToolCallViewModel.TaskState)
            or nameof(ToolCallViewModel.Name) or nameof(ToolCallViewModel.Detail))
            ToolActivity?.Invoke(this, EventArgs.Empty);
        Refresh();
    }

    private void OnMessageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MessageViewModel.IsStreaming) or nameof(MessageViewModel.IsPending))
            Refresh();
    }

    private void Refresh()
    {
        var next = new HeaderState(DisplayLabel, HeaderArgPreview, CountText, StatusText,
            IsSuccessful, IsError, IsBackgroundRunning, CanStop, IconGlyph, IsGroup, ErrorDetail,
            IsShimmering, ShowsSpinner);
        if (next == _header) return;
        if (_header is { } previous && (previous.Shimmer || previous.Spinner) && next.Successful)
        {
            CompletedAt = FrameLoop.Now;
            Notify(nameof(CompletedAt));
        }
        _header = next;
        foreach (var name in new[] { nameof(DisplayLabel), nameof(HeaderArgPreview), nameof(HasHeaderArgPreview),
                     nameof(CountText), nameof(StatusText), nameof(IsSuccessful), nameof(IsError),
                     nameof(IsBackgroundRunning), nameof(CanStop), nameof(IconGlyph),
                     nameof(IsGroup), nameof(CallCount), nameof(ErrorDetail), nameof(HasErrorDetail),
                     nameof(IsShimmering), nameof(ShowsSpinner) })
            Notify(name);
    }

    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ToolActivity;

    public void Dispose()
    {
        Message.PropertyChanged -= OnMessageChanged;
        foreach (var tool in _tools) tool.PropertyChanged -= OnToolChanged;
        foreach (var child in GroupItems) child.Dispose();
    }
}
