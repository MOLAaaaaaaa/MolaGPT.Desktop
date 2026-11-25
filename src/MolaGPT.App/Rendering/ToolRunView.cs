using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MolaGPT.App.Rendering;

public sealed class ToolRunView : Decorator
{
    private readonly StackPanel _panel = new();
    private readonly StackPanel _archivedPanel = new();
    private readonly StackPanel _livePanel = new();
    private readonly Border _rail;
    private readonly ToolRevealPresenter _summaryReveal;
    private readonly ToolRevealPresenter _archivedReveal;
    private readonly TextBlock _icon = new()
    {
        FontSize = 13,
        Width = 16,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _summary = new()
    {
        FontSize = 13,
        FontWeight = FontWeight.Medium,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.NoWrap
    };
    private readonly TextBlock _preview = new()
    {
        FontSize = 11.5,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0)
    };
    private readonly ToolChevron _chevron = new()
    {
        ClosedAngle = -90,
        OpenAngle = 0,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.65,
        Margin = new Thickness(6, 0, 0, 0)
    };
    private readonly Dictionary<ToolRow, ToolRevealPresenter> _entries = new();
    private ToolRunRow? _row;
    private bool _subscribed;
    private bool _queued;

    public ToolRunView()
    {
        _icon.Classes.Add("icon");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("16,8,Auto,*,Auto") };
        header.Children.Add(_icon);
        Grid.SetColumn(_summary, 2);
        header.Children.Add(_summary);
        Grid.SetColumn(_preview, 3);
        header.Children.Add(_preview);
        Grid.SetColumn(_chevron, 4);
        header.Children.Add(_chevron);

