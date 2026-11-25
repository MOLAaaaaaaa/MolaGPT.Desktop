using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MolaGPT.Presentation;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Rendering;

public abstract class ProcessTimelineStep
{
    protected ProcessTimelineStep(string key) => Key = key;
    public string Key { get; }
}

public sealed class ProcessThinkingStep : ProcessTimelineStep
{
    public ProcessThinkingStep(ThinkingSegmentViewModel segment)
        : base($"think:{segment.TimelineIndex}")
        => Segment = segment;

    public ThinkingSegmentViewModel Segment { get; }
}

public sealed class ProcessToolStep : ProcessTimelineStep
{
    public ProcessToolStep(ToolRow row)
        : base($"tool:{row.Tool.Id}")
        => Row = row;

    public ToolRow Row { get; }
}

internal abstract record ProcessRunInput;
internal sealed record ProcessThinkingInput(ThinkingSegmentViewModel Segment) : ProcessRunInput;
internal sealed record ProcessToolInput(IReadOnlyList<ToolCallViewModel> Tools) : ProcessRunInput;

/// <summary>
/// 思考与工具调用的统一过程时间线行（对齐 Mobile ProcessTimeline 与新版无底卡时间线设计）。
/// 将同一段正文前连续的多轮「思考 + 工具调用」聚合成一条可折叠轨迹，并保留桌面端完整的
/// ToolRow 详情能力与虚拟化懒解析特性。
/// </summary>
public sealed class ProcessTimelineRow : TranscriptRow, INotifyPropertyChanged, IDisposable
{
    private sealed class PersistedChoice
    {
        public bool UserControlled;
        public bool IsExpanded;
    }

    private sealed record HeaderState(string Title, string IconGlyph, bool IsActive, bool IsStopped);

    private static readonly ConditionalWeakTable<ThinkingSegmentViewModel, PersistedChoice> s_choices = new();

    private readonly List<ThinkingSegmentViewModel> _subscribedSegments = new();
    private readonly List<ToolRow> _subscribedTools = new();
    private ThinkingSegmentViewModel? _choiceAnchor;
    private HeaderState _header = new("思考过程", "\uEA80", false, false);
    private bool _hasFollowingBody;
    private bool _settled;
    private bool _isExpanded;
    private bool _userControlled;
    private bool _hasEmbeddedTools;
    private ToolRow? _activeEmbeddedTool;
    private bool _syncing;
    private bool _disposed;

    public ProcessTimelineRow(MessageViewModel message, string firstKey)
        : base(message, $"{message.RowKey()}:process:{firstKey}")
    {
        message.PropertyChanged += OnMessageChanged;
    }

    public ObservableCollection<ProcessTimelineStep> Steps { get; } = new();

    public string Title => _header.Title;
    public string IconGlyph => _header.IconGlyph;
    public bool IsActive => _header.IsActive;
    public bool IsStopped => _header.IsStopped;

    public bool HasFollowingBody => _hasFollowingBody;
    public bool IsSettled => _settled;

    public bool HasRunningBackgroundTool =>
        Steps.OfType<ProcessToolStep>().Any(step => step.Row.IsBackgroundRunning);

    public bool NeedsAutoCollapse =>
        !_userControlled && _isExpanded && !IsActive && Message.AutoCollapseThinkingOnComplete && !HasRunningBackgroundTool;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _userControlled = true;
            SaveChoice();
            if (_isExpanded == value) return;
            _isExpanded = value;
            Notify(nameof(IsExpanded));
            Notify(nameof(EstimatedHeight));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public double EstimatedHeight
    {
        get
        {
            if (!_isExpanded) return 34;
            double total = 38;
            foreach (var step in Steps)
            {
                total += step switch
                {
                    ProcessThinkingStep => 72,
                    ProcessToolStep tool => tool.Row.IsExpanded ? 180 : 30,
                    _ => 30
                };
            }
            return total;
        }
    }

    public string CopyableThinkingText() => string.Join(
        "\n\n",
        Steps.OfType<ProcessThinkingStep>()
            .Select(step => step.Segment.Source?.Trim())
            .Where(text => !string.IsNullOrEmpty(text)));

    internal void UpdateEmbeddedTools(bool hasEmbeddedTools, ToolRow? activeEmbeddedTool)
    {
        if (_hasEmbeddedTools == hasEmbeddedTools && ReferenceEquals(_activeEmbeddedTool, activeEmbeddedTool)) return;
        _hasEmbeddedTools = hasEmbeddedTools;
        _activeEmbeddedTool = activeEmbeddedTool;
        RefreshHeader();
    }

