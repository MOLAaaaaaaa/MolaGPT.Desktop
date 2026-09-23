using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MolaGPT.Presentation.Visuals;

namespace MolaGPT.App.Rendering;

/// <summary>
/// An inline data chart: line, bar, area, scatter or pie, with a legend that
/// toggles series and a hover readout. Fixed height, like the function plot,
/// so the transcript's row estimate holds.
/// </summary>
public sealed class ChartView : UserControl
{
    public const double SurfaceHeight = 260;
    public const double EstimatedHeight = 340;

    private readonly ChartSpec _spec;
    private readonly ChartSurface _surface;
    private readonly List<(Border Swatch, int Index)> _swatches = new();

    public ChartView(ChartSpec spec)
    {
        _spec = spec;
        _surface = new ChartSurface(spec) { Height = SurfaceHeight, Margin = new Thickness(8, 4, 12, 0) };

        var root = new StackPanel();
        root.Children.Add(BuildHeader());
        root.Children.Add(_surface);
        root.Children.Add(BuildLegend());
        Content = new Border { Classes = { "uiblockframe" }, Child = root };
        HorizontalAlignment = HorizontalAlignment.Stretch;

        ActualThemeVariantChanged += (_, _) => RefreshSwatches();
        AttachedToVisualTree += (_, _) => RefreshSwatches();
    }

