using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Personalization;

namespace MolaGPT.ViewModels;

/// <summary>Outcome of a memory-center operation. <see cref="Text"/> is user-facing.</summary>
public sealed record PersonalizationNotice(bool Ok, string? Text)
{
    public static PersonalizationNotice None { get; } = new(true, null);
    public static PersonalizationNotice Success(string text) => new(true, text);
    public static PersonalizationNotice Fail(string? text, string fallback) =>
        new(false, string.IsNullOrWhiteSpace(text) ? fallback : text);
}

/// <summary>
/// Memory center (MolaGPT Tracks). Aggregates the three server-side data sets —
/// long-term memory entries, pending candidates, and style preferences.
/// Ports the mobile <c>PersonalizationViewModel</c> contract: optimistic writes
/// with rollback, server Chinese reasons surfaced verbatim.
///
/// Every operation returns a <see cref="PersonalizationNotice"/>. The window
/// shows it via <c>NotificationCenter</c>; the VM itself never touches UI services.
/// </summary>
public sealed partial class PersonalizationViewModel : ObservableObject
{
    public const int MemoryTextMax = 300;

    private readonly MolaPersonalizationService _api;
    private readonly SettingsViewModel _settings;
    private readonly MolaGptProxyProvider _proxy;

    public PersonalizationViewModel(
        MolaPersonalizationService api,
        SettingsViewModel settings,
        MolaGptProxyProvider proxy)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
    }

    public ObservableCollection<MemoryEntryRow> Entries { get; } = [];
    public ObservableCollection<MemorySectionGroup> Groups { get; } = [];
    public ObservableCollection<MemoryCandidateRow> Candidates { get; } = [];

    [ObservableProperty] private MemoryProjection _projection = new();
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isAddingEntry;
    [ObservableProperty] private StylePreferences _style = new();
    [ObservableProperty] private bool _styleDirty;
    [ObservableProperty] private bool _isSavingStyle;

    /// <summary>Inline projection line under the entries header.</summary>
    public string ProjectionText => Projection.Budget > 0
        ? $"已注入 {Projection.Entries} 条 · 跳过 {Projection.Skipped} 条 · {Projection.Tokens}/{Projection.Budget} tokens"
        : Entries.Count == 0 ? string.Empty : $"共 {Entries.Count} 条记忆";

    /// <summary>Budget bar is only honest once the server reported a budget.</summary>
    public bool HasProjectionBar => Projection.Budget > 0;

    /// <summary>Over budget means entries were dropped, not merely nearly full.</summary>
    public bool IsProjectionTight => Projection.Skipped > 0;

    public string CandidatesTitle => $"待确认 · {Candidates.Count}";

    private static long NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private async Task<MolaGptStatus?> SafeStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await _proxy.FetchStatusAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    // -- load --

    /// <summary>First / re-entry load: entries + candidates + style in parallel.</summary>
    public async Task<string?> LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var entriesTask = _api.GetMemoryEntriesAsync(ct);
            var candidatesTask = _api.GetCandidatesAsync(ct);
            var styleTask = _api.GetStylePreferencesAsync(ct);
            // Status is only the toggle readback: never let it fail the load.
            var statusTask = SafeStatusAsync(ct);
            await Task.WhenAll(entriesTask, candidatesTask, styleTask, statusTask).ConfigureAwait(true);

            var now = NowSeconds();
            var failed = new List<string>();
            var entriesResult = await entriesTask.ConfigureAwait(true);
            if (entriesResult is not null)
            {
                ReplaceEntries(entriesResult.Entries, now);
                Projection = entriesResult.Projection;
                // Server toggle wins for multi-device consistency.
                if (entriesResult.MemoryEnabled != _settings.TracksEnabled)
                    _settings.TracksEnabled = entriesResult.MemoryEnabled;
            }
            else failed.Add("记忆条目");
            var candidates = await candidatesTask.ConfigureAwait(true);
            if (candidates is not null) ReplaceCandidates(candidates, now);
            else failed.Add("待确认");
            var style = await styleTask.ConfigureAwait(true);
            if (style is not null) { Style = style; StyleDirty = false; }
            else failed.Add("对话风格");
            // Server toggle wins for multi-device consistency.
            var serverToggle = (await statusTask.ConfigureAwait(true))?.PersonalizedMemoryEnabled;
            if (serverToggle.HasValue && serverToggle.Value != _settings.TracksEnabled)
                _settings.TracksEnabled = serverToggle.Value;
            return failed.Count == 0 ? null : $"加载失败：{string.Join("、", failed)}，请稍后重试";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<string?> RefreshAsync(CancellationToken ct = default)
    {
        if (IsRefreshing) return null;
        IsRefreshing = true;
        try
        {
            var now = NowSeconds();
            var entriesTask = _api.GetMemoryEntriesAsync(ct);
            var candidatesTask = _api.GetCandidatesAsync(ct);
            await Task.WhenAll(entriesTask, candidatesTask).ConfigureAwait(true);
            var fresh = await entriesTask.ConfigureAwait(true);
            var cands = await candidatesTask.ConfigureAwait(true);
            if (fresh is null) return "刷新失败，请稍后再试";
            ReplaceEntries(fresh.Entries, now);
            Projection = fresh.Projection;
            if (cands is not null) ReplaceCandidates(cands, now);
            return null;
        }
        catch (Exception ex)
        {
            return $"刷新失败：{ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    // -- entries --

    /// <summary>Optimistic rating with local confidence prediction; revoke by re-tapping.</summary>
    public async Task<PersonalizationNotice> RateAsync(string entryId, MemoryRating? rating, CancellationToken ct = default)
    {
        var row = Entries.FirstOrDefault(e => e.Id == entryId);
        if (row is null) return PersonalizationNotice.None;
        var snapshot = row.Snapshot();
        row.ApplyRating(rating, NowSeconds());
        try
        {
            var result = await _api.RateMemoryEntryAsync(entryId, rating, ct).ConfigureAwait(true);
            if (result.Succeeded)
            {
                _ = _api.TriggerEvolutionAsync(ct);
                return rating is null
                    ? PersonalizationNotice.Success("已取消评分")
                    : PersonalizationNotice.Success("已记录反馈 · MolaGPT 将据此调整");
            }
            row.Restore(snapshot);
            return PersonalizationNotice.Fail(result.Message, "评分提交失败");
        }
        catch (Exception ex)
        {
            row.Restore(snapshot);
            return new PersonalizationNotice(false, $"评分提交失败：{ex.Message}");
        }
    }

    public async Task<PersonalizationNotice> UpdateAsync(string entryId, string newText, CancellationToken ct = default)
    {
        var text = newText.Trim();
        if (text.Length == 0) return new PersonalizationNotice(false, "记忆内容不能为空");
        if (text.Length > MemoryTextMax) return new PersonalizationNotice(false, $"记忆内容不能超过 {MemoryTextMax} 字");
        var row = Entries.FirstOrDefault(e => e.Id == entryId);
        if (row is null) return PersonalizationNotice.None;
        var old = row.Text;
        row.Text = text;
        try
        {
            var result = await _api.UpdateMemoryEntryAsync(entryId, text, ct).ConfigureAwait(true);
            if (result.Succeeded) return PersonalizationNotice.Success("记忆已更新");
            row.Text = old;
            return PersonalizationNotice.Fail(result.Message, "更新失败");
        }
        catch (Exception ex)
        {
            row.Text = old;
            return new PersonalizationNotice(false, $"更新失败：{ex.Message}");
        }
    }

    public async Task<PersonalizationNotice> DeleteAsync(string entryId, CancellationToken ct = default)
    {
        var row = Entries.FirstOrDefault(e => e.Id == entryId);
        if (row is null) return PersonalizationNotice.None;
        Entries.Remove(row);
        RebuildGroups(NowSeconds());
        try
        {
            var result = await _api.DeleteMemoryEntryAsync(entryId, ct).ConfigureAwait(true);
            if (result.Succeeded) return PersonalizationNotice.Success("已删除该记忆");
            Entries.Add(row);
            SortEntries();
            RebuildGroups(NowSeconds());
            return PersonalizationNotice.Fail(result.Message, "删除失败");
        }
        catch (Exception ex)
        {
            Entries.Add(row);
            SortEntries();
            RebuildGroups(NowSeconds());
            return new PersonalizationNotice(false, $"删除失败：{ex.Message}");
        }
    }

    /// <summary>No optimistic insert: the server normalizes the text and mints
    /// the id, and the guardrail may reject. Reload for the authoritative list.</summary>
    public async Task<PersonalizationNotice> AddAsync(string text, MemorySection section, CancellationToken ct = default)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return new PersonalizationNotice(false, "记忆内容不能为空");
        if (trimmed.Length > MemoryTextMax) return new PersonalizationNotice(false, $"记忆内容不能超过 {MemoryTextMax} 字");
        if (IsAddingEntry) return PersonalizationNotice.None;
        IsAddingEntry = true;
        try
        {
            var result = await _api.AddMemoryEntryAsync(trimmed, section, null, ct).ConfigureAwait(true);
            if (!result.Succeeded)
                return PersonalizationNotice.Fail(result.Message, "添加失败，请稍后再试");
            var fresh = await _api.GetMemoryEntriesAsync(ct).ConfigureAwait(true);
            if (fresh is not null)
            {
                ReplaceEntries(fresh.Entries, NowSeconds());
                Projection = fresh.Projection;
            }
            return PersonalizationNotice.Success("已添加到记忆");
        }
        catch (Exception ex)
        {
            return new PersonalizationNotice(false, $"添加失败：{ex.Message}");
        }
        finally
        {
            IsAddingEntry = false;
        }
    }

    public async Task<PersonalizationNotice> ClearAllAsync(CancellationToken ct = default)
    {
        if (Entries.Count == 0) return PersonalizationNotice.None;
        var snapshot = Entries.ToList();
        var snapshotProjection = Projection;
        Entries.Clear();
        Groups.Clear();
        Projection = new MemoryProjection(Budget: snapshotProjection.Budget);
        try
        {
            var result = await _api.DeleteAllMemoriesAsync(ct).ConfigureAwait(true);
            if (result.Succeeded) return PersonalizationNotice.Success("已清除全部记忆");
            foreach (var row in snapshot) Entries.Add(row);
            Projection = snapshotProjection;
            RebuildGroups(NowSeconds());
            return PersonalizationNotice.Fail(result.Message, "清除失败");
        }
        catch (Exception ex)
        {
            foreach (var row in snapshot) Entries.Add(row);
            Projection = snapshotProjection;
            RebuildGroups(NowSeconds());
            return new PersonalizationNotice(false, $"清除失败：{ex.Message}");
        }
    }

    // -- candidates --

    public async Task<PersonalizationNotice> AcceptCandidateAsync(string candidateId, CancellationToken ct = default)
    {
        var row = Candidates.FirstOrDefault(c => c.Id == candidateId);
        if (row is null) return PersonalizationNotice.None;
        Candidates.Remove(row);
        try
        {
            var add = await _api.AddMemoryEntryAsync(row.Text, row.Section, row.Id, ct).ConfigureAwait(true);
            if (!add.Succeeded)
            {
                Candidates.Add(row);
                return PersonalizationNotice.Fail(add.Message, "添加失败，请稍后再试");
            }
            _ = await _api.DismissCandidateAsync(row.Id, false, ct).ConfigureAwait(true);
            var fresh = await _api.GetMemoryEntriesAsync(ct).ConfigureAwait(true);
            if (fresh is not null)
            {
                ReplaceEntries(fresh.Entries, NowSeconds());
                Projection = fresh.Projection;
            }
            return PersonalizationNotice.Success("已记住");
        }
        catch (Exception ex)
        {
            Candidates.Add(row);
            return new PersonalizationNotice(false, $"操作失败：{ex.Message}");
        }
    }

    public async Task<PersonalizationNotice> DismissCandidateAsync(string candidateId, CancellationToken ct = default)
    {
        var row = Candidates.FirstOrDefault(c => c.Id == candidateId);
        if (row is null) return PersonalizationNotice.None;
        Candidates.Remove(row);
        try
        {
            var result = await _api.DismissCandidateAsync(candidateId, true, ct).ConfigureAwait(true);
            if (result.Succeeded) return PersonalizationNotice.Success("已忽略 · 不会再次建议");
            Candidates.Add(row);
            return PersonalizationNotice.Fail(result.Message, "操作失败");
        }
        catch (Exception ex)
        {
            Candidates.Add(row);
            return new PersonalizationNotice(false, $"操作失败：{ex.Message}");
        }
    }

    // -- style --

    public void ToggleStyle(ConversationStyle style)
    {
        Style = Style.Toggled(style);
        StyleDirty = true;
    }

    public void SetCustomInstruction(string text)
    {
        var clamped = text.Length <= StylePreferences.CustomInstructionMax
            ? text : text[..StylePreferences.CustomInstructionMax];
        if (Style.CustomInstruction != clamped)
        {
            Style = Style with { CustomInstruction = clamped };
            StyleDirty = true;
        }
    }

    public async Task<PersonalizationNotice> SaveStyleAsync(CancellationToken ct = default)
    {
        if (IsSavingStyle) return PersonalizationNotice.None;
        IsSavingStyle = true;
        try
        {
            var result = await _api.UpdateStylePreferencesAsync(Style, ct).ConfigureAwait(true);
            if (result.Succeeded)
            {
                StyleDirty = false;
                return PersonalizationNotice.Success("风格偏好已保存");
            }
            return PersonalizationNotice.Fail(result.Message, "保存失败，请稍后再试");
        }
        catch (Exception ex)
        {
            return new PersonalizationNotice(false, $"保存失败：{ex.Message}");
        }
        finally
        {
            IsSavingStyle = false;
        }
    }

    // -- internals --

    private void ReplaceEntries(IEnumerable<MemoryEntry> entries, long now)
    {
        Entries.Clear();
        foreach (var e in entries) Entries.Add(new MemoryEntryRow(e, now));
        SortEntries();
        RebuildGroups(now);
        OnPropertyChanged(nameof(ProjectionText));
    }

    private void SortEntries()
    {
        var order = MemorySections.Ordered
            .Select((s, i) => (s, i))
            .ToDictionary(p => p.s, p => p.i);
        var sorted = Entries
            .OrderBy(e => order.GetValueOrDefault(e.Section, int.MaxValue))
            .ThenByDescending(e => e.Confidence)
            .ToList();
        Entries.Clear();
        foreach (var e in sorted) Entries.Add(e);
    }

    private void RebuildGroups(long now)
    {
        Groups.Clear();
        foreach (var section in MemorySections.Ordered)
        {
            var rows = Entries.Where(e => e.Section == section).ToList();
            if (rows.Count == 0) continue;
            foreach (var row in rows) row.RefreshDerived(now);
            Groups.Add(new MemorySectionGroup(section, rows));
        }
        OnPropertyChanged(nameof(ProjectionText));
    }

    private void ReplaceCandidates(IEnumerable<MemoryCandidate> candidates, long now)
    {
        Candidates.Clear();
        foreach (var c in candidates) Candidates.Add(new MemoryCandidateRow(c, now));
        OnPropertyChanged(nameof(CandidatesTitle));
    }

    partial void OnProjectionChanged(MemoryProjection value)
    {
        OnPropertyChanged(nameof(ProjectionText));
        OnPropertyChanged(nameof(HasProjectionBar));
        OnPropertyChanged(nameof(IsProjectionTight));
    }
}