        var toggle = new Button
        {
            Content = header,
            Padding = new Thickness(6, 4),
            MinHeight = 30,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        toggle.Classes.Add("toolsummary");
        toggle.Click += (_, _) =>
        {
            if (_row is not null) _row.IsExpanded = !_row.IsExpanded;
        };
        // 将左外溢放到 ClipToBounds=true 的 ToolRevealPresenter 外侧，
        // 既让 16px 图标与正文/下方 ToolRow 左边缘 0px 对齐，又不裁切悬停圆角。
        _summaryReveal = new ToolRevealPresenter
        {
            IsOpen = false,
            Margin = new Thickness(-6, 0, 0, 0),
            Child = toggle
        };

        _rail = new Border
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 4)
        };

        // 竖轨与缩进只属于被折叠归档的已完成工具组；未归档项（进行中 / 失败）
        // 放在下方平级 _livePanel 中，不冒充汇总行的子项。
        var archivedHost = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("16,8,*"),
            Margin = new Thickness(0, 2, 0, 4)
        };
        archivedHost.Children.Add(_rail);
        Grid.SetColumn(_archivedPanel, 2);
        archivedHost.Children.Add(_archivedPanel);

        _archivedReveal = new ToolRevealPresenter
        {
            IsOpen = false,
            Duration = TimeSpan.FromMilliseconds(220),
            Child = archivedHost
        };

        _panel.Children.Add(_summaryReveal);
        _panel.Children.Add(_archivedReveal);
        _panel.Children.Add(_livePanel);
        Child = _panel;
        DataContextChanged += (_, _) => AttachRow();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeChanged;
        AttachRow();
        ApplyBrushes();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unsubscribe();
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void AttachRow()
    {
        var next = DataContext as ToolRunRow;
        if (!ReferenceEquals(_row, next))
        {
            Unsubscribe();
            _row = next;
            _archivedPanel.Children.Clear();
            _livePanel.Children.Clear();
            _entries.Clear();
        }
        if (_row is not null && !_subscribed && this.IsAttachedToVisualTree())
        {
            _row.PropertyChanged += OnRowChanged;
            _row.Entries.CollectionChanged += OnEntriesChanged;
            _subscribed = true;
        }
        Refresh();
    }

    private void Unsubscribe()
    {
        if (_row is null || !_subscribed) return;
        _row.PropertyChanged -= OnRowChanged;
        _row.Entries.CollectionChanged -= OnEntriesChanged;
        _subscribed = false;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => QueueRefresh();
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRefresh();

    private void QueueRefresh()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            if (this.IsAttachedToVisualTree()) Refresh();
        }, DispatcherPriority.Render);
    }

    private void Refresh()
    {
        if (_row is null) return;
        var hasSummary = _row.HasSummary;
        _icon.Text = _row.IconGlyph;
        _summary.Text = _row.Summary;
        var hasPreview = !string.IsNullOrWhiteSpace(_row.Preview);
        _preview.Text = _row.Preview;
        _preview.IsVisible = hasPreview;
        ToolTip.SetTip(_preview, hasPreview ? _row.Preview : null);
        _chevron.IsExpanded = _row.IsExpanded;
        _summaryReveal.IsOpen = hasSummary;

        foreach (var gone in _entries.Keys.Where(entry => !_row.Entries.Contains(entry)).ToArray())
        {
            var control = _entries[gone];
            _archivedPanel.Children.Remove(control);
            _livePanel.Children.Remove(control);
            _entries.Remove(gone);
        }

        var archivedIndex = 0;
        var liveIndex = 0;
        for (var i = 0; i < _row.Entries.Count; i++)
        {
            var entry = _row.Entries[i];
            var inArchive = hasSummary && entry.IsArchived;
            var targetPanel = inArchive ? _archivedPanel : _livePanel;
            var otherPanel = inArchive ? _livePanel : _archivedPanel;
            var targetIndex = inArchive ? archivedIndex++ : liveIndex++;
            var visible = !inArchive || _row.IsExpanded;

            if (!_entries.TryGetValue(entry, out var reveal))
            {
                reveal = new ToolRevealPresenter { IsOpen = true, Duration = TimeSpan.FromMilliseconds(220) };
                _entries[entry] = reveal;
                targetPanel.Children.Insert(targetIndex, reveal);
            }
            else
            {
                if (ReferenceEquals(reveal.Parent, otherPanel))
                    otherPanel.Children.Remove(reveal);
                var currentIndex = targetPanel.Children.IndexOf(reveal);
                if (currentIndex < 0)
                    targetPanel.Children.Insert(targetIndex, reveal);
                else if (currentIndex != targetIndex)
                    targetPanel.Children.Move(currentIndex, targetIndex);
            }

            // The parent transcript supplies the shared typed-argument template.
            if (visible && reveal.Child is null)
                reveal.Child = new ContentControl
                {
                    Content = entry,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
            reveal.IsOpen = true;
            if (entry.AnimateEntrance && this.IsAttachedToVisualTree())
            {
                entry.AnimateEntrance = false;
                if (!inArchive) reveal.AnimateEntrance();
            }
        }

        _archivedReveal.IsOpen = hasSummary && _row.IsExpanded && archivedIndex > 0;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyBrushes();

    private void ApplyBrushes()
    {
        var secondary = TryBrush("Brush.Text.Secondary");
        var muted = TryBrush("Brush.Text.Muted");
        _icon.Foreground = secondary;
        _summary.Foreground = secondary;
        _preview.Foreground = muted ?? secondary;
        _chevron.Foreground = secondary;

        if (this.TryFindResource("Color.Border", ActualThemeVariant, out var borderVal) && borderVal is Color borderColor)
        {
            _rail.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)(borderColor.A * 0.85), borderColor.R, borderColor.G, borderColor.B), 0),
                    new GradientStop(Color.FromArgb((byte)(borderColor.A * 0.7), borderColor.R, borderColor.G, borderColor.B), 0.85),
                    new GradientStop(Color.FromArgb(0, borderColor.R, borderColor.G, borderColor.B), 1)
                }
            };
        }
        else if (TryBrush("Brush.Border") is { } fallbackRail)
        {
            _rail.Background = fallbackRail;
        }
    }

    private IBrush? TryBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
}
