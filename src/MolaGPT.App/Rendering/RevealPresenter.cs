using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
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

    static RevealPresenter()
    {
        AffectsMeasure<RevealPresenter>(RevealProperty);
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

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;

        child.Measure(availableSize);
        var natural = child.DesiredSize;
        return new Size(natural.Width, natural.Height * Math.Clamp(Reveal, 0, 1));
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
        _running?.Cancel();
        _running?.Dispose();
        _running = null;

        if (!_settled || !IsAttachedToVisualTree())
        {
            Reveal = open ? 1d : 0d;
            IsVisible = open;
            return;
        }

        var cts = new CancellationTokenSource();
        _running = cts;

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

        try
        {
            await animation.RunAsync(this, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested || !ReferenceEquals(_running, cts)) return;

        Reveal = open ? 1d : 0d;
        IsVisible = open;
    }

    private bool IsAttachedToVisualTree() => VisualRoot is not null;
}
