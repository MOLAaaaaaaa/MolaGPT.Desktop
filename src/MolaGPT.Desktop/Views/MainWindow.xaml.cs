using System.ComponentModel;
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
    private const double MessagesWheelDistanceScale = 0.92;
    private const double MessagesWheelAnimationMs = 170;
    private const double MessagesBottomInsetMin = 132;
    private const double MessagesBottomGap = 0;
    private const double MessagesBottomStickTolerance = 48;
    private const double ConversationGroupDefaultByokMaxHeight = 240;
    private const double ConversationGroupRestoreDelayMs = 1200;

    private double _messagesScrollStartOffset;
    private double _messagesScrollTargetOffset;
    private DateTime _messagesScrollAnimationStart;
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
    private bool _conversationGroupLayoutFocused;
    private bool _clearingOtherConversationGroupSelection;
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

    /// <summary>Keep the maximize/restore glyph in sync and inset the content when
    /// maximized — a WindowChrome window's client area otherwise overhangs the work
    /// area by the resize border, clipping the title bar and card edges.</summary>
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        if (RootBorder is not null)
            RootBorder.Margin = maximized ? new Thickness(8) : new Thickness(0);
        if (MaximizeRestoreGlyph is not null)
            MaximizeRestoreGlyph.Text = maximized ? "" : "";
        if (MaximizeRestoreButton is not null)
            MaximizeRestoreButton.ToolTip = maximized ? "向下还原" : "最大化";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableModernWindowFrame();
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
            oldMainVm.ConversationList.PropertyChanged -= OnConversationListPropertyChanged;
        }
        else if (e.OldValue is INotifyPropertyChanged oldVm)
        {
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }

        if (e.NewValue is MainViewModel newVm)
        {
            newVm.PropertyChanged += OnVmPropertyChanged;
            newVm.ConversationList.PropertyChanged += OnConversationListPropertyChanged;
            // Apply initial state without animation so first paint is right.
            ApplySidebarState(newVm.SidebarCollapsed, animate: false);
        }
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

    private void ConversationListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_clearingOtherConversationGroupSelection) return;
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
        if (grew && _followStreamBottom && !_messagesScrollAnimating)
            QueueMessagesScrollToEnd();

        UpdateScrollToBottomButton();
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

            if (_messagesScrollAnimating)
            {
                CompositionTarget.Rendering -= AnimateMessagesScrollFrame;
                _messagesScrollAnimating = false;
            }

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

        var providers = _providers.Providers
            .OrderBy(prov => prov.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Top section = the current mode's whole "family": Chat alone, or both
        // local-agent wallets (Work + BYOK) when in an agent mode. The active
        // mode's group is listed first so the user's current pick stays on top.
        var topModes = ModesInChatFamily(currentMode);
        foreach (var mode in topModes)
            AddModelSelectorModeSection(rows, providers.Where(p => p.ToAppMode() == mode), mode, query);

        // Other section = the modes across the Chat boundary; picking one of these
        // starts a new conversation.
        var otherModes = new[] { AppMode.Chat, AppMode.Work, AppMode.Byok }
            .Where(mode => !topModes.Contains(mode));
        var otherRows = new List<ModelSelectorRow>();
        foreach (var mode in otherModes)
            AddModelSelectorModeSection(otherRows, providers.Where(p => p.ToAppMode() == mode), mode, query);

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
        string query)
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

            if (!addedHeader)
            {
                rows.Add(ModelSelectorRow.ForHeader($"{ModeLabel(mode)} 模型"));
                addedHeader = true;
            }

            foreach (var model in models)
                rows.Add(ModelSelectorRow.ForModel(prov, model));
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

    private void MessagesScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        HandleMessagesMouseWheel(e);
    }

    private void MessagesScroll_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        HandleMessagesMouseWheel(e);
    }

    private void HandleMessagesMouseWheel(MouseWheelEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsTextInputSurface(source)) return;
        if (MessagesScroll.ScrollableHeight <= 0) return;

        var origin = _messagesScrollAnimating ? _messagesScrollTargetOffset : MessagesScroll.VerticalOffset;
        var next = Math.Clamp(
            origin - (e.Delta * MessagesWheelDistanceScale),
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

    private void AnimateMessagesScrollTo(double targetOffset)
    {
        _messagesScrollStartOffset = MessagesScroll.VerticalOffset;
        _messagesScrollTargetOffset = targetOffset;
        _messagesScrollAnimationStart = DateTime.UtcNow;

        if (_messagesScrollAnimating) return;
        _messagesScrollAnimating = true;
        CompositionTarget.Rendering += AnimateMessagesScrollFrame;
    }

    private void AnimateMessagesScrollFrame(object? sender, EventArgs e)
    {
        var elapsed = (DateTime.UtcNow - _messagesScrollAnimationStart).TotalMilliseconds;
        var t = Math.Clamp(elapsed / MessagesWheelAnimationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        var offset = _messagesScrollStartOffset + ((_messagesScrollTargetOffset - _messagesScrollStartOffset) * eased);

        MessagesScroll.ScrollToVerticalOffset(Math.Clamp(offset, 0, MessagesScroll.ScrollableHeight));

        if (t < 1 && Math.Abs(MessagesScroll.VerticalOffset - _messagesScrollTargetOffset) > 0.25) return;

        MessagesScroll.ScrollToVerticalOffset(Math.Clamp(_messagesScrollTargetOffset, 0, MessagesScroll.ScrollableHeight));
        CompositionTarget.Rendering -= AnimateMessagesScrollFrame;
        _messagesScrollAnimating = false;
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
        ProviderModel? model)
    {
        HeaderText = headerText;
        HintText = hintText;
        EmptyText = emptyText;
        Provider = provider;
        Model = model;
    }

    public string? HeaderText { get; }
    public string? HintText { get; }
    public string? EmptyText { get; }
    public IChatProvider? Provider { get; }
    public ProviderModel? Model { get; }
    public string? ModelName => Model?.DisplayName;
    public Visibility HeaderVisibility => HeaderText is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HintVisibility => HintText is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyVisibility => EmptyText is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ModelVisibility => Model is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ThinkingVisibility => Model?.SupportsThinking == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ToolsVisibility => Model?.SupportsToolCalling == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VisionVisibility => Model?.SupportsVision == true ? Visibility.Visible : Visibility.Collapsed;

    public static ModelSelectorRow ForHeader(string text) => new(text, null, null, null, null);
    public static ModelSelectorRow ForHint(string text) => new(null, text, null, null, null);
    public static ModelSelectorRow ForEmpty(string text) => new(null, null, text, null, null);
    public static ModelSelectorRow ForModel(IChatProvider provider, ProviderModel model) => new(null, null, null, provider, model);
}
