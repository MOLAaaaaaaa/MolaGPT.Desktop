using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MolaGPT.Presentation.Visuals;

namespace MolaGPT.App.Rendering;

/// <summary>
/// An inline function plot: curves, parameter sliders and a legend.
///
/// The point of drawing it natively rather than in a web view is that it is
/// part of the transcript — it scrolls, virtualizes, follows the theme and
/// costs nothing when off screen. The plot keeps a fixed height so the
/// virtualizer's estimate for the row is also its real height.
/// </summary>
public sealed class FunctionPlotView : UserControl
{
    public const double PlotHeight = 300;
    public const double EstimatedHeight = 400;

    private readonly FunctionPlotSpec _spec;
    private readonly PlotSurface _surface;
    private readonly List<Button> _legendItems = new();
    private readonly List<MathView> _formulas = new();

    public FunctionPlotView(FunctionPlotSpec spec)
    {
        _spec = spec;
        _surface = new PlotSurface(spec) { Height = PlotHeight, Margin = new Thickness(6, 2, 6, 0) };

        var root = new StackPanel();
        root.Children.Add(BuildHeader());
        root.Children.Add(_surface);
        if (spec.Params.Count > 0) root.Children.Add(BuildSliders());
        root.Children.Add(BuildLegend());

        Content = new Border { Classes = { "uiblockframe" }, Child = root };
        HorizontalAlignment = HorizontalAlignment.Stretch;
        ActualThemeVariantChanged += (_, _) => RefreshLegendColors();
        // Resources resolve only once attached; colours picked in the constructor
        // would be the fallbacks.
        AttachedToVisualTree += (_, _) => RefreshLegendColors();
    }

    private Control BuildHeader()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14, 8, 6, 0) };
        header.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_spec.Title) ? "函数图像" : _spec.Title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        tools.Children.Add(Tool("", "放大（Ctrl + 滚轮）", () => _surface.Zoom(1 / 1.5, null)));
        tools.Children.Add(Tool("", "缩小", () => _surface.Zoom(1.5, null)));
        tools.Children.Add(Tool("", "复位（双击图像）", () => _surface.ResetView()));
        Grid.SetColumn(tools, 1);
        header.Children.Add(tools);
        return header;
    }

    private Control BuildSliders()
    {
        var panel = new StackPanel { Margin = new Thickness(14, 4, 14, 0), Spacing = 0 };
        for (var i = 0; i < _spec.Params.Count; i++)
        {
            var index = i;
            var param = _spec.Params[i];
            var value = new TextBlock
            {
                Text = VisualTheme.Readout(param.Default),
                FontSize = 12,
                MinWidth = 52,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var slider = new Slider
            {
                Minimum = param.Min,
                Maximum = param.Max,
                Value = param.Default,
                SmallChange = param.Step,
                LargeChange = param.Step * 10,
                TickFrequency = param.Step,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0),
            };
            // Fluent keeps 15px above and below the track for tick bars and clips
            // to its bounds, so in a 32px row the track sat 9px low and the thumb's
            // lower edge was cut off. 6 + 20 + 6 fills the row, track centred.
            slider.Resources["SliderPreContentMargin"] = new GridLength(6);
            slider.Resources["SliderPostContentMargin"] = new GridLength(6);
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty) return;
                var v = slider.Value;
                value.Text = VisualTheme.Readout(v);
                _surface.SetParameter(index, v);
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 32 };
            var name = new MathView
            {
                Latex = ParamLatex(param.Name),
                FormulaSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 18,
            };
            _formulas.Add(name);
            row.Children.Add(name);
            Grid.SetColumn(slider, 1);
            row.Children.Add(slider);
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            panel.Children.Add(row);
        }

        return panel;
    }

    private Control BuildLegend()
    {
        var legend = new WrapPanel { Margin = new Thickness(8, 4, 8, 8) };
        for (var i = 0; i < _spec.Curves.Count; i++)
        {
            var index = i;
            var curve = _spec.Curves[i];
            var swatch = new Border
            {
                Width = 14,
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
            };

            Control label;
            if (curve.Error is { } error)
            {
                label = new TextBlock
                {
                    Text = $"{curve.Source}：{error}",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("Brush.Danger.Foreground"));
            }
            else if (!string.IsNullOrWhiteSpace(curve.Label))
            {
                label = new TextBlock { Text = curve.Label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            }
            else
            {
                var math = new MathView { Latex = curve.Latex, FormulaSize = 14, VerticalAlignment = VerticalAlignment.Center };
                _formulas.Add(math);
                label = math;
            }

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(swatch);
            content.Children.Add(label);
            var item = new Button { Classes = { "uilegend" }, Content = content, Tag = swatch };
            if (curve.Error is null)
            {
                ToolTip.SetTip(item, "点击显示或隐藏");
                item.Click += (_, _) =>
                {
                    var hidden = _surface.ToggleHidden(index);
                    item.Opacity = hidden ? 0.45 : 1;
                };
            }
            else
            {
                item.IsHitTestVisible = false;
            }

            _legendItems.Add(item);
            legend.Children.Add(item);
        }

        RefreshLegendColors();
        return legend;
    }

    private void RefreshLegendColors()
    {
        // MathView draws with whatever Foreground it had when it built the
        // formula; set it from the theme rather than rely on inheritance.
        var text = VisualTheme.Brush(this, "Brush.Text.Primary", Brushes.Black);
        foreach (var formula in _formulas) formula.Foreground = text;
        for (var i = 0; i < _legendItems.Count; i++)
        {
            if (_legendItems[i].Tag is Border swatch)
                swatch.Background = new SolidColorBrush(VisualTheme.Series(this, i));
        }
    }

    private static Button Tool(string glyph, string tip, Action action)
    {
        var button = new Button
        {
            Classes = { "inlineaction" },
            Width = 28,
            Height = 26,
            Padding = new Thickness(0),
            Content = new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 12 },
        };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private static string ParamLatex(string name) => name switch
    {
        "alpha" or "beta" or "gamma" or "delta" or "lambda" or "mu" or "sigma" or "omega" or "phi" or "rho" or "kappa" => "\\" + name,
        _ when name.Length > 1 && char.IsLetter(name[0]) && name[1..].All(char.IsDigit) => $"{name[0]}_{{{name[1..]}}}",
        _ => name.Length == 1 ? name : $"\\mathit{{{name}}}",
    };
}

