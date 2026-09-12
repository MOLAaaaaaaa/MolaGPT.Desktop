using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using MolaGPT.Core.Models;
using MolaGPT.Desktop.Services;

namespace MolaGPT.App.Views;

public partial class LorebookWindow : MolaContentWindow
{
    private List<Lorebook> _books = [];
    private Lorebook? _book;
    private LoreEntry? _entry;
    private bool _loading;
    private Action? _undoDelete;
    private readonly NotificationCenter? _notifications;
    public Func<Lorebook, string?>? CheckBookDeletion { get; set; }

    public LorebookWindow() : this(null) { }

    public LorebookWindow(NotificationCenter? notifications)
    {
        _notifications = notifications;
        InitializeComponent();
        PART_ImportBook.Click += OnImportBook;
        PART_ExportBook.Click += OnExportBook;
        PART_BookScanScope.SelectionChanged += (_, _) => PART_ScanDepth.IsVisible = PART_BookScanScope.SelectedIndex == 0;
        PART_EntryScanScope.SelectionChanged += (_, _) =>
        {
            PART_EntryDepth.IsVisible = PART_EntryScanScope.SelectedIndex == 1;
            if (!_loading && PART_EntryDepth.IsVisible && PART_EntryDepth.Value is null)
                PART_EntryDepth.Value = _book!.ScanDepth;
        };
        PART_BudgetPriorityMode.SelectionChanged += (_, _) => PART_BudgetPriority.IsEnabled = PART_BudgetPriorityMode.SelectedIndex == 1;
        PART_Placement.SelectionChanged += (_, _) => RefreshEntryOptions();
        PART_Selective.IsCheckedChanged += (_, _) => RefreshEntryOptions();
        PART_UseRegex.IsCheckedChanged += (_, _) => RefreshEntryOptions();
        PART_UseProbability.IsCheckedChanged += (_, _) => RefreshEntryOptions();
        PART_Group.TextChanged += (_, _) => RefreshEntryOptions();
        PART_GroupMode.SelectionChanged += (_, _) => RefreshEntryOptions();
        PART_Enabled.IsCheckedChanged += (_, _) =>
        {
            if (!_loading && _entry is not null) _entry.Enabled = PART_Enabled.IsChecked == true;
        };
        PART_Books.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            SaveBook();
            LoadBook(PART_Books.SelectedItem as Lorebook);
        };
        PART_Entries.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            SaveEntry();
            LoadEntry(PART_Entries.SelectedItem as LoreEntry);
        };
        PART_EntrySearch.TextChanged += (_, _) =>
        {
            if (_loading) return;
            SaveEntry();
            RefreshEntries(_entry);
        };
        PART_ClearEntrySearch.Click += (_, _) =>
        {
            PART_EntrySearch.Clear();
            PART_EntrySearch.Focus();
        };
        PART_AddBook.Click += (_, _) =>
        {
            SaveBook();
            var book = new Lorebook { ScanScope = LoreScanScope.Recent };
            _books.Add(book);
            RefreshBooks(book);
            Dispatcher.UIThread.Post(() => { PART_BookName.Focus(); PART_BookName.SelectAll(); });
        };
        PART_DeleteBook.Click += (_, _) =>
        {
            if (_book is null) return;
            if (CheckBookDeletion?.Invoke(_book) is { } reason)
            {
                _notifications?.Error("无法删除世界书", reason, "lorebook-delete");
                return;
            }
            SaveBook();
            var book = _book;
            var entry = _entry;
            var index = _books.IndexOf(book);
            _books.Remove(book);
            _undoDelete = () =>
            {
                _books.Insert(index, book);
                RefreshBooks(book);
                RefreshEntries(entry);
            };
            PART_UndoDelete.IsVisible = true;
            _book = null;
            _entry = null;
            RefreshBooks(_books.ElementAtOrDefault(index) ?? _books.LastOrDefault());
        };
        PART_AddEntry.Click += (_, _) =>
        {
            if (_book is null) return;
            SaveEntry();
            var entry = new LoreEntry { Name = "新条目", InsertionOrder = 100, ScanScope = LoreScanScope.Inherit };
            _book.Entries.Add(entry);
            PART_EntrySearch.Clear();
            RefreshEntries(entry);
            Dispatcher.UIThread.Post(() => { PART_EntryName.Focus(); PART_EntryName.SelectAll(); });
        };
        PART_DeleteEntry.Click += (_, _) =>
        {
            if (_book is null || _entry is null) return;
            SaveEntry();
            var book = _book;
            var entry = _entry;
            var index = book.Entries.IndexOf(entry);
            book.Entries.Remove(entry);
            _undoDelete = () =>
            {
                book.Entries.Insert(index, entry);
                RefreshBooks(book);
                RefreshEntries(entry);
            };
            PART_UndoDelete.IsVisible = true;
            _entry = null;
            RefreshEntries(book.Entries.ElementAtOrDefault(index) ?? book.Entries.LastOrDefault());
        };
        PART_UndoDelete.Click += (_, _) =>
        {
            SaveBook();
            _undoDelete?.Invoke();
            _undoDelete = null;
            PART_UndoDelete.IsVisible = false;
        };
        PART_BookName.LostFocus += (_, _) => SaveBook();
        PART_EntryName.LostFocus += (_, _) => SaveEntry();
        PART_Keywords.LostFocus += (_, _) => SaveEntry();
        PART_Content.LostFocus += (_, _) => SaveEntry();
        PART_Cancel.Click += (_, _) => Close(null);
        PART_Save.Click += (_, _) => { SaveBook(); Close(_books); };
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_book is null) PART_AddBook.Focus();
            else if (_entry is null) PART_AddEntry.Focus();
            else PART_EntrySearch.Focus();
        });
    }

    public Task<List<Lorebook>?> ShowForAsync(List<Lorebook> books, Window owner, RoleCompatibility compatibility = RoleCompatibility.CharacterCardSpec)
    {
        PART_BookBudgetRow.IsVisible = compatibility != RoleCompatibility.SillyTavern;
        PART_BudgetPriorityRow.IsVisible = compatibility != RoleCompatibility.SillyTavern;
        _books = RoleJson.Deserialize<List<Lorebook>>(RoleJson.Serialize(books));
        RefreshBooks(_books.FirstOrDefault());
        return ShowDialog<List<Lorebook>?>(owner);
    }

    private void RefreshBooks(Lorebook? selected)
    {
        _loading = true;
        PART_Books.ItemsSource = _books.ToArray();
        PART_Books.SelectedItem = selected;
        _loading = false;
        LoadBook(selected);
    }

    private void LoadBook(Lorebook? book)
    {
        _book = book;
        _entry = null;
        PART_BookEditor.IsVisible = book is not null;
        PART_BookEmpty.IsVisible = book is null;
        PART_DeleteBook.IsEnabled = book is not null;
        PART_ExportBook.IsEnabled = book is not null;
        if (book is null) return;
        _loading = true;
        PART_BookName.Text = book.Name;
        PART_BookEnabled.IsChecked = book.Enabled;
        PART_ScanDepth.Value = book.ScanDepth;
        PART_BookScanScope.SelectedIndex = (int)(book.ScanScope ?? (book.ScanDepth == 0 ? LoreScanScope.All : LoreScanScope.Recent)) - 1;
        PART_Recursive.IsChecked = book.RecursiveScanning;
        PART_Budget.Value = book.TokenBudget;
        _loading = true;
        PART_EntrySearch.Clear();
        _loading = false;
        RefreshEntries(book.Entries.FirstOrDefault());
    }

    private void RefreshEntries(LoreEntry? selected)
    {
        _loading = true;
        var search = PART_EntrySearch.Text?.Trim() ?? "";
        var entries = _book?.Entries.Where(entry => search.Length == 0
            || entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.Content.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.Keywords.Any(keyword => keyword.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray() ?? [];
        if (selected is null || !entries.Contains(selected)) selected = entries.FirstOrDefault();
        PART_Entries.ItemsSource = entries;
        PART_Entries.SelectedItem = selected;
        _loading = false;
        PART_Entries.IsVisible = entries.Length > 0;
        PART_EntriesEmpty.IsVisible = entries.Length == 0;
        PART_EntriesEmpty.Text = search.Length > 0 ? "未找到匹配的条目" : "暂无条目";
        PART_EntryCount.Text = search.Length > 0 ? $"匹配 {entries.Length} / 共 {_book!.Entries.Count} 条" : $"条目 · {entries.Length}";
        PART_ClearEntrySearch.IsVisible = !string.IsNullOrEmpty(PART_EntrySearch.Text);
        PART_AddEntry.IsEnabled = _book is not null;
        LoadEntry(selected);
        PART_EntryEmpty.IsVisible = selected is null && entries.Length > 0;
    }

    private void LoadEntry(LoreEntry? entry)
    {
        _entry = entry;
        PART_EntryScroll.IsVisible = entry is not null;
        PART_EntryEmpty.IsVisible = entry is null;
        PART_DeleteEntry.IsEnabled = entry is not null;
        if (entry is null) return;
        _loading = true;
        PART_EntryScroll.Offset = default;
        PART_EntryName.Text = entry.Name;
        PART_Keywords.Text = string.Join("\n", entry.Keywords);
        PART_SecondaryKeywords.Text = string.Join("\n", entry.SecondaryKeywords);
        PART_Content.Text = entry.Content;
        PART_Enabled.IsChecked = entry.Enabled;
        PART_Constant.IsChecked = entry.Constant;
        PART_CaseSensitive.IsChecked = entry.CaseSensitive;
        PART_Selective.IsChecked = entry.Selective;
        PART_Priority.Value = entry.Order;
        var budgetPriority = entry.BudgetPriority ?? (entry.InsertionOrder is null ? entry.Priority : (int?)null);
        PART_BudgetPriorityMode.SelectedIndex = budgetPriority.HasValue ? 1 : 0;
        PART_BudgetPriority.Value = budgetPriority ?? entry.Order;
        PART_UseRegex.IsChecked = entry.UseRegex;
        PART_WholeWords.IsChecked = entry.MatchWholeWords;
        PART_SelectiveLogic.SelectedIndex = (int)entry.SelectiveLogic;
        PART_EntryDepth.Value = entry.ScanDepth;
        PART_EntryScanScope.SelectedIndex = (int)(entry.ScanScope ?? (entry.ScanDepth is null
            ? LoreScanScope.Inherit : entry.ScanDepth == 0 ? LoreScanScope.All : LoreScanScope.Recent));
        PART_Placement.SelectedIndex = (int)entry.Placement;
        PART_InjectionDepth.Value = entry.Depth;
        PART_EntryRole.SelectedIndex = entry.Role switch { "user" => 1, "assistant" => 2, _ => 0 };
        PART_ExcludeRecursion.IsChecked = entry.ExcludeRecursion;
        PART_PreventRecursion.IsChecked = entry.PreventRecursion;
        PART_DelayRecursion.Value = entry.DelayUntilRecursion;
        PART_UseProbability.IsChecked = entry.UseProbability;
        PART_Probability.Value = entry.Probability;
        PART_Group.Text = entry.Group;
        PART_OutletName.Text = entry.OutletName;
        PART_GroupMode.SelectedIndex = entry.GroupOverride ? 1 : 0;
        PART_GroupWeight.Value = entry.GroupWeight;
        PART_Sticky.Value = entry.Sticky;
        PART_Cooldown.Value = entry.Cooldown;
        PART_Delay.Value = entry.Delay;
        PART_IgnoreBudget.IsChecked = entry.IgnoreBudget;
        _loading = false;
        RefreshEntryOptions();
    }

    private void RefreshEntryOptions()
    {
        PART_SecondaryConditions.IsVisible = PART_Selective.IsChecked == true;
        PART_WholeWords.IsEnabled = PART_UseRegex.IsChecked != true;
        var atDepth = PART_Placement.SelectedIndex == (int)LorePosition.AtDepth;
        PART_EntryRoleRow.IsVisible = atDepth;
        PART_InjectionDepthRow.IsVisible = atDepth;
        PART_OutletRow.IsVisible = PART_Placement.SelectedIndex == (int)LorePosition.Outlet;
        PART_Probability.IsEnabled = PART_UseProbability.IsChecked == true;
        PART_GroupOptions.IsVisible = !string.IsNullOrWhiteSpace(PART_Group.Text);
        PART_GroupWeight.IsEnabled = PART_GroupMode.SelectedIndex == 0;
    }

    private void SaveBook()
    {
        if (_loading) return;
        SaveEntry();
        if (_book is null) return;
        _book.Name = PART_BookName.Text?.Trim() ?? "";
        _book.Enabled = PART_BookEnabled.IsChecked == true;
        if (PART_ScanDepth.Value is { } scanDepth) _book.ScanDepth = (int)scanDepth;
        _book.ScanScope = (LoreScanScope)(PART_BookScanScope.SelectedIndex + 1);
        _book.RecursiveScanning = PART_Recursive.IsChecked == true;
        // An empty box means the user cleared it, not that they asked for 2048 —
        // which is what the old default silently wrote over their own number.
        _book.TokenBudget = PART_Budget.Value is { } budget ? (int)budget : _book.TokenBudget;
    }

    private void SaveEntry()
    {
        if (_loading || _entry is null) return;
        _entry.Name = PART_EntryName.Text?.Trim() ?? "";
        _entry.Content = PART_Content.Text ?? "";
        _entry.Keywords = ReadKeywords(PART_Keywords.Text, _entry.Keywords);
        _entry.SecondaryKeywords = ReadKeywords(PART_SecondaryKeywords.Text, _entry.SecondaryKeywords);
        _entry.Enabled = PART_Enabled.IsChecked == true;
        _entry.Constant = PART_Constant.IsChecked == true;
        _entry.CaseSensitive = PART_CaseSensitive.IsChecked == true;
        _entry.Selective = PART_Selective.IsChecked == true;
        if (PART_Priority.Value is { } order) _entry.InsertionOrder = (int)order;
        _entry.BudgetPriority = PART_BudgetPriorityMode.SelectedIndex == 1 ? (int)(PART_BudgetPriority.Value ?? 0) : null;
        _entry.UseRegex = PART_UseRegex.IsChecked == true;
        _entry.MatchWholeWords = PART_WholeWords.IsChecked == true;
        _entry.SelectiveLogic = (LoreSelectiveLogic)Math.Max(0, PART_SelectiveLogic.SelectedIndex);
        _entry.ScanDepth = PART_EntryDepth.Value is { } depth ? (int)depth : null;
        _entry.ScanScope = (LoreScanScope)Math.Max(0, PART_EntryScanScope.SelectedIndex);
        _entry.Position = (LorePosition)Math.Max(0, PART_Placement.SelectedIndex);
        _entry.BeforeCharacter = _entry.Position == LorePosition.BeforeCharacter;
        if (PART_InjectionDepth.Value is { } injectionDepth) _entry.Depth = (int)injectionDepth;
        _entry.Role = PART_EntryRole.SelectedIndex switch { 1 => "user", 2 => "assistant", _ => "system" };
        _entry.ExcludeRecursion = PART_ExcludeRecursion.IsChecked == true;
        _entry.PreventRecursion = PART_PreventRecursion.IsChecked == true;
        _entry.DelayUntilRecursion = (int)(PART_DelayRecursion.Value ?? 0);
        _entry.UseProbability = PART_UseProbability.IsChecked == true;
        if (PART_Probability.Value is { } probability) _entry.Probability = (int)probability;
        _entry.Group = PART_Group.Text?.Trim() ?? "";
        _entry.OutletName = PART_OutletName.Text?.Trim() ?? "";
        _entry.GroupOverride = PART_GroupMode.SelectedIndex == 1;
        if (PART_GroupWeight.Value is { } weight) _entry.GroupWeight = (int)weight;
        _entry.Sticky = (int)(PART_Sticky.Value ?? 0);
        _entry.Cooldown = (int)(PART_Cooldown.Value ?? 0);
        _entry.Delay = (int)(PART_Delay.Value ?? 0);
        _entry.IgnoreBudget = PART_IgnoreBudget.IsChecked == true;
    }

    private List<string> ReadKeywords(string? text, List<string> current)
    {
        if (text == string.Join("\n", current)) return current;
        var lines = (text ?? "").Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return lines.SelectMany(line => PART_UseRegex.IsChecked == true || line.StartsWith('/') && line.LastIndexOf('/') > 0
            ? new[] { line } : line.Split([',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToList();
    }

    private async void OnImportBook(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入世界书", FileTypeFilter = [new FilePickerFileType("世界书") { Patterns = ["*.json"] }]
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var book = CharacterCardReader.ReadLorebook(buffer.ToArray());
            SaveBook();
            _books.Add(book);
            RefreshBooks(book);
        }
        catch (Exception ex) { _notifications?.Error("世界书导入失败", ex.Message, "lorebook-import"); }
    }

    private async void OnExportBook(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_book is null) return;
        SaveBook();
        var name = string.Concat(_book.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出世界书", SuggestedFileName = name + ".json", DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("世界书") { Patterns = ["*.json"] }]
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(CharacterCardWriter.WriteLorebook(_book));
            _notifications?.Success("世界书已导出", _book.Name, key: "lorebook-export");
        }
        catch (Exception ex) { _notifications?.Error("世界书导出失败", ex.Message, "lorebook-export"); }
    }
}
