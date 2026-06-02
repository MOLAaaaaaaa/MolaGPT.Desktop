using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MolaGPT.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.ViewModels;

/// <summary>
/// Sidebar conversation list view model. Backed by SQLite via
/// <see cref="ConversationRepository"/> when one is supplied; otherwise (M1
/// boot) falls back to in-memory mock data. Selection changes always raise
/// <see cref="ConversationSelected"/> so MainViewModel can swap the chat.
///
/// The list is split into two parallel collections — <see cref="ByokItems"/>
/// and <see cref="MolaGptItems"/> — both individually collapsible. Group
/// expansion state persists across restarts via <see cref="SettingsRepository"/>
/// under the keys <c>sidebar_group_byok_expanded</c> /
/// <c>sidebar_group_molagpt_expanded</c>. BYOK is rendered first (above
/// MolaGPT) per product preference.
///
/// We keep a single master <see cref="Items"/> collection (used by sync /
/// search / upsert paths) and derive the two group collections by
/// subscribing to its CollectionChanged event. Two-collection-with-sync is
/// simpler than introducing a WPF <c>ICollectionView</c> in this project,
/// which intentionally has no PresentationFramework reference.
/// </summary>
public sealed partial class ConversationListViewModel : ObservableObject
{
    public const string ImageWorkbenchProviderId = "byok-image-workbench";
    private const string SettingByokExpandedKey = "sidebar_group_byok_expanded";
    private const string SettingMolaGptExpandedKey = "sidebar_group_molagpt_expanded";

    public ObservableCollection<ConversationListItem> Items { get; } = new();

    /// <summary>BYOK conversations only — bound to the BYOK ListBox in the sidebar.</summary>
    public ObservableCollection<ConversationListItem> ByokItems { get; } = new();

    /// <summary>MolaGPT-account conversations only — bound to the MolaGPT ListBox.</summary>
    public ObservableCollection<ConversationListItem> MolaGptItems { get; } = new();

    [ObservableProperty] private string? _selectedId;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ByokGroupChevron))]
    private bool _isByokGroupExpanded = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MolaGptGroupChevron))]
    private bool _isMolaGptGroupExpanded = true;

    /// <summary>Fluent chevron glyph for the BYOK group header (E70D = down,
    /// E70E = up). Bound directly so the header doesn't need a converter.</summary>
    public string ByokGroupChevron => IsByokGroupExpanded ? "" : "";
    public string MolaGptGroupChevron => IsMolaGptGroupExpanded ? "" : "";

    /// <summary>Counts shown next to each group header.</summary>
    public int ByokCount => ByokItems.Count;
    public int MolaGptCount => MolaGptItems.Count;

    /// <summary>True when the BYOK group has at least one conversation.
    /// Lets the XAML hide the entire group block (header + list) for
    /// users who never use BYOK, instead of showing an empty section.</summary>
    public bool HasByokConversations => ByokItems.Count > 0;
    public bool HasMolaGptConversations => MolaGptItems.Count > 0;

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

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_bulkUpdatingItems) return;

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Reset only fires from Items.Clear() in current code paths, so
            // Items is empty here and groups simply mirror that emptiness.
            ByokItems.Clear();
            MolaGptItems.Clear();
            RaiseGroupCountChanges();
            return;
        }

        if (e.OldItems is not null)
        {
            foreach (ConversationListItem old in e.OldItems)
            {
                if (old.IsByok) ByokItems.Remove(old);
                else MolaGptItems.Remove(old);
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
                var target = n.IsByok ? ByokItems : MolaGptItems;
                if (prepend) target.Insert(0, n);
                else target.Add(n);
            }
        }
        RaiseGroupCountChanges();
    }

    private void RaiseGroupCountChanges()
    {
        OnPropertyChanged(nameof(ByokCount));
        OnPropertyChanged(nameof(MolaGptCount));
        OnPropertyChanged(nameof(HasByokConversations));
        OnPropertyChanged(nameof(HasMolaGptConversations));
    }

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

        _bulkUpdatingItems = true;
        try
        {
            Items.Clear();
            ByokItems.Clear();
            MolaGptItems.Clear();
            SetSelectedIds(Array.Empty<string>());

            foreach (var row in rows)
            {
                var item = new ConversationListItem(
                    row.Id,
                    string.IsNullOrWhiteSpace(row.Title) ? "新对话" : row.Title,
                    DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAt),
                    IsByokProvider(row.ProviderId),
                    _personas?.Find(row.PersonaId)?.Name,
                    IsImageWorkbenchProvider(row.ProviderId));

                // Restore the spinner for any task still generating in the
                // background — ApplyRows builds fresh items (IsGenerating
                // defaults false), so without this a mid-flight workbench task
                // loses its sidebar spinner on the next reload.
                if (_generatingIds.Contains(row.Id))
                    item.IsGenerating = true;

                Items.Add(item);
                if (item.IsByok) ByokItems.Add(item);
                else MolaGptItems.Add(item);
            }
        }
        finally
        {
            _bulkUpdatingItems = false;
        }

        RaiseGroupCountChanges();
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
                || item.IsByok != IsByokProvider(row.ProviderId)
                || item.IsImageTask != IsImageWorkbenchProvider(row.ProviderId))
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
    public void UpsertItem(string id, string title, DateTimeOffset updatedAt, string? providerId = null, string? personaLabel = null)
    {
        if (string.IsNullOrEmpty(id)) return;
        var existingIdx = -1;
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].Id == id) { existingIdx = i; break; }
        }

        var normalized = string.IsNullOrWhiteSpace(title) ? "新对话" : title;
        var isByok = providerId is null && existingIdx >= 0
            ? Items[existingIdx].IsByok
            : IsByokProvider(providerId);
        var isImageTask = providerId is null && existingIdx >= 0
            ? Items[existingIdx].IsImageTask
            : IsImageWorkbenchProvider(providerId);
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
        var next = new ConversationListItem(id, normalized, updatedAt, isByok, resolvedPersonaLabel, isImageTask);
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
                IsByokProvider(row.ProviderId),
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

    private static bool IsByokProvider(string? providerId) =>
        !string.IsNullOrWhiteSpace(providerId)
        && !string.Equals(providerId, "molagpt-proxy", StringComparison.OrdinalIgnoreCase);

    public static bool IsImageWorkbenchProvider(string? providerId) =>
        string.Equals(providerId, ImageWorkbenchProviderId, StringComparison.OrdinalIgnoreCase);
}

public sealed class ConversationListItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Id { get; }
    public string Title { get; }
    public DateTimeOffset UpdatedAt { get; }
    public bool IsByok { get; }
    public bool IsImageTask { get; }
    public string IconGlyph => IsImageTask ? "\uE91B" : "\uE8BD";

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
        : this(id, title, updatedAt, isByok, null) { }

    public ConversationListItem(
        string id, string title, DateTimeOffset updatedAt, bool isByok, string? personaLabel)
        : this(id, title, updatedAt, isByok, personaLabel, false) { }

    public ConversationListItem(
        string id, string title, DateTimeOffset updatedAt, bool isByok, string? personaLabel, bool isImageTask)
    {
        Id = id;
        Title = title;
        UpdatedAt = updatedAt;
        IsByok = isByok;
        IsImageTask = isImageTask;
        _personaLabel = personaLabel;
    }

    public string TimeLabel => UpdatedAt.ToLocalTime().ToString(
        UpdatedAt.Date == DateTime.UtcNow.Date ? "HH:mm" : "M/d");
}
