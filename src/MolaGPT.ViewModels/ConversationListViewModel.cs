using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using MolaGPT.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Chat;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.ViewModels;

/// <summary>
/// Sidebar conversation list view model. Backed by SQLite via
/// <see cref="ConversationRepository"/> when one is supplied; otherwise (M1
/// boot) falls back to in-memory mock data. Selection changes always raise
/// <see cref="ConversationSelected"/> so MainViewModel can swap the chat.
///
/// The list is split into two rendered collections — <see cref="ByokItems"/>
/// and <see cref="MolaGptConversationItems"/> — both individually collapsible. Group
/// expansion state persists across restarts via <see cref="SettingsRepository"/>
/// under the keys <c>sidebar_group_byok_expanded</c> /
/// <c>sidebar_group_molagpt_expanded</c>. BYOK is rendered first (above
/// MolaGPT) per product preference. MolaGPT Chat and Work are shown together;
/// Work remains marked at row level and does not participate in Chat cloud sync.
///
/// We keep a single master <see cref="Items"/> collection (used by sync /
/// search / upsert paths) and derive the rendered group collections by
/// subscribing to its CollectionChanged event. Two-collection-with-sync is
/// simpler than introducing a WPF <c>ICollectionView</c> in this project,
/// which intentionally has no PresentationFramework reference.
/// </summary>
public sealed partial class ConversationListViewModel : ObservableObject
{
    public const string ImageWorkbenchProviderId = "byok-image-workbench";
    private const string SettingByokExpandedKey = "sidebar_group_byok_expanded";
    private const string SettingMolaGptExpandedKey = "sidebar_group_molagpt_expanded";
    private const string SettingWorkExpandedKey = "sidebar_group_work_expanded";

    private readonly BulkObservableCollection<ConversationListItem> _items = new();
    private readonly BulkObservableCollection<ConversationListItem> _byokItems = new();
    private readonly BulkObservableCollection<ConversationListItem> _molaGptItems = new();
    private readonly BulkObservableCollection<ConversationListItem> _workItems = new();
    private readonly BulkObservableCollection<ConversationListItem> _molaGptConversationItems = new();

    public ObservableCollection<ConversationListItem> Items => _items;

    /// <summary>BYOK conversations only — bound to the BYOK ListBox in the sidebar.</summary>
    public ObservableCollection<ConversationListItem> ByokItems => _byokItems;

    /// <summary>MolaGPT Chat (cloud) conversations — bound to the Chat ListBox.</summary>
    public ObservableCollection<ConversationListItem> MolaGptItems => _molaGptItems;

    /// <summary>MolaGPT Work (local-agent, shared quota) conversations — bound to the Work ListBox.</summary>
    public ObservableCollection<ConversationListItem> WorkItems => _workItems;

    /// <summary>MolaGPT Chat + Work conversations — bound to the unified MolaGPT sidebar group.</summary>
    public ObservableCollection<ConversationListItem> MolaGptConversationItems => _molaGptConversationItems;

