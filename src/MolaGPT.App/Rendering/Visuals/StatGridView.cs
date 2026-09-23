using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MolaGPT.Presentation.Visuals;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Key figures as a row of cards: value and unit, the change, and when there
/// is history a sparkline the reader can scrub for any earlier value.
///
/// Colour follows <see cref="StatTone"/>, never the arrow's direction; see
/// there for why.
/// </summary>
public sealed class StatGridView : UserControl
{
    public const double EstimatedHeight = 150;

    public StatGridView(StatGridSpec spec)
    {
        var root = new StackPanel();
        if (!string.IsNullOrWhiteSpace(spec.Title))
        {
            root.Children.Add(new TextBlock
            {
                Text = spec.Title,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(14, 10, 12, 0),
            });
        }

        var grid = new AutoFitGrid
        {
            MinColumnWidth = 150,
            MaxColumns = 4,
            Spacing = 10,
            Margin = new Thickness(12, string.IsNullOrWhiteSpace(spec.Title) ? 12 : 10, 12, 12),
        };
        foreach (var item in spec.Items)
            grid.Children.Add(new StatCard(item, spec.Periods));
        root.Children.Add(grid);

        Content = new Border { Classes = { "uiblockframe" }, Child = root };
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }
}

internal sealed class StatCard : Border
{
    private readonly StatItem _item;
    private readonly IReadOnlyList<string> _periods;
    private readonly TextBlock _value = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _period = new() { Classes = { "muted" }, FontSize = 11, IsVisible = false, Margin = new Thickness(8, 0, 0, 0) };

    public StatCard(StatItem item, IReadOnlyList<string> periods)
    {
        _item = item;
        _periods = periods;
        Classes.Add("uistatcard");

        var top = new StackPanel();
        var labelRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        labelRow.Children.Add(new TextBlock
        {
            Text = item.Label,
            Classes = { "muted" },
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(_period, 1);
        labelRow.Children.Add(_period);
        top.Children.Add(labelRow);
        top.Children.Add(_value);
        ShowValue(item.Value);

        if (item.History.Count > 1)
        {
            var spark = new Sparkline(item.History, item.Tone) { Height = 32, Margin = new Thickness(0, 6, 0, 0) };
            spark.HoverChanged += OnHover;
            top.Children.Add(spark);
        }

        var body = new DockPanel();
        if (item.Delta is not null || item.Note is not null)
        {
            var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 6, 0, 0) };
            if (item.Delta is not null) footer.Children.Add(Delta(item));
            if (item.Note is not null)
            {
                var note = new TextBlock
                {
                    Text = item.Note,
                    Classes = { "muted" },
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                ToolTip.SetTip(note, item.Note);
                Grid.SetColumn(note, 1);
                footer.Children.Add(note);
            }

            // Docked to the bottom so cards sharing a row keep their footers level.
            DockPanel.SetDock(footer, Dock.Bottom);
            body.Children.Add(footer);
        }

        body.Children.Add(top);
        Child = body;
    }

    private void OnHover(int? index)
    {
        if (index is not { } i)
        {
            _period.IsVisible = false;
            ShowValue(_item.Value);
            return;
        }

        // Periods and history both end at the latest point; line them up from
        // the end so a list one entry short still labels the recent values.
        var p = i + _periods.Count - _item.History.Count;
        _period.Text = p >= 0 && p < _periods.Count ? _periods[p] : $"#{i + 1}";
        _period.IsVisible = true;
        ShowValue(VisualTheme.Readout(_item.History[i]));
    }

    private void ShowValue(string value)
    {
        var inlines = new InlineCollection { new Run(value) { FontSize = 22, FontWeight = FontWeight.SemiBold } };
        if (_item.Unit is not null)
        {
            var unit = new Run(" " + _item.Unit) { FontSize = 12 };
            unit.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("Brush.Text.Muted"));
            inlines.Add(unit);
        }

        _value.Inlines = inlines;
    }

    private static TextBlock Delta(StatItem item)
    {
        // No glyph for flat: a dash in front of "持平" or "0%" reads as a minus.
        var arrow = item.Trend switch
        {
            StatTrend.Up => "▲ ",
            StatTrend.Down => "▼ ",
            _ => null,
        };
        var text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var inlines = new InlineCollection();
        if (arrow is not null) inlines.Add(new Run(arrow) { FontSize = 8.5, BaselineAlignment = BaselineAlignment.Center });
        inlines.Add(new Run(item.Delta));
        text.Inlines = inlines;
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(ToneKey(item.Tone)));
        return text;
    }

    internal static string ToneKey(StatTone tone) => tone switch
    {
        StatTone.Good => "Brush.Success",
        StatTone.Bad => "Brush.Danger.Foreground",
        _ => "Brush.Text.Secondary",
    };
}

/// <summary>A small line of past values. Scrubbing reports the nearest point;
/// the card shows it in place of the headline value.</summary>
internal sealed class Sparkline : Control
{
    private readonly IReadOnlyList<double> _values;
    private readonly StatTone _tone;
    private int? _hover;

    public event Action<int?>? HoverChanged;

    public Sparkline(IReadOnlyList<double> values, StatTone tone)
    {
        _values = values;
        _tone = tone;
        Cursor = new Cursor(StandardCursorType.Cross);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, 0);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var ratio = Math.Clamp(e.GetPosition(this).X / Math.Max(1, Bounds.Width), 0, 1);
        SetHover((int)Math.Round(ratio * (_values.Count - 1)));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(null);
    }

    private void SetHover(int? index)
    {
        if (index == _hover) return;
        _hover = index;
        InvalidateVisual();
        HoverChanged?.Invoke(index);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        // Hit-testable across its whole box, not just along the stroke.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (width <= 0 || height <= 0 || _values.Count < 2) return;

        var min = _values.Min();
        var span = _values.Max() - min;
        if (span <= 0) span = 1;
        double X(int i) => i * width / (_values.Count - 1);
        double Y(double v) => 3 + (1 - (v - min) / span) * (height - 6);

        var color = _tone == StatTone.Neutral
            ? VisualTheme.Series(this, 0)
            : (VisualTheme.Brush(this, StatCard.ToneKey(_tone), Brushes.Gray) as ISolidColorBrush)?.Color ?? Colors.Gray;
        var stroke = new SolidColorBrush(color);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(X(0), Y(_values[0])), false);
            for (var i = 1; i < _values.Count; i++) g.LineTo(new Point(X(i), Y(_values[i])));
            g.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(stroke, 1.5, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), geometry);

        if (_hover is { } h)
        {
            var guide = VisualTheme.Brush(this, "Brush.Border", Brushes.LightGray);
            context.DrawLine(new Pen(guide, 1), new Point(X(h), 0), new Point(X(h), height));
            var halo = VisualTheme.Brush(this, "Brush.Bg.Primary", Brushes.White);
            context.DrawEllipse(stroke, new Pen(halo, 1.5), new Point(X(h), Y(_values[h])), 3.2, 3.2);
        }
        else
        {
            var last = _values.Count - 1;
            context.DrawEllipse(stroke, null, new Point(X(last), Y(_values[last])), 2.5, 2.5);
        }
    }
}