    private Control BuildHeader()
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(14, 10, 12, 0) };
        header.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_spec.Title) ? "图表" : _spec.Title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrWhiteSpace(_spec.Unit))
        {
            header.Children.Add(new TextBlock
            {
                Text = "单位：" + _spec.Unit,
                Classes = { "muted" },
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        return header;
    }

    private Control BuildLegend()
    {
        var legend = new WrapPanel { Margin = new Thickness(8, 4, 8, 8) };
        if (_spec.Type == ChartType.Pie)
        {
            var values = _spec.Series[0].Values;
            var total = values.Where(v => v is > 0).Sum(v => v!.Value);
            for (var i = 0; i < _spec.Categories.Count && i < values.Count; i++)
            {
                if (values[i] is not { } value || value <= 0) continue;
                legend.Children.Add(LegendItem(i, $"{_spec.Categories[i]}  {value / total:P1}", toggle: false));
            }
        }
        else if (_spec.Series.Count > 1 || !string.IsNullOrWhiteSpace(_spec.Series[0].Name))
        {
            for (var i = 0; i < _spec.Series.Count; i++)
                legend.Children.Add(LegendItem(i, _spec.Series[i].Name, toggle: _spec.Series.Count > 1));
        }

        return legend;
    }

    private Control LegendItem(int index, string text, bool toggle)
    {
        var swatch = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        _swatches.Add((swatch, index));
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(swatch);
        content.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var item = new Button { Classes = { "uilegend" }, Content = content };
        if (toggle)
        {
            ToolTip.SetTip(item, "点击显示或隐藏");
            item.Click += (_, _) => item.Opacity = _surface.ToggleSeries(index) ? 0.45 : 1;
        }
        else
        {
            item.IsHitTestVisible = false;
        }

        return item;
    }

    private void RefreshSwatches()
    {
        foreach (var (swatch, index) in _swatches)
            swatch.Background = new SolidColorBrush(VisualTheme.Series(this, index));
    }
}

internal sealed class ChartSurface : Control
{
    private readonly ChartSpec _spec;
    private readonly bool[] _hidden;
    private Point? _hover;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width, 0);

    public ChartSurface(ChartSpec spec)
    {
        _spec = spec;
        _hidden = new bool[spec.Series.Count];
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public bool ToggleSeries(int index)
    {
        _hidden[index] = !_hidden[index];
        InvalidateVisual();
        return _hidden[index];
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    private Typeface Typeface => new(GetValue(TextElement.FontFamilyProperty));

    private FormattedText Text(string text, IBrush brush, double size = 11) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, size, brush);

    private IBrush SeriesBrush(int index, double alpha = 1)
    {
        var color = VisualTheme.Series(this, index);
        return new SolidColorBrush(alpha >= 1 ? color : Color.FromArgb((byte)(255 * alpha), color.R, color.G, color.B));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 40 || Bounds.Height < 40) return;
        switch (_spec.Type)
        {
            case ChartType.Pie: RenderPie(context); break;
            case ChartType.Scatter: RenderScatter(context); break;
            default: RenderCategorical(context); break;
        }
    }

    // ---- categorical (line / bar / area) -------------------------------------------

    private void RenderCategorical(DrawingContext context)
    {
        var n = _spec.Categories.Count;
        if (n == 0) return;
        var visible = Enumerable.Range(0, _spec.Series.Count).Where(i => !_hidden[i]).ToList();
        var stacked = _spec.Stacked && _spec.Type is ChartType.Bar or ChartType.Area;

        double min = 0, max = 0;
        for (var c = 0; c < n; c++)
        {
            double positive = 0, negative = 0;
            foreach (var s in visible)
            {
                if (Value(s, c) is not { } v) continue;
                if (stacked)
                {
                    if (v >= 0) positive += v; else negative += v;
                }
                else
                {
                    min = Math.Min(min, v);
                    max = Math.Max(max, v);
                }
            }

            if (stacked)
            {
                min = Math.Min(min, negative);
                max = Math.Max(max, positive);
            }
        }

        if (max - min < 1e-12) max = min + 1;
        var step = VisualTheme.NiceStep((max - min) / 5);
        var yMin = Math.Floor(min / step) * step;
        var yMax = Math.Ceiling(max / step) * step;

        var muted = VisualTheme.Brush(this, "Brush.Text.Muted", Brushes.Gray);
        var labels = new List<(double Value, FormattedText Text)>();
        for (var v = yMin; v <= yMax + step * 1e-9; v += step)
            labels.Add((v, Text(VisualTheme.Format(v, step), muted)));
        var left = labels.Max(l => l.Text.Width) + 10;
        var plot = new Rect(left, 8, Bounds.Width - left - 4, Bounds.Height - 8 - 22);

        double Y(double v) => plot.Bottom - (v - yMin) / (yMax - yMin) * plot.Height;
        var band = plot.Width / n;
        double X(int c) => plot.X + band * (c + 0.5);

        var grid = new Pen(VisualTheme.Brush(this, "Brush.Divider", Brushes.LightGray), 1);
        foreach (var (value, text) in labels)
        {
            var y = Y(value);
            context.DrawLine(grid, new Point(plot.X, y), new Point(plot.Right, y));
            context.DrawText(text, new Point(left - text.Width - 6, y - text.Height / 2));
        }

        context.DrawLine(new Pen(VisualTheme.Brush(this, "Brush.Text.Muted", Brushes.Gray), 1), new Point(plot.X, Y(0)), new Point(plot.Right, Y(0)));

        // Category labels: every k-th, so they never overlap.
        var widest = _spec.Categories.Max(c => Text(c, muted).Width) + 8;
        var every = Math.Max(1, (int)Math.Ceiling(widest / Math.Max(1, band)));
        for (var c = 0; c < n; c += every)
        {
            var text = Text(_spec.Categories[c], muted);
            context.DrawText(text, new Point(X(c) - text.Width / 2, plot.Bottom + 5));
        }

        if (_spec.Type == ChartType.Bar)
        {
            var group = band * 0.72;
            var baseline = new double[n];
            var negativeBase = new double[n];
            for (var k = 0; k < visible.Count; k++)
            {
                var s = visible[k];
                var fill = SeriesBrush(s);
                for (var c = 0; c < n; c++)
                {
                    if (Value(s, c) is not { } v) continue;
                    Rect rect;
                    if (stacked)
                    {
                        var from = v >= 0 ? baseline[c] : negativeBase[c];
                        var to = from + v;
                        if (v >= 0) baseline[c] = to; else negativeBase[c] = to;
                        rect = new Rect(X(c) - group / 2, Math.Min(Y(from), Y(to)), group, Math.Abs(Y(to) - Y(from)));
                    }
                    else
                    {
                        var width = group / Math.Max(1, visible.Count);
                        var x = X(c) - group / 2 + width * k;
                        rect = new Rect(x + 1, Math.Min(Y(0), Y(v)), Math.Max(1, width - 2), Math.Abs(Y(v) - Y(0)));
                    }

                    context.DrawRectangle(fill, null, rect, 2, 2);
                }
            }
        }
        else
        {
            var cumulative = new double[n];
            foreach (var s in visible)
            {
                var points = new List<Point?>();
                var lower = new List<Point>();
                for (var c = 0; c < n; c++)
                {
                    if (Value(s, c) is not { } v)
                    {
                        points.Add(null);
                        continue;
                    }

                    var from = stacked ? cumulative[c] : 0;
                    var to = stacked ? from + v : v;
                    if (stacked) cumulative[c] = to;
                    points.Add(new Point(X(c), Y(to)));
                    lower.Add(new Point(X(c), Y(from)));
                }

                if (_spec.Type == ChartType.Area)
                {
                    var solid = points.Where(p => p is not null).Select(p => p!.Value).ToList();
                    if (solid.Count >= 2)
                    {
                        var area = new StreamGeometry();
                        using (var g = area.Open())
                        {
                            g.BeginFigure(solid[0], true);
                            foreach (var p in solid.Skip(1)) g.LineTo(p);
                            for (var i = lower.Count - 1; i >= 0; i--) g.LineTo(lower[i]);
                            g.EndFigure(true);
                        }

                        context.DrawGeometry(SeriesBrush(s, 0.22), null, area);
                    }
                }

                DrawPolyline(context, points, new Pen(SeriesBrush(s), 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round));
                if (n <= 24)
                {
                    var dotFill = VisualTheme.Brush(this, "Brush.Bg.Primary", Brushes.White);
                    var dotPen = new Pen(SeriesBrush(s), 2);
                    foreach (var p in points)
                        if (p is { } point) context.DrawEllipse(dotFill, dotPen, point, 3, 3);
                }
            }
        }

        if (_hover is { } hover && plot.Contains(hover))
        {
            var c = Math.Clamp((int)((hover.X - plot.X) / band), 0, n - 1);
            context.DrawLine(new Pen(VisualTheme.Brush(this, "Brush.Border.Strong", Brushes.Gray), 1), new Point(X(c), plot.Y), new Point(X(c), plot.Bottom));
            var lines = new List<(string, int)> { (_spec.Categories[c], -1) };
            foreach (var s in visible)
                lines.Add(($"{_spec.Series[s].Name}：{(Value(s, c) is { } v ? VisualTheme.Readout(v) : "–")}{_spec.Unit}", s));
            DrawTooltip(context, hover, lines);
        }
    }

    private double? Value(int series, int category)
    {
        var values = _spec.Series[series].Values;
        return category < values.Count ? values[category] : null;
    }

    private static void DrawPolyline(DrawingContext context, IReadOnlyList<Point?> points, IPen pen)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var open = false;
            foreach (var p in points)
            {
                if (p is not { } point)
                {
                    if (open) { g.EndFigure(false); open = false; }
                    continue;
                }

                if (!open) { g.BeginFigure(point, false); open = true; }
                else g.LineTo(point);
            }

            if (open) g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    // ---- scatter -----------------------------------------------------------------------

    private void RenderScatter(DrawingContext context)
    {
        var visible = Enumerable.Range(0, _spec.Series.Count).Where(i => !_hidden[i]).ToList();
        var all = visible.SelectMany(s => _spec.Series[s].Points).ToList();
        if (all.Count == 0) return;

        var (x0, x1, xStep) = NiceRange(all.Min(p => p.X), all.Max(p => p.X));
        var (y0, y1, yStep) = NiceRange(all.Min(p => p.Y), all.Max(p => p.Y));
        var muted = VisualTheme.Brush(this, "Brush.Text.Muted", Brushes.Gray);
        var yLabels = new List<(double, FormattedText)>();
        for (var v = y0; v <= y1 + yStep * 1e-9; v += yStep) yLabels.Add((v, Text(VisualTheme.Format(v, yStep), muted)));
        var left = yLabels.Max(l => l.Item2.Width) + 10;
        var plot = new Rect(left, 8, Bounds.Width - left - 8, Bounds.Height - 8 - 22);
        double X(double v) => plot.X + (v - x0) / (x1 - x0) * plot.Width;
        double Y(double v) => plot.Bottom - (v - y0) / (y1 - y0) * plot.Height;

        var grid = new Pen(VisualTheme.Brush(this, "Brush.Divider", Brushes.LightGray), 1);
        foreach (var (v, text) in yLabels)
        {
            context.DrawLine(grid, new Point(plot.X, Y(v)), new Point(plot.Right, Y(v)));
            context.DrawText(text, new Point(left - text.Width - 6, Y(v) - text.Height / 2));
        }

        for (var v = x0; v <= x1 + xStep * 1e-9; v += xStep)
        {
            context.DrawLine(grid, new Point(X(v), plot.Y), new Point(X(v), plot.Bottom));
            var text = Text(VisualTheme.Format(v, xStep), muted);
            context.DrawText(text, new Point(X(v) - text.Width / 2, plot.Bottom + 5));
        }

        (double Distance, int Series, (double X, double Y) Point)? nearest = null;
        foreach (var s in visible)
        {
            var fill = SeriesBrush(s, 0.85);
            foreach (var p in _spec.Series[s].Points)
            {
                var screen = new Point(X(p.X), Y(p.Y));
                context.DrawEllipse(fill, null, screen, 3.6, 3.6);
                if (_hover is { } hover)
                {
                    var d = Math.Sqrt(Math.Pow(hover.X - screen.X, 2) + Math.Pow(hover.Y - screen.Y, 2));
                    if (d < 16 && (nearest is null || d < nearest.Value.Distance)) nearest = (d, s, p);
                }
            }
        }

        if (nearest is { } hit && _hover is { } at)
        {
            var screen = new Point(X(hit.Point.X), Y(hit.Point.Y));
            context.DrawEllipse(null, new Pen(SeriesBrush(hit.Series), 2), screen, 6, 6);
            DrawTooltip(context, at, [(_spec.Series[hit.Series].Name, hit.Series),
                ($"x = {VisualTheme.Readout(hit.Point.X)}   y = {VisualTheme.Readout(hit.Point.Y)}", -1)]);
        }
    }

    private static (double Min, double Max, double Step) NiceRange(double min, double max)
    {
        if (max - min < 1e-12) { min -= 1; max += 1; }
        var step = VisualTheme.NiceStep((max - min) / 5);
        return (Math.Floor(min / step) * step, Math.Ceiling(max / step) * step, step);
    }

    // ---- pie -----------------------------------------------------------------------------

    private void RenderPie(DrawingContext context)
    {
        var values = _spec.Series[0].Values;
        var slices = new List<(int Index, double Value)>();
        for (var i = 0; i < values.Count && i < _spec.Categories.Count; i++)
            if (values[i] is { } v && v > 0) slices.Add((i, v));
        var total = slices.Sum(s => s.Value);
        if (total <= 0) return;

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 10;
        var inner = radius * 0.56;
        var separator = new Pen(VisualTheme.Brush(this, "Brush.Bg.Primary", Brushes.White), 2);

        int? hovered = null;
        if (_hover is { } hover)
        {
            var dx = hover.X - center.X;
            var dy = hover.Y - center.Y;
            var r = Math.Sqrt(dx * dx + dy * dy);
            if (r >= inner && r <= radius)
            {
                var angle = (Math.Atan2(dy, dx) + Math.PI / 2 + Math.Tau) % Math.Tau;
                var acc = 0d;
                foreach (var (index, value) in slices)
                {
                    var sweep = value / total * Math.Tau;
                    if (angle >= acc && angle < acc + sweep) { hovered = index; break; }
                    acc += sweep;
                }
            }
        }

        var start = -Math.PI / 2;
        foreach (var (index, value) in slices)
        {
            var sweep = value / total * Math.Tau;
            var grow = hovered == index ? 5 : 0;
            context.DrawGeometry(SeriesBrush(index), separator, Slice(center, radius + grow, inner, start, sweep));
            start += sweep;
        }

        if (hovered is { } h && _hover is { } at)
        {
            var value = values[h]!.Value;
            DrawTooltip(context, at, [(_spec.Categories[h], h), ($"{VisualTheme.Readout(value)}{_spec.Unit}（{value / total:P1}）", -1)]);
        }
    }

    private static StreamGeometry Slice(Point c, double outer, double inner, double start, double sweep)
    {
        if (sweep >= Math.Tau - 1e-6) sweep = Math.Tau - 1e-4;
        Point P(double r, double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        var large = sweep > Math.PI;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(P(outer, start), true);
            g.ArcTo(P(outer, start + sweep), new Size(outer, outer), 0, large, SweepDirection.Clockwise);
            g.LineTo(P(inner, start + sweep));
            g.ArcTo(P(inner, start), new Size(inner, inner), 0, large, SweepDirection.CounterClockwise);
            g.EndFigure(true);
        }

        return geometry;
    }

    // ---- tooltip -----------------------------------------------------------------------------

    private void DrawTooltip(DrawingContext context, Point at, IReadOnlyList<(string Text, int Series)> lines)
    {
        var foreground = VisualTheme.Brush(this, "Brush.Text.Primary", Brushes.Black);
        var texts = lines.Select((l, i) => (Text: Text(l.Text, foreground, i == 0 ? 11.5 : 11), l.Series)).ToList();
        var width = texts.Max(t => t.Text.Width + (t.Series >= 0 ? 14 : 0)) + 18;
        var height = texts.Sum(t => t.Text.Height + 1) + 10;
        var x = at.X + 14 + width > Bounds.Width ? at.X - 14 - width : at.X + 14;
        var y = Math.Clamp(at.Y - height / 2, 2, Math.Max(2, Bounds.Height - height - 2));
        var box = new Rect(x, y, width, height);
        context.DrawRectangle(
            VisualTheme.Brush(this, "Brush.Bg.Elevated", Brushes.White),
            new Pen(VisualTheme.Brush(this, "Brush.Border", Brushes.LightGray), 1),
            box, 6, 6);

        var ty = box.Y + 5;
        foreach (var (text, series) in texts)
        {
            var tx = box.X + 9;
            if (series >= 0)
            {
                context.DrawRectangle(SeriesBrush(series), null, new Rect(tx, ty + text.Height / 2 - 4, 8, 8), 2, 2);
                tx += 14;
            }

            context.DrawText(text, new Point(tx, ty));
            ty += text.Height + 1;
        }
    }
}
