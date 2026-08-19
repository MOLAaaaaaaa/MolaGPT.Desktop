using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Tools.PythonExecution;
using MolaGPT.Core.Models;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly ProviderRegistry _providers;

    /// <summary>Sidebar widths matching the design tokens
    /// (Size.Sidebar.Width=280, Size.Sidebar.Collapsed=80, but we collapse to
    /// 0 because the expand pill lives in the floating header instead).</summary>
    private const double SidebarExpandedWidth = 280;
    private const double SidebarCollapsedWidth = 0;
    private const double SidebarGapWidth = 16;
    private const double SidebarAnimationMs = 220;
    /// <summary>Pixels per scrolled line. WPF's own pixel-mode ScrollViewer
    /// uses 16, which at the default 3 lines/notch lands near 48px — short
    /// enough that each notch reads as its own discrete step. This puts a notch
    /// at ~96px, in the same range as Chrome's wheel tick.</summary>
    private const double MessagesWheelLinePixels = 32;
    /// <summary>Natural frequency (rad/s) of the critically damped spring that
    /// drives the message list. This is the one knob for scroll feel — raise it
    /// for snappier, lower it for floatier. Critically damped means it never
    /// overshoots, so there is no bounce to tune away.
    ///
    /// The spring carries velocity as state, which is what the two simpler
    /// models could not do. A fixed-duration ease resets its clock and start
    /// point on every notch, so each notch relaunches at the ease's fastest
    /// frame. An exponential chase has speed proportional to distance left, so
    /// a notch fired from rest also peaks on its very first frame — a lurch,
    /// then a crawl. A spring starts from the velocity it already had (zero,
    /// from rest), accelerates, then decelerates: it eases in as well as out,
    /// and its peak speed is ~1/e of the chase's for the same frequency, which
    /// is roughly a third of the motion blur over the same travel.
    ///
    /// Tuned against Chrome's wheel scrolling, which spreads a tick over
    /// roughly 250-300ms. At 30 a notch was visually finished inside ~120ms —
    /// only a handful of frames — so consecutive notches read as separate
    /// steps instead of one continuous glide. Note this trades in the helpful
    /// direction: peak speed is w*distance/e, so softening the spring buys
    /// back more than lengthening the notch spends.</summary>
    private const double MessagesScrollSpringFrequency = 20.0;
    /// <summary>Distance at which the spring snaps to the target and unhooks.
    /// Below one device pixel at every scale factor we ship, so the snap
    /// itself is never visible.</summary>
    private const double MessagesScrollSettleEpsilon = 0.75;
    /// <summary>Companion to the distance test: near the target the spring is
    /// barely moving, but checking distance alone could unhook it mid-flight
    /// while it still had speed to shed (e.g. passing through the target after
    /// a direction reversal).</summary>
    private const double MessagesScrollSettleVelocity = 40.0;
    /// <summary>Ceiling on a single frame's dt, so a GC pause or a dropped
    /// frame can't be integrated into one large jump.</summary>
    private const double MessagesScrollMaxFrameSeconds = 0.05;
    private const double MessagesBottomInsetMin = 132;
    private const double MessagesBottomGap = 0;
    private const double MessagesBottomStickTolerance = 48;
    /// <summary>How close to the top the user has to get before the next slice
    /// of history is pulled in — roughly a screenful of warning so the content
    /// is there by the time they reach it.</summary>
    private const double MessagesOlderLoadTriggerPx = 240;
    private const int MessagesOlderLoadBatch = 20;
    private const double ConversationGroupDefaultByokMaxHeight = 240;
    private const double ConversationGroupRestoreDelayMs = 1200;

    // The spring integrates its own position rather than reading VerticalOffset
    // back each frame: ScrollToVerticalOffset doesn't take effect until the
    // next layout pass, so reading it would feed us a stale value and the
    // steps would compound short.
    private double _messagesScrollCurrent;
    private double _messagesScrollVelocity;
    private double _messagesScrollTargetOffset;
    private DateTime _messagesScrollLastFrame;
    private bool _messagesScrollAnimating;
    private bool _scrollToBottomVisible;

    // Stream-follow uses an explicit gesture-driven stick state instead of a
    // per-frame geometry test. During a streaming re-render the ScrollViewer's
    // extent briefly shrinks then grows and WPF clamps the offset toward the
    // bottom; a geometry test ("are we near the bottom right now?") then
    // misreads that clamp as "user is at the bottom" and yanks them down even
    // after they scrolled up. Instead: we follow only while _followStreamBottom
    // is true, the user's own upward scroll detaches it, and scrolling back to
    // the bottom (or sending a new message) re-attaches it.
    private bool _followStreamBottom = true;
    // Set around our own ScrollToVerticalOffset calls so the resulting
    // ScrollChanged isn't mistaken for a user gesture.
    private bool _programmaticScroll;

    // Set while the message list is being rewritten wholesale — a conversation
    // load, or an older-history prepend. Both grow the scroll extent, which the
    // _followStreamBottom path reads as "new content arrived, jump to the
    // newest". During a load that would drag every message through the viewport
    // and force it to render on the way past; during a prepend it would throw
    // the user to the bottom of the conversation they were scrolling up through.
    private bool _suppressFollowBottom;
    private bool _loadingOlderMessages;
    private bool _olderLoadQueued;
    private bool _conversationGroupLayoutFocused;
    private bool _clearingOtherConversationGroupSelection;
    private bool _restoringConversationGroupSelection;
    private readonly DispatcherTimer _conversationGroupLayoutRestoreTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(ConversationGroupRestoreDelayMs)
    };

    public MainWindow(ProviderRegistry providers)
    {
        InitializeComponent();
        _providers = providers;

        // Re-build the model dropdown whenever providers come or go
        // (login finishes, BYOK provider added/removed in Settings, etc).
        _providers.Changed += (_, _) => Dispatcher.InvokeAsync(RebuildModelSelector);
        Loaded += (_, _) => RebuildModelSelector();
        Loaded += (_, _) => QueueMessagesViewportUpdate();

        // Wire sidebar fold/unfold to MainViewModel.SidebarCollapsed.
        // We listen on DataContextChanged because DI sets DataContext after
        // construction; MVM is a singleton so we never see a second swap.
        DataContextChanged += OnDataContextChanged;
        _conversationGroupLayoutRestoreTimer.Tick += (_, _) =>
        {
            _conversationGroupLayoutRestoreTimer.Stop();
            if (ConversationGroupsHost?.IsMouseOver == true)
                return;
            RestoreConversationGroupLayout();
        };
    }

    public void ShowImageWorkbench(ImageGenerationWorkbenchWindow workbench)
    {
        if (ImageWorkbenchHost.Content is ImageGenerationWorkbenchWindow oldWorkbench)
            oldWorkbench.DetachHeaderModelSelector();

        ImageWorkbenchHost.Content = workbench;
        workbench.AttachHeaderModelSelector(
            WorkbenchModelSelectorButton,
            WorkbenchModelSelectorPopup,
            WorkbenchModelSelectorItems,
            WorkbenchModelSelectorSearchBox,
            WorkbenchModelLabel);
        if (DataContext is MainViewModel vm)
            vm.IsImageWorkbenchVisible = true;
    }

    public void HideImageWorkbench()
    {
        if (ImageWorkbenchHost.Content is ImageGenerationWorkbenchWindow workbench)
        {
            workbench.NotifyClosedWhileGenerating();
            workbench.DetachHeaderModelSelector();
        }

        if (DataContext is MainViewModel vm)
            vm.CloseImageWorkbench();
    }

    public bool IsImageWorkbenchGenerating =>
        ImageWorkbenchHost.Content is ImageGenerationWorkbenchWindow { IsGenerating: true };

    // ===== Self-drawn window chrome (WindowChrome) caption buttons =====
    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    // Close goes through Window.Close() so the existing closing logic (tray / confirm) still runs.
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Keep the maximize/restore glyph in sync. The content no longer
    /// needs an inset when maximized — <see cref="WindowProc"/> clamps the
    /// maximized rect to the work area, so nothing overhangs it any more.</summary>
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        if (MaximizeRestoreGlyph is not null)
            MaximizeRestoreGlyph.Text = maximized ? "" : "";
        if (MaximizeRestoreButton is not null)
            MaximizeRestoreButton.ToolTip = maximized ? "向下还原" : "最大化";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableModernWindowFrame();

        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WindowProc);
    }

    /// <summary>
    /// Clamp the maximized window to the monitor's work area.
    /// <para>
    /// A <c>WindowStyle=None</c> + <c>WindowChrome</c> window maximizes to the
    /// full monitor bounds inflated by the resize border rather than to the work
    /// area — measured as a 1934x1094 rect against a 1920x1032 work area, so 55px
    /// of the window sat behind the taskbar. On a normal window that strip is
    /// just frame and nobody notices; on a self-drawn one it is content, and the
    /// composer's bottom edge and the last sidebar row got cut off. Answering
    /// WM_GETMINMAXINFO with the work area fixes it at the source instead of
    /// insetting the content to compensate.
    /// </para>
    /// </summary>
    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO)
            return IntPtr.Zero;

        // Resolve the work area from the monitor the window is actually on: it
        // differs per screen (taskbar edge, docked bars), so a cached primary
        // work area would clip again on a secondary display.
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return IntPtr.Zero;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return IntPtr.Zero;

        // Both structs are in physical pixels, so no DPI conversion here.
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        // handled=true below skips WPF's own WM_GETMINMAXINFO handling, which is
        // where MinWidth/MinHeight normally reach the OS — so re-apply them here
        // or the window becomes draggable down to nothing. These are DIPs, the
        // struct is physical pixels.
        var dpi = VisualTreeHelper.GetDpi(this);
        mmi.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
        mmi.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);
        Marshal.StructureToPtr(mmi, lParam, true);

        handled = true;
        return IntPtr.Zero;
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x0002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    /// <summary>Win11: round the window corners, which also makes DWM draw the
    /// system drop shadow — so the borderless WindowChrome window reads as a
    /// floating card instead of a flat rectangle. Swallowed on Windows 10 / older
    /// where the attribute is unknown (corners simply stay square).</summary>
    private void TryEnableModernWindowFrame()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch
        {
            // Pre-Win11 or DWM unavailable — keep the plain square frame.
        }
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldMainVm)
        {
            oldMainVm.PropertyChanged -= OnVmPropertyChanged;
            oldMainVm.Chat.PropertyChanged -= OnChatPropertyChanged;
            oldMainVm.ConversationList.PropertyChanged -= OnConversationListPropertyChanged;
            oldMainVm.ConversationList.SelectionRestoreRequested -= OnSelectionRestoreRequested;
            oldMainVm.Composer.MessageSubmitted -= OnComposerMessageSubmitted;
        }
        else if (e.OldValue is INotifyPropertyChanged oldVm)
        {
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }

        if (e.NewValue is MainViewModel newVm)
        {
            newVm.PropertyChanged += OnVmPropertyChanged;
            newVm.Chat.PropertyChanged += OnChatPropertyChanged;
            newVm.ConversationList.PropertyChanged += OnConversationListPropertyChanged;
            newVm.ConversationList.SelectionRestoreRequested += OnSelectionRestoreRequested;
            newVm.Composer.MessageSubmitted += OnComposerMessageSubmitted;
            // Apply initial state without animation so first paint is right.
            ApplySidebarState(newVm.SidebarCollapsed, animate: false);
        }
    }

    /// <summary>
    /// Park stream-follow for the duration of a conversation load, then
    /// re-attach it with a single scroll once the list has settled.
    /// <para>
    /// Without this, each message added during the load grows the extent and
    /// re-triggers follow-bottom, which walks the viewport down through the
    /// entire history — every message gets realized and fully rendered on its
    /// way past, then recycled unseen. Suppressing it keeps the load
    /// proportional to what's actually on screen.
    /// </para>
    /// </summary>
    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChatViewModel.IsConversationLoading)
            || sender is not ChatViewModel chat)
            return;

        if (chat.IsConversationLoading)
        {
            _suppressFollowBottom = true;
            return;
        }

        _suppressFollowBottom = false;
        // A freshly opened conversation always starts pinned to its newest
        // message, whatever the user had been reading in the previous one.
        _followStreamBottom = true;
        QueueMessagesScrollToEnd();
        QueueEnsureMessagesFillViewport();
    }

    /// <summary>A fresh send re-attaches bottom-follow even if the user had
    /// scrolled up, so their new message and the reply come into view.</summary>
    private void OnComposerMessageSubmitted()
    {
        _followStreamBottom = true;
        QueueMessagesScrollToEnd();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SidebarCollapsed)
            && sender is MainViewModel vm)
        {
            ApplySidebarState(vm.SidebarCollapsed, animate: true);
        }
        else if (e.PropertyName == nameof(MainViewModel.ArtifactPanelVisible)
            && sender is MainViewModel vm2)
        {
            ApplyArtifactPanelState(vm2.ArtifactPanelVisible, animate: true);
        }
    }

    /// <summary>Opens the file explorer with the clicked artifact selected. The
    /// row's DataContext is the <see cref="ArtifactItemViewModel"/>.</summary>
    private void ArtifactCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is FrameworkElement fe && fe.DataContext is ViewModels.ArtifactItemViewModel artifact)
        {
            if (vm.RevealArtifactCommand.CanExecute(artifact))
                vm.RevealArtifactCommand.Execute(artifact);
        }
    }

    private void OnConversationListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConversationListViewModel.SelectedId)
            || sender is not ConversationListViewModel list)
            return;

        if (list.SelectedId is null)
        {
            ClearConversationGroupSelection();
            return;
        }

        ApplyConversationGroupSelection(list.SelectedId);
    }

    /// <summary>
    /// Gate the row's context menu just before it opens. "打开本地工作目录" stays
    /// visible but disabled when there is nothing to open, so the action is
    /// discoverable on the conversations that lack a folder too — a menu item
    /// that comes and goes reads as a bug.
    /// </summary>
    private void ConversationRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu, DataContext: ConversationListItem item }) return;

        var openFolder = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(m => string.Equals(m.Tag as string, "open-folder", StringComparison.Ordinal));
        if (openFolder is null) return;

        var folder = ResolveConversationFolder(item);
        openFolder.IsEnabled = folder is not null;
        openFolder.ToolTip = folder ?? ExplainMissingConversationFolder(item);
    }

    /// <summary>Why a conversation has no folder to open. Worth spelling out: the
    /// three reasons are not interchangeable, and "没有文件" would be a lie for an
    /// image task that has produced plenty of them.</summary>
    private static string ExplainMissingConversationFolder(ConversationListItem item) => item switch
    {
        { Group: AppMode.Chat } => "MolaGPT 账号对话没有本地工作目录：附件和工具都在服务端运行",
        { IsImageTask: true } => "图像工作台的图片存放在共享附件库中，没有单独的会话目录",
        _ => "这个对话还没有产生本地文件"
    };

    /// <summary>
    /// The conversation's Python workspace — where attachments are copied and
    /// where tool runs write their artifacts. Null when the conversation cannot
    /// have one (MolaGPT proxy chats run everything server-side) or when nothing
    /// has been written yet. The path itself is derivable for any id, so
    /// existence is what decides, not the path.
    /// </summary>
    private static string? ResolveConversationFolder(ConversationListItem item)
    {
        if (item.Group == AppMode.Chat) return null;
        var dir = PythonExecutionTool.GetSessionDirectory(item.Id);
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>The row a context-menu click was raised for. The menu sits in its
    /// own visual tree, so we go through PlacementTarget instead of binding.</summary>
    private static ConversationListItem? ResolveContextMenuConversation(object sender) =>
        sender is MenuItem { Parent: ContextMenu menu }
            ? (menu.PlacementTarget as FrameworkElement)?.DataContext as ConversationListItem
            : null;

    private void OpenConversationFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveContextMenuConversation(sender) is not { } item) return;
        if (ResolveConversationFolder(item) is not { } folder) return;

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(folder);
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Open conversation folder failed: {ex}");
        }
    }

    private void DeleteConversationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (ResolveContextMenuConversation(sender) is not { } item) return;
        vm.ConversationList.DeleteConversationCommand.Execute(item.Id);
    }

    private void ConversationListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_clearingOtherConversationGroupSelection || _restoringConversationGroupSelection) return;
        if (DataContext is not MainViewModel vm || sender is not ListBox listBox) return;

        // The sidebar is split into two parallel ListBoxes (BYOK / MolaGPT).
        // Keep bulk selection within one source group so own-key and account
        // conversations do not get mixed during Ctrl/Shift selection.
        if (e.AddedItems.Count > 0)
            ClearOtherConversationGroupSelection(listBox);

        var ids = new List<string>();
        foreach (var lb in ConversationGroupListBoxes())
            ids.AddRange(lb.SelectedItems.OfType<ConversationListItem>().Select(i => i.Id));
        vm.ConversationList.SetSelectedIds(ids);

        // Drive the active conversation off e.AddedItems rather than the
        // SelectedValue binding. Under SelectionMode=Extended + a SelectedValue
        // round-trip, when the previous active row is removed from
        // SelectedItems the binding can fire with a stale id, and when items
        // get reshuffled (UpsertItem inserts a row at index 0) the
        // SelectedItem briefly references the wrong container. e.AddedItems
        // unambiguously reflects what the user just clicked.
        if (e.AddedItems.Count > 0
            && e.AddedItems[^1] is ConversationListItem clicked
            && clicked.Id != vm.ConversationList.SelectedId)
        {
            // Paint the loading overlay BEFORE the (synchronous, UI-thread)
            // load work begins. The VM flips IsConversationLoading inside
            // LoadConversationAsync, but that binding update can't paint while
            // the UI thread is busy building a huge FlowDocument — so we set it
            // here and force one render pass so the overlay is actually visible
            // before the freeze. Fast loads finish within a frame and the
            // overlay's fade-in keeps the brief flash unobtrusive.
            if (clicked.Id != vm.Chat.ConversationId)
                ShowConversationLoadingOverlayNow(vm);

            vm.ConversationList.SelectById(clicked.Id);
        }
    }

    private void OnSelectionRestoreRequested(object? sender, IReadOnlyList<string> selectedIds)
    {
        var selected = selectedIds.ToHashSet(StringComparer.Ordinal);
        _restoringConversationGroupSelection = true;
        try
        {
            foreach (var listBox in ConversationGroupListBoxes())
            {
                listBox.SelectedItems.Clear();
                foreach (var item in listBox.Items.OfType<ConversationListItem>())
                {
                    if (selected.Contains(item.Id))
                        listBox.SelectedItems.Add(item);
                }
            }
        }
        finally
        {
            _restoringConversationGroupSelection = false;
        }
    }

    private void ShowConversationLoadingOverlayNow(MainViewModel vm)
    {
        vm.Chat.IsConversationLoading = true;
        // Push the overlay through layout + render synchronously so it is on
        // screen before the load freezes the UI thread.
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    /// <summary>All sidebar conversation group ListBoxes (BYOK / MolaGPT),
    /// skipping any not yet realized. Single source of truth so selection sync
    /// stays correct as groups are added.</summary>
    private IEnumerable<ListBox> ConversationGroupListBoxes()
    {
        if (ByokListBox is not null) yield return ByokListBox;
        if (MolaGptListBox is not null) yield return MolaGptListBox;
    }

    private void ClearConversationGroupSelection()
    {
        if (ConversationGroupListBoxes().All(lb => lb.SelectedItems.Count == 0))
            return;

        _clearingOtherConversationGroupSelection = true;
        try
        {
            foreach (var lb in ConversationGroupListBoxes())
                lb.SelectedItems.Clear();
        }
        finally
        {
            _clearingOtherConversationGroupSelection = false;
        }
    }

    /// <summary>
    /// SelectedValue is OneWayToSource, so VM-driven selection (draft → first
    /// send, notification click) must push into the ListBoxes here.
    /// </summary>
    private void ApplyConversationGroupSelection(string id, bool allowRetry = true)
    {
        if (string.IsNullOrEmpty(id)) return;

        var owner = ConversationGroupListBoxes()
            .FirstOrDefault(lb => ConversationListContainsId(lb, id));
        if (owner is null)
        {
            if (!allowRetry) return;
            Dispatcher.InvokeAsync(
                () => ApplyConversationGroupSelection(id, allowRetry: false),
                DispatcherPriority.Loaded);
            return;
        }

        if (IsConversationSelectedInListBoxes(id))
            return;

        _clearingOtherConversationGroupSelection = true;
        try
        {
            foreach (var lb in ConversationGroupListBoxes())
            {
                if (ReferenceEquals(lb, owner)) lb.SelectedValue = id;
                else lb.SelectedItems.Clear();
            }
        }
        finally
        {
            _clearingOtherConversationGroupSelection = false;
        }
    }

    private static bool ConversationListContainsId(ListBox? listBox, string id)
    {
        if (listBox?.ItemsSource is not System.Collections.IEnumerable items) return false;
        foreach (var item in items)
        {
            if (item is ConversationListItem row && row.Id == id)
                return true;
        }
        return false;
    }

    private bool IsConversationSelectedInListBoxes(string id)
    {
        foreach (var lb in ConversationGroupListBoxes())
            if (lb.SelectedValue is string sel && sel == id) return true;
        return false;
    }

    private void ClearOtherConversationGroupSelection(ListBox activeList)
    {
        _clearingOtherConversationGroupSelection = true;
        try
        {
            foreach (var lb in ConversationGroupListBoxes())
            {
                if (ReferenceEquals(lb, activeList)) continue;
                if (lb.SelectedItems.Count > 0) lb.SelectedItems.Clear();
            }
        }
        finally
        {
            _clearingOtherConversationGroupSelection = false;
        }
    }

    private void ConversationGroupList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender == ByokListBox)
            FocusConversationGroupLayout(byok: true);
        else if (sender == MolaGptListBox)
            FocusConversationGroupLayout(byok: false);
    }

    private void ConversationGroupList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) < 0.01)
            return;

        if (sender == ByokListBox)
            FocusConversationGroupLayout(byok: true);
        else if (sender == MolaGptListBox)
            FocusConversationGroupLayout(byok: false);
    }

    private void ConversationGroupsHost_MouseEnter(object sender, MouseEventArgs e)
    {
        _conversationGroupLayoutRestoreTimer.Stop();
    }

    private void ConversationGroupsHost_MouseLeave(object sender, MouseEventArgs e)
    {
        ScheduleConversationGroupLayoutRestore();
    }

    private void ConversationGroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if (!_conversationGroupLayoutFocused || sender is not FrameworkElement { Tag: string tag })
            return;

        Dispatcher.InvokeAsync(() =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.ConversationList.IsByokGroupExpanded = true;
                vm.ConversationList.IsMolaGptGroupExpanded = true;
            }

            FocusConversationGroupLayout(string.Equals(tag, "byok", StringComparison.Ordinal));
        }, DispatcherPriority.Input);
    }

    private void FocusConversationGroupLayout(bool byok)
    {
        if (ByokListRow is null || MolaGptListRow is null || ByokListBox is null || MolaGptListBox is null)
            return;

        _conversationGroupLayoutRestoreTimer.Stop();
        _conversationGroupLayoutFocused = true;
        if (byok)
        {
            ByokListBox.ClearValue(MaxHeightProperty);
            ByokListRow.Height = new GridLength(1, GridUnitType.Star);
            MolaGptListRow.Height = new GridLength(0);
        }
        else
        {
            ByokListBox.MaxHeight = ConversationGroupDefaultByokMaxHeight;
            ByokListRow.Height = new GridLength(0);
            MolaGptListRow.Height = new GridLength(1, GridUnitType.Star);
        }
        if (ConversationGroupsHost?.IsMouseOver != true)
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
        if (ByokListRow is null || MolaGptListRow is null || ByokListBox is null)
            return;

        if (DataContext is MainViewModel vm)
        {
            vm.ConversationList.IsByokGroupExpanded = true;
            vm.ConversationList.IsMolaGptGroupExpanded = true;
        }

        ByokListBox.MaxHeight = ConversationGroupDefaultByokMaxHeight;
        ByokListRow.Height = GridLength.Auto;
        MolaGptListRow.Height = new GridLength(1, GridUnitType.Star);
        _conversationGroupLayoutFocused = false;
    }

    /// <summary>
    /// Keep the expensive chat layout out of the animation loop. The sidebar
    /// and main pane move visually with render transforms; grid columns change
    /// only once at the edge of the transition.
    /// </summary>
    private void ApplySidebarState(bool collapsed, bool animate)
    {
        if (SidebarColumn is null || SidebarCard is null || GapColumn is null || MainCard is null) return;

        StopSidebarVisualAnimation();

        double from = SidebarColumn.Width.IsAbsolute ? SidebarColumn.Width.Value : SidebarExpandedWidth;
        double to = collapsed ? SidebarCollapsedWidth : SidebarExpandedWidth;
        double travel = SidebarExpandedWidth + SidebarGapWidth;

        if (!animate || from == to)
        {
            SidebarColumn.Width = new GridLength(to);
            GapColumn.Width = new GridLength(collapsed ? 0 : SidebarGapWidth);
            SidebarCard.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            SidebarCard.Opacity = collapsed ? 0 : 1;
            SetElementOffset(SidebarCard, 0);
            SetElementOffset(MainCard, 0);
            Panel.SetZIndex(SidebarCard, 0);
            Panel.SetZIndex(MainCard, 0);
            RestoreMessagesViewportAfterSidebarAnimation();
            QueueMessagesViewportUpdate();
            return;
        }

        FreezeMessagesViewportForSidebarAnimation();

        if (collapsed)
        {
            SidebarColumn.Width = new GridLength(SidebarExpandedWidth);
            GapColumn.Width = new GridLength(SidebarGapWidth);
            SidebarCard.Visibility = Visibility.Visible;
            SidebarCard.Opacity = SidebarCard.Opacity <= 0 ? 1 : SidebarCard.Opacity;
            SetElementOffset(SidebarCard, 0);
            SetElementOffset(MainCard, 0);
            Panel.SetZIndex(MainCard, 1);

            AnimateElementOffset(SidebarCard, 0, -travel);
            AnimateElementOffset(MainCard, 0, -travel, completed: () =>
            {
                SidebarColumn.Width = new GridLength(SidebarCollapsedWidth);
                GapColumn.Width = new GridLength(0);
                SidebarCard.Visibility = Visibility.Collapsed;
                SidebarCard.Opacity = 0;
                SetElementOffset(SidebarCard, 0);
                SetElementOffset(MainCard, 0);
                Panel.SetZIndex(MainCard, 0);
                RestoreMessagesViewportAfterSidebarAnimation();
            });
            AnimateElementOpacity(SidebarCard, SidebarCard.Opacity, 0);
        }
        else
        {
            SidebarColumn.Width = new GridLength(SidebarExpandedWidth);
            GapColumn.Width = new GridLength(SidebarGapWidth);
            SidebarCard.Visibility = Visibility.Visible;
            SidebarCard.Opacity = 0;
            SetElementOffset(SidebarCard, -travel);
            SetElementOffset(MainCard, -travel);
            Panel.SetZIndex(SidebarCard, 1);

            AnimateElementOffset(SidebarCard, -travel, 0);
            AnimateElementOffset(MainCard, -travel, 0, completed: () =>
            {
                SidebarColumn.Width = new GridLength(SidebarExpandedWidth);
                GapColumn.Width = new GridLength(SidebarGapWidth);
                SidebarCard.Visibility = Visibility.Visible;
                SidebarCard.Opacity = 1;
                SetElementOffset(SidebarCard, 0);
                SetElementOffset(MainCard, 0);
                Panel.SetZIndex(SidebarCard, 0);
                RestoreMessagesViewportAfterSidebarAnimation();
            });
            AnimateElementOpacity(SidebarCard, 0, 1);
        }
    }

    private const double ArtifactPanelWidth = 300;
    private const double ArtifactPanelGapWidth = 16;

    /// <summary>
    /// Shows/hides the session artifact panel by toggling its grid column width
    /// and fading the card. Simpler than the sidebar's slide because the panel
    /// sits at the trailing edge and doesn't reflow the virtualized message list
    /// horizontally in a way that needs viewport freezing.
    /// </summary>
    private void ApplyArtifactPanelState(bool visible, bool animate)
    {
        if (ArtifactColumn is null || ArtifactGapColumn is null || ArtifactCard is null) return;

        ArtifactColumn.Width = new GridLength(visible ? ArtifactPanelWidth : 0);
        ArtifactGapColumn.Width = new GridLength(visible ? ArtifactPanelGapWidth : 0);

        if (!visible)
        {
            ArtifactCard.BeginAnimation(OpacityProperty, null);
            ArtifactCard.Opacity = 0;
            ArtifactCard.Visibility = Visibility.Collapsed;
            return;
        }

        ArtifactCard.Visibility = Visibility.Visible;
        if (animate)
        {
            // Set the base value to the animation's target FIRST: AnimateElementOpacity
            // uses FillBehavior.Stop, so when the 0→1 animation ends it reverts to the
            // base value. If the base were still 0 (set by a prior hide) the panel would
            // flash in then vanish, leaving an empty column. Base=1 makes it stick.
            ArtifactCard.BeginAnimation(OpacityProperty, null);
            ArtifactCard.Opacity = 1;
            AnimateElementOpacity(ArtifactCard, 0, 1);
        }
        else
        {
            ArtifactCard.BeginAnimation(OpacityProperty, null);
            ArtifactCard.Opacity = 1;
        }
    }

    private void StopSidebarVisualAnimation()
    {
        if (SidebarCard is not null)
        {
            StopElementOffsetAnimation(SidebarCard);
            SidebarCard.BeginAnimation(OpacityProperty, null);
        }
        if (MainCard is not null)
            StopElementOffsetAnimation(MainCard);
        RestoreMessagesViewportAfterSidebarAnimation();
    }

    private void AnimateElementOffset(FrameworkElement element, double from, double to, Action? completed = null)
    {
        var transform = EnsureTranslateTransform(element);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(SidebarAnimationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        if (completed is not null)
        {
            animation.Completed += (_, _) =>
            {
                transform.BeginAnimation(TranslateTransform.XProperty, null);
                completed();
            };
        }
        transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateElementOpacity(UIElement element, double from, double to)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(SidebarAnimationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        element.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopElementOffsetAnimation(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform transform)
            transform.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private TranslateTransform EnsureTranslateTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform transform)
            return transform;

        transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private void SetElementOffset(FrameworkElement element, double x)
    {
        EnsureTranslateTransform(element).X = x;
    }

    private void FreezeMessagesViewportForSidebarAnimation()
    {
        if (MessagesScroll is null
            || MessagesScroll.Visibility != Visibility.Visible
            || MessagesScroll.ActualWidth <= 0)
        {
            return;
        }

        MessagesScroll.Width = MessagesScroll.ActualWidth;
    }

    private void RestoreMessagesViewportAfterSidebarAnimation()
    {
        if (MessagesScroll is null) return;
        MessagesScroll.ClearValue(FrameworkElement.WidthProperty);
        QueueMessagesViewportUpdate();
    }

    /// <summary>Open the chat model selector programmatically (e.g. from a failed
    /// turn's "换个模型" recovery button). Anchors the popup to the header pill.</summary>
    public void OpenChatModelSelector()
    {
        if (ModelSelectorPopup.IsOpen) return;
        ModelSelectorSearchBox.Text = string.Empty;
        RebuildModelSelector();
        ModelSelectorPopup.PlacementTarget = ChatModelSelectorButton;
        ModelSelectorPopup.IsOpen = true;
        // Move focus into the popup so its Esc handler receives keys (mirrors the
        // pill-click path). Without this, Esc would go to whatever opened us.
        Dispatcher.BeginInvoke(new Action(() => ModelSelectorSearchBox.Focus()), DispatcherPriority.Input);
    }

    /// <summary>Esc dismisses the model selector from anywhere inside it
    /// (search box, list). Without this the popup only closed on outside-click.</summary>
    private void ModelSelectorPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ModelSelectorPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OpenModelSelector_Click(object sender, RoutedEventArgs e)
    {
        var opening = !ModelSelectorPopup.IsOpen;
        if (opening)
            ModelSelectorSearchBox.Text = string.Empty;

        RebuildModelSelector();
        ModelSelectorPopup.PlacementTarget = (UIElement)sender;
        ModelSelectorPopup.IsOpen = opening;
        if (opening)
            Dispatcher.BeginInvoke(new Action(() => ModelSelectorSearchBox.Focus()), DispatcherPriority.Input);
    }

    private void ModelSelectorSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ModelSelectorPopup?.IsOpen == true)
            RebuildModelSelector();
    }

    private void WorkbenchModelSelectorSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ImageWorkbenchHost?.Content is ImageGenerationWorkbenchWindow workbench)
            workbench.RebuildHeaderModelSelector();
    }

    private void ComposerHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateMessagesBottomInset();
        QueueMessagesViewportUpdate();
    }

    private void MessagesScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateMessagesBottomInset();
        QueueMessagesViewportUpdate();
        // A taller viewport can outgrow the loaded tail and leave nothing to
        // scroll, which would strand the rest of the history.
        QueueEnsureMessagesFillViewport();
    }

    private void UpdateMessagesBottomInset()
    {
        if (MessagesScroll is null || ComposerHost is null)
            return;

        var keepBottomAnchored = IsMessagesNearBottom();
        var bottom = Math.Max(
            MessagesBottomInsetMin,
            ComposerHost.ActualHeight + ComposerHost.Margin.Bottom + MessagesBottomGap);

        if (Math.Abs(MessagesScroll.Margin.Bottom - bottom) < 0.5)
            return;

        MessagesScroll.Margin = new Thickness(
            MessagesScroll.Margin.Left,
            MessagesScroll.Margin.Top,
            MessagesScroll.Margin.Right,
            bottom);

        if (keepBottomAnchored)
            QueueMessagesScrollToEnd();
    }

    private void MessagesScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0)
            QueueMessagesViewportUpdate();

        // A vertical offset change with NO extent/viewport change is a real
        // scroll movement. If it wasn't us (programmatic) and wasn't our wheel
        // animation, it's the user dragging the scrollbar / keyboard — treat it
        // as a gesture and re-evaluate the stick state from where they landed.
        bool offsetMovedOnly = e.VerticalChange != 0
            && e.ExtentHeightChange == 0
            && e.ViewportHeightChange == 0;
        if (offsetMovedOnly && !_programmaticScroll && !_messagesScrollAnimating)
            _followStreamBottom = IsMessagesNearBottom();

        // Content grew (streaming reply lengthening, or viewport shrinking).
        // Follow ONLY if we're still attached — never re-derive "attached" from
        // current geometry here, because the streaming re-render transiently
        // clamps the offset toward the bottom and a geometry test would falsely
        // read "at bottom" and yank a scrolled-up user back down.
        var grew = e.ExtentHeightChange > 0 || e.ViewportHeightChange < 0;
        if (grew && _followStreamBottom && !_messagesScrollAnimating && !_suppressFollowBottom)
            QueueMessagesScrollToEnd();

        QueueMaybeLoadOlderMessages();
        UpdateScrollToBottomButton();
    }

    /// <summary>
    /// Defer the paging check out of the ScrollChanged callback: it runs a
    /// synchronous UpdateLayout, which is not safe to do from inside the
    /// layout pass that raised this event. Loaded priority puts it right after
    /// the current pass, so the new messages land in the same frame.
    /// </summary>
    private void QueueMaybeLoadOlderMessages()
    {
        if (_olderLoadQueued)
            return;

        _olderLoadQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _olderLoadQueued = false;
            MaybeLoadOlderMessages();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Pull in the next slice of history once the user scrolls near
    /// the top. A conversation opens with only its newest messages, so this is
    /// what makes the rest of it reachable.</summary>
    private void MaybeLoadOlderMessages()
    {
        if (_loadingOlderMessages || _suppressFollowBottom) return;
        if (MessagesScroll is null || DataContext is not MainViewModel vm) return;
        if (!vm.Chat.HasOlderMessages) return;
        // Nothing to scroll yet — that case belongs to EnsureMessagesFillViewport.
        if (MessagesScroll.ScrollableHeight <= 0) return;
        if (MessagesScroll.VerticalOffset > MessagesOlderLoadTriggerPx) return;

        LoadOlderMessagesPreservingScroll(vm, MessagesOlderLoadBatch);
    }

    private void QueueEnsureMessagesFillViewport() =>
        Dispatcher.InvokeAsync(EnsureMessagesFillViewport, DispatcherPriority.Loaded);

    /// <summary>
    /// A conversation whose newest messages don't fill the viewport has nothing
    /// to scroll, so <see cref="MaybeLoadOlderMessages"/> could never fire and
    /// the remaining history would be unreachable. Top up until there is
    /// something to scroll, or until we run out of history.
    /// </summary>
    private void EnsureMessagesFillViewport()
    {
        if (_loadingOlderMessages || _suppressFollowBottom || MessagesScroll is null) return;
        if (DataContext is not MainViewModel vm) return;

        // Bounded so a run of very short messages can't spin here.
        for (int pass = 0; pass < 8; pass++)
        {
            if (!vm.Chat.HasOlderMessages || MessagesScroll.ScrollableHeight > 0)
                return;
            if (LoadOlderMessagesPreservingScroll(vm, MessagesOlderLoadBatch) <= 0)
                return;
        }
    }

    /// <summary>
    /// Prepend older messages without moving what the user is looking at.
    /// <para>
    /// Two passes. First shift by however much the extent grew — close, but
    /// only an estimate while the panel virtualizes, because the messages that
    /// were just inserted above the viewport have never been measured. Then,
    /// with the anchor message back near the viewport and its container
    /// realized again, correct against its real position. Both passes run
    /// inside this frame via UpdateLayout, so no intermediate scroll position
    /// is ever painted.
    /// </para>
    /// </summary>
    /// <returns>How many messages were inserted.</returns>
    private int LoadOlderMessagesPreservingScroll(MainViewModel vm, int count)
    {
        if (MessagesScroll is null) return 0;

        var anchor = vm.Chat.Messages.Count > 0 ? vm.Chat.Messages[0] : null;
        var anchorBefore = GetMessageViewportOffset(anchor);
        var oldExtent = MessagesScroll.ExtentHeight;
        var oldOffset = MessagesScroll.VerticalOffset;

        _loadingOlderMessages = true;
        var previousSuppress = _suppressFollowBottom;
        _suppressFollowBottom = true;
        _programmaticScroll = true;
        try
        {
            var inserted = vm.Chat.LoadOlderMessages(count);
            if (inserted <= 0)
                return 0;

            MessagesScroll.UpdateLayout();
            var delta = MessagesScroll.ExtentHeight - oldExtent;
            if (delta > 0)
                MessagesScroll.ScrollToVerticalOffset(oldOffset + delta);

            if (anchorBefore is { } before)
            {
                MessagesScroll.UpdateLayout();
                if (GetMessageViewportOffset(anchor) is { } after)
                    MessagesScroll.ScrollToVerticalOffset(
                        MessagesScroll.VerticalOffset + (after - before));
            }

            return inserted;
        }
        finally
        {
            _programmaticScroll = false;
            _suppressFollowBottom = previousSuppress;
            _loadingOlderMessages = false;
        }
    }

    /// <summary>Y position of a message's container relative to the scroller,
    /// or null when that container isn't realized — in which case the caller
    /// keeps the extent-delta estimate instead.</summary>
    private double? GetMessageViewportOffset(object? message)
    {
        if (message is null || MessagesItems is null || MessagesScroll is null)
            return null;
        if (MessagesItems.ItemContainerGenerator.ContainerFromItem(message) is not FrameworkElement container)
            return null;

        try
        {
            return container.TransformToAncestor(MessagesScroll).Transform(default(Point)).Y;
        }
        catch (InvalidOperationException)
        {
            // Container was recycled out from under us between realize and
            // transform; the estimate stands.
            return null;
        }
    }

    private void UpdateScrollToBottomButton()
    {
        if (ScrollToBottomButton is null || MessagesScroll is null)
            return;

        // Only meaningful once there's something to scroll. Near the bottom the
        // button is redundant (stream-follow keeps us pinned), so hide it.
        var show = MessagesScroll.ScrollableHeight > MessagesBottomStickTolerance
                   && !IsMessagesNearBottom();
        if (show == _scrollToBottomVisible)
            return;

        _scrollToBottomVisible = show;
        ScrollToBottomButton.IsHitTestVisible = show;
        ScrollToBottomButton.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(show ? 1.0 : 0.0, TimeSpan.FromMilliseconds(show ? 160 : 120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void ScrollToBottomButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessagesScroll is null)
            return;

        // Explicit "go to latest" — re-attach stream-follow so a still-running
        // reply keeps us pinned afterwards.
        _followStreamBottom = true;
        AnimateMessagesScrollTo(MessagesScroll.ScrollableHeight);
    }

    private void QueueMessagesViewportUpdate()
    {
        Dispatcher.InvokeAsync(() =>
        {
            UpdateMessagesBottomInset();
            UpdateScrollToBottomButton();
        }, DispatcherPriority.Render);
    }

    private bool IsMessagesNearBottom()
    {
        if (MessagesScroll is null || MessagesScroll.ScrollableHeight <= 0)
            return true;

        return MessagesScroll.ScrollableHeight - MessagesScroll.VerticalOffset <= MessagesBottomStickTolerance;
    }

    private void QueueMessagesScrollToEnd()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (MessagesScroll is null)
                return;

            CancelMessagesScrollAnimation();

            _programmaticScroll = true;
            try
            {
                MessagesScroll.ScrollToVerticalOffset(MessagesScroll.ScrollableHeight);
            }
            finally
            {
                _programmaticScroll = false;
            }
        }, DispatcherPriority.ContextIdle);
    }

    private void RebuildModelSelector()
    {
        if (DataContext is not MainViewModel vm) return;
        var rows = new List<ModelSelectorRow>();
        var query = ModelSelectorSearchBox?.Text?.Trim() ?? string.Empty;
        var currentMode = vm.Chat.CurrentMode;
        var activeProviderId = vm.Chat.ActiveProvider?.Id;
        var activeModelId = vm.Chat.ActiveModel?.Id;

        var providers = _providers.Providers
            .OrderBy(prov => prov.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Top section = the current mode's whole "family": Chat alone, or both
        // local-agent wallets (Work + BYOK) when in an agent mode. The active
        // mode's group is listed first so the user's current pick stays on top.
        var topModes = ModesInChatFamily(currentMode);
        foreach (var mode in topModes)
            AddModelSelectorModeSection(rows, providers.Where(p => p.ToAppMode() == mode), mode, query, activeProviderId, activeModelId);

        // Other section = the modes across the Chat boundary; picking one of these
        // starts a new conversation.
        var otherModes = new[] { AppMode.Chat, AppMode.Work, AppMode.Byok }
            .Where(mode => !topModes.Contains(mode));
        var otherRows = new List<ModelSelectorRow>();
        foreach (var mode in otherModes)
            AddModelSelectorModeSection(otherRows, providers.Where(p => p.ToAppMode() == mode), mode, query, activeProviderId, activeModelId);

        if (otherRows.Any(row => row.Model is not null))
        {
            rows.Add(ModelSelectorRow.ForHeader("其他模式可用模型"));
            rows.Add(ModelSelectorRow.ForHint("选择这些模型会切换到对应模式，并新建一个对话。"));
            rows.AddRange(otherRows);
        }

        if (rows.Count == 0)
        {
            rows.Add(ModelSelectorRow.ForEmpty(
                string.IsNullOrWhiteSpace(query)
                    ? "当前对话没有可切换的同类型模型"
                    : "没有匹配的模型"));
        }

        ModelSelectorItems.ItemsSource = rows;
    }

    /// <summary>Modes on the same side of the Chat ↔ local-agent boundary as
    /// <paramref name="currentMode"/>, ordered with the active mode first. Chat is
    /// alone; Work and BYOK travel together (either can continue an agent chat).</summary>
    private static IReadOnlyList<AppMode> ModesInChatFamily(AppMode currentMode) =>
        currentMode == AppMode.Chat
            ? new[] { AppMode.Chat }
            : currentMode == AppMode.Work
                ? new[] { AppMode.Work, AppMode.Byok }
                : new[] { AppMode.Byok, AppMode.Work };

    private static void AddModelSelectorModeSection(
        ICollection<ModelSelectorRow> rows,
        IEnumerable<IChatProvider> providers,
        AppMode mode,
        string query,
        string? activeProviderId,
        string? activeModelId)
    {
        var addedHeader = false;
        foreach (var prov in providers)
        {
            var providerMatches = MatchesModelSearch(query, prov.DisplayName, prov.Id);
            var models = prov.Models
                .Where(model => providerMatches || MatchesModelSearch(query, model.DisplayName, model.Id))
                .ToList();
            if (models.Count == 0)
                continue;

            if (mode == AppMode.Byok)
            {
                // Mirrors the phone's picker: BYOK gets one group per provider
                // ("自定义 API · <名称>") so models from different providers don't
                // blur into one flat list. Chat/Work are single-provider modes
                // and keep the shared mode header.
                rows.Add(ModelSelectorRow.ForHeader($"{ModeLabel(mode)} · {prov.DisplayName}"));
            }
            else if (!addedHeader)
            {
                rows.Add(ModelSelectorRow.ForHeader($"{ModeLabel(mode)} 模型"));
                addedHeader = true;
            }

            foreach (var model in models)
            {
                var isActive = string.Equals(prov.Id, activeProviderId, StringComparison.Ordinal)
                               && string.Equals(model.Id, activeModelId, StringComparison.Ordinal);
                rows.Add(ModelSelectorRow.ForModel(prov, model, isActive));
            }
        }
    }

    private static string ModeLabel(AppMode mode) => mode switch
    {
        AppMode.Chat => "MolaGPT Chat",
        AppMode.Work => "MolaGPT 账号",
        _ => "自定义 API"
    };

    private void ModelSelectorRow_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm
            || (sender as FrameworkElement)?.DataContext is not ModelSelectorRow row
            || row.Provider is null
            || row.Model is null)
        {
            return;
        }

        // Only crossing the Chat ↔ local-agent boundary needs a new conversation;
        // Work ↔ BYOK share the local agent thread and continue the current one.
        if (vm.Chat.CurrentMode.CrossesChatBoundary(row.Provider.ToAppMode()))
        {
            vm.ConversationList.ClearSelection();
            vm.IsImageWorkbenchVisible = false;
            vm.Chat.StartDraftConversation();
        }

        vm.Chat.SetActiveByIds(row.Provider.Id, row.Model.Id, ignoreConversationBoundary: true);
        ModelSelectorPopup.IsOpen = false;
    }

    private void WorkbenchModelSelectorRow_Click(object sender, RoutedEventArgs e)
    {
        if (ImageWorkbenchHost?.Content is not ImageGenerationWorkbenchWindow workbench)
            return;

        workbench.SelectHeaderModelFromRow((sender as FrameworkElement)?.DataContext);
    }

    private static bool MatchesModelSearch(string query, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    private UIElement BuildModelSelectorContent(ProviderModel model)
    {
        var row = new DockPanel { LastChildFill = true };
        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        DockPanel.SetDock(badges, Dock.Right);

        if (model.SupportsThinking) badges.Children.Add(BuildModelBadge("推理"));
        if (model.SupportsToolCalling) badges.Children.Add(BuildModelBadge("工具"));
        if (model.SupportsVision) badges.Children.Add(BuildModelBadge("视觉"));
        if (badges.Children.Count > 0) row.Children.Add(badges);

        row.Children.Add(new TextBlock
        {
            Text = model.DisplayName,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        return row;
    }

    private Border BuildModelBadge(string text) => new()
    {
        Margin = new Thickness(6, 0, 0, 0),
        Padding = new Thickness(5, 1, 5, 1),
        CornerRadius = (CornerRadius)FindResource("Radius.Sm"),
        Background = (Brush)FindResource("Brush.Bg.Tertiary"),
        BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
        BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Brush.Text.Muted"),
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    /// <summary>How far one wheel notch travels. Follows the user's Windows
    /// "scroll this many lines at a time" setting instead of a constant of our
    /// own, so the chat scrolls the same distance as every other app on the
    /// machine. A negative value is Windows' "one screen at a time".</summary>
    private double MessagesWheelNotchDistance =>
        SystemParameters.WheelScrollLines > 0
            ? SystemParameters.WheelScrollLines * MessagesWheelLinePixels
            : MessagesScroll.ViewportHeight;

    private void MessagesScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsTextInputSurface(source)) return;
        if (MessagesScroll.ScrollableHeight <= 0) return;

        // Stack onto the pending target, not onto the current position: a fast
        // spin must cover the full distance its notches asked for, otherwise
        // the notches that land mid-flight get partially swallowed.
        var origin = _messagesScrollAnimating ? _messagesScrollTargetOffset : MessagesScroll.VerticalOffset;
        var next = Math.Clamp(
            origin - (e.Delta / Mouse.MouseWheelDeltaForOneLine * MessagesWheelNotchDistance),
            0,
            MessagesScroll.ScrollableHeight);

        // The wheel is an explicit user gesture: scrolling up detaches
        // stream-follow; landing at (or within tolerance of) the bottom
        // re-attaches it. This is what lets the user read back during a stream
        // without being dragged down, then resume following by scrolling down.
        _followStreamBottom =
            MessagesScroll.ScrollableHeight - next <= MessagesBottomStickTolerance;

        AnimateMessagesScrollTo(next);
        e.Handled = true;
    }

    private void CancelMessagesScrollAnimation()
    {
        if (!_messagesScrollAnimating) return;
        CompositionTarget.Rendering -= AnimateMessagesScrollFrame;
        _messagesScrollAnimating = false;
        _messagesScrollVelocity = 0;
    }

    /// <summary>Point the spring at a new offset. Retargeting mid-flight is the
    /// normal case and deliberately touches nothing else — not the clock, not
    /// the position, and above all not the velocity — so consecutive notches
    /// blend into one continuous motion that keeps whatever speed it had built
    /// up, instead of each notch restarting from scratch.</summary>
    private void AnimateMessagesScrollTo(double targetOffset)
    {
        _messagesScrollTargetOffset = targetOffset;

        if (_messagesScrollAnimating) return;

        _messagesScrollCurrent = MessagesScroll.VerticalOffset;
        _messagesScrollVelocity = 0;
        _messagesScrollLastFrame = DateTime.UtcNow;
        _messagesScrollAnimating = true;
        CompositionTarget.Rendering += AnimateMessagesScrollFrame;
    }

    /// <summary>Closed-form step of a critically damped spring. Solving it
    /// analytically rather than integrating numerically keeps it stable at any
    /// dt — a stiff spring stepped explicitly across a long frame would
    /// oscillate or blow up, which on screen is a visible stutter.</summary>
    private void AnimateMessagesScrollFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Clamp(
            (now - _messagesScrollLastFrame).TotalSeconds,
            0,
            MessagesScrollMaxFrameSeconds);
        _messagesScrollLastFrame = now;

        // Re-clamp every frame: a streaming reply changes ScrollableHeight
        // underneath us, and a target left dangling past the end would keep
        // the spring hooked forever.
        var scrollable = MessagesScroll.ScrollableHeight;
        var target = Math.Clamp(_messagesScrollTargetOffset, 0, scrollable);
        var clamped = Math.Clamp(_messagesScrollCurrent, 0, scrollable);
        if (clamped != _messagesScrollCurrent)
        {
            // Ran into an end — drop the velocity rather than let it press on.
            _messagesScrollCurrent = clamped;
            _messagesScrollVelocity = 0;
        }

        var offset = _messagesScrollCurrent - target;
        if (Math.Abs(offset) <= MessagesScrollSettleEpsilon
            && Math.Abs(_messagesScrollVelocity) <= MessagesScrollSettleVelocity)
        {
            _messagesScrollCurrent = target;
            _messagesScrollVelocity = 0;
            MessagesScroll.ScrollToVerticalOffset(target);
            CancelMessagesScrollAnimation();
            return;
        }

        // x(t) = target + (offset + a·t)·e^(-ωt),  a = v + ω·offset
        // v(t) = (v - ω·a·t)·e^(-ωt)
        var w = MessagesScrollSpringFrequency;
        var decay = Math.Exp(-w * dt);
        var a = _messagesScrollVelocity + (w * offset);

        _messagesScrollCurrent = target + ((offset + (a * dt)) * decay);
        _messagesScrollVelocity = (_messagesScrollVelocity - (w * a * dt)) * decay;

        MessagesScroll.ScrollToVerticalOffset(_messagesScrollCurrent);
    }

    private static bool IsTextInputSurface(DependencyObject source) =>
        FindVisualAncestor<TextBoxBase>(source) is not null
        || FindVisualAncestor<PasswordBox>(source) is not null
        || FindVisualAncestor<ComboBox>(source) is not null
        || FindVisualAncestor<ScrollBar>(source) is not null;

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source is Visual or Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }
}

