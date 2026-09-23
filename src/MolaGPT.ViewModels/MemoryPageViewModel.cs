using CommunityToolkit.Mvvm.ComponentModel;
using MolaGPT.Core.Memory;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels.Services;
using MemorySection = MolaGPT.Core.Personalization.MemorySection;

namespace MolaGPT.ViewModels;

/// <summary>One row in the memory page. Carries the entry it came from so an
/// edit goes back to the same line of the same file.</summary>
public sealed partial class LocalMemoryEntryRow : ObservableObject
{
    public LocalMemoryEntryRow(MemoryEntry entry, DateOnly today)
    {
        Entry = entry;
        _text = entry.Text;
        SectionLabel = MemorySectionRules.Heading(entry.Section);
        OriginLabel = entry.Origin switch
        {
            MemoryOrigin.Manual => "手动添加",
            MemoryOrigin.Confirmed => "已确认",
            MemoryOrigin.Tool => "模型记录",
            _ => "自动整理"
        };
        var effective = entry.EffectiveConfidence(today);
        ConfidenceLabel = entry.IsPermanent ? "长期保留" : $"可信度 {effective:0.00}";
        IsFaded = !entry.IsPermanent && effective < 0.15;
    }

    public MemoryEntry Entry { get; }
    public string SectionLabel { get; }
    public string OriginLabel { get; }
    public string ConfidenceLabel { get; }

    /// <summary>Decayed past the injection floor: still in the file, still
    /// editable here, simply not being sent any more.</summary>
    public bool IsFaded { get; }

    [ObservableProperty] private string _text;
    [ObservableProperty] private bool _isEditing;

    public bool IsDirty => !string.Equals(Text.Trim(), Entry.Text, StringComparison.Ordinal);
}

public sealed record LocalMemoryTopicRow(MemoryTopic Topic, IReadOnlyList<LocalMemoryEntryRow> Entries)
{
    public string Id => Topic.Id;
    public string Title => Topic.Title;
    public string Summary => Topic.Summary;
    public string UpdatedLabel => Entries.Select(row => row.Entry.LastReinforced).Append(Topic.UpdatedAt)
        .Where(date => date is not null).Max()?.ToString("yyyy-MM-dd") ?? "未记录日期";
}

public sealed record LocalMemoryGroupRow(string Name, IReadOnlyList<LocalMemoryTopicRow> Topics);

/// <summary>
/// The memory settings page. Reads through <see cref="MemoryService"/> so the
/// numbers shown here are the ones a request would actually send — a second
/// estimate would eventually disagree, and the visible one is the one the user
/// would believe.
/// </summary>
public sealed partial class MemoryPageViewModel : ObservableObject
{
    private readonly MemoryService _memory;
    private readonly MemoryConsolidator _consolidator;
    private readonly SettingsViewModel _settings;
    private readonly Action<Action> _dispatch;

