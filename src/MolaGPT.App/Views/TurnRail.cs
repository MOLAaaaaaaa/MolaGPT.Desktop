using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;

namespace MolaGPT.App.Views;

/// <summary>
/// The outline of a long conversation along the transcript's left edge: one
/// tick per user turn, the turn being read drawn stronger. Pointing at a tick
/// previews its prompt; a click jumps there.
///
/// One control drawing every tick rather than a button per turn. A conversation
/// can run to hundreds of turns, and the turn under the pointer is found from the
/// pointer's height, so ticks packed tighter than a pointer can aim still resolve
/// to exactly one turn.
/// </summary>
public sealed class TurnRail : Control, ICustomHitTest
{
    /// <summary>Tick spacing while the rail fits; it tightens when it would not.</summary>
    private const double PreferredPitch = 10;
    private const double PadY = 8;
    private const double TickInset = 5;
    private const double TickWidth = 8;
    private const double TickWidthLit = 14;
    private const double TickHeight = 2;
    private const double IdleOpacity = 0.4;
    private const double LiveOpacity = 0.65;

    public static readonly StyledProperty<int> CountProperty =
        AvaloniaProperty.Register<TurnRail, int>(nameof(Count));
    public static readonly StyledProperty<int> ActiveIndexProperty =
        AvaloniaProperty.Register<TurnRail, int>(nameof(ActiveIndex), -1);
    public static readonly StyledProperty<IBrush?> TickBrushProperty =
        AvaloniaProperty.Register<TurnRail, IBrush?>(nameof(TickBrush));
    public static readonly StyledProperty<IBrush?> ActiveBrushProperty =
        AvaloniaProperty.Register<TurnRail, IBrush?>(nameof(ActiveBrush));
    public static readonly StyledProperty<IBrush?> HoverBrushProperty =
        AvaloniaProperty.Register<TurnRail, IBrush?>(nameof(HoverBrush));
    public static readonly StyledProperty<IBrush?> HoverBackgroundProperty =
        AvaloniaProperty.Register<TurnRail, IBrush?>(nameof(HoverBackground));

    public int Count { get => GetValue(CountProperty); set => SetValue(CountProperty, value); }
    public int ActiveIndex { get => GetValue(ActiveIndexProperty); set => SetValue(ActiveIndexProperty, value); }
    public IBrush? TickBrush { get => GetValue(TickBrushProperty); set => SetValue(TickBrushProperty, value); }
    public IBrush? ActiveBrush { get => GetValue(ActiveBrushProperty); set => SetValue(ActiveBrushProperty, value); }
    public IBrush? HoverBrush { get => GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }
    public IBrush? HoverBackground { get => GetValue(HoverBackgroundProperty); set => SetValue(HoverBackgroundProperty, value); }

    /// <summary>The turn under the pointer, or -1.</summary>
    public int HoveredIndex { get; private set; } = -1;

    /// <summary>Raised when <see cref="HoveredIndex"/> changes, -1 included.</summary>
    public event EventHandler<int>? HoverChanged;

    public event EventHandler<int>? TurnInvoked;

    static TurnRail()
    {
        AffectsMeasure<TurnRail>(CountProperty);
        AffectsRender<TurnRail>(CountProperty, ActiveIndexProperty, TickBrushProperty,
            ActiveBrushProperty, HoverBrushProperty, HoverBackgroundProperty);
    }

    public TurnRail()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private double Pitch => Count > 0 ? Math.Max(0, Bounds.Height - (2 * PadY)) / Count : 0;

    /// <summary>Vertical centre of tick <paramref name="index"/>, in this control's coordinates.</summary>
    public double TickCenter(int index) => PadY + ((index + 0.5) * Pitch);

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = (Count * PreferredPitch) + (2 * PadY);
        if (!double.IsInfinity(availableSize.Height)) height = Math.Min(height, availableSize.Height);
        return new Size(0, Math.Max(0, height));
    }

    public override void Render(DrawingContext context)
    {
        var count = Count;
        if (count == 0) return;

        var live = IsPointerOver;
        if (live && HoverBackground is { } background)
            context.DrawRectangle(background, null, new RoundedRect(new Rect(Bounds.Size), Bounds.Width / 2));

        var active = ActiveIndex;
        var hovered = HoveredIndex;
        if (TickBrush is { } tick)
        {
            using (context.PushOpacity(live ? LiveOpacity : IdleOpacity))
            {
                for (var i = 0; i < count; i++)
                {
                    if (i != active && i != hovered) DrawTick(context, tick, i, TickWidth);
                }
            }
        }

        // Lit ticks last: once the rail is packed tighter than a tick is tall,
        // their neighbours would otherwise paint over them.
        if (active >= 0 && active < count && active != hovered && ActiveBrush is { } activeBrush)
            DrawTick(context, activeBrush, active, TickWidthLit);
        if (hovered >= 0 && hovered < count && HoverBrush is { } hoverBrush)
            DrawTick(context, hoverBrush, hovered, TickWidthLit);
    }

    private void DrawTick(DrawingContext context, IBrush brush, int index, double width)
    {
        var top = Math.Round(TickCenter(index) - (TickHeight / 2));
        var rect = new Rect(TickInset, top, width, TickHeight);
        context.FillRectangle(brush, rect, (float)(TickHeight / 2));
    }

    private int IndexAt(double y) =>
        Count == 0 || Pitch <= 0 ? -1 : Math.Clamp((int)Math.Floor((y - PadY) / Pitch), 0, Count - 1);

    private void SetHovered(int index)
    {
        if (index == HoveredIndex) return;
        HoveredIndex = index;
        InvalidateVisual();
        HoverChanged?.Invoke(this, index);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        InvalidateVisual();
        SetHovered(IndexAt(e.GetPosition(this).Y));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetHovered(IndexAt(e.GetPosition(this).Y));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        InvalidateVisual();
        SetHovered(-1);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var index = IndexAt(e.GetPosition(this).Y);
        if (index < 0) return;
        e.Handled = true;
        TurnInvoked?.Invoke(this, index);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CountProperty && HoveredIndex >= Count) SetHovered(-1);
        else if (change.Property == IsVisibleProperty && !IsVisible) SetHovered(-1);
    }
}
