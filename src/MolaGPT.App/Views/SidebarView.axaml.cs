using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Tools.PythonExecution;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class SidebarView : UserControl
{
    private const double ConversationGroupDefaultByokMaxHeight = 240;
    private static readonly TimeSpan ConversationGroupRestoreDelay = TimeSpan.FromMilliseconds(1200);

    private ConversationListViewModel? _vm;
    private bool _syncing;
    private bool _conversationGroupLayoutFocused;
    private readonly DispatcherTimer _conversationGroupLayoutRestoreTimer = new()
    {
        Interval = ConversationGroupRestoreDelay
    };

    public SidebarView()
    {
        InitializeComponent();

        PART_ByokList.SelectionChanged += OnSelectionChanged;
        PART_MolaGptList.SelectionChanged += OnSelectionChanged;
        PART_ByokList.AddHandler(
            PointerWheelChangedEvent,
            OnByokPointerWheelChanged,
            RoutingStrategies.Tunnel);
        PART_MolaGptList.AddHandler(
            PointerWheelChangedEvent,
            OnMolaGptPointerWheelChanged,
            RoutingStrategies.Tunnel);
        PART_ByokList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            OnByokScrollChanged,
            RoutingStrategies.Bubble);
        PART_MolaGptList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            OnMolaGptScrollChanged,
            RoutingStrategies.Bubble);
        PART_ConversationGroupsHost.PointerEntered += (_, _) =>
            _conversationGroupLayoutRestoreTimer.Stop();
        PART_ConversationGroupsHost.PointerExited += (_, _) =>
            ScheduleConversationGroupLayoutRestore();
        PART_ByokHeader.Click += OnConversationGroupHeaderClick;
        PART_MolaGptHeader.Click += OnConversationGroupHeaderClick;
        PART_NewChat.Click += (_, _) => NewConversationRequested?.Invoke(this, EventArgs.Empty);
        PART_NewImageTask.Click += (_, _) => NewImageTaskRequested?.Invoke(this, EventArgs.Empty);
        PART_Collapse.Click += (_, _) => CollapseRequested?.Invoke(this, EventArgs.Empty);
        DataContextChanged += (_, _) => Attach(DataContext as ConversationListViewModel);
        _conversationGroupLayoutRestoreTimer.Tick += (_, _) =>
        {
            _conversationGroupLayoutRestoreTimer.Stop();
            if (!PART_ConversationGroupsHost.IsPointerOver)
                RestoreConversationGroupLayout();
        };
    }


    public event EventHandler? NewConversationRequested;
    public event EventHandler? NewImageTaskRequested;
    public event EventHandler? CollapseRequested;

    private void OnByokPointerWheelChanged(object? sender, PointerWheelEventArgs e) =>
        FocusConversationGroupLayout(byok: true);

    private void OnMolaGptPointerWheelChanged(object? sender, PointerWheelEventArgs e) =>
        FocusConversationGroupLayout(byok: false);

    private void OnByokScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.OffsetDelta.Y) > 0.01)
            FocusConversationGroupLayout(byok: true);
    }

    private void OnMolaGptScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.OffsetDelta.Y) > 0.01)
            FocusConversationGroupLayout(byok: false);
    }

    private void OnConversationGroupHeaderClick(object? sender, RoutedEventArgs e)
    {
        if (!_conversationGroupLayoutFocused || sender is not Control { Tag: string tag })
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is not null)
            {
                _vm.IsByokGroupExpanded = true;
                _vm.IsMolaGptGroupExpanded = true;
            }

            FocusConversationGroupLayout(string.Equals(tag, "byok", StringComparison.Ordinal));
        }, DispatcherPriority.Input);
    }

    private void FocusConversationGroupLayout(bool byok)
    {
        _conversationGroupLayoutRestoreTimer.Stop();
        _conversationGroupLayoutFocused = true;

        if (byok)
        {
            PART_ByokList.MaxHeight = double.PositiveInfinity;
            PART_ConversationGroupsHost.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            PART_ConversationGroupsHost.RowDefinitions[3].Height = new GridLength(0);
        }
        else
        {
            PART_ByokList.MaxHeight = ConversationGroupDefaultByokMaxHeight;
            PART_ConversationGroupsHost.RowDefinitions[1].Height = new GridLength(0);
            PART_ConversationGroupsHost.RowDefinitions[3].Height = new GridLength(1, GridUnitType.Star);
        }

        if (!PART_ConversationGroupsHost.IsPointerOver)
            ScheduleConversationGroupLayoutRestore();
    }

    private void ScheduleConversationGroupLayoutRestore()
    {
        if (!_conversationGroupLayoutFocused)
            return;

        _conversationGroupLayoutRestoreTimer.Stop();
        _conversationGroupLayoutRestoreTimer.Start();
    }

    private void RestoreConversationGroupLayout()
    {
        if (_vm is not null)
        {
            _vm.IsByokGroupExpanded = true;
            _vm.IsMolaGptGroupExpanded = true;
        }

        PART_ByokList.MaxHeight = ConversationGroupDefaultByokMaxHeight;
        PART_ConversationGroupsHost.RowDefinitions[1].Height = GridLength.Auto;
        PART_ConversationGroupsHost.RowDefinitions[3].Height = new GridLength(1, GridUnitType.Star);
        _conversationGroupLayoutFocused = false;
    }

    /// <summary>
    /// The view model exposes SelectedId and SelectById rather than a
    /// SelectedItem, so selection is mirrored in both directions here. The
    /// <see cref="_syncing"/> guard matters: without it, pushing the view
    /// model's id into the ListBox raises SelectionChanged, which pushes it
    /// straight back and re-enters LoadConversationAsync.
    /// </summary>
    private void Attach(ConversationListViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.SelectionRestoreRequested -= OnSelectionRestoreRequested;
        }
        _vm = vm;
        if (_vm is null) return;

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.SelectionRestoreRequested += OnSelectionRestoreRequested;
        SyncSelectionFromViewModel();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationListViewModel.SelectedId))
            SyncSelectionFromViewModel();
    }

    /// <summary>
    /// Pushes the view model's selection into whichever group holds it, and
    /// clears the other. Two ListBoxes each keep their own selection, so without
    /// the clear a conversation stays highlighted in the BYOK group after the
    /// user picks one from the MolaGPT group.
    /// </summary>
    private void SyncSelectionFromViewModel()
    {
        if (_vm is null || _syncing) return;

        var target = _vm.SelectedId is { Length: > 0 } id ? _vm.FindItem(id) : null;

        _syncing = true;
        try
        {
            var inByok = target is not null && _vm.ByokItems.Contains(target);
            PART_ByokList.SelectedItems!.Clear();
            PART_MolaGptList.SelectedItems!.Clear();
            if (target is not null)
            {
                if (inByok) PART_ByokList.SelectedItems!.Add(target);
                else PART_MolaGptList.SelectedItems!.Add(target);
            }
        }
        finally { _syncing = false; }
    }

    // ---- row actions -------------------------------------------------------

    /// <summary>Hover trash button. The id travels on Tag rather than through the
    /// DataContext so a recycled row cannot delete the wrong conversation.</summary>
    private void OnDeleteConversation(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is Control { Tag: string id } && id.Length > 0)
            _vm.DeleteConversationCommand.Execute(id);

        // Without this the click also selects the row it just deleted.
        e.Handled = true;
    }

    private void OnDeleteConversationFromMenu(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (RowOf(sender) is { } item) _vm.DeleteConversationCommand.Execute(item.Id);
    }

    /// <summary>
    /// Opens the conversation's Python workspace — where attachments are copied
    /// and where tool runs write their artifacts.
    /// </summary>
    private void OnOpenConversationFolder(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } item) return;
        if (FolderOf(item) is not { } folder) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch
        {
            // Explorer refusing to open is not worth a dialog; the menu item is
            // already disabled for the cases we can predict.
        }
    }

    /// <summary>
    /// Null when the conversation cannot have a workspace — MolaGPT account
    /// chats run everything server-side — or when nothing has been written yet.
    /// The path is derivable for any id, so existence is what decides.
    /// </summary>
    private static string? FolderOf(ConversationListItem item)
    {
        if (item.Group == AppMode.Chat) return null;
        var directory = PythonExecutionTool.GetSessionDirectory(item.Id);
        return Directory.Exists(directory) ? directory : null;
    }

    /// <summary>The row a context-menu click was raised for. The menu lives in
    /// its own visual tree, so this goes through the placement target rather
    /// than walking up from the item.</summary>
    private static ConversationListItem? RowOf(object? sender) =>
        sender is MenuItem { Parent: ContextMenu menu }
            ? (menu.PlacementTarget as Control)?.DataContext as ConversationListItem
            : null;

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm is null) return;
        if (sender is not ListBox list) return;

        _syncing = true;
        try
        {
            var other = ReferenceEquals(list, PART_ByokList) ? PART_MolaGptList : PART_ByokList;
            if (e.AddedItems.Count > 0) other.SelectedItems!.Clear();

            var ids = PART_ByokList.SelectedItems!.OfType<ConversationListItem>()
                .Concat(PART_MolaGptList.SelectedItems!.OfType<ConversationListItem>())
                .Select(item => item.Id)
                .ToArray();
            _vm.SetSelectedIds(ids);

            if (e.AddedItems.Count > 0 && e.AddedItems[^1] is ConversationListItem clicked)
                _vm.SelectById(clicked.Id);
        }
        finally { _syncing = false; }
    }

    private void OnSelectionRestoreRequested(object? sender, IReadOnlyList<string> selectedIds)
    {
        var selected = selectedIds.ToHashSet(StringComparer.Ordinal);
        _syncing = true;
        try
        {
            PART_ByokList.SelectedItems!.Clear();
            PART_MolaGptList.SelectedItems!.Clear();
            foreach (var item in PART_ByokList.Items!.OfType<ConversationListItem>())
                if (selected.Contains(item.Id)) PART_ByokList.SelectedItems!.Add(item);
            foreach (var item in PART_MolaGptList.Items!.OfType<ConversationListItem>())
                if (selected.Contains(item.Id)) PART_MolaGptList.SelectedItems!.Add(item);
        }
        finally { _syncing = false; }
    }
}