    public MemoryPageViewModel(MemoryService memory, MemoryConsolidator consolidator, SettingsViewModel settings,
        Action<Action> dispatch)
    {
        _memory = memory;
        _consolidator = consolidator;
        _settings = settings;
        _dispatch = dispatch;
        _memory.Changed += (_, _) => _dispatch(Refresh);
        // 称呼's dot answers to two switches that live over there, not here.
        _settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SettingsViewModel.ShowStatusDots)
                or nameof(SettingsViewModel.MemoryEnabled))
                _dispatch(() => OnPropertyChanged(nameof(ProfileNeedsName)));
        };
        _isConsolidating = _consolidator.IsRunning;
        _consolidator.RunningChanged += running => _dispatch(() => IsConsolidating = running);
    }

    [ObservableProperty] private IReadOnlyList<LocalMemoryEntryRow> _entries = [];
    [ObservableProperty] private IReadOnlyList<LocalMemoryCandidateRow> _candidates = [];
    [ObservableProperty] private IReadOnlyList<LocalMemoryGroupRow> _groups = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsDetailVisible))]
    private LocalMemoryTopicRow? _selectedTopic;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsDetailVisible))]
    private bool _isEditingTopic;
    [ObservableProperty] private string _topicTitle = "";
    [ObservableProperty] private string _topicSummary = "";
    [ObservableProperty] private string _topicGroup = MemoryTopics.Groups[0];
    public IReadOnlyList<string> GroupChoices => MemoryTopics.Groups;
    public bool IsOverview => SelectedTopic is null && !IsEditingTopic;
    public bool IsDetailVisible => SelectedTopic is not null || IsEditingTopic;

    public IReadOnlyList<string> SectionChoices { get; } =
        MemorySectionRules.All.Select(MemorySectionRules.Heading).ToArray();

    public IReadOnlyList<int> BudgetChoices { get; } = MemoryProjector.BudgetOptions;

    [ObservableProperty] private string _newEntryText = string.Empty;
    [ObservableProperty] private string _newEntrySection = MemorySectionRules.Heading(MemorySection.Identity);
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWriteLocked))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWriteLocked))]
    private bool _isConsolidating;
    public bool IsWriteLocked => IsBusy || IsConsolidating;
    [ObservableProperty] private bool _hasCandidates;
    [ObservableProperty] private bool _isEmpty = true;

    public string MemoryFolder => _memory.Files.Root;

    // ---- 个人资料 (profile.md) ---------------------------------------------
    //
    // Four named fields rather than a free-text paragraph: 称呼 has to be a value
    // something else can read, not a sentence someone would have to parse out.
    // The 氛围模式 identity drop-down's 「沿用个人资料」 reads exactly this name.
    //
    // The model cannot write this file — it may only tag one of its own entries
    // with a profile_key, and MemoryProjector lets the file win over that tag.
    // So what the user types here is the last word on the subject.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileNeedsName))]
    private string _profileName = "";
    [ObservableProperty] private string _profileOccupation = "";
    [ObservableProperty] private string _profileLocation = "";
    [ObservableProperty] private string _profileLanguage = "";

    /// <summary>
    /// The 称呼 row's red dot, same device and the same two gates as 整理模型's:
    /// nothing is unfinished while the feature is switched off, and the user can
    /// turn the dots off altogether in 外观.
    ///
    /// Not an error — memory works without a 称呼 — but it is the one field with
    /// a second consumer: blank, 氛围模式's 「沿用个人资料」 has nothing to offer.
    /// </summary>
    public bool ProfileNeedsName =>
        _settings.ShowStatusDots && _settings.MemoryEnabled && string.IsNullOrWhiteSpace(ProfileName);

    private void LoadProfile()
    {
        var profile = _memory.Profile();
        ProfileName = profile.Get(MemoryProfile.PreferredName) ?? "";
        ProfileOccupation = profile.Get(MemoryProfile.Occupation) ?? "";
        ProfileLocation = profile.Get(MemoryProfile.Location) ?? "";
        ProfileLanguage = profile.Get(MemoryProfile.Language) ?? "";
    }

    /// <summary>
    /// Called when a field commits, not on every keystroke: each save rewrites
    /// profile.md and moves its mtime, and every reader re-reads on that. A
    /// no-op edit is dropped here so tabbing through the card does not churn the
    /// file or bounce a refresh back at the boxes being tabbed through.
    /// </summary>
    public void SaveProfile()
    {
        var current = _memory.Profile();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MemoryProfile.PreferredName] = ProfileName.Trim(),
            [MemoryProfile.Occupation] = ProfileOccupation.Trim(),
            [MemoryProfile.Location] = ProfileLocation.Trim(),
            [MemoryProfile.Language] = ProfileLanguage.Trim()
        };
        if (MemoryProfile.Keys.All(key =>
            string.Equals(current.Get(key) ?? "", fields[key], StringComparison.Ordinal))) return;
        _memory.SaveProfile(new MemoryProfile(fields));
    }

    public void Refresh()
    {
        if (_consolidator.IsRunning || IsEditingTopic) return;
        LoadProfile();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var entries = _memory.Entries();
        Entries = MemorySectionRules.All
            .SelectMany(section => entries.Where(entry => entry.Section == section))
            .Select(entry => new LocalMemoryEntryRow(entry, today)).ToArray();
        var topics = _memory.Topics().Select(topic => new LocalMemoryTopicRow(topic,
            Entries.Where(row => MemoryTopics.EntryTopicId(row.Entry) == topic.Id).ToArray()))
            .Where(topic => topic.Entries.Count > 0).ToArray();
        Groups = MemoryTopics.Groups.Select(group => new LocalMemoryGroupRow(group,
            topics.Where(topic => topic.Topic.Group == group).ToArray())).Where(group => group.Topics.Count > 0).ToArray();
        if (SelectedTopic is { } selected) SelectedTopic = topics.FirstOrDefault(topic => topic.Id == selected.Id);
        Candidates = _memory.Candidates();
        HasCandidates = Candidates.Count > 0;
        IsEmpty = Entries.Count == 0;

        var projection = MemoryProjector.Project(
            _memory.Entries(), _memory.Profile(), _settings.MemoryBudgetTokens);
        Summary = Entries.Count == 0
            ? "暂无记忆。可在此添加，或编辑 MEMORY.md。"
            : $"共 {Entries.Count} 条 · 本轮使用 {projection.Injected} 条"
              + (projection.Skipped > 0 ? $" · 另有 {projection.Skipped} 条暂不带入" : string.Empty);
    }

    public void OpenTopic(LocalMemoryTopicRow topic)
    {
        SelectedTopic = topic;
        NewEntrySection = MemorySectionRules.Heading(topic.Entries[0].Entry.Section);
        NewEntryText = "";
        Status = "";
    }

    public void BackToOverview()
    {
        IsEditingTopic = false;
        SelectedTopic = null;
        Status = "";
        Refresh();
    }

    public void BeginTopicEdit(bool create)
    {
        if (create) SelectedTopic = null;
        TopicTitle = SelectedTopic?.Title ?? "";
        TopicSummary = SelectedTopic?.Summary ?? "";
        TopicGroup = SelectedTopic?.Topic.Group ?? MemoryTopics.Groups[0];
        NewEntryText = "";
        Status = "";
        IsEditingTopic = true;
    }

    public void CancelTopicEdit()
    {
        IsEditingTopic = false;
        Status = "";
        Refresh();
    }

    public void SaveTopic()
    {
        var title = TopicTitle.Trim();
        if (title.Length == 0 || title.Length > 60 || TopicSummary.Length > 240)
        {
            Status = "主题名称需为 1–60 字，摘要最多 240 字。";
            return;
        }
        var sameName = _memory.Topics().FirstOrDefault(topic => topic.Id != SelectedTopic?.Id && topic.Group == TopicGroup
            && MemoryGuards.NormalizeKey(topic.Title) == MemoryGuards.NormalizeKey(title));
        if (sameName is not null && (SelectedTopic is not null
            || Entries.Any(row => MemoryTopics.EntryTopicId(row.Entry) == sameName.Id)))
        {
            Status = "已有同名主题，请在该主题中补充记录。";
            return;
        }
        if (SelectedTopic is null && MemoryGuards.RejectionReason(NewEntryText, _settings.MemoryAllowSensitive) is { } reason)
        {
            Status = reason;
            return;
        }
        if (SelectedTopic is null && _memory.Entries().Any(entry =>
            MemoryGuards.NormalizeKey(entry.Text) == MemoryGuards.NormalizeKey(NewEntryText)))
        {
            Status = "已有相同记录，请在原主题中编辑。";
            return;
        }
        var topic = _memory.Files.SaveTopic(new MemoryTopic(SelectedTopic?.Id ?? sameName?.Id ?? Guid.NewGuid().ToString("N"),
            TopicGroup, title, TopicSummary.Trim()));
        if (SelectedTopic is null)
        {
            var added = _memory.AddManual(MemoryTopics.DefaultSection(topic.Group), NewEntryText, topic.Id);
            if (!added.Ok) { Status = added.Message ?? "写入失败。"; return; }
        }
        IsEditingTopic = false;
        Refresh();
        SelectedTopic = Groups.SelectMany(group => group.Topics).First(row => row.Id == topic.Id);
        NewEntryText = "";
        Status = "";
    }

    public void DeleteSelectedTopic()
    {
        if (SelectedTopic is null) return;
        var result = _memory.DeleteTopic(SelectedTopic.Id);
        if (!result.Ok) { Status = result.Message ?? "删除失败。"; return; }
        BackToOverview();
    }

    public void Add()
    {
        var text = NewEntryText.Trim();
        if (text.Length == 0) return;

        var section = MemorySectionRules.All.FirstOrDefault(
            candidate => MemorySectionRules.Heading(candidate) == NewEntrySection);
        var result = _memory.AddManual(section, text, SelectedTopic?.Id);
        Status = result.Ok ? string.Empty : result.Message ?? string.Empty;
        if (result.Ok) NewEntryText = string.Empty;
        Refresh();
    }

    public void Save(LocalMemoryEntryRow row)
    {
        if (!row.IsDirty) return;
        var result = _memory.EditManual(row.Entry, row.Text);
        Status = result.Ok ? string.Empty : result.Message ?? string.Empty;
        Refresh();
    }

    public void Delete(LocalMemoryEntryRow row)
    {
        var result = _memory.DeleteManual(row.Entry);
        Status = result.Ok ? string.Empty : result.Message ?? string.Empty;
        Refresh();
    }

    public void Accept(LocalMemoryCandidateRow candidate)
    {
        _memory.AcceptCandidate(candidate);
        Refresh();
    }

    public void Ignore(LocalMemoryCandidateRow candidate)
    {
        _memory.IgnoreCandidate(candidate);
        Refresh();
    }

    public async Task<MemoryConsolidationReport> ConsolidateAsync()
    {
        if (IsWriteLocked) return MemoryConsolidationReport.Blocked("另一项记忆操作尚未完成。");
        IsBusy = true;
        IsConsolidating = true;
        Status = string.Empty;
        try
        {
            // File scanning and the first database reads happen before the
            // consolidator reaches its first await. Keep those off the UI thread
            // so the settings window remains usable while a manual pass starts.
            return await Task.Run(() => _consolidator.RunAsync(manual: true, CancellationToken.None))
                .ConfigureAwait(true);
        }
        finally
        {
            IsConsolidating = false;
            IsBusy = false;
            Refresh();
        }
    }

    public async Task RebuildIndexAsync()
    {
        if (IsWriteLocked) return;
        IsBusy = true;
        Status = "正在重建索引…";
        try
        {
            await Task.Run(_memory.Index.RebuildIndex).ConfigureAwait(true);
            Status = $"索引已重建，包含 {_memory.Index.IndexedMessageCount()} 条消息。";
        }
        catch (Exception ex)
        {
            Status = "重建失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ClearAll()
    {
        _memory.ClearAll();
        Status = "记忆已清除，历史消息不会再次用于整理。";
        Refresh();
    }
}