public sealed class ModelSelectorRow
{
    private ModelSelectorRow(
        string? headerText,
        string? hintText,
        string? emptyText,
        IChatProvider? provider,
        ProviderModel? model,
        bool isActive = false)
    {
        HeaderText = headerText;
        HintText = hintText;
        EmptyText = emptyText;
        Provider = provider;
        Model = model;
        IsActive = isActive;
    }

    public string? HeaderText { get; }
    public string? HintText { get; }
    public string? EmptyText { get; }
    public IChatProvider? Provider { get; }
    public ProviderModel? Model { get; }
    /// <summary>True for the row matching the conversation's current provider+model.</summary>
    public bool IsActive { get; }
    public string? ModelName => Model?.DisplayName;
    public Visibility HeaderVisibility => HeaderText is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HintVisibility => HintText is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyVisibility => EmptyText is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ModelVisibility => Model is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ActiveGlyphVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThinkingVisibility => Model?.SupportsThinking == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ToolsVisibility => Model?.SupportsToolCalling == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VisionVisibility => Model?.SupportsVision == true ? Visibility.Visible : Visibility.Collapsed;

    public static ModelSelectorRow ForHeader(string text) => new(text, null, null, null, null);
    public static ModelSelectorRow ForHint(string text) => new(null, text, null, null, null);
    public static ModelSelectorRow ForEmpty(string text) => new(null, null, text, null, null);
    public static ModelSelectorRow ForModel(IChatProvider provider, ProviderModel model, bool isActive = false) =>
        new(null, null, null, provider, model, isActive);
}