    internal void Sync(
        IReadOnlyList<ProcessRunInput> inputs,
        bool animateNewEntries,
        IReadOnlyDictionary<string, MarkupUnitBlock> markups,
        bool hasFollowingBody,
        bool settled,
        bool initial)
    {
        _syncing = true;
        try
        {
            _hasFollowingBody = hasFollowingBody;
            _settled = settled;

            var existingThinking = Steps.OfType<ProcessThinkingStep>()
                .ToDictionary(step => step.Segment);
            var existingTools = Steps.OfType<ProcessToolStep>()
                .ToDictionary(step => step.Row.Tool.Id, StringComparer.Ordinal);
            var keptTools = new HashSet<ToolRow>();

            var nextSteps = new List<ProcessTimelineStep>(inputs.Count);
            foreach (var input in inputs)
            {
                switch (input)
                {
                    case ProcessThinkingInput thinking:
                        if (!existingThinking.TryGetValue(thinking.Segment, out var thinkStep))
                            thinkStep = new ProcessThinkingStep(thinking.Segment);
                        nextSteps.Add(thinkStep);
                        break;
                    case ProcessToolInput tools when tools.Tools.Count > 0:
                        var firstId = tools.Tools[0].Id;
                        if (!existingTools.TryGetValue(firstId, out var toolStep))
                        {
                            var row = new ToolRow(Message, tools.Tools[0], nextSteps.Count)
                            {
                                AnimateEntrance = animateNewEntries,
                                OnUserExpanded = KeepOpen
                            };
                            toolStep = new ProcessToolStep(row);
                        }
                        toolStep.Row.SyncTools(tools.Tools);
                        toolStep.Row.SetMarkups(markups);
                        keptTools.Add(toolStep.Row);
                        nextSteps.Add(toolStep);
                        break;
                }
            }

            foreach (var oldStep in existingTools.Values)
            {
                if (!keptTools.Contains(oldStep.Row))
                {
                    oldStep.Row.PropertyChanged -= OnToolChanged;
                    oldStep.Row.Dispose();
                }
            }

            ReconcileSteps(nextSteps);
            RewireSubscriptions();

            if (_choiceAnchor is null)
            {
                _choiceAnchor = Steps.OfType<ProcessThinkingStep>().FirstOrDefault()?.Segment;
                if (_choiceAnchor is not null && s_choices.TryGetValue(_choiceAnchor, out var saved))
                {
                    _userControlled = saved.UserControlled;
                    _isExpanded = saved.IsExpanded;
                }
            }

            RefreshHeader();

            if (initial && !_userControlled)
            {
                var shouldOpen = IsActive || HasRunningBackgroundTool || !Message.AutoCollapseThinkingOnComplete;
                if (_isExpanded != shouldOpen)
                {
                    _isExpanded = shouldOpen;
                    Notify(nameof(IsExpanded));
                }
            }
            else if (!_userControlled && IsActive && !_isExpanded)
            {
                _isExpanded = true;
                Notify(nameof(IsExpanded));
            }

            Notify(nameof(HasFollowingBody));
            Notify(nameof(IsSettled));
            Notify(nameof(EstimatedHeight));
        }
        finally
        {
            _syncing = false;
        }
    }

