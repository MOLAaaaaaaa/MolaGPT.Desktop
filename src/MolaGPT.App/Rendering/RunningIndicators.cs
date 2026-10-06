using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Reactive;
using Avalonia.VisualTree;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Drives a looping "still running" motion on the render clock, and only while
/// its owner is attached and effectively visible — a spinner in a hidden panel
/// or a recycled transcript row asks for no frames. Phase comes from one shared
/// clock, so indicators on screen together move in step and a stall resumes at
/// the right place instead of replaying what it missed.
/// </summary>
internal sealed class FrameLoop
{
    private static readonly long Epoch = Stopwatch.GetTimestamp();

    /// <summary>The clock every tick reads, in seconds.</summary>
    public static double Now => Stopwatch.GetElapsedTime(Epoch).TotalSeconds;

    private readonly Visual _owner;
    private readonly Action<double> _tick;
    private readonly Action<TimeSpan> _frame;
    private readonly List<IDisposable> _visibility = new();
    private bool _active;
    private bool _queued;

    public FrameLoop(Visual owner, Action<double> tick)
    {
        _owner = owner;
        _tick = tick;
        _frame = OnFrame;
        owner.AttachedToVisualTree += (_, _) => WatchVisibility();
        owner.DetachedFromVisualTree += (_, _) => UnwatchVisibility();
        if (owner.IsAttachedToVisualTree()) WatchVisibility();
    }

    // Visual.IsEffectivelyVisibleChanged is internal, so the chain is watched
    // directly: any ancestor showing again has to restart the frames.
    private void WatchVisibility()
    {
        UnwatchVisibility();
        var observer = new AnonymousObserver<bool>(_ => Update());
        for (Visual? visual = _owner; visual is not null; visual = visual.GetVisualParent())
            _visibility.Add(visual.GetObservable(Visual.IsVisibleProperty).Subscribe(observer));
    }

    private void UnwatchVisibility()
    {
        foreach (var subscription in _visibility) subscription.Dispose();
        _visibility.Clear();
    }

    public bool IsActive
    {
        get => _active;
        set
        {
            _active = value;
            Update();
        }
    }

    private bool ShouldRun => _active && _owner.IsEffectivelyVisible && _owner.IsAttachedToVisualTree();

    private void Update()
    {
        if (!ShouldRun) return;
        _tick(Now);
        Request();
    }

    // A queued callback cannot be withdrawn; it simply finds the loop stopped.
    private void Request()
    {
        if (_queued || TopLevel.GetTopLevel(_owner) is not { } topLevel) return;
        _queued = true;
        topLevel.RequestAnimationFrame(_frame);
    }

    private void OnFrame(TimeSpan _)
    {
        _queued = false;
        if (!ShouldRun) return;
        _tick(Now);
        Request();
    }
}

/// <summary>
/// A faint ring with a quarter arc turning once a second — the web client's
/// loading spinner. Drawn at a legible angle from the first frame, so a stalled
/// UI thread still leaves something that reads as busy.
/// </summary>
public sealed class RingSpinner : Control
{
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<RingSpinner, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> ArcBrushProperty =
        AvaloniaProperty.Register<RingSpinner, IBrush?>(nameof(ArcBrush));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<RingSpinner, double>(nameof(Thickness), 2d);

    private double _angle;

    static RingSpinner()
    {
        AffectsRender<RingSpinner>(TrackBrushProperty, ArcBrushProperty, ThicknessProperty);
    }

    public RingSpinner()
    {
        _ = new FrameLoop(this, seconds =>
        {
            _angle = seconds % 1d * 360d;
            InvalidateVisual();
        }) { IsActive = true };
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

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var thickness = Math.Max(1d, Thickness);
        var radius = (Math.Min(Bounds.Width, Bounds.Height) - thickness) / 2d;
        if (radius <= 0) return;

        var centre = new Point(Bounds.Width / 2d, Bounds.Height / 2d);
        if (TrackBrush is { } track)
            context.DrawEllipse(null, new Pen(track, thickness), centre, radius, radius);
        if (ArcBrush is not { } arc) return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointAt(centre, radius, _angle - 45d), isFilled: false);
            ctx.ArcTo(PointAt(centre, radius, _angle + 45d), new Size(radius, radius),
                rotationAngle: 0, isLargeArc: false, sweepDirection: SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(arc, thickness), geometry);
    }

    // Degrees clockwise from twelve o'clock.
    private static Point PointAt(Point centre, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180d;
        return new Point(centre.X + radius * Math.Sin(radians), centre.Y - radius * Math.Cos(radians));
    }
}

/// <summary>
/// A highlight that sweeps across content while its work is in progress, on
/// ChatGPT's timing: two seconds, CSS <c>ease</c>, so it crosses quickly and then
/// rests off the end. The band drops the content to a quarter opacity, which over
/// any background reads as it pressed three quarters of the way into it — the
/// same in either theme and on a hovered or tinted row.
///
/// The band is a fixed width, so a long row and a short label sweep alike. It
/// travels the element's bounds; put it on an element sized to its content, or
/// the band spends part of its pass over empty space.
/// </summary>
public static class Shimmer
{
    public static readonly AttachedProperty<bool> IsActiveProperty =
        AvaloniaProperty.RegisterAttached<Visual, bool>("IsActive", typeof(Shimmer));

