using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MolaGPT.Desktop.Services;

namespace MolaGPT.App.Views;

/// <summary>
/// A single notification card. Owns its own auto-dismiss countdown so the host
/// only has to decide what exists, not how long each thing lives.
/// </summary>
public partial class NotificationBanner : UserControl
{
    private const double CardWidth = 344;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(80);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);

    private readonly DispatcherTimer _timer;
    private TimeSpan _total;
    private TimeSpan _remaining;
    private bool _hovered;

    /// <summary>Raised when the countdown expires, the close button is used, or an action runs.</summary>
    public event EventHandler<NotificationBanner>? DismissRequested;

    public AppNotification Notification { get; private set; } = new();

    public string? Key => Notification.Key;

    public NotificationBanner()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += OnTick;

        PART_Close.Click += (_, _) => DismissRequested?.Invoke(this, this);
        PART_Action.Click += (_, _) => InvokeAction();
        PART_Card.PointerPressed += OnCardPressed;
        PointerEntered += (_, _) => SetHovered(true);
        PointerExited += (_, _) => SetHovered(false);
    }

    /// <summary>
    /// Renders a notification into this card. Called again for every update to
    /// the same key, which is what keeps a download to one banner instead of
    /// one per progress report.
    /// </summary>
    public void Apply(AppNotification notification)
    {
        Notification = notification;

        PART_Icon.Text = IconFor(notification.Kind);
        PART_Icon.Foreground = AccentFor(notification.Kind);

        PART_Title.Text = notification.Title;

        PART_Body.Text = notification.Body ?? string.Empty;
        PART_Body.IsVisible = !string.IsNullOrWhiteSpace(notification.Body);

        var isProgress = notification.Kind == NotifyKind.Progress;
        PART_Progress.IsVisible = isProgress;
        if (isProgress)
        {
            PART_Progress.IsIndeterminate = notification.Progress is null;
            if (notification.Progress is { } value)
                PART_Progress.Value = Math.Clamp(value, 0, 1);
        }

        PART_Action.Content = notification.ActionText ?? string.Empty;
        PART_Action.IsVisible = !string.IsNullOrWhiteSpace(notification.ActionText);

        PART_Card.Cursor = notification.Action is null ? ArrowCursor : HandCursor;

        RestartCountdown(notification.EffectiveDuration);
    }

    private void RestartCountdown(TimeSpan? duration)
    {
        _timer.Stop();

        if (duration is not { TotalMilliseconds: > 0 })
        {
            _total = _remaining = TimeSpan.Zero;
            PART_Timer.IsVisible = false;
            return;
        }

        _total = _remaining = duration.Value;
        PART_Timer.Background = AccentFor(Notification.Kind);
        PART_Timer.Width = CardWidth;
        PART_Timer.IsVisible = true;

        // A banner that appears under the pointer should not start draining
        // until the pointer leaves.
        if (!_hovered) _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining -= TickInterval;
        if (_remaining <= TimeSpan.Zero)
        {
            _timer.Stop();
            PART_Timer.Width = 0;
            DismissRequested?.Invoke(this, this);
            return;
        }

        PART_Timer.Width = CardWidth * (_remaining.TotalMilliseconds / _total.TotalMilliseconds);
    }

    private void SetHovered(bool hovered)
    {
        _hovered = hovered;
        PART_Close.Opacity = hovered ? 1 : 0;

        if (hovered) _timer.Stop();
        else if (_remaining > TimeSpan.Zero) _timer.Start();
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Notification.Action is null) return;
        // The close and action buttons handle their own clicks.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        InvokeAction();
    }

    /// <summary>
    /// Dismisses before running, not after: a retry action typically republishes
    /// the same key, and dismissing afterwards would take the replacement with it.
    /// </summary>
    private void InvokeAction()
    {
        var action = Notification.Action;
        DismissRequested?.Invoke(this, this);
        action?.Invoke();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private static string IconFor(NotifyKind kind) => kind switch
    {
        NotifyKind.Success => "\uE10B",
        NotifyKind.Warning => "\uE7BA",
        NotifyKind.Error => "\uE783",
        NotifyKind.Progress => "\uE895",
        _ => "\uE946"
    };

    private IBrush AccentFor(NotifyKind kind)
    {
        var key = kind switch
        {
            NotifyKind.Success => "Brush.Success",
            NotifyKind.Warning => "Brush.Warning",
            NotifyKind.Error => "Brush.Error",
            NotifyKind.Progress => "Brush.Primary",
            _ => "Brush.Info"
        };

        return this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Gray;
    }
}
