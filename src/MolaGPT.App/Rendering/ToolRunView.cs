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
    private readonly ToolRevealPresenter _summaryReveal;
    private readonly TextBlock _summary = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _preview = new()
    {
        FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(10, 0, 0, 0)
    };
    private readonly ToolChevron _chevron = new()
    {
        ClosedAngle = -90, OpenAngle = 0, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0)
    };
    private readonly Dictionary<ToolRow, ToolRevealPresenter> _entries = new();
    private ToolRunRow? _row;
    private bool _subscribed;
    private bool _queued;

    public ToolRunView()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        header.Children.Add(_chevron);
        header.Children.Add(_summary);
        header.Children.Add(_preview);
        Grid.SetColumn(_summary, 1);
        Grid.SetColumn(_preview, 2);
        var toggle = new Button
        {
            Content = header, Padding = new Thickness(4, 4), MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        toggle.Classes.Add("toolsummary");
        toggle.Click += (_, _) =>
        {
            if (_row is not null) _row.IsExpanded = !_row.IsExpanded;
        };
        _summaryReveal = new ToolRevealPresenter { IsOpen = false, Child = toggle };
        _panel.Children.Add(_summaryReveal);
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
            foreach (var control in _entries.Values) _panel.Children.Remove(control);
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
        _summary.Text = _row.Summary;
        _preview.Text = _row.Preview;
        ToolTip.SetTip(_preview, _row.Preview);
        _chevron.IsExpanded = _row.IsExpanded;
        _summaryReveal.IsOpen = _row.HasSummary;

        foreach (var gone in _entries.Keys.Where(entry => !_row.Entries.Contains(entry)).ToArray())
        {
            _panel.Children.Remove(_entries[gone]);
            _entries.Remove(gone);
        }
        for (var i = 0; i < _row.Entries.Count; i++)
        {
            var entry = _row.Entries[i];
            var visible = _row.IsExpanded || !entry.IsArchived;
            if (!_entries.TryGetValue(entry, out var reveal))
            {
                reveal = new ToolRevealPresenter { IsOpen = visible, Duration = TimeSpan.FromMilliseconds(220) };
                _entries[entry] = reveal;
                _panel.Children.Insert(i + 1, reveal);
            }
            else
            {
                var index = _panel.Children.IndexOf(reveal);
                if (index != i + 1) _panel.Children.Move(index, i + 1);
            }
            // The parent transcript supplies the shared typed-argument template.
            if (visible && reveal.Child is null)
                reveal.Child = new ContentControl
                {
                    Content = entry,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
            reveal.IsOpen = visible;
            if (entry.AnimateEntrance && this.IsAttachedToVisualTree())
            {
                entry.AnimateEntrance = false;
                if (visible) reveal.AnimateEntrance();
            }
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyBrushes();
    private void ApplyBrushes()
    {
        if (!this.TryFindResource("Brush.Text.Secondary", ActualThemeVariant, out var value)) return;
        _summary.Foreground = value as IBrush;
        _preview.Foreground = value as IBrush;
        _chevron.Foreground = value as IBrush;
    }
}
