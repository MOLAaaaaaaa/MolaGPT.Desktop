using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
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
        // Keep bulk selection within one source group. BYOK and MolaGPT
        // conversations have different account/sync semantics, so crossing
        // groups while Ctrl/Shift-selecting clears the previous group first.
        if (e.AddedItems.Count > 0)
            ClearOtherConversationGroupSelection(listBox);

        var ids = new List<string>();
        if (ByokListBox is not null)
            ids.AddRange(ByokListBox.SelectedItems.OfType<ConversationListItem>().Select(i => i.Id));
        if (MolaGptListBox is not null)
            ids.AddRange(MolaGptListBox.SelectedItems.OfType<ConversationListItem>().Select(i => i.Id));
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
            vm.ConversationList.SelectById(clicked.Id);
        }
    }

    private void ClearConversationGroupSelection()
    {
        if ((ByokListBox?.SelectedItems.Count ?? 0) == 0
            && (MolaGptListBox?.SelectedItems.Count ?? 0) == 0)
            return;

        _clearingOtherConversationGroupSelection = true;
        try
        {
            ByokListBox?.SelectedItems.Clear();
            MolaGptListBox?.SelectedItems.Clear();
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

        var inByok = ConversationListContainsId(ByokListBox, id);
        var inMola = ConversationListContainsId(MolaGptListBox, id);
        if (!inByok && !inMola)
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
            if (inByok)
            {
                MolaGptListBox?.SelectedItems.Clear();
                if (ByokListBox is not null)
                    ByokListBox.SelectedValue = id;
            }
            else
            {
                ByokListBox?.SelectedItems.Clear();
                if (MolaGptListBox is not null)
                    MolaGptListBox.SelectedValue = id;
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
        if (ByokListBox?.SelectedValue is string byokId && byokId == id) return true;
        if (MolaGptListBox?.SelectedValue is string molaId && molaId == id) return true;
        return false;
    }

    private void ClearOtherConversationGroupSelection(ListBox activeList)
    {
        var other = ReferenceEquals(activeList, ByokListBox) ? MolaGptListBox : ByokListBox;
        if (other is null || other.SelectedItems.Count == 0) return;

        _clearingOtherConversationGroupSelection = true;
        try
        {
            other.SelectedItems.Clear();
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
    }

    private void QueueMessagesViewportUpdate()
    {
        Dispatcher.InvokeAsync(() =>
        {
            UpdateMessagesBottomInset();
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

            MessagesScroll.ScrollToVerticalOffset(MessagesScroll.ScrollableHeight);
        }, DispatcherPriority.ContextIdle);
    }

    private void RebuildModelSelector()
    {
        if (DataContext is not MainViewModel vm) return;
        var rows = new List<ModelSelectorRow>();
        var query = ModelSelectorSearchBox?.Text?.Trim() ?? string.Empty;

        var providers = _providers.Providers
            .Where(vm.Chat.CanSwitchToProvider)
            .OrderBy(prov => prov.Kind == ProviderKind.MolaGptProxy ? 1 : 0)
            .ToList();

        foreach (var prov in providers)
        {
            var providerMatches = MatchesModelSearch(query, prov.DisplayName, prov.Id);
            var models = prov.Models
                .Where(model => providerMatches || MatchesModelSearch(query, model.DisplayName, model.Id))
                .ToList();
            if (models.Count == 0)
                continue;

            rows.Add(ModelSelectorRow.ForHeader(prov.DisplayName));

            foreach (var model in models)
                rows.Add(ModelSelectorRow.ForModel(prov, model));
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

    private void ModelSelectorRow_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm
            || (sender as FrameworkElement)?.DataContext is not ModelSelectorRow row
            || row.Provider is null
            || row.Model is null)
        {
            return;
        }

        vm.Chat.SetActiveByIds(row.Provider.Id, row.Model.Id);
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
        string? emptyText,
        IChatProvider? provider,
        ProviderModel? model)
    {
        HeaderText = headerText;
        EmptyText = emptyText;
        Provider = provider;
        Model = model;
    }

    public string? HeaderText { get; }
    public string? EmptyText { get; }
    public IChatProvider? Provider { get; }
    public ProviderModel? Model { get; }
    public string? ModelName => Model?.DisplayName;
    public Visibility HeaderVisibility => HeaderText is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyVisibility => EmptyText is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ModelVisibility => Model is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ThinkingVisibility => Model?.SupportsThinking == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ToolsVisibility => Model?.SupportsToolCalling == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VisionVisibility => Model?.SupportsVision == true ? Visibility.Visible : Visibility.Collapsed;

    public static ModelSelectorRow ForHeader(string text) => new(text, null, null, null);
    public static ModelSelectorRow ForEmpty(string text) => new(null, text, null, null);
    public static ModelSelectorRow ForModel(IChatProvider provider, ProviderModel model) => new(null, null, provider, model);
}
