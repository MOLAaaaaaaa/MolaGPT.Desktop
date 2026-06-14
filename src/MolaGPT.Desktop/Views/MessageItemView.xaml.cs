using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using MolaGPT.Storage;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Views;

public partial class MessageItemView : UserControl
{
    private const double UserBubbleMaxWidth = 640;
    private const double UserAvatarWidth = 32;
    private const double UserAvatarLeftMargin = 12;
    private const double UserBubbleMinWidth = 96;
    private const double ToolCardWheelDistanceScale = 0.92;
    private const double ToolCardWheelAnimationMs = 170;
    private Popup? _statsPopup;

    public MessageItemView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => UpdateUserBubbleMaxWidth();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MessageViewModel oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        if (e.NewValue is MessageViewModel newVm)
        {
            newVm.PropertyChanged += OnVmPropertyChanged;
            ApplyRole(newVm.Role);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MessageViewModel.Role) && sender is MessageViewModel vm)
            ApplyRole(vm.Role);
    }

    private void ApplyRole(string role)
    {
        if (role == "user")
        {
            UserStack.Visibility = Visibility.Visible;
            AssistantGrid.Visibility = Visibility.Collapsed;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            UpdateUserBubbleMaxWidth();
        }
        else
        {
            UserStack.Visibility = Visibility.Collapsed;
            AssistantGrid.Visibility = Visibility.Visible;
            HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }

    private void UpdateUserBubbleMaxWidth()
    {
        var available = ActualWidth;
        if (double.IsNaN(available) || double.IsInfinity(available) || available <= 0)
            return;

        var max = available - UserAvatarWidth - UserAvatarLeftMargin;
        UserBubbleBorder.MaxWidth = Math.Max(UserBubbleMinWidth, Math.Min(UserBubbleMaxWidth, max));
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MessageViewModel vm) return;
        CopyText(GetCleanMarkdownForCopy(vm.Content));
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MessageViewModel vm })
            CopyText(GetCleanMarkdownForCopy(vm.Content));
        e.Handled = true;
    }

    private void CopyTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MessageDisplayBlockViewModel { Tool: { } tool } })
            CopyText(BuildToolCopy(tool));
        e.Handled = true;
    }

    // Clicking anywhere on the tool-card header row toggles the body open/closed,
    // mirroring the chevron. The chevron ToggleButton (x:Name="ToolBodyToggle")
    // lives in the same header Grid as the click strip that raised this event.
    private void ToolHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element
            && FindHeaderToggle(element) is { } toggle)
        {
            toggle.IsChecked = !(toggle.IsChecked ?? false);
            e.Handled = true;
        }
    }

    private static ToggleButton? FindHeaderToggle(DependencyObject start)
    {
        // Walk up to the enclosing header Grid, then find the body toggle among
        // its direct children. Marked by Tag="bodyToggle" so one handler serves
        // both the single tool card and the grouped card (their toggles need
        // distinct x:Names within a single DataTemplate namescope).
        for (DependencyObject? node = start; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not Grid grid) continue;
            var count = VisualTreeHelper.GetChildrenCount(grid);
            for (var i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(grid, i) is ToggleButton { Tag: "bodyToggle" } toggle)
                    return toggle;
            }
        }
        return null;
    }

    private void CopyTextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: string text })
            CopyText(text);
        e.Handled = true;
    }

    private void CopyPythonCode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string code })
            CopyText(code);
        e.Handled = true;
    }

    private void ToolCardPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (sender is ScrollViewer viewer && TrySmoothScrollToolCardViewer(viewer, e.Delta))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = this
        };

        if (VisualTreeHelper.GetParent(this) is UIElement parent)
            parent.RaiseEvent(forwarded);
        else
            RaiseEvent(forwarded);
    }

    private static bool TrySmoothScrollToolCardViewer(ScrollViewer viewer, int delta)
    {
        if (viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled)
            return false;
        if (viewer.ScrollableHeight <= 0)
            return false;

        var state = GetToolCardScrollState(viewer);
        var origin = state.Animating ? state.TargetOffset : viewer.VerticalOffset;
        var target = Math.Clamp(
            origin - (delta * ToolCardWheelDistanceScale),
            0,
            viewer.ScrollableHeight);

        if (Math.Abs(target - origin) < 0.25)
            return false;

        state.StartOffset = viewer.VerticalOffset;
        state.TargetOffset = target;
        state.AnimationStart = DateTime.UtcNow;

        if (!state.Animating)
        {
            state.Animating = true;
            state.Owner = viewer;
            state.FrameHandler ??= (_, _) => AnimateToolCardScrollFrame(state);
            CompositionTarget.Rendering += state.FrameHandler;
        }

        return true;
    }

    private static SmoothScrollViewerState GetToolCardScrollState(ScrollViewer viewer)
    {
        if (viewer.Tag is SmoothScrollViewerState state) return state;
        state = new SmoothScrollViewerState();
        viewer.Tag = state;
        return state;
    }

    private static void AnimateToolCardScrollFrame(SmoothScrollViewerState state)
    {
        var viewer = state.Owner;
        if (viewer is null)
            return;

        var elapsed = (DateTime.UtcNow - state.AnimationStart).TotalMilliseconds;
        var t = Math.Clamp(elapsed / ToolCardWheelAnimationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        var offset = state.StartOffset + ((state.TargetOffset - state.StartOffset) * eased);

        viewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, viewer.ScrollableHeight));

        if (t < 1 && Math.Abs(viewer.VerticalOffset - state.TargetOffset) > 0.25) return;

        viewer.ScrollToVerticalOffset(Math.Clamp(state.TargetOffset, 0, viewer.ScrollableHeight));
        if (state.FrameHandler is not null)
            CompositionTarget.Rendering -= state.FrameHandler;
        state.Animating = false;
    }

    private void StatsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not MessageViewModel vm) return;

        _statsPopup?.SetCurrentValue(Popup.IsOpenProperty, false);

        var panel = new StackPanel
        {
            MinWidth = 220,
            MaxWidth = 280,
        };

        var header = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        var copyStats = new Button
        {
            Style = TryFindResource("InlineActionButton") as Style,
            Width = 26,
            Height = 24,
            ToolTip = "复制统计",
            Content = new TextBlock
            {
                Text = "\uE8C8",
                FontFamily = TryFindResource("Font.Icon") as FontFamily
                             ?? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12
            }
        };
        copyStats.Click += (_, args) =>
        {
            CopyText(vm.ResponseStatsText);
            args.Handled = true;
        };
        DockPanel.SetDock(copyStats, Dock.Right);
        header.Children.Add(copyStats);
        header.Children.Add(new TextBlock
        {
            Text = "响应统计",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = FindBrush("Brush.Text.Secondary", Brushes.DimGray),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(header);

        var rows = (vm.ResponseStatsText ?? "暂无响应统计")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rows.Length == 0) rows = new[] { "暂无响应统计" };

        foreach (var row in rows)
            panel.Children.Add(BuildStatsRow(row));

        var card = new Border
        {
            Background = FindBrush("Brush.Bg.Primary", Brushes.White),
            BorderBrush = FindBrush("Brush.Border", Brushes.LightGray),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            CornerRadius = FindCornerRadius("Radius.Lg", new CornerRadius(12)),
            Child = panel,
            Effect = TryFindResource("Shadow.Popup") as System.Windows.Media.Effects.Effect
        };

        _statsPopup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Top,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
            Child = card
        };
        _statsPopup.SetCurrentValue(Popup.IsOpenProperty, true);
        e.Handled = true;
    }

    private Grid BuildStatsRow(string row)
    {
        var parts = row.Split('：', 2);
        var grid = new Grid
        {
            Margin = new Thickness(0, 3, 0, 3)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock
        {
            Text = parts[0],
            FontSize = 13,
            Foreground = FindBrush("Brush.Text.Secondary", Brushes.DimGray),
            VerticalAlignment = VerticalAlignment.Center
        });

        var value = new TextBlock
        {
            Text = parts.Length > 1 ? parts[1] : string.Empty,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = FindBrush("Brush.Text.Primary", Brushes.Black),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        return grid;
    }

    private Brush FindBrush(string key, Brush fallback)
    {
        try { return TryFindResource(key) as Brush ?? fallback; }
        catch { return fallback; }
    }

    private CornerRadius FindCornerRadius(string key, CornerRadius fallback)
    {
        try { return TryFindResource(key) is CornerRadius radius ? radius : fallback; }
        catch { return fallback; }
    }

    private static void CopyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Clipboard.SetText(text.Trim());
    }

    private static string BuildToolCopy(ToolCallViewModel tool)
    {
        var builder = new StringBuilder();
        builder.Append("工具：").AppendLine(tool.Label);
        if (!string.Equals(tool.Name, tool.Label, StringComparison.OrdinalIgnoreCase))
            builder.Append("名称：").AppendLine(tool.Name);
        builder.Append("状态：").AppendLine(tool.StatusText);
        if (!string.IsNullOrWhiteSpace(tool.Detail))
            builder.Append("详情：").AppendLine(tool.Detail);
        if (!string.IsNullOrWhiteSpace(tool.Summary))
            builder.Append("摘要：").AppendLine(tool.Summary);
        if (!string.IsNullOrWhiteSpace(tool.Provider))
            builder.Append("服务：").AppendLine(tool.Provider);
        if (!string.IsNullOrWhiteSpace(tool.DisplayArgumentsJson))
        {
            builder.AppendLine();
            builder.AppendLine("输入参数：");
            builder.AppendLine(tool.DisplayArgumentsJson);
        }
        if (!string.IsNullOrWhiteSpace(tool.DisplayResultPreviewJson))
        {
            builder.AppendLine();
            builder.AppendLine("结果预览：");
            builder.AppendLine(tool.DisplayResultPreviewJson);
        }

        return builder.ToString().Trim();
    }

    private static string GetCleanMarkdownForCopy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var text = MessageViewModel.StripSystemHints(raw);
        text = Regex.Replace(text, @"<ref\s+source=""[^""]*""(?:\s*/>|>[\s\S]*?</ref>)", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<blockquote\s+class=""[^""]*\btool-status\b[^""]*""[^>]*>[\s\S]*?</blockquote>", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<DSanalysis\b[^>]*>[\s\S]*?</DSanalysis>", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<steel-step\b[^>]*>[\s\S]*?</steel-step>", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<think>[\s\S]*?</think>", string.Empty, RegexOptions.IgnoreCase);
        return text.Trim();
    }

    /// <summary>
    /// Click on a sent attachment card: opens the fullscreen image preview for
    /// image attachments. In-memory bytes preferred (just-sent, no disk hit);
    /// on reload, BYOK images re-read from the local <c>AttachmentStore</c> via
    /// <see cref="AttachmentChip.LocalName"/>, MolaGPT-mode images fall back to
    /// <see cref="AttachmentChip.ThumbnailUrl"/>. Non-previewable cards no-op.
    /// </summary>
    private void OnAttachmentCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AttachmentChip chip) return;
        if (!chip.HasInlinePreview) return;

        var owner = Window.GetWindow(this);
        if (chip.Bytes is { Length: > 0 })
        {
            ImagePreviewWindow.Show(owner, chip.Bytes, chip.FileName);
        }
        else if (!string.IsNullOrWhiteSpace(chip.LocalName))
        {
            var bytes = App.Services.GetRequiredService<AttachmentStore>().Load(chip.LocalName);
            if (bytes is { Length: > 0 })
                ImagePreviewWindow.Show(owner, bytes, chip.FileName);
            else if (!string.IsNullOrWhiteSpace(chip.ThumbnailUrl))
                ImagePreviewWindow.Show(owner, chip.ThumbnailUrl!, chip.FileName);
        }
        else if (!string.IsNullOrWhiteSpace(chip.ThumbnailUrl))
        {
            ImagePreviewWindow.Show(owner, chip.ThumbnailUrl!, chip.FileName);
        }

        e.Handled = true;
    }

    private sealed class SmoothScrollViewerState
    {
        public ScrollViewer? Owner { get; set; }
        public EventHandler? FrameHandler { get; set; }
        public double StartOffset { get; set; }
        public double TargetOffset { get; set; }
        public DateTime AnimationStart { get; set; }
        public bool Animating { get; set; }
    }
}
