using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MolaGPT.App.Rendering;

/// <summary>
/// The context gauge's ring: a track with an arc swept clockwise from twelve
/// o'clock for <see cref="Percent"/> of the way round.
///
/// Drawn rather than animated on purpose. This app runs a custom low-latency swap
/// chain that stops producing frames when idle, so an animated fill can freeze
/// partway and sit there misreporting the number — the one failure a gauge must not
/// have. Every repaint here is a direct consequence of the value changing.
/// </summary>
public sealed class ContextRing : Control
{
    public static readonly StyledProperty<double> PercentProperty =
        AvaloniaProperty.Register<ContextRing, double>(nameof(Percent));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<ContextRing, double>(nameof(Thickness), 2.5d);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ContextRing, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> ArcBrushProperty =
        AvaloniaProperty.Register<ContextRing, IBrush?>(nameof(ArcBrush));

    /// <summary>
    /// True while the reading is unknown — before the first reply, and in the gap
    /// after a compaction. Draws the track alone: an arc of length zero would look
    /// like a measurement of zero, which is the opposite of the truth at the moment
    /// a compaction has just happened.
    /// </summary>
    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<ContextRing, bool>(nameof(IsIndeterminate));

    static ContextRing()
    {
        AffectsRender<ContextRing>(
            PercentProperty,
            ThicknessProperty,
            TrackBrushProperty,
            ArcBrushProperty,
            IsIndeterminateProperty);
    }

    public double Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? ArcBrush
    {
        get => GetValue(ArcBrushProperty);
        set => SetValue(ArcBrushProperty, value);
    }

    public bool IsIndeterminate
    {
        get => GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var thickness = Math.Max(1d, Thickness);
        var radius = (size - thickness) / 2d;
        if (radius <= 0) return;

        var centre = new Point(Bounds.Width / 2d, Bounds.Height / 2d);

        if (TrackBrush is { } track)
        {
            context.DrawEllipse(null, new Pen(track, thickness), centre, radius, radius);
        }

        if (IsIndeterminate || ArcBrush is not { } arcBrush) return;

        var percent = Math.Clamp(Percent, 0d, 100d);
        if (percent <= 0d) return;

        var pen = new Pen(arcBrush, thickness, lineCap: PenLineCap.Round);

        // A full ring cannot be expressed as one arc — start and end coincide and
        // the sweep is ambiguous — so it is drawn as the ellipse it actually is.
        if (percent >= 99.9d)
        {
            context.DrawEllipse(null, pen, centre, radius, radius);
            return;
        }

        var (start, end, isLargeArc) = ArcGeometry(centre, radius, percent);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false);
            ctx.ArcTo(
                end,
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: isLargeArc,
                sweepDirection: SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    /// <summary>
    /// Where the arc begins, where it ends, and whether it takes the long way.
    ///
    /// Split out of <see cref="Render"/> so it can be checked directly: get the
    /// start angle or the sweep direction wrong and the control still draws, still
    /// throws nothing, and simply points at a different number than it was given —
    /// and the headless test platform this repo uses cannot photograph it to catch
    /// that (see TestApp.BuildAvaloniaApp).
    /// </summary>
    internal static (Point Start, Point End, bool IsLargeArc) ArcGeometry(
        Point centre,
        double radius,
        double percent)
    {
        var sweep = Math.Clamp(percent, 0d, 100d) / 100d * Math.PI * 2d;
        return (
            // Twelve o'clock.
            new Point(centre.X, centre.Y - radius),
            // Clockwise: +sin on x, −cos on y.
            new Point(
                centre.X + radius * Math.Sin(sweep),
                centre.Y - radius * Math.Cos(sweep)),
            sweep > Math.PI);
    }
}