    public void TryAutoCollapse(bool allowed)
    {
        if (!allowed || !NeedsAutoCollapse) return;
        _isExpanded = false;
        Notify(nameof(IsExpanded));
        Notify(nameof(EstimatedHeight));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void KeepOpen()
    {
        _userControlled = true;
        SaveChoice();
        if (!_isExpanded)
        {
            _isExpanded = true;
            Notify(nameof(IsExpanded));
            Notify(nameof(EstimatedHeight));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SaveChoice()
    {
        if (_choiceAnchor is null) return;
        var entry = s_choices.GetOrCreateValue(_choiceAnchor);
        entry.UserControlled = _userControlled;
        entry.IsExpanded = _isExpanded;
    }

    private void ReconcileSteps(List<ProcessTimelineStep> next)
    {
        var same = Steps.Count == next.Count;
        if (same)
        {
            for (var i = 0; i < next.Count; i++)
            {
                if (!ReferenceEquals(Steps[i], next[i]))
                {
                    same = false;
                    break;
                }
            }
        }
        if (same) return;

        for (var i = 0; i < next.Count; i++)
        {
            if (i < Steps.Count && ReferenceEquals(Steps[i], next[i])) continue;
            var existingIndex = Steps.IndexOf(next[i]);
            if (existingIndex >= 0) Steps.Move(existingIndex, i);
            else Steps.Insert(i, next[i]);
        }
        while (Steps.Count > next.Count) Steps.RemoveAt(Steps.Count - 1);
    }

    private void RewireSubscriptions()
    {
        foreach (var seg in _subscribedSegments) seg.PropertyChanged -= OnSegmentChanged;
        _subscribedSegments.Clear();
        foreach (var tool in _subscribedTools) tool.PropertyChanged -= OnToolChanged;
        _subscribedTools.Clear();

        foreach (var step in Steps)
        {
            switch (step)
            {
                case ProcessThinkingStep thinking:
                    thinking.Segment.PropertyChanged += OnSegmentChanged;
                    _subscribedSegments.Add(thinking.Segment);
                    break;
                case ProcessToolStep tool:
                    tool.Row.PropertyChanged += OnToolChanged;
                    _subscribedTools.Add(tool.Row);
                    break;
            }
        }
    }

    private void OnMessageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName is nameof(MessageViewModel.IsStreaming)
            or nameof(MessageViewModel.IsRevealing)
            or nameof(MessageViewModel.WasStopped))
        {
            RefreshHeader();
        }
    }

    private void OnSegmentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName is nameof(ThinkingSegmentViewModel.IsThinking)
            or nameof(ThinkingSegmentViewModel.ElapsedSeconds))
        {
            RefreshHeader();
        }
    }

    private void OnToolChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName is nameof(ToolRow.IsShimmering)
            or nameof(ToolRow.IsBackgroundRunning)
            or nameof(ToolRow.DisplayLabel)
            or nameof(ToolRow.IconGlyph)
            or nameof(ToolRow.IsExpanded))
        {
            RefreshHeader();
            if (!_syncing && e.PropertyName is nameof(ToolRow.IsExpanded) or nameof(ToolRow.IsBackgroundRunning))
            {
                if (e.PropertyName == nameof(ToolRow.IsExpanded))
                    Notify(nameof(EstimatedHeight));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void RefreshHeader()
    {
        var active = (Message.IsStreaming || Message.IsRevealing) && !_hasFollowingBody;
        var hasTools = _hasEmbeddedTools
            || Steps.OfType<ProcessToolStep>().Any()
            || Steps.OfType<ProcessThinkingStep>().Any(step =>
                step.Segment.Source?.Contains("data-tool-call", StringComparison.OrdinalIgnoreCase) == true);
        var stopped = Message.WasStopped && !_hasFollowingBody && !active;

        var totalSeconds = 0d;
        foreach (var step in Steps)
        {
            if (step is ProcessThinkingStep thinking && thinking.Segment.ElapsedSeconds > 0)
                totalSeconds += thinking.Segment.ElapsedSeconds;
        }

        HeaderState next;
        if (active)
        {
            var runningTool = Steps.OfType<ProcessToolStep>()
                .Select(step => step.Row)
                .LastOrDefault(row => row.IsShimmering)
                ?? (_activeEmbeddedTool is { IsShimmering: true } embedded ? embedded : null);

            if (runningTool is not null)
            {
                if (IsWebSearch(runningTool.Tool.Name))
                {
                    next = new HeaderState("正在联网搜索…", "\uE721", true, false);
                }
                else
                {
                    var raw = runningTool.DisplayLabel?.Trim();
                    var title = string.IsNullOrEmpty(raw)
                        ? "正在处理…"
                        : raw.StartsWith("正在", StringComparison.Ordinal)
                            ? (raw.EndsWith('…') || raw.EndsWith("...", StringComparison.Ordinal) ? raw : raw + "…")
                            : $"正在{raw}…";
                    var glyph = string.IsNullOrEmpty(runningTool.IconGlyph) ? "\uE90F" : runningTool.IconGlyph;
                    next = new HeaderState(title, glyph, true, false);
                }
            }
            else
            {
                var liveSeconds = (int)Math.Floor(totalSeconds);
                var title = liveSeconds >= 1 ? $"正在思考… {liveSeconds}s" : "正在思考…";
                next = new HeaderState(title, "\uEA80", true, false);
            }
        }
        else if (stopped)
        {
            next = new HeaderState(hasTools ? "过程已停止" : "思考已停止", "\uEA80", false, true);
        }
        else
        {
            var rounded = totalSeconds > 0 ? Math.Max(1, (int)Math.Round(totalSeconds)) : 0;
            var title = rounded > 0 ? $"已思考 {rounded}s" : "思考过程";
            next = new HeaderState(title, "\uEA80", false, false);
        }

        if (next == _header) return;
        var wasActive = _header.IsActive;
        _header = next;
        Notify(nameof(Title));
        Notify(nameof(IconGlyph));
        Notify(nameof(IsActive));
        Notify(nameof(IsStopped));
        if (wasActive && !next.IsActive && !_syncing)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsWebSearch(string? name) =>
        string.Equals(name, "search_web", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "web_search", StringComparison.OrdinalIgnoreCase);

    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Changed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Message.PropertyChanged -= OnMessageChanged;
        foreach (var seg in _subscribedSegments) seg.PropertyChanged -= OnSegmentChanged;
        _subscribedSegments.Clear();
        foreach (var tool in _subscribedTools)
        {
            tool.PropertyChanged -= OnToolChanged;
            tool.Dispose();
        }
        _subscribedTools.Clear();
    }
}
