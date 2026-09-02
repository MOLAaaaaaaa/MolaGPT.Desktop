using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Animates a panel of unknown height open and shut.
///
/// The obvious approach — animate <see cref="Layoutable.Height"/> — does not
/// work here, because the target is Auto: the content is markdown whose height
/// depends on the width it is given, and there is no number to animate towards
/// until after a measure pass.
///
/// So the animated value is a 0…1 fraction that <see cref="MeasureOverride"/>
/// multiplies the child's own desired height by. The child is always measured at
/// its natural size and always arranged at it; only the space this control
/// reports shrinks. That means the content never reflows during the animation —
/// it is revealed, not squashed — and the reveal works for any content without
/// anyone measuring it in advance.
/// </summary>
public sealed class RevealPresenter : Decorator
{
    private const double MaxLayoutAnimatedHeight = 1200;

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<RevealPresenter, bool>(nameof(IsOpen), true);

    /// <summary>Fraction of the child's height currently shown. Animated; not
    /// meant to be set directly except by this control.</summary>
    public static readonly StyledProperty<double> RevealProperty =
        AvaloniaProperty.Register<RevealPresenter, double>(nameof(Reveal), 1d);

    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<RevealPresenter, TimeSpan>(
            nameof(Duration), TimeSpan.FromMilliseconds(200));

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public double Reveal
    {
        get => GetValue(RevealProperty);
        set => SetValue(RevealProperty, value);
    }

    public TimeSpan Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    private CancellationTokenSource? _running;
    private bool _settled;
    private bool _fadeOnly;
    private Control? _cachedChild;
    private CacheMode? _previousChildCacheMode;

    internal string PerformanceLabel { get; set; } = "reveal";

    static RevealPresenter()
    {
        RevealProperty.Changed.AddClassHandler<RevealPresenter>((x, _) => x.OnRevealChanged());
        IsOpenProperty.Changed.AddClassHandler<RevealPresenter>((x, e) =>
            x.Animate(e.NewValue is true));
    }

    public RevealPresenter()
    {
        // Content taller than the reported height has to be cut off, or a
        // half-revealed panel paints over whatever is below it.
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // First attach jumps straight to the resting value: a row scrolling back
        // into view must not replay the animation.
        if (_settled) return;
        _settled = true;
        Reveal = IsOpen ? 1d : 0d;
        IsVisible = IsOpen;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        var running = _running;
        _running = null;
        running?.Cancel();
        RestoreAnimationCache();
        AnimationPerformanceTrace.EndReveal(this, Reveal, "detached");
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var traceStarted = AnimationPerformanceTrace.Timestamp();
        if (Child is not { } child) return default;

        child.Measure(availableSize);
        var natural = child.DesiredSize;
        var fadeOnly = natural.Height > MaxLayoutAnimatedHeight;
        if (_fadeOnly != fadeOnly)
        {
            _fadeOnly = fadeOnly;
            if (!fadeOnly) child.Opacity = 1;
        }

        if (_running is not null && !_fadeOnly)
            EnableAnimationCache(child);
        else
            RestoreAnimationCache();

        var reveal = Math.Clamp(Reveal, 0, 1);
        if (_fadeOnly) child.Opacity = reveal;

        // Animating the height of a body several viewports tall recalculates the
        // transcript extent on every tick while its lower edge is not even on
        // screen. Such bodies occupy their final space once and use a cheap fade;
        // ordinary cards retain the full height-reveal motion.
        var height = _fadeOnly
            ? IsOpen || reveal > 0 ? natural.Height : 0
            : natural.Height * reveal;

        AnimationPerformanceTrace.SetRevealMode(this, _fadeOnly, natural.Height);
        AnimationPerformanceTrace.RevealMeasureFinished(this, traceStarted);
        return new Size(natural.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { } child) return finalSize;

        // Arranged at full height even while partly revealed, so the text inside
        // keeps its final line breaks from the first frame instead of reflowing
        // as the panel opens.
        child.Arrange(new Rect(0, 0, finalSize.Width, child.DesiredSize.Height));
        return finalSize;
    }

