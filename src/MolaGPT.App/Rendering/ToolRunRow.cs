using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using MolaGPT.ViewModels;
using MolaGPT.Presentation;

namespace MolaGPT.App.Rendering;

public sealed class ToolRunRow : TranscriptRow, INotifyPropertyChanged, IDisposable
{
    private const int ProseCollapseCharacters = 240;
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1);
    private bool _userControlled;
    private bool _isExpanded;
    private bool _settled;
    private int _followingProseCharacters;
    private long _lastToolActivity = Stopwatch.GetTimestamp();
    private bool _syncing;

    public ToolRunRow(MessageViewModel message, string firstToolId)
        : base(message, $"{message.RowKey()}:tool-run:{firstToolId}") { }

    public ObservableCollection<ToolRow> Entries { get; } = new();
    // 只有归档条数 ≥ 2 时才折叠出汇总行；单条工具本身已是一行紧凑折叠控件，
    // 外层再套一层「已完成 xxx」既省不下高度又多一次无意义点击。
    public bool HasSummary => Entries.Count(entry => entry.IsArchived) >= 2;

    public string IconGlyph
    {
        get
        {
            var archived = ArchivedEntries();
            if (archived.Count == 0) return "\uE90F";
            if (archived.All(IsWebSearch)) return "\uE721";
            if (archived.Count == 1 && !string.IsNullOrEmpty(archived[0].IconGlyph))
                return archived[0].IconGlyph;
            return "\uE90F";
        }
    }

    public string Summary
    {
        get
        {
            var archived = ArchivedEntries();
            if (archived.Count == 0) return string.Empty;
            var total = archived.Sum(entry => entry.CallCount);
            if (archived.All(IsWebSearch))
                return total == 1 ? "已完成联网搜索" : $"已完成 {total} 次联网搜索";
            if (archived.Count == 1 && archived[0].CallCount == 1)
            {
                var label = archived[0].DisplayLabel?.Trim();
                if (!string.IsNullOrEmpty(label))
                    return IsCompletedLabel(label) ? label : $"已完成 {label}";
            }
            return $"已完成 {total} 项操作";
        }
    }

    public string Preview
    {
        get
        {
            var archived = ArchivedEntries();
            if (archived.Count == 0) return string.Empty;
            if (archived.All(IsWebSearch))
            {
                return string.Join(" · ", archived
                    .Select(entry => entry.HeaderArgPreview)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Take(2));
            }
            if (archived.Count == 1 && archived[0].CallCount == 1)
                return archived[0].HeaderArgPreview ?? string.Empty;
            return string.Join(" · ", archived
                .Take(2).Select(entry => string.IsNullOrWhiteSpace(entry.HeaderArgPreview)
                    ? entry.DisplayLabel : $"{entry.DisplayLabel} {entry.HeaderArgPreview}"));
        }
    }

    public double EstimatedHeight => (HasSummary ? 34 : 0)
        + Entries.Where(entry => !HasSummary || IsExpanded || !entry.IsArchived)
            .Sum(entry => entry.IsExpanded ? 180d : 30d) + 8;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _userControlled = true;
            if (_isExpanded == value) return;
            _isExpanded = value;
            Notify(nameof(IsExpanded));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool NeedsProseSignal => !_userControlled && !_settled && RetiredToolIds is null;
    public IReadOnlySet<string>? RetiredToolIds { get; private set; }
    public bool NeedsAutoCollapse => !_userControlled
        && (_settled || RetiredToolIds is not null || _followingProseCharacters >= ProseCollapseCharacters)
        && Entries.Count(entry => entry.IsSuccessful) >= 2
        && Entries.Any(entry => !entry.IsArchived && entry.IsSuccessful);

    public void Sync(IReadOnlyList<IReadOnlyList<ToolCallViewModel>> groups, bool animateNewEntries,
        IReadOnlyDictionary<string, MarkupUnitBlock> markup)
    {
        _syncing = true;
        try
        {
            for (var i = 0; i < groups.Count; i++)
            {
                var tools = groups[i];
                var existing = Entries.FirstOrDefault(entry => entry.Tool.Id == tools[0].Id);
                if (existing is null)
                {
                    existing = new ToolRow(Message, tools[0], i)
                    {
                        AnimateEntrance = animateNewEntries,
                        OnUserExpanded = KeepOpen
                    };
                    existing.ToolActivity += OnToolActivity;
                    existing.PropertyChanged += OnEntryChanged;
                    Entries.Insert(i, existing);
                    _lastToolActivity = Stopwatch.GetTimestamp();
                }
                else
                {
                    var previousIndex = Entries.IndexOf(existing);
                    if (previousIndex != i) Entries.Move(previousIndex, i);
                }
                existing.SyncTools(tools);
                existing.SetMarkups(markup);
                if (existing.IsArchived && !existing.IsSuccessful) existing.IsArchived = false;
            }
            while (Entries.Count > groups.Count)
            {
                var removed = Entries[^1];
                removed.ToolActivity -= OnToolActivity;
                removed.PropertyChanged -= OnEntryChanged;
                removed.Dispose();
                Entries.RemoveAt(Entries.Count - 1);
            }
            EnsureArchiveMinimum();
        }
        finally { _syncing = false; }
        RefreshSummary();
    }

    public void UpdateContext(bool settled, int followingProseCharacters)
    {
        _settled = settled;
        _followingProseCharacters = followingProseCharacters;
    }

    public void TryAutoCollapse(bool allowed, bool initial = false)
    {
        if (!allowed || !NeedsAutoCollapse) return;
        if (!_settled && Stopwatch.GetElapsedTime(_lastToolActivity) < QuietPeriod) return;
        RetiredToolIds ??= Entries.SelectMany(entry => entry.ToolIds).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            if (!entry.IsSuccessful) continue;
            entry.IsArchived = true;
            if (initial) entry.AnimateEntrance = false;
        }
        RefreshSummary();
    }

    public void KeepOpen()
    {
        _userControlled = true;
        if (!_isExpanded && HasSummary)
        {
            _isExpanded = true;
            Notify(nameof(IsExpanded));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnToolActivity(object? sender, EventArgs e)
    {
        _lastToolActivity = Stopwatch.GetTimestamp();
        if (!_syncing) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing) return;
        if (sender is ToolRow { IsArchived: true, IsSuccessful: false } entry)
            entry.IsArchived = false;
        EnsureArchiveMinimum();
        RefreshSummary();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureArchiveMinimum()
    {
        if (Entries.Count(entry => entry.IsArchived) == 1)
        {
            foreach (var entry in Entries)
                if (entry.IsArchived) entry.IsArchived = false;
        }
    }

    private List<ToolRow> ArchivedEntries() => Entries.Where(entry => entry.IsArchived).ToList();

    private static bool IsCompletedLabel(string label) =>
        label.StartsWith("已", StringComparison.Ordinal)
        || label.EndsWith("完成", StringComparison.Ordinal)
        || label.EndsWith("失败", StringComparison.Ordinal)
        || label.EndsWith("停止", StringComparison.Ordinal);

    private static bool IsWebSearch(ToolRow entry) =>
        string.Equals(entry.Tool.Name, "search_web", StringComparison.OrdinalIgnoreCase)
        || string.Equals(entry.Tool.Name, "web_search", StringComparison.OrdinalIgnoreCase);

    private void RefreshSummary()
    {
        Notify(nameof(HasSummary));
        Notify(nameof(IconGlyph));
        Notify(nameof(Summary));
        Notify(nameof(Preview));
        Notify(nameof(EstimatedHeight));
    }

    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Changed;

    public void Dispose()
    {
        foreach (var entry in Entries)
        {
            entry.ToolActivity -= OnToolActivity;
            entry.PropertyChanged -= OnEntryChanged;
            entry.Dispose();
        }
    }
}