    [ObservableProperty] private string? _selectedId;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ByokGroupChevron))]
    private bool _isByokGroupExpanded = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MolaGptGroupChevron))]
    private bool _isMolaGptGroupExpanded = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkGroupChevron))]
    private bool _isWorkGroupExpanded = true;

    /// <summary>Fluent chevron glyph for the BYOK group header (E70D = down,
    /// E70E = up). Bound directly so the header doesn't need a converter.</summary>
    public string ByokGroupChevron => IsByokGroupExpanded ? "" : "";
    public string MolaGptGroupChevron => IsMolaGptGroupExpanded ? "" : "";
    public string WorkGroupChevron => IsWorkGroupExpanded ? "" : "";

    /// <summary>Counts shown next to each group header.</summary>
    public int ByokCount => ByokItems.Count;
    public int MolaGptCount => MolaGptItems.Count;
    public int WorkCount => WorkItems.Count;
    public int MolaGptConversationCount => MolaGptConversationItems.Count;

    /// <summary>True when the BYOK group has at least one conversation.
    /// Lets the XAML hide the entire group block (header + list) for
    /// users who never use BYOK, instead of showing an empty section.</summary>
    public bool HasByokConversations => ByokItems.Count > 0;
    public bool HasMolaGptConversations => MolaGptItems.Count > 0;
    public bool HasWorkConversations => WorkItems.Count > 0;
    public bool HasMolaGptConversationItems => MolaGptConversationItems.Count > 0;

    private readonly ConversationRepository? _repository;
    private readonly PersonaListViewModel? _personas;
    private readonly SettingsRepository? _settingsRepo;
    private readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _generatingIds = new(StringComparer.Ordinal);
    private bool _bulkUpdatingItems;
    private bool _suspendExpansionPersist;

    /// <summary>Raised whenever the active conversation changes (click, new, programmatic).</summary>
    public event EventHandler<string>? ConversationSelected;
    public event EventHandler<IReadOnlyList<string>>? ConversationsDeleted;

    public IRelayCommand DeleteSelectedConversationsCommand { get; }

    /// <summary>Toggle the BYOK group's expansion. Bound from the group
    /// header click handler.</summary>
    [RelayCommand]
    private void ToggleByokGroup() => IsByokGroupExpanded = !IsByokGroupExpanded;

    [RelayCommand]
    private void ToggleMolaGptGroup() => IsMolaGptGroupExpanded = !IsMolaGptGroupExpanded;

    [RelayCommand]
    private void ToggleWorkGroup() => IsWorkGroupExpanded = !IsWorkGroupExpanded;

    /// <summary>
    /// True iff the user has multi-selected (≥2) conversations — drives the
    /// bulk-action bar's visibility. A single click on a row counts as a
    /// navigation, not a bulk selection, so this stays false at count == 1.
    /// </summary>
    public bool HasSelectedConversations => SelectedCount >= 2;
    public string SelectionSummary => SelectedCount < 2 ? string.Empty : $"已选 {SelectedCount} 个对话";

    public ConversationListViewModel() : this(null, null, null) { }

    public ConversationListViewModel(ConversationRepository? repository)
        : this(repository, null, null) { }

    public ConversationListViewModel(ConversationRepository? repository, PersonaListViewModel? personas)
        : this(repository, personas, null) { }

    public ConversationListViewModel(
        ConversationRepository? repository,
        PersonaListViewModel? personas,
        SettingsRepository? settingsRepo)
    {
        _repository = repository;
        _personas = personas;
        _settingsRepo = settingsRepo;

        // Mirror Items -> ByokItems / MolaGptItems on every mutation so the
        // two group ListBoxes stay in sync without anyone having to dual-
        // write. This is the only side effect of touching Items, so all the
        // existing UpsertItem / ApplyRows / RemoveItem paths just work.
        Items.CollectionChanged += OnItemsCollectionChanged;

        // Restore persisted expansion state. Default = expanded.
        if (_settingsRepo is not null)
        {
            _suspendExpansionPersist = true;
            try
            {
                if (bool.TryParse(_settingsRepo.Get(SettingByokExpandedKey), out var byok))
                    IsByokGroupExpanded = byok;
                if (bool.TryParse(_settingsRepo.Get(SettingMolaGptExpandedKey), out var mgpt))
                    IsMolaGptGroupExpanded = mgpt;
                if (bool.TryParse(_settingsRepo.Get(SettingWorkExpandedKey), out var work))
                    IsWorkGroupExpanded = work;
            }
            finally { _suspendExpansionPersist = false; }
        }

        DeleteSelectedConversationsCommand = new RelayCommand(
            DeleteSelectedConversations,
            () => HasSelectedConversations);

        // Re-render the sidebar badges when persona names change (rename in
        // Settings) so cached labels don't go stale.
        if (_personas is not null)
            _personas.PersonasChanged += (_, _) => RefreshAllPersonaLabels();

        Reload();
    }

    partial void OnIsByokGroupExpandedChanged(bool value)
    {
        if (_suspendExpansionPersist || _settingsRepo is null) return;
        _settingsRepo.Set(SettingByokExpandedKey, value.ToString());
    }

    partial void OnIsMolaGptGroupExpandedChanged(bool value)
    {
        if (_suspendExpansionPersist || _settingsRepo is null) return;
        _settingsRepo.Set(SettingMolaGptExpandedKey, value.ToString());
    }

    partial void OnIsWorkGroupExpandedChanged(bool value)
    {
        if (_suspendExpansionPersist || _settingsRepo is null) return;
        _settingsRepo.Set(SettingWorkExpandedKey, value.ToString());
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_bulkUpdatingItems) return;

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Reset only fires from Items.Clear() in current code paths, so
            // Items is empty here and groups simply mirror that emptiness.
            ByokItems.Clear();
            MolaGptItems.Clear();
            WorkItems.Clear();
            MolaGptConversationItems.Clear();
            RaiseGroupCountChanges();
            return;
        }

        if (e.OldItems is not null)
        {
            foreach (ConversationListItem old in e.OldItems)
            {
                GroupFor(old).Remove(old);
                if (old.IsMolaGptAccountConversation)
                    MolaGptConversationItems.Remove(old);
            }
        }
        if (e.NewItems is not null)
        {
            // Mirror the insertion position from Items into the group so
            // both bulk-load (Items.Add → NewStartingIndex grows) and
            // single-row upsert (Items.Insert(0, ...) → NewStartingIndex == 0)
            // preserve recency-first ordering. Without this branch we'd
            // always prepend, which silently reverses ApplyRows / Reload
            // output (oldest-first instead of newest-first).
            var prepend = e.NewStartingIndex == 0;
            foreach (ConversationListItem n in e.NewItems)
            {
                var target = GroupFor(n);
                if (prepend) target.Insert(0, n);
                else target.Add(n);

                if (n.IsMolaGptAccountConversation)
                    RefreshMolaGptConversationItems();
            }
        }
        RaiseGroupCountChanges();
    }

    /// <summary>Maps a conversation item to its mirror collection by group.</summary>
    private ObservableCollection<ConversationListItem> GroupFor(ConversationListItem item) => item.Group switch
    {
        AppMode.Chat => _molaGptItems,
        AppMode.Work => _workItems,
        _ => _byokItems,
    };

    private void RaiseGroupCountChanges()
    {
        OnPropertyChanged(nameof(ByokCount));
        OnPropertyChanged(nameof(MolaGptCount));
        OnPropertyChanged(nameof(WorkCount));
        OnPropertyChanged(nameof(MolaGptConversationCount));
        OnPropertyChanged(nameof(HasByokConversations));
        OnPropertyChanged(nameof(HasMolaGptConversations));
        OnPropertyChanged(nameof(HasWorkConversations));
        OnPropertyChanged(nameof(HasMolaGptConversationItems));
    }

    private void RefreshMolaGptConversationItems() =>
        _molaGptConversationItems.ReplaceAll(Items.Where(item => item.IsMolaGptAccountConversation));

    private void RefreshAllPersonaLabels()
    {
        if (_repository is null || _personas is null) return;
        var byId = _repository.ListActive().ToDictionary(r => r.Id, r => r.PersonaId);
        foreach (var item in Items)
        {
            if (byId.TryGetValue(item.Id, out var pid))
                item.PersonaLabel = _personas.Find(pid)?.Name;
        }
    }

    /// <summary>Reload from SQLite (or use mocks when no repository is wired).</summary>
    public void Reload()
    {
        if (_repository is null)
        {
            Items.Clear();
            SetSelectedIds(Array.Empty<string>());
            // M1 fallback so the empty UI isn't completely barren.
            Items.Add(new ConversationListItem("mock-1", "欢迎使用 MolaGPT 桌面版", DateTimeOffset.UtcNow, false));
            return;
        }

        ApplyRows(_repository.ListActive());
    }

    /// <summary>
    /// Reload from SQLite without doing the database read on the dispatcher.
    /// The caller should still invoke this from the UI thread so collection
    /// replacement happens on the owning context.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (_repository is null)
        {
            Reload();
            return;
        }

        var rows = await Task.Run(() => _repository.ListActive(), cancellationToken)
            .ConfigureAwait(true);
        if (cancellationToken.IsCancellationRequested) return;
        ApplyRows(rows);
    }

    private void ApplyRows(IReadOnlyList<ConversationRow> rows)
    {
        if (RowsMatchCurrentItems(rows))
            return;

        ReplaceAllItems(rows.Select(row => BuildItem(row)).ToList(), clearSelection: true);
    }

    public void ApplyCloudSyncChanges(
        IReadOnlyList<ConversationRow> changedRows,
        IReadOnlyList<string> removedIds)
    {
        var removed = removedIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var changed = changedRows
            .Where(row => !string.IsNullOrWhiteSpace(row.Id))
            .GroupBy(row => row.Id, StringComparer.Ordinal)
            .Select(group => group.OrderBy(row => row.UpdatedAt).Last())
            .ToDictionary(row => row.Id, StringComparer.Ordinal);

        if (removed.Count == 0 && changed.Count == 0)
            return;

        var existingById = Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var nextItems = new List<ConversationListItem>(Items.Count + changed.Count);
        foreach (var item in Items)
        {
            if (removed.Contains(item.Id) || changed.ContainsKey(item.Id))
                continue;
            nextItems.Add(item);
        }

        foreach (var row in changed.Values)
        {
            existingById.TryGetValue(row.Id, out var existing);
            nextItems.Add(BuildItem(row, existing));
        }

        nextItems.Sort(CompareSidebarItems);
        ReplaceAllItems(nextItems, clearSelection: false);

        if (SelectedId is not null && !nextItems.Any(item => item.Id == SelectedId))
            SelectedId = null;

        var visibleIds = nextItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (_selectedIds.RemoveWhere(id => !visibleIds.Contains(id)) > 0)
            RefreshSelectionProperties();
    }

    private ConversationListItem BuildItem(ConversationRow row, ConversationListItem? existing = null)
    {
        var item = new ConversationListItem(
            row.Id,
            string.IsNullOrWhiteSpace(row.Title) ? "新对话" : row.Title,
            DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAt),
            ClassifyGroup(row.ProviderId),
            _personas?.Find(row.PersonaId)?.Name ?? existing?.PersonaLabel,
            IsImageWorkbenchProvider(row.ProviderId),
            row.Pinned);

        if (_generatingIds.Contains(row.Id) || existing?.IsGenerating == true)
            item.IsGenerating = true;

        return item;
    }

    private void ReplaceAllItems(IReadOnlyList<ConversationListItem> items, bool clearSelection)
    {
        _bulkUpdatingItems = true;
        try
        {
            if (clearSelection)
                SetSelectedIds(Array.Empty<string>());

            _items.ReplaceAll(items);
            _byokItems.ReplaceAll(items.Where(item => item.Group == AppMode.Byok));
            _molaGptItems.ReplaceAll(items.Where(item => item.Group == AppMode.Chat));
            _workItems.ReplaceAll(items.Where(item => item.Group == AppMode.Work));
            _molaGptConversationItems.ReplaceAll(items.Where(item => item.IsMolaGptAccountConversation));
        }
        finally
        {
            _bulkUpdatingItems = false;
        }

        RaiseGroupCountChanges();
    }

    private static int CompareSidebarItems(ConversationListItem left, ConversationListItem right)
    {
        var pinned = right.Pinned.CompareTo(left.Pinned);
        if (pinned != 0) return pinned;

        var updated = right.UpdatedAt.CompareTo(left.UpdatedAt);
        if (updated != 0) return updated;

        return string.CompareOrdinal(left.Id, right.Id);
    }

    private bool RowsMatchCurrentItems(IReadOnlyList<ConversationRow> rows)
    {
        if (Items.Count != rows.Count) return false;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var item = Items[i];
            if (item.Id != row.Id
                || (!string.IsNullOrWhiteSpace(row.Title) && item.Title != row.Title)
                || item.UpdatedAt != DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAt)
                || item.Group != ClassifyGroup(row.ProviderId)
                || item.IsImageTask != IsImageWorkbenchProvider(row.ProviderId)
                || item.Pinned != row.Pinned)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Mirror in-memory state so the sidebar updates without a full reload.
    /// Called from <see cref="ChatViewModel"/> when it auto-creates a
    /// conversation on first message, or when a streaming answer finalizes
    /// and the title / updated_at move. If the id is already present we
    /// update the title and move it to the top on message commit.
    /// </summary>
    public void UpsertItem(
        string id,
        string title,
        DateTimeOffset updatedAt,
        string? providerId = null,
        string? personaLabel = null,
        bool? pinned = null)
    {
        if (string.IsNullOrEmpty(id)) return;
        var existingIdx = -1;
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].Id == id) { existingIdx = i; break; }
        }

        var normalized = string.IsNullOrWhiteSpace(title) ? "新对话" : title;
        var group = providerId is null && existingIdx >= 0
            ? Items[existingIdx].Group
            : ClassifyGroup(providerId);
        var isImageTask = providerId is null && existingIdx >= 0
            ? Items[existingIdx].IsImageTask
            : IsImageWorkbenchProvider(providerId);
        var isPinned = pinned ?? (existingIdx >= 0 && Items[existingIdx].Pinned);
        var wasGenerating = existingIdx >= 0 && Items[existingIdx].IsGenerating;
        // Caller passes null personaLabel when it has no info to add (e.g.
        // sidebar refresh from cloud sync). Preserve the existing label in
        // that case so the badge doesn't flicker to "自定义" then back.
        // Pass "" to explicitly clear (e.g. user un-bound the persona).
        string? resolvedPersonaLabel = personaLabel switch
        {
            null => existingIdx >= 0 ? Items[existingIdx].PersonaLabel : null,
            "" => null,
            _ => personaLabel
        };
        var next = new ConversationListItem(id, normalized, updatedAt, group, resolvedPersonaLabel, isImageTask, isPinned);
        if (wasGenerating) next.IsGenerating = true;

        if (existingIdx < 0)
        {
            Items.Insert(0, next);
            return;
        }

        // Replace in place if title unchanged + position is already top;
        // otherwise re-sort so recency-first ordering holds.
        var current = Items[existingIdx];
        if (current.Title == normalized && existingIdx == 0)
        {
            Items[existingIdx] = next;
            return;
        }
        Items.RemoveAt(existingIdx);
        Items.Insert(0, next);
    }

    /// <summary>
    /// Remove a conversation from the sidebar without hitting SQLite
    /// (the repo-facing soft-delete path is <see cref="DeleteConversationCommand"/>).
    /// Useful when ChatViewModel detects a stale id externally.
    /// </summary>
    public void RemoveItem(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].Id == id)
            {
                Items.RemoveAt(i);
                if (SelectedId == id) SelectedId = null;
                _selectedIds.Remove(id);
                RefreshSelectionProperties();
                return;
            }
        }
    }

    public void ClearSelection()
    {
        SelectedId = null;
        SetSelectedIds(Array.Empty<string>());
    }

    public void SelectById(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        SelectedId = id;
    }

    public string CreateImageWorkbenchConversation(string? title = null, string? modelId = null)
    {
        var id = $"image_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var row = new ConversationRow(
            Id: id,
            Title: string.IsNullOrWhiteSpace(title) ? "图像工作台" : title.Trim(),
            ModelId: modelId,
            ProviderId: ImageWorkbenchProviderId,
            CreatedAt: now.ToUnixTimeMilliseconds(),
            UpdatedAt: now.ToUnixTimeMilliseconds(),
            Pinned: false,
            DeletedAt: null);
        _repository?.Upsert(row);
        UpsertItem(row.Id, row.Title, now, row.ProviderId, "图像");
        return id;
    }

    public ConversationListItem? FindItem(string id) =>
        Items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    public void SetSelectedIds(IEnumerable<string> ids)
    {
        _selectedIds.Clear();
        foreach (var id in ids)
        {
            if (!string.IsNullOrEmpty(id)) _selectedIds.Add(id);
        }
        RefreshSelectionProperties();
    }

    [RelayCommand]
    private void NewConversation()
    {
        ClearSelection();
    }

    [RelayCommand]
    private void DeleteConversation(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        DeleteConversations(new[] { id });
    }

    private void DeleteSelectedConversations()
    {
        DeleteConversations(_selectedIds.ToArray());
    }

    private void DeleteConversations(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return;

        var idSet = new HashSet<string>(ids, StringComparer.Ordinal);
        var deleted = new List<string>();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (int i = Items.Count - 1; i >= 0; i--)
        {
            var item = Items[i];
            if (!idSet.Contains(item.Id)) continue;

            Items.RemoveAt(i);
            deleted.Add(item.Id);
            _selectedIds.Remove(item.Id);
        }

        if (deleted.Count == 0) return;

        _repository?.SoftDeleteMany(deleted, timestamp);

        if (SelectedId is not null && idSet.Contains(SelectedId))
            SelectedId = null;

        RefreshSelectionProperties();
        ConversationsDeleted?.Invoke(this, deleted);
    }

    /// <summary>Generated by [ObservableProperty] on _selectedId — fires the public event.</summary>
    partial void OnSelectedIdChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            ConversationSelected?.Invoke(this, value);
    }

    /// <summary>When the user types in the sidebar search box, re-query
    /// SQLite for matching titles. We keep it simple and case-insensitive —
    /// the repo returns all rows and we filter in-memory because the typical
    /// conversation count is small (&lt;1000).</summary>
    partial void OnSearchQueryChanged(string value)
    {
        if (_repository is null) return;
        var needle = (value ?? string.Empty).Trim();
        Items.Clear();
        SetSelectedIds(Array.Empty<string>());
        foreach (var row in _repository.ListActive())
        {
            var title = string.IsNullOrWhiteSpace(row.Title) ? "新对话" : row.Title;
            if (needle.Length > 0 && title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            Items.Add(new ConversationListItem(
                row.Id,
                title,
                DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAt),
                ClassifyGroup(row.ProviderId),
                _personas?.Find(row.PersonaId)?.Name,
                IsImageWorkbenchProvider(row.ProviderId)));
        }
    }

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelectedConversations));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void RefreshSelectionProperties()
    {
        var countChanged = SelectedCount != _selectedIds.Count;
        SelectedCount = _selectedIds.Count;
        if (!countChanged)
        {
            OnPropertyChanged(nameof(HasSelectedConversations));
            OnPropertyChanged(nameof(SelectionSummary));
        }
        DeleteSelectedConversationsCommand.NotifyCanExecuteChanged();
    }

    public void SetGenerating(string conversationId, bool isGenerating)
    {
        if (string.IsNullOrEmpty(conversationId)) return;

        // Track generating ids out-of-band so a Reload() / ApplyRows() that
        // rebuilds every item (e.g. when the workbench closes and triggers a
        // sidebar refresh) can restore the spinner for tasks still running in
        // the background, instead of silently dropping it to the default false.
        if (isGenerating) _generatingIds.Add(conversationId);
        else _generatingIds.Remove(conversationId);

        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].Id == conversationId)
            {
                Items[i].IsGenerating = isGenerating;
                return;
            }
        }
    }

    /// <summary>Three-way sidebar group for a conversation, derived from its
    /// provider id. molagpt-proxy → Chat, molagpt-local-tools → Work, anything
    /// else (incl. BYOK image workbench) → Byok. Mirrors <see cref="AppMode"/>.</summary>
    private static AppMode ClassifyGroup(string? providerId)
    {
        if (string.Equals(providerId, MolaGptProviderIds.Proxy, StringComparison.OrdinalIgnoreCase))
            return AppMode.Chat;
        if (string.Equals(providerId, MolaGptProviderIds.LocalTools, StringComparison.OrdinalIgnoreCase))
            return AppMode.Work;
        return AppMode.Byok;
    }

    public static bool IsImageWorkbenchProvider(string? providerId) =>
        string.Equals(providerId, ImageWorkbenchProviderId, StringComparison.OrdinalIgnoreCase);
}