/// <summary>
/// The drawing surface: axes, grid, curves, and pan / zoom.
///
/// Plain wheel scrolls the transcript; zoom needs Ctrl, because a plot that
/// captured every wheel tick would trap the reader scrolling past it.
/// </summary>
internal sealed class PlotSurface : Control
{
    private readonly FunctionPlotSpec _spec;
    private readonly double[] _values;
    private readonly bool[] _hidden;
    private readonly bool _piTicks;
    private (double X0, double X1, double Y0, double Y1) _home;
    private (double X0, double X1, double Y0, double Y1) _view;

    private List<(int Curve, StreamGeometry Geometry)>? _geometry;
    private Size _geometrySize;
    private Point? _hover;
    private Point? _dragStart;
    private (double X0, double X1, double Y0, double Y1) _dragView;

    // Width is whatever the answer column offers; height is fixed by the owner.
    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width, 0);

    public PlotSurface(FunctionPlotSpec spec)
    {
        _spec = spec;
        _values = new double[FunctionPlotSpec.FixedVariables.Length + spec.Params.Count];
        for (var i = 0; i < spec.Params.Count; i++)
            _values[FunctionPlotSpec.FixedVariables.Length + i] = spec.Params[i].Default;
        _hidden = new bool[spec.Curves.Count];
        _piTicks = spec.X is { } x && IsPiMultiple(x.Min) && IsPiMultiple(x.Max);
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        DoubleTapped += (_, _) => ResetView();
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public void SetParameter(int index, double value)
    {
        _values[FunctionPlotSpec.FixedVariables.Length + index] = value;
        Invalidate();
    }

    public bool ToggleHidden(int index)
    {
        _hidden[index] = !_hidden[index];
        InvalidateVisual();
        return _hidden[index];
    }

    public void ResetView()
    {
        if (Bounds.Width <= 0) return;
        _view = _home = ComputeHome(Bounds.Size);
        Invalidate();
    }

    public void Zoom(double factor, Point? around)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var anchor = around ?? new Point(size.Width / 2, size.Height / 2);
        var (ax, ay) = ToWorld(anchor, size);
        var (x0, x1, y0, y1) = _view;
        var nx0 = ax - (ax - x0) * factor;
        var nx1 = ax + (x1 - ax) * factor;
        var ny0 = ay - (ay - y0) * factor;
        var ny1 = ay + (y1 - ay) * factor;
        if (nx1 - nx0 < 1e-6 || nx1 - nx0 > 1e7) return;
        _view = (nx0, nx1, ny0, ny1);
        Invalidate();
    }

    private void Invalidate()
    {
        _geometry = null;
        InvalidateVisual();
    }

    // ---- interaction -------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragStart = e.GetPosition(this);
        _dragView = _view;
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        _hover = position;
        if (_dragStart is { } start && Bounds.Width > 0)
        {
            var (x0, x1, y0, y1) = _dragView;
            var dx = (position.X - start.X) / Bounds.Width * (x1 - x0);
            var dy = (position.Y - start.Y) / Bounds.Height * (y1 - y0);
            _view = (x0 - dx, x1 - dx, y0 + dy, y1 + dy);
            Invalidate();
            return;
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is null) return;
        _dragStart = null;
        e.Pointer.Capture(null);
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            base.OnPointerWheelChanged(e);
            return;
        }

        Zoom(e.Delta.Y > 0 ? 1 / 1.25 : 1.25, e.GetPosition(this));
        e.Handled = true;
    }

    // ---- coordinates ---------------------------------------------------------

    private (double X, double Y) ToWorld(Point p, Size size)
    {
        var (x0, x1, y0, y1) = _view;
        return (x0 + p.X / size.Width * (x1 - x0), y1 - p.Y / size.Height * (y1 - y0));
    }

    private Point ToScreen(double x, double y, Size size)
    {
        var (x0, x1, y0, y1) = _view;
        var sx = (x - x0) / (x1 - x0) * size.Width;
        var sy = (y1 - y) / (y1 - y0) * size.Height;
        // Keep far-off points finite and bounded so the geometry stays sane.
        return new Point(Math.Clamp(sx, -4 * size.Width, 5 * size.Width), Math.Clamp(sy, -4 * size.Height, 5 * size.Height));
    }

    private (double, double, double, double) ComputeHome(Size size)
    {
        var aspect = size.Height / Math.Max(1, size.Width);
        var hasExplicit = _spec.Curves.Any(c => c.Error is null && c.Kind is CurveKind.ExplicitY);

        (double Min, double Max) x;
        if (_spec.X is { } givenX)
        {
            x = givenX;
        }
        else if (!hasExplicit && Extent() is { } box)
        {
            // Closed shapes (circles, roses, parametric loops): frame them.
            var cx = (box.X0 + box.X1) / 2;
            var cy = (box.Y0 + box.Y1) / 2;
            var half = Math.Max((box.X1 - box.X0) / 2, (box.Y1 - box.Y0) / 2 / aspect) * 1.15;
            if (half <= 0) half = 1;
            return (cx - half, cx + half, cy - half * aspect, cy + half * aspect);
        }
        else
        {
            x = (-10, 10);
        }

        if (_spec.Y is { } givenY) return (x.Min, x.Max, givenY.Min, givenY.Max);

        if (hasExplicit && SampleRange(x.Min, x.Max) is { } range)
            return (x.Min, x.Max, range.Min, range.Max);

        // Equal scale on both axes, so a circle looks like one.
        var halfY = (x.Max - x.Min) * aspect / 2;
        return (x.Min, x.Max, -halfY, halfY);
    }

    private (double Min, double Max)? SampleRange(double x0, double x1)
    {
        var values = new List<double>();
        var v = (double[])_values.Clone();
        foreach (var curve in _spec.Curves)
        {
            if (curve.Error is not null || curve.Kind != CurveKind.ExplicitY || curve.Fn is null) continue;
            for (var i = 0; i <= 400; i++)
            {
                v[0] = x0 + (x1 - x0) * i / 400;
                var y = Safe(curve.Fn, v);
                if (double.IsFinite(y)) values.Add(y);
            }
        }

        if (values.Count < 2) return null;
        values.Sort();
        // Trim the extreme tails so one asymptote does not flatten everything else.
        var lo = values[(int)(values.Count * 0.02)];
        var hi = values[(int)Math.Min(values.Count - 1, values.Count * 0.98)];
        if (hi - lo < 1e-9)
        {
            lo -= 1;
            hi += 1;
        }

        var pad = (hi - lo) * 0.12;
        return (lo - pad, hi + pad);
    }

    private (double X0, double X1, double Y0, double Y1)? Extent()
    {
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        var v = (double[])_values.Clone();
        var any = false;
        foreach (var curve in _spec.Curves)
        {
            if (curve.Error is not null || curve.Fn is null) continue;
            if (curve.Kind is CurveKind.Polar or CurveKind.Parametric)
            {
                for (var i = 0; i <= 720; i++)
                {
                    var t = curve.TMin + (curve.TMax - curve.TMin) * i / 720;
                    SetT(v, t);
                    var (px, py) = PolarOrParametric(curve, v, t);
                    if (!double.IsFinite(px) || !double.IsFinite(py)) continue;
                    minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
                    minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
                    any = true;
                }
            }
            else if (curve.Kind == CurveKind.Implicit)
            {
                // Find where the zero set lives on a coarse grid over [-20, 20].
                const int n = 120;
                for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                {
                    var x = -20 + 40.0 * i / n;
                    var y = -20 + 40.0 * j / n;
                    v[0] = x; v[1] = y;
                    var a = Safe(curve.Fn, v);
                    v[0] = x + 40.0 / n;
                    var b = Safe(curve.Fn, v);
                    v[0] = x; v[1] = y + 40.0 / n;
                    var c = Safe(curve.Fn, v);
                    if (!double.IsFinite(a)) continue;
                    if ((double.IsFinite(b) && Math.Sign(a) != Math.Sign(b)) || (double.IsFinite(c) && Math.Sign(a) != Math.Sign(c)))
                    {
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                        any = true;
                    }
                }
            }
        }

        return any ? (minX, maxX, minY, maxY) : null;
    }

    // ---- rendering -------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Bounds.Size;
        if (size.Width < 10 || size.Height < 10) return;

        if (_home == default)
            _view = _home = ComputeHome(size);

        if (_geometry is null || _geometrySize != size)
        {
            _geometry = BuildGeometry(size);
            _geometrySize = size;
        }

        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty));
        var grid = new Pen(VisualTheme.Brush(this, "Brush.Divider", Brushes.LightGray), 1);
        var axis = new Pen(VisualTheme.Brush(this, "Brush.Text.Muted", Brushes.Gray), 1);
        var label = VisualTheme.Brush(this, "Brush.Text.Muted", Brushes.Gray);

        var labels = new List<(FormattedText Text, Point At)>();
        DrawGrid(context, size, grid, axis, label, typeface, labels);

        for (var i = 0; i < _geometry.Count; i++)
        {
            var (curve, geometry) = _geometry[i];
            if (_hidden[curve]) continue;
            var pen = new Pen(new SolidColorBrush(VisualTheme.Series(this, curve)), 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(null, pen, geometry);
        }

        // Labels last, on a backing of the plot colour, so a curve passing
        // through a tick number does not strike it out.
        var backing = VisualTheme.Brush(this, "Brush.Bg.Primary", Brushes.White) is ISolidColorBrush solid
            ? new SolidColorBrush(solid.Color, 0.82)
            : null;
        foreach (var (text, at) in labels)
        {
            if (backing is not null) context.DrawRectangle(backing, null, new Rect(at.X - 2, at.Y, text.Width + 4, text.Height), 2, 2);
            context.DrawText(text, at);
        }

        if (_hover is { } hover && _dragStart is null)
            DrawReadout(context, size, hover, typeface);
    }

    private void DrawGrid(DrawingContext context, Size size, IPen grid, IPen axis, IBrush label, Typeface typeface, List<(FormattedText, Point)> labels)
    {
        var (x0, x1, y0, y1) = _view;
        var xStep = _piTicks ? PiStep((x1 - x0) / Math.Max(2, size.Width / 90)) : VisualTheme.NiceStep((x1 - x0) / Math.Max(2, size.Width / 90));
        var yStep = VisualTheme.NiceStep((y1 - y0) / Math.Max(2, size.Height / 56));

        var origin = ToScreen(0, 0, size);
        var axisY = Math.Clamp(origin.Y, 0, size.Height);
        var axisX = Math.Clamp(origin.X, 0, size.Width);

        for (var x = Math.Ceiling(x0 / xStep) * xStep; x <= x1; x += xStep)
        {
            var sx = ToScreen(x, 0, size).X;
            context.DrawLine(grid, new Point(sx, 0), new Point(sx, size.Height));
            if (Math.Abs(x) < xStep * 1e-6) continue;
            var text = Label(_piTicks ? FormatPi(x) : VisualTheme.Format(x, xStep), label, typeface);
            var ty = axisY + 3 + text.Height > size.Height ? axisY - text.Height - 2 : axisY + 3;
            labels.Add((text, new Point(Math.Clamp(sx - text.Width / 2, 2, Math.Max(2, size.Width - text.Width - 2)), ty)));
        }

        for (var y = Math.Ceiling(y0 / yStep) * yStep; y <= y1; y += yStep)
        {
            var sy = ToScreen(0, y, size).Y;
            context.DrawLine(grid, new Point(0, sy), new Point(size.Width, sy));
            if (Math.Abs(y) < yStep * 1e-6) continue;
            var text = Label(VisualTheme.Format(y, yStep), label, typeface);
            var tx = axisX - text.Width - 4 < 0 ? axisX + 4 : axisX - text.Width - 4;
            labels.Add((text, new Point(tx, Math.Clamp(sy - text.Height / 2, 0, Math.Max(0, size.Height - text.Height)))));
        }

        if (origin.Y >= 0 && origin.Y <= size.Height)
            context.DrawLine(axis, new Point(0, origin.Y), new Point(size.Width, origin.Y));
        if (origin.X >= 0 && origin.X <= size.Width)
            context.DrawLine(axis, new Point(origin.X, 0), new Point(origin.X, size.Height));
        if (origin.X >= 0 && origin.X <= size.Width && origin.Y >= 0 && origin.Y <= size.Height)
        {
            var zero = Label("0", label, typeface);
            labels.Add((zero, new Point(origin.X - zero.Width - 4, origin.Y + 3)));
        }
    }

    private void DrawReadout(DrawingContext context, Size size, Point hover, Typeface typeface)
    {
        var (wx, wy) = ToWorld(hover, size);
        var lines = new List<(string Text, IBrush? Swatch)> { ($"x = {VisualTheme.Readout(wx)}   y = {VisualTheme.Readout(wy)}", null) };

        var v = (double[])_values.Clone();
        v[0] = wx;
        for (var i = 0; i < _spec.Curves.Count && lines.Count < 5; i++)
        {
            var curve = _spec.Curves[i];
            if (_hidden[i] || curve.Error is not null || curve.Kind != CurveKind.ExplicitY || curve.Fn is null) continue;
            var y = Safe(curve.Fn, v);
            if (!double.IsFinite(y)) continue;
            var brush = new SolidColorBrush(VisualTheme.Series(this, i));
            var point = ToScreen(wx, y, size);
            if (point.Y >= 0 && point.Y <= size.Height)
                context.DrawEllipse(brush, new Pen(VisualTheme.Brush(this, "Brush.Bg.Primary", Brushes.White), 1.5), point, 4, 4);
            lines.Add(($"{(string.IsNullOrWhiteSpace(curve.Label) ? "y" : curve.Label)} = {VisualTheme.Readout(y)}", brush));
        }

        var foreground = VisualTheme.Brush(this, "Brush.Text.Primary", Brushes.Black);
        var texts = lines.Select(l => (Text: Label(l.Text, foreground, typeface), l.Swatch)).ToList();
        var width = texts.Max(t => t.Text.Width + (t.Swatch is null ? 0 : 14)) + 16;
        var height = texts.Sum(t => t.Text.Height) + 10;
        var box = new Rect(size.Width - width - 8, 8, width, height);
        context.DrawRectangle(
            VisualTheme.Brush(this, "Brush.Bg.Elevated", Brushes.White),
            new Pen(VisualTheme.Brush(this, "Brush.Border", Brushes.LightGray), 1),
            box, 6, 6);

        var y0 = box.Y + 5;
        foreach (var (text, swatch) in texts)
        {
            var x = box.X + 8;
            if (swatch is not null)
            {
                context.DrawEllipse(swatch, null, new Point(x + 4, y0 + text.Height / 2), 3.5, 3.5);
                x += 14;
            }

            context.DrawText(text, new Point(x, y0));
            y0 += text.Height;
        }
    }

    private static FormattedText Label(string text, IBrush brush, Typeface typeface) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, brush);

    // ---- sampling ----------------------------------------------------------------

    private List<(int, StreamGeometry)> BuildGeometry(Size size)
    {
        var result = new List<(int, StreamGeometry)>();
        var v = (double[])_values.Clone();
        for (var i = 0; i < _spec.Curves.Count; i++)
        {
            var curve = _spec.Curves[i];
            if (curve.Error is not null || curve.Fn is null) continue;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                switch (curve.Kind)
                {
                    case CurveKind.ExplicitY: TraceExplicit(g, curve.Fn, v, size, alongX: true); break;
                    case CurveKind.ExplicitX: TraceExplicit(g, curve.Fn, v, size, alongX: false); break;
                    case CurveKind.Implicit: TraceImplicit(g, curve.Fn, v, size); break;
                    default: TraceParametric(g, curve, v, size); break;
                }
            }

            result.Add((i, geometry));
        }

        return result;
    }

    /// <summary>y = f(x) sampled per pixel column (or x = g(y) per row). A jump
    /// bigger than the view with a midpoint that does not sit between the two
    /// samples is an asymptote, not a steep stretch — lift the pen there
    /// instead of drawing the vertical line through infinity.</summary>
    private void TraceExplicit(StreamGeometryContext g, Func<double[], double> fn, double[] v, Size size, bool alongX)
    {
        var (x0, x1, y0, y1) = _view;
        var pixels = alongX ? size.Width : size.Height;
        var samples = (int)Math.Max(64, pixels * 1.5);
        var (from, to) = alongX ? (x0, x1) : (y0, y1);
        var span = alongX ? y1 - y0 : x1 - x0;
        var slot = alongX ? 0 : 1;

        var open = false;
        double prevT = 0, prevV = 0;
        for (var i = 0; i <= samples; i++)
        {
            var t = from + (to - from) * i / samples;
            v[slot] = t;
            var value = Safe(fn, v);
            if (!double.IsFinite(value))
            {
                if (open) { g.EndFigure(false); open = false; }
                continue;
            }

            if (open && Math.Abs(value - prevV) > span * 1.5)
            {
                v[slot] = (t + prevT) / 2;
                var mid = Safe(fn, v);
                var lo = Math.Min(value, prevV) - span * 0.1;
                var hi = Math.Max(value, prevV) + span * 0.1;
                if (!double.IsFinite(mid) || mid < lo || mid > hi)
                {
                    g.EndFigure(false);
                    open = false;
                }
            }

            var point = alongX ? ToScreen(t, value, size) : ToScreen(value, t, size);
            if (!open)
            {
                g.BeginFigure(point, false);
                open = true;
            }
            else
            {
                g.LineTo(point);
            }

            prevT = t;
            prevV = value;
        }

        if (open) g.EndFigure(false);
    }

    /// <summary>F(x, y) = 0 by marching squares on a 4px grid. A sign change
    /// whose edge midpoint is larger in magnitude than both ends is a pole
    /// (tan, 1/x), and is skipped rather than drawn as a line.</summary>
    private void TraceImplicit(StreamGeometryContext g, Func<double[], double> fn, double[] v, Size size)
    {
        const double cell = 4;
        var nx = (int)Math.Ceiling(size.Width / cell);
        var ny = (int)Math.Ceiling(size.Height / cell);
        var field = new double[nx + 1, ny + 1];
        var world = new (double X, double Y)[nx + 1, ny + 1];
        for (var i = 0; i <= nx; i++)
        for (var j = 0; j <= ny; j++)
        {
            var (wx, wy) = ToWorld(new Point(i * cell, j * cell), size);
            world[i, j] = (wx, wy);
            v[0] = wx;
            v[1] = wy;
            field[i, j] = Safe(fn, v);
        }

        Point? Crossing(int ai, int aj, int bi, int bj)
        {
            var a = field[ai, aj];
            var b = field[bi, bj];
            if (!double.IsFinite(a) || !double.IsFinite(b) || Math.Sign(a) == Math.Sign(b) || a == b) return null;
            var (ax, ay) = world[ai, aj];
            var (bx, by) = world[bi, bj];
            v[0] = (ax + bx) / 2;
            v[1] = (ay + by) / 2;
            var mid = Safe(fn, v);
            if (!double.IsFinite(mid) || Math.Abs(mid) > Math.Max(Math.Abs(a), Math.Abs(b))) return null;
            var t = a / (a - b);
            return new Point((ai + (bi - ai) * t) * cell, (aj + (bj - aj) * t) * cell);
        }

        var edges = new List<Point>(4);
        for (var i = 0; i < nx; i++)
        for (var j = 0; j < ny; j++)
        {
            edges.Clear();
            if (Crossing(i, j, i + 1, j) is { } top) edges.Add(top);
            if (Crossing(i + 1, j, i + 1, j + 1) is { } right) edges.Add(right);
            if (Crossing(i, j + 1, i + 1, j + 1) is { } bottom) edges.Add(bottom);
            if (Crossing(i, j, i, j + 1) is { } left) edges.Add(left);
            for (var k = 0; k + 1 < edges.Count; k += 2)
            {
                g.BeginFigure(edges[k], false);
                g.LineTo(edges[k + 1]);
                g.EndFigure(false);
            }
        }
    }

    private void TraceParametric(StreamGeometryContext g, PlotCurve curve, double[] v, Size size)
    {
        var range = curve.TMax - curve.TMin;
        var samples = (int)Math.Clamp(range / Math.Tau * 900, 400, 6000);
        var limit = 3 * Math.Max(size.Width, size.Height);
        var open = false;
        Point previous = default;
        for (var i = 0; i <= samples; i++)
        {
            var t = curve.TMin + range * i / samples;
            SetT(v, t);
            var (x, y) = PolarOrParametric(curve, v, t);
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                if (open) { g.EndFigure(false); open = false; }
                continue;
            }

            var point = ToScreen(x, y, size);
            if (open && (Math.Abs(point.X - previous.X) > limit || Math.Abs(point.Y - previous.Y) > limit))
            {
                g.EndFigure(false);
                open = false;
            }

            if (!open)
            {
                g.BeginFigure(point, false);
                open = true;
            }
            else
            {
                g.LineTo(point);
            }

            previous = point;
        }

        if (open) g.EndFigure(false);
    }

    private static (double X, double Y) PolarOrParametric(PlotCurve curve, double[] v, double t)
    {
        if (curve.Kind == CurveKind.Parametric && curve.FnY is not null)
            return (Safe(curve.Fn!, v), Safe(curve.FnY, v));
        var r = Safe(curve.Fn!, v);
        return (r * Math.Cos(t), r * Math.Sin(t));
    }

    private static void SetT(double[] v, double t)
    {
        v[2] = t;
        v[3] = t;
        v[4] = t;
    }

    private static double Safe(Func<double[], double> fn, double[] v)
    {
        try
        {
            return fn(v);
        }
        catch (ArithmeticException)
        {
            return double.NaN;
        }
    }

    // ---- π ticks -----------------------------------------------------------------

    private static bool IsPiMultiple(double value)
    {
        if (Math.Abs(value) < 1e-9) return true;
        var quarters = value / (Math.PI / 4);
        return Math.Abs(quarters - Math.Round(quarters)) < 1e-3;
    }

    private static double PiStep(double raw)
    {
        var quarter = Math.PI / 4;
        var multiple = Math.Pow(2, Math.Ceiling(Math.Log2(Math.Max(raw / quarter, 1))));
        return multiple * quarter;
    }

    private static string FormatPi(double value)
    {
        var quarters = (int)Math.Round(value / (Math.PI / 4));
        if (quarters == 0) return "0";
        var divisor = Gcd(Math.Abs(quarters), 4);
        var numerator = quarters / divisor;
        var denominator = 4 / divisor;
        var head = numerator switch { 1 => "π", -1 => "-π", _ => numerator + "π" };
        return denominator == 1 ? head : head + "/" + denominator;
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}
