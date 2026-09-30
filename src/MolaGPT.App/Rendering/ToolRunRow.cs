using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using MolaGPT.ViewModels;

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
    public bool HasSummary => Entries.Any(entry => entry.IsArchived);
    public string Summary => $"已完成 {Entries.Where(entry => entry.IsArchived).Sum(entry => entry.CallCount)} 次调用";
    public string Preview => string.Join(" · ", Entries.Where(entry => entry.IsArchived)
        .Take(2).Select(entry => string.IsNullOrWhiteSpace(entry.HeaderArgPreview)
            ? entry.DisplayLabel : $"{entry.DisplayLabel} {entry.HeaderArgPreview}"));
    public double EstimatedHeight => (HasSummary ? 30 : 0)
        + Entries.Where(entry => IsExpanded || !entry.IsArchived).Sum(entry => entry.IsExpanded ? 180d : 30d) + 8;

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
        && Entries.Any(entry => !entry.IsArchived && entry.IsSuccessful);

    public void Sync(IReadOnlyList<IReadOnlyList<ToolCallViewModel>> groups, bool animateNewEntries)
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
        RefreshSummary();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshSummary()
    {
        Notify(nameof(HasSummary));
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
