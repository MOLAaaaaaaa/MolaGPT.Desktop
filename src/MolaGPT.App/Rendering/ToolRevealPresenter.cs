using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MolaGPT.App.Rendering;

// UI time, rather than the compositor's completion event, owns the resting value.
// Steps ride the render clock: a Win32 DispatcherTimer snaps to the 15.6 ms system
// tick and lands at an uneven 16/31 ms. The one-shot timer only settles the value
// if frames stop arriving.
internal sealed class ToolMotion : IDisposable
{
    private static readonly TimeSpan SettleGrace = TimeSpan.FromMilliseconds(100);

    private readonly Visual _owner;
    private readonly Action<double> _apply;
    private readonly Action<TimeSpan> _frame;
    private readonly DispatcherTimer _settle = new();
    private long _started;
    private double _from;
    private double _to;
    private double _milliseconds;
    private bool _frameQueued;

    public ToolMotion(Visual owner, Action<double> apply)
    {
        _owner = owner;
        _apply = apply;
        _frame = OnFrame;
        _settle.Tick += OnSettle;
    }

    public bool IsRunning { get; private set; }

    public void Start(double from, double to, TimeSpan duration)
    {
        Stop();
        _from = from;
        _to = to;
        _milliseconds = duration.TotalMilliseconds;
        if (_milliseconds <= 0 || Math.Abs(from - to) < 0.001 || TopLevel.GetTopLevel(_owner) is not { } topLevel)
        {
            _apply(to);
            return;
        }
        _started = Stopwatch.GetTimestamp();
        IsRunning = true;
        _apply(from);
        _settle.Interval = duration + SettleGrace;
        _settle.Start();
        RequestFrame(topLevel);
    }

    public void Stop()
    {
        IsRunning = false;
        _settle.Stop();
    }

    // A queued callback cannot be withdrawn; a restart reuses it instead of queueing another.
    private void RequestFrame(TopLevel topLevel)
    {
        if (_frameQueued) return;
        _frameQueued = true;
        topLevel.RequestAnimationFrame(_frame);
    }

    private void OnFrame(TimeSpan _)
    {
        _frameQueued = false;
        if (!IsRunning) return;
        var progress = Math.Clamp(Stopwatch.GetElapsedTime(_started).TotalMilliseconds / _milliseconds, 0, 1);
        if (progress >= 1 || TopLevel.GetTopLevel(_owner) is not { } topLevel)
        {
            Finish();
            return;
        }
        var eased = 1 - Math.Pow(1 - progress, 3);
        _apply(_from + (_to - _from) * eased);
        if (IsRunning) RequestFrame(topLevel);
    }

    private void OnSettle(object? sender, EventArgs e)
    {
        if (IsRunning) Finish();
        else _settle.Stop();
    }

    private void Finish()
    {
        Stop();
        _apply(_to);
    }

    public void Dispose()
    {
        Stop();
        _settle.Tick -= OnSettle;
    }
}

public sealed class ToolRevealPresenter : Decorator
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<ToolRevealPresenter, bool>(nameof(IsOpen), true);
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<ToolRevealPresenter, TimeSpan>(nameof(Duration), TimeSpan.FromMilliseconds(200));

    private readonly ToolMotion _motion;
    private double _reveal = 1;
    private bool _attached;
    private Control? _cachedChild;
    private CacheMode? _previousChildCacheMode;
    public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public TimeSpan Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }

    public ToolRevealPresenter()
    {
        ClipToBounds = true;
        _motion = new ToolMotion(this, SetReveal);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsOpenProperty) return;
        if (!_attached)
        {
            SetReveal(IsOpen ? 1 : 0);
            return;
        }
        if (IsOpen) IsVisible = true;
        _motion.Start(_reveal, IsOpen ? 1 : 0, Duration);
    }

    public void AnimateEntrance()
    {
        if (!_attached || !IsOpen) return;
        IsVisible = true;
        _motion.Start(0, 1, TimeSpan.FromMilliseconds(120));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        SetReveal(IsOpen ? 1 : 0);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _motion.Stop();
        SetReveal(IsOpen ? 1 : 0);
        base.OnDetachedFromVisualTree(e);
    }

    private void SetReveal(double value)
    {
        _reveal = Math.Clamp(value, 0, 1);
        IsVisible = IsOpen || _reveal > 0;

        // Rasterized once while the height moves; the text inside does not change.
        if (_motion.IsRunning && Child is { } child) EnableAnimationCache(child);
        else RestoreAnimationCache();

        InvalidateMeasure();
        InvalidateVisual();
    }

    private void EnableAnimationCache(Control child)
    {
        if (ReferenceEquals(_cachedChild, child)) return;
        RestoreAnimationCache();
        _cachedChild = child;
        _previousChildCacheMode = child.CacheMode;
        child.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
    }

    private void RestoreAnimationCache()
    {
        if (_cachedChild is null) return;
        _cachedChild.CacheMode = _previousChildCacheMode;
        _cachedChild = null;
        _previousChildCacheMode = null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;
        child.Measure(availableSize);
        return new Size(child.DesiredSize.Width, child.DesiredSize.Height * _reveal);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, finalSize.Width, Child.DesiredSize.Height));
        return finalSize;
    }
}

public sealed class ToolChevron : TextBlock
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<ToolChevron, bool>(nameof(IsExpanded));
    public static readonly StyledProperty<double> ClosedAngleProperty =
        AvaloniaProperty.Register<ToolChevron, double>(nameof(ClosedAngle));
    public static readonly StyledProperty<double> OpenAngleProperty =
        AvaloniaProperty.Register<ToolChevron, double>(nameof(OpenAngle), 180);
    private readonly RotateTransform _rotation = new();
    private readonly ToolMotion _motion;
    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public double ClosedAngle { get => GetValue(ClosedAngleProperty); set => SetValue(ClosedAngleProperty, value); }
    public double OpenAngle { get => GetValue(OpenAngleProperty); set => SetValue(OpenAngleProperty, value); }
    private double Target => IsExpanded ? OpenAngle : ClosedAngle;

    public ToolChevron()
    {
        Text = "";
        Classes.Add("icon");
        FontSize = 10;
        RenderTransformOrigin = RelativePoint.Center;
        RenderTransform = _rotation;
        _motion = new ToolMotion(this, value => _rotation.Angle = value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsExpandedProperty && change.Property != OpenAngleProperty && change.Property != ClosedAngleProperty) return;
        if (this.IsAttachedToVisualTree()) _motion.Start(_rotation.Angle, Target, TimeSpan.FromMilliseconds(140));
        else _rotation.Angle = Target;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _rotation.Angle = Target;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _motion.Stop();
        _rotation.Angle = Target;
        base.OnDetachedFromVisualTree(e);
    }
}