public sealed class ConversationListItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Id { get; }
    public string Title { get; }
    public DateTimeOffset UpdatedAt { get; }
    /// <summary>Three-way sidebar group (Byok / Chat / Work).</summary>
    public AppMode Group { get; }
    /// <summary>Back-compat: true for any non-MolaGPT-account conversation (Byok group).</summary>
    public bool IsByok => Group == AppMode.Byok;
    public bool IsWork => Group == AppMode.Work;
    public bool IsMolaGptAccountConversation => Group is AppMode.Chat or AppMode.Work;
    public bool IsImageTask { get; }
    public bool Pinned { get; }
    public string IconGlyph => IsImageTask ? "\uE91B" : "\uE8BD";
    public string ModeBadgeLabel => IsWork ? "Work" : string.Empty;

    private bool _isGenerating;
    public bool IsGenerating
    {
        get => _isGenerating;
        set => SetProperty(ref _isGenerating, value);
    }

    private string? _personaLabel;
    /// <summary>
    /// Display label for the persona bound to this conversation, or null
    /// when no persona is attached. Used by the sidebar BYOK badge —
    /// when set, the badge shows this name instead of the generic "自定义".
    /// </summary>
    public string? PersonaLabel
    {
        get => _personaLabel;
        set
        {
            if (SetProperty(ref _personaLabel, value))
                OnPropertyChanged(nameof(BadgeLabel));
        }
    }

    /// <summary>Sidebar badge text. Falls back to "自定义" only when no
    /// persona name was resolved.</summary>
    public string BadgeLabel => IsImageTask ? "图像" : string.IsNullOrWhiteSpace(PersonaLabel) ? "自定义" : PersonaLabel!;

    public ConversationListItem(string id, string title, DateTimeOffset updatedAt, bool isByok)
        : this(id, title, updatedAt, isByok ? AppMode.Byok : AppMode.Chat, null, false) { }

    public ConversationListItem(
        string id, string title, DateTimeOffset updatedAt, bool isByok, string? personaLabel)
        : this(id, title, updatedAt, isByok ? AppMode.Byok : AppMode.Chat, personaLabel, false) { }

    public ConversationListItem(
        string id,
        string title,
        DateTimeOffset updatedAt,
        AppMode group,
        string? personaLabel,
        bool isImageTask,
        bool pinned = false)
    {
        Id = id;
        Title = title;
        UpdatedAt = updatedAt;
        Group = group;
        IsImageTask = isImageTask;
        Pinned = pinned;
        _personaLabel = personaLabel;
    }

    public string TimeLabel => UpdatedAt.ToLocalTime().ToString(
        UpdatedAt.Date == DateTime.UtcNow.Date ? "HH:mm" : "M/d");
}

internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
