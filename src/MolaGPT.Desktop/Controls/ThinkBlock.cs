using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MolaGPT.Desktop.Controls;

/// <summary>
/// Renders a model's chain-of-thought ("思考过程") as a fold-able card with
/// a left brand stripe, an animated status header (脉动 / 静态), and a
/// nested <see cref="MarkdownPresenter"/> for the body.
///
/// Layout:
/// <code>
/// ┌ ▌ ● 思考中… 12.3 s        ▾ │
/// │   (markdown body)            │
/// └──────────────────────────────┘
/// </code>
///
/// Four dependency properties:
///   - <see cref="Source"/>         the thinking markdown text (streamed)
///   - <see cref="IsThinking"/>     true while the model is still emitting
///                                  reasoning chunks; flips to false the
///                                  first time normal content arrives.
///   - <see cref="ElapsedSeconds"/> wall-clock seconds since the first
///                                  reasoning chunk arrived; ticks while
///                                  IsThinking is true, frozen when done.
///   - <see cref="IsExpanded"/>     fold/unfold state. Defaults to true while
///                                  thinking, auto-folds when done so the
///                                  finalized answer is the first thing the
///                                  user sees.
///
/// Folding uses a cubic-bezier(0.4,0,0.2,1) 260ms storyboard on
/// <see cref="FrameworkElement.MaxHeight"/> + <see cref="UIElement.Opacity"/>
/// for a smooth expand/collapse transition.
/// </summary>
public sealed class ThinkBlock : Control
{
    private const double ExpandedAnimationMaxHeight = 1200;

    static ThinkBlock()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ThinkBlock), new FrameworkPropertyMetadata(typeof(ThinkBlock)));
    }

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(ThinkBlock),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsThinkingProperty = DependencyProperty.Register(
        nameof(IsThinking), typeof(bool), typeof(ThinkBlock),
        new PropertyMetadata(false, OnIsThinkingChanged));

    public static readonly DependencyProperty ElapsedSecondsProperty = DependencyProperty.Register(
        nameof(ElapsedSeconds), typeof(double), typeof(ThinkBlock),
        new PropertyMetadata(0.0));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(ThinkBlock),
        new PropertyMetadata(true, OnIsExpandedChanged));

    public string Source
    {
        get => (string)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsThinking
    {
        get => (bool)GetValue(IsThinkingProperty);
        set => SetValue(IsThinkingProperty, value);
    }

    public double ElapsedSeconds
    {
        get => (double)GetValue(ElapsedSecondsProperty);
        set => SetValue(ElapsedSecondsProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    private FrameworkElement? _bodyHost;
    private FrameworkElement? _arrow;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _bodyHost = GetTemplateChild("PART_Body") as FrameworkElement;
        _arrow = GetTemplateChild("PART_Arrow") as FrameworkElement;
        EnsureCopyContextMenu();
        UpdateExpandedVisualState(animate: false);

        if (GetTemplateChild("PART_HeaderToggle") is ButtonBase toggle)
        {
            toggle.Click -= OnHeaderToggleClick;
            toggle.Click += OnHeaderToggleClick;
        }
    }

    private void OnHeaderToggleClick(object sender, RoutedEventArgs e) => IsExpanded = !IsExpanded;

    private void EnsureCopyContextMenu()
    {
        if (ContextMenu is not null) return;

        var copyItem = new MenuItem { Header = "复制思考内容" };
        copyItem.Click += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(Source))
                Clipboard.SetText(Source.Trim());
            e.Handled = true;
        };

        var menu = new ContextMenu();
        menu.Opened += (_, _) => copyItem.IsEnabled = !string.IsNullOrWhiteSpace(Source);
        menu.Items.Add(copyItem);
        ContextMenu = menu;
    }

    private static void OnIsThinkingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // When thinking finishes, auto-collapse so the answer is the first
        // thing the user sees. The user can re-expand via the chevron.
        if (d is ThinkBlock tb && e.NewValue is false && e.OldValue is true)
        {
            tb.IsExpanded = false;
        }
    }

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThinkBlock tb) tb.UpdateExpandedVisualState(animate: true);
    }

    private void UpdateExpandedVisualState(bool animate)
    {
        if (_bodyHost is null) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(animate ? 260 : 0);
        var targetOpacity = IsExpanded ? 1 : 0;

        if (!animate)
        {
            _bodyHost.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            _bodyHost.BeginAnimation(UIElement.OpacityProperty, null);
            _bodyHost.MaxHeight = IsExpanded ? double.PositiveInfinity : 0;
            _bodyHost.Opacity = targetOpacity;
            UpdateArrow(angle: IsExpanded ? 90 : 0, duration: dur, ease);
            return;
        }

        // MaxHeight defaults to Infinity, which cannot be used as an animation
        // value. Animate with a finite cap, then release it so long reasoning
        // blocks participate in the outer ScrollViewer instead of being clipped.
        var currentMaxHeight = double.IsInfinity(_bodyHost.MaxHeight)
            ? Math.Max(_bodyHost.ActualHeight, ExpandedAnimationMaxHeight)
            : _bodyHost.MaxHeight;
        var targetMaxHeight = IsExpanded ? ExpandedAnimationMaxHeight : 0;
        if (IsExpanded && currentMaxHeight > ExpandedAnimationMaxHeight)
        {
            currentMaxHeight = 0;
        }

        _bodyHost.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        _bodyHost.BeginAnimation(UIElement.OpacityProperty, null);
        _bodyHost.MaxHeight = currentMaxHeight;

        var bodyHost = _bodyHost;
        var maxHeightAnimation = new DoubleAnimation
        {
            From = currentMaxHeight,
            To = targetMaxHeight,
            Duration = dur,
            EasingFunction = ease
        };
        maxHeightAnimation.Completed += (_, _) =>
        {
            if (!ReferenceEquals(bodyHost, _bodyHost)) return;
            bodyHost.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            bodyHost.MaxHeight = IsExpanded ? double.PositiveInfinity : 0;
        };

        _bodyHost.BeginAnimation(
            FrameworkElement.MaxHeightProperty,
            maxHeightAnimation);
        _bodyHost.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation
            {
                From = _bodyHost.Opacity,
                To = targetOpacity,
                Duration = dur,
                EasingFunction = ease
            });

        UpdateArrow(angle: IsExpanded ? 90 : 0, duration: dur, ease);
    }

    private void UpdateArrow(double angle, Duration duration, IEasingFunction ease)
    {
        if (_arrow is not null)
        {
            if (_arrow.RenderTransform is not RotateTransform rt)
            {
                rt = new RotateTransform(0);
                _arrow.RenderTransformOrigin = new Point(0.5, 0.5);
                _arrow.RenderTransform = rt;
            }
            rt.BeginAnimation(
                RotateTransform.AngleProperty,
                new DoubleAnimation
                {
                    To = angle,
                    Duration = duration,
                    EasingFunction = ease
                });
        }
    }
}

/// <summary>
/// Converts (IsThinking, ElapsedSeconds) → display string for the ThinkBlock
/// header. Used as a MultiBinding converter inside Generic.xaml.
/// </summary>
public sealed class ThinkStatusConverter : IMultiValueConverter
{
    public static readonly ThinkStatusConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool thinking = values.Length > 0 && values[0] is bool b && b;
        double secs = values.Length > 1 && values[1] is double d ? d : 0;
        if (thinking)
            return string.Format(CultureInfo.InvariantCulture, "思考中… {0:0.0} s", secs);
        if (secs > 0)
            return string.Format(CultureInfo.InvariantCulture, "思考已完成 · 用时 {0:0.0} 秒", secs);
        return "思考已完成";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