    private async void Animate(bool open)
    {
        var previous = _running;
        _running = null;
        previous?.Cancel();

        if (!_settled || !IsAttachedToVisualTree())
        {
            Reveal = open ? 1d : 0d;
            IsVisible = open;
            RestoreAnimationCache();
            AnimationPerformanceTrace.EndReveal(this, Reveal, "not-attached");
            return;
        }

        var cts = new CancellationTokenSource();
        _running = cts;
        AnimationPerformanceTrace.BeginReveal(
            this,
            PerformanceLabel,
            open,
            Reveal,
            Duration);

        // Visible for the whole of an opening animation, and only hidden once a
        // closing one has finished — otherwise there is nothing to animate.
        if (open) IsVisible = true;

        var animation = new Animation
        {
            Duration = Duration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(RevealProperty, Math.Clamp(Reveal, 0, 1)) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(RevealProperty, open ? 1d : 0d) }
                }
            }
        };

        var reason = "cancelled";
        try
        {
            await animation.RunAsync(this, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_running, cts)) return;

            Reveal = open ? 1d : 0d;
            IsVisible = open;
            reason = "settled";
        }
        catch (OperationCanceledException)
        {
            // A new direction owns the cache and trace from this point onward.
        }
        finally
        {
            if (ReferenceEquals(_running, cts))
            {
                _running = null;
                RestoreAnimationCache();
                AnimationPerformanceTrace.EndReveal(this, Reveal, reason);
            }

            cts.Dispose();
        }
    }

    private void OnRevealChanged()
    {
        AnimationPerformanceTrace.RevealFrame(this);
        if (_fadeOnly)
        {
            if (Child is { } child) child.Opacity = Math.Clamp(Reveal, 0, 1);
            return;
        }

        InvalidateMeasure();
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

    private bool IsAttachedToVisualTree() => VisualRoot is not null;
}

/// <summary>
/// The other half of the transcript's folding: a tool card's raw JSON payload,
/// kept whole but shown short.
///
/// Nothing upstream shortens the payload any more. The cut copy was also the
/// copy persisted with the conversation, so trimming it there lost the tail of
/// every search and every directory listing for good. Bounding the room it takes
/// on screen is the actual requirement, and it belongs here.
///
/// The crop lands on a line boundary. A half-drawn last row is what made the
/// fixed <c>MaxHeight</c> this replaces look like a rendering bug.
/// </summary>
public sealed class RawPayloadView : Control
{
    private const double Budget = 180;
    private const double Gap = 6;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<RawPayloadView, string?>(nameof(Text));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<RawPayloadView, bool>(
            nameof(IsExpanded), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    private readonly SelectableTextBlock _body = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Crop _crop;
    private readonly Button _toggle;
    private double _cropHeight;

    static RawPayloadView()
    {
        TextProperty.Changed.AddClassHandler<RawPayloadView>((x, e) => x._body.Text = e.NewValue as string);
        AffectsMeasure<RawPayloadView>(IsExpandedProperty);
    }

    public RawPayloadView()
    {
        _crop = new Crop { Child = _body, ClipToBounds = true };

        _toggle = new Button { HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
        _toggle.Classes.Add("rawtoggle");
        _toggle.Click += (_, _) => IsExpanded = !IsExpanded;

        LogicalChildren.Add(_crop);
        LogicalChildren.Add(_toggle);
        VisualChildren.Add(_crop);
        VisualChildren.Add(_toggle);
    }

    /// <summary>The mono face is the same in both themes, so it is pulled once.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (this.TryFindResource("Font.Mono", ActualThemeVariant, out var mono) && mono is FontFamily family)
            _body.FontFamily = family;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _crop.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var full = _crop.DesiredSize;

        // A payload that fits gets no toggle: an affordance for content already
        // fully on screen is noise.
        if (full.Height <= Budget + 0.5)
        {
            _toggle.IsVisible = false;
            _cropHeight = full.Height;
            return full;
        }

        var lines = string.IsNullOrEmpty(_body.Text) ? null : _body.TextLayout.TextLines;
        _cropHeight = IsExpanded ? full.Height : WholeLines(lines);

        _toggle.IsVisible = true;
        _toggle.Content = IsExpanded ? "收起" : $"显示全部 {lines?.Count ?? 0} 行";
        _toggle.Measure(availableSize);

        return new Size(full.Width, _cropHeight + Gap + _toggle.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _crop.Arrange(new Rect(0, 0, finalSize.Width, Math.Min(_cropHeight, finalSize.Height)));

        if (_toggle.IsVisible)
            _toggle.Arrange(new Rect(0, _cropHeight + Gap, finalSize.Width, _toggle.DesiredSize.Height));

        return finalSize;
    }

    /// <summary>The tallest run of whole lines still inside the budget.</summary>
    private static double WholeLines(IReadOnlyList<TextLine>? lines)
    {
        if (lines is null) return Budget;

        var height = 0d;
        foreach (var line in lines)
        {
            if (height > 0 && height + line.Height > Budget) break;
            height += line.Height;
        }

        return height > 0 ? height : Budget;
    }

    /// <summary>
    /// Draws its child at the child's own height, cut to whatever height it is
    /// given. A plain <see cref="Decorator"/> arranges the child into the short
    /// rect instead, and a <see cref="TextBlock"/> handed a short rect drops
    /// lines rather than letting them be clipped.
    /// </summary>
    private sealed class Crop : Decorator
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            if (Child is not { } child) return default;
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            return child.DesiredSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Child is { } child)
                child.Arrange(new Rect(0, 0, finalSize.Width, Math.Max(finalSize.Height, child.DesiredSize.Height)));

            return finalSize;
        }
    }
}