/// <summary>One entry flattened for binding — no converters needed in XAML.</summary>
public sealed partial class MemoryEntryRow : ObservableObject
{
    private long _lastTs;
    private long _createdTs;
    private long? _ttl;
    private int _sourceCount;

    public MemoryEntryRow(MemoryEntry entry, long nowSeconds)
    {
        Id = entry.Id;
        Section = MemorySections.FromWire(entry.Section);
        SectionLabel = MemorySections.Label(Section);
        CategoryLabel = InsightCategories.FromWire(entry.Category) is { } cat
            ? InsightCategories.Label(cat) : null;
        Confidence = entry.Confidence;
        IsPermanent = entry.Permanent;
        IsUserSet = entry.UserSet;
        Recurrence = entry.Recurrence;
        UserRating = entry.UserRating;
        Text = entry.Text;
        _lastTs = entry.LastTs;
        _createdTs = entry.CreatedTs;
        _ttl = entry.Ttl;
        _sourceCount = entry.Sources?.Count ?? 0;
        RefreshDerived(nowSeconds);
    }

    public string Id { get; }
    public MemorySection Section { get; }
    public string SectionLabel { get; }
    public string? CategoryLabel { get; }
    public bool HasCategoryLabel => !string.IsNullOrEmpty(CategoryLabel);
    public double Confidence { get; private set; }
    public bool IsPermanent { get; }
    public bool IsUserSet { get; }
    public int Recurrence { get; }
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAgree))]
    [NotifyPropertyChangedFor(nameof(IsDoubt))]
    [NotifyPropertyChangedFor(nameof(IsReject))]
    private MemoryRating? _userRating;
    [ObservableProperty] private string _tierLabel = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfidenceText))]
    private int _confidencePercent;
    /// <summary>Filled width of the confidence meter, in pixels of
    /// <see cref="MeterTrackWidth"/>. A width rather than a converter so the
    /// card stays a plain Border and never depends on a ProgressBar template.</summary>
    [ObservableProperty] private double _meterWidth;
    [ObservableProperty] private string _statusLabel = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFlags))]
    private bool _isExpired;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFlags))]
    private bool _isNearExpiry;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMetaText))]
    private string _metaText = string.Empty;

    public const double MeterTrackWidth = 46;

    public bool HasMetaText => !string.IsNullOrEmpty(MetaText);
    /// <summary>Whether the flag row has anything to say. Without it the empty
    /// row still claims the parent's inter-item spacing.</summary>
    public bool HasFlags =>
        HasCategoryLabel || IsPermanent || IsUserSet || IsNearExpiry || IsExpired;
    public string ConfidenceText => $"{ConfidencePercent}%";
    public bool IsAgree => UserRating == MemoryRating.Agree;
    public bool IsDoubt => UserRating == MemoryRating.Doubt;
    public bool IsReject => UserRating == MemoryRating.Reject;

    public void RefreshDerived(long now)
    {
        TierLabel = ConfidenceTiers.Label(ConfidenceTiers.Of(Confidence));
        ConfidencePercent = (int)Math.Round(Confidence * 100);
        // Floor at 2px: a 5% memory should still read as "there, but barely",
        // not as an empty track indistinguishable from a render bug.
        MeterWidth = Math.Clamp(MeterTrackWidth * Confidence, 2, MeterTrackWidth);
        var days = DaysSince(_lastTs > 0 ? _lastTs : _createdTs, now);
        var status = MemoryStatuses.Of(Confidence, days);
        StatusLabel = MemoryStatuses.Label(status);
        IsExpired = _ttl.HasValue && _ttl.Value < now;
        IsNearExpiry = !IsPermanent && !IsExpired && MemoryStatuses.NearExpiry(status);
        var parts = new List<string>();
        if (_sourceCount > 0) parts.Add($"来自 {_sourceCount} 次对话");
        if (Recurrence > 1) parts.Add($"提及 {Recurrence} 次");
        var reference = _lastTs > 0 ? _lastTs : _createdTs;
        if (reference > 0) parts.Add($"{RelativeDays(reference, now)}更新");
        MetaText = string.Join(" · ", parts);
    }

    public (double Confidence, MemoryRating? Rating, long LastTs) Snapshot() =>
        (Confidence, UserRating, _lastTs);

    public void ApplyRating(MemoryRating? rating, long now)
    {
        var prev = UserRating.HasValue ? MemoryRatings.Delta(UserRating.Value) : 0;
        var next = rating.HasValue ? MemoryRatings.Delta(rating.Value) : 0;
        Confidence = Math.Clamp(Confidence - prev + next,
            MemoryRatings.ConfidenceMin, MemoryRatings.ConfidenceMax);
        UserRating = rating;
        // Agree proves "still true now": the server refreshes last_ts, mirror it.
        if (rating == MemoryRating.Agree) _lastTs = now;
        RefreshDerived(now);
    }

    public void Restore((double Confidence, MemoryRating? Rating, long LastTs) snapshot)
    {
        Confidence = snapshot.Confidence;
        UserRating = snapshot.Rating;
        _lastTs = snapshot.LastTs;
        RefreshDerived(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private static int DaysSince(long ts, long now)
    {
        if (ts <= 0) return 0;
        return (int)Math.Max(0, (now - ts) / 86_400L);
    }

    public static string RelativeDays(long ts, long now)
    {
        if (ts <= 0) return "未知时间";
        var days = (int)((now - ts) / 86_400L);
        return days switch
        {
            <= 0 => "今天",
            1 => "昨天",
            < 7 => $"{days}天前",
            < 30 => $"{days / 7}周前",
            _ => $"{days / 30}个月前"
        };
    }
}

public sealed class MemorySectionGroup
{
    public MemorySectionGroup(MemorySection section, IReadOnlyList<MemoryEntryRow> items)
    {
        Section = section;
        Label = MemorySections.Label(section);
        Items = items;
    }

    public MemorySection Section { get; }
    public string Label { get; }
    public IReadOnlyList<MemoryEntryRow> Items { get; }
    public string Title => $"{Label} · {Items.Count}";
}

public sealed class MemoryCandidateRow
{
    public MemoryCandidateRow(MemoryCandidate candidate, long now)
    {
        Id = candidate.Id;
        Text = candidate.Text;
        Quote = candidate.Quote;
        Section = candidate.Section;
        MetaText = $"{MemorySections.Label(candidate.Section)} · {MemoryEntryRow.RelativeDays(candidate.ObservedTs, now)}发现";
    }

    public string Id { get; }
    public string Text { get; }
    public string? Quote { get; }
    public bool HasQuote => !string.IsNullOrEmpty(Quote);
    public MemorySection Section { get; }
    public string SectionLabel => MemorySections.Label(Section);
    public string MetaText { get; }
}
