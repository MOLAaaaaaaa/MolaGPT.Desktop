using System.ComponentModel;
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
        Clipboard.SetText(GetCleanMarkdownForCopy(vm.Content));
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

        panel.Children.Add(new TextBlock
        {
            Text = "响应统计",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = FindBrush("Brush.Text.Secondary", Brushes.DimGray),
            Margin = new Thickness(0, 0, 0, 8)
        });

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

    private static string GetCleanMarkdownForCopy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var text = raw;
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
}