    private static readonly AttachedProperty<ShimmerState?> StateProperty =
        AvaloniaProperty.RegisterAttached<Visual, ShimmerState?>("State", typeof(Shimmer));

    static Shimmer()
    {
        IsActiveProperty.Changed.AddClassHandler<Visual>((visual, e) =>
        {
            var active = e.GetNewValue<bool>();
            var state = visual.GetValue(StateProperty);
            if (state is null)
            {
                if (!active) return;
                state = new ShimmerState(visual);
                visual.SetValue(StateProperty, state);
            }
            state.SetActive(active);
        });
    }

    public static bool GetIsActive(Visual visual) => visual.GetValue(IsActiveProperty);
    public static void SetIsActive(Visual visual, bool value) => visual.SetValue(IsActiveProperty, value);

    private sealed class ShimmerState
    {
        private const double Period = 2d;
        private const double Band = 120d;
        private static readonly KeySpline Ease = new(0.25, 0.1, 0.25, 1.0);

        private static readonly ImmutableGradientStop[] Stops =
        [
            new(0, Colors.White),
            new(0.4, Color.FromArgb(64, 255, 255, 255)),
            new(0.6, Color.FromArgb(64, 255, 255, 255)),
            new(1, Colors.White)
        ];

        private readonly Visual _owner;
        private readonly FrameLoop _loop;

        public ShimmerState(Visual owner)
        {
            _owner = owner;
            _loop = new FrameLoop(owner, Apply);
        }

        public void SetActive(bool active)
        {
            if (active)
            {
                _loop.IsActive = true;
            }
            else
            {
                // Cleared at once, not left to the last frame: a frozen pass would
                // keep part of a finished label faded.
                _loop.IsActive = false;
                _owner.ClearValue(Visual.OpacityMaskProperty);
            }
        }

        // The band enters wholly before the left edge and leaves wholly past the
        // right one. A new brush each frame: the compositor snapshots the mask when
        // it is assigned, so moving the points of the same instance never reaches
        // the screen.
        private void Apply(double seconds)
        {
            var progress = Ease.GetSplineProgress(seconds % Period / Period);
            var start = -Band + (_owner.Bounds.Width + Band) * progress;
            _owner.OpacityMask = new ImmutableLinearGradientBrush(Stops, 1, null, null, GradientSpreadMethod.Pad,
                new RelativePoint(start, 0, RelativeUnit.Absolute),
                new RelativePoint(start + Band, 0, RelativeUnit.Absolute));
        }
    }
}

/// <summary>
/// A green wash that fades from a row whose work just finished. The sweep that
/// marked it running stops where the eye already is, so the news has to land
/// there too, not only in a status glyph at the far end of the row.
///
/// <see cref="SinceProperty"/> is when the work finished on the
/// <see cref="FrameLoop.Now"/> clock, so a row recycled mid-fade picks up where
/// it was and one first shown afterwards shows nothing. The background is set
/// each frame and cleared at the end rather than animated: a stalled animation
/// would leave the row tinted.
/// </summary>
public static class CompletionFlash
{
    public static readonly AttachedProperty<double> SinceProperty =
        AvaloniaProperty.RegisterAttached<Border, double>("Since", typeof(CompletionFlash), double.NaN);

    private static readonly AttachedProperty<FlashState?> StateProperty =
        AvaloniaProperty.RegisterAttached<Border, FlashState?>("State", typeof(CompletionFlash));

    static CompletionFlash()
    {
        SinceProperty.Changed.AddClassHandler<Border>((border, e) =>
        {
            var since = e.GetNewValue<double>();
            var state = border.GetValue(StateProperty);
            if (state is null)
            {
                if (double.IsNaN(since)) return;
                state = new FlashState(border);
                border.SetValue(StateProperty, state);
            }
            state.Start(since);
        });
    }

    public static double GetSince(Border border) => border.GetValue(SinceProperty);
    public static void SetSince(Border border, double value) => border.SetValue(SinceProperty, value);

    private sealed class FlashState
    {
        private const double Duration = 1.1d;
        private static readonly KeySpline EaseOut = new(0, 0, 0.58, 1);

        private readonly Border _owner;
        private readonly FrameLoop _loop;
        private double _since = double.NaN;

        public FlashState(Border owner)
        {
            _owner = owner;
            _loop = new FrameLoop(owner, Apply);
        }

        public void Start(double since)
        {
            _since = since;
            if (double.IsNaN(since)) Stop();
            else _loop.IsActive = true;
        }

        private void Apply(double seconds)
        {
            var elapsed = seconds - _since;
            if (elapsed is < 0 or >= Duration
                || !_owner.TryFindResource("Color.Success.Surface", _owner.ActualThemeVariant, out var value)
                || value is not Color color)
            {
                Stop();
                return;
            }
            var strength = 1 - EaseOut.GetSplineProgress(elapsed / Duration);
            _owner.Background = new ImmutableSolidColorBrush(color, strength);
        }

        private void Stop()
        {
            _loop.IsActive = false;
            _owner.ClearValue(Border.BackgroundProperty);
        }
    }
}
