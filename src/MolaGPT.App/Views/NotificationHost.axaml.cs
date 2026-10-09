using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MolaGPT.Desktop.Services;

namespace MolaGPT.App.Views;

/// <summary>
/// The in-app notification stack. Children are managed by hand rather than
/// through an ItemsControl: replacing an item there discards the container, and
/// a progress banner has to survive dozens of updates without being rebuilt.
/// </summary>
public partial class NotificationHost : UserControl
{
    private const int MaxVisible = 3;

    private readonly List<NotificationBanner> _banners = new();
    private bool _expanded;
    private bool _secondaryWindow;
    private NotificationBanner? _latest;
    private Flyout? _overflowFlyout;
    private readonly StackPanel _overflowContent = new() { Spacing = 8 };
    private readonly Dictionary<NotificationBanner, NotificationBanner> _overflowViews = new();

    public NotificationHost()
    {
        InitializeComponent();
        IsVisible = false;
        PART_More.Click += (_, _) =>
        {
            if (_secondaryWindow)
            {
                ShowOverflow();
                return;
            }
            _expanded = true;
            Reflow();
        };
    }

    /// <summary>
    /// Adds a banner, or updates the existing one with the same key in place.
    /// </summary>
    public void Show(AppNotification notification)
    {
        if (!string.IsNullOrEmpty(notification.Key))
        {
            var existing = _banners.FirstOrDefault(
                b => string.Equals(b.Key, notification.Key, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.Apply(notification);
                _latest = existing;
                Reflow();
                RefreshOverflow();
                return;
            }
        }

        var banner = new NotificationBanner();
        banner.SetSecondaryWindowLayout(_secondaryWindow);
        banner.DismissRequested += (_, b) => Remove(b);
        banner.Apply(notification);

        _banners.Add(banner);
        _latest = banner;
        PART_Stack.Children.Add(banner);
        Reflow();
        RefreshOverflow();
    }

    internal void SetSecondaryWindowLayout(bool secondaryWindow)
    {
        _overflowFlyout?.Hide();
        _secondaryWindow = secondaryWindow;
        HorizontalAlignment = HorizontalAlignment.Right;
        Width = secondaryWindow ? 320 : double.NaN;
        Margin = default;
        PART_Layout.HorizontalAlignment = PART_Stack.HorizontalAlignment = secondaryWindow
            ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        foreach (var banner in _banners) banner.SetSecondaryWindowLayout(secondaryWindow);
        Grid.SetRow(PART_More, secondaryWindow ? 0 : 1);
        PART_More.VerticalAlignment = VerticalAlignment.Top;
        PART_More.Margin = secondaryWindow ? new Thickness(0, 10, 40, 0) : new Thickness(0, 10, 0, 0);
        PART_More.Padding = secondaryWindow ? new Thickness(4, 2) : new Thickness(12, 5);
        Reflow();
    }

    private void ShowOverflow()
    {
        _overflowFlyout ??= new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Content = new ScrollViewer
            {
                MaxHeight = 320, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = _overflowContent
            }
        };
        _overflowContent.MaxWidth = PART_Stack.Bounds.Width;
        RefreshOverflow();
        _overflowFlyout.ShowAt(PART_More);
    }

    private void RefreshOverflow()
    {
        if (_overflowFlyout is null) return;
        foreach (var removed in _overflowViews.Keys.Where(banner => !_banners.Contains(banner)).ToArray())
        {
            _overflowContent.Children.Remove(_overflowViews[removed]);
            _overflowViews.Remove(removed);
        }
        foreach (var original in _banners)
        {
            if (!_overflowViews.TryGetValue(original, out var view))
            {
                view = new NotificationBanner();
                view.SetSecondaryWindowLayout(true);
                view.Classes.Add("details");
                view.DismissRequested += (_, _) => Remove(original);
                _overflowViews.Add(original, view);
                _overflowContent.Children.Add(view);
            }
            view.Apply(original.Notification with { Sticky = true });
        }
        if (_banners.Count == 0) _overflowFlyout.Hide();
    }

    public void Dismiss(string key)
    {
        var banner = _banners.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.Ordinal));
        if (banner is not null) Remove(banner);
    }

    public void Clear()
    {
        foreach (var banner in _banners.ToList()) Remove(banner);
    }

    internal void TransferNotificationsTo(NotificationHost target)
    {
        _overflowFlyout?.Hide();
        NotificationBanner? latest = null;
        foreach (var banner in _banners)
        {
            target.Show(banner.Notification);
            var copy = target._banners[^1];
            banner.CopyCountdownTo(copy);
            if (ReferenceEquals(banner, _latest)) latest = copy;
        }
        target._latest = latest;
        target._expanded = _expanded;
        target.Reflow();
        Clear();
    }

    /// <summary>
    /// Removal is immediate and never waits on an animation. An exit fade that
    /// stalls with the render clock would strand a half-transparent card that
    /// nothing ever takes off the screen; the countdown hairline already gives
    /// the disappearance its warning.
    /// </summary>
    private void Remove(NotificationBanner banner)
    {
        // The countdown and the close button can both fire for one banner.
        if (!_banners.Remove(banner)) return;

        if (ReferenceEquals(_latest, banner)) _latest = _banners.LastOrDefault();
        PART_Stack.Children.Remove(banner);
        Reflow();
        RefreshOverflow();
    }

    /// <summary>
    /// Main windows show a stack; secondary windows show the latest event and
    /// keep the rest available through the overflow button.
    /// </summary>
    private void Reflow()
    {
        IsVisible = _banners.Count > 0;
        for (var i = 0; i < _banners.Count; i++)
        {
            _banners[i].IsVisible = _secondaryWindow
                ? ReferenceEquals(_banners[i], _latest) : _expanded || i < MaxVisible;
            _banners[i].ReserveOverflowButton(_secondaryWindow && _banners.Count > 1 && ReferenceEquals(_banners[i], _latest));
        }

        var hidden = _banners.Count - (_secondaryWindow ? 1 : MaxVisible);
        if ((_secondaryWindow || !_expanded) && hidden > 0)
        {
            PART_More.Content = _secondaryWindow ? $"+{hidden}" : $"还有 {hidden} 条";
            ToolTip.SetTip(PART_More, $"还有 {hidden} 条通知");
            PART_More.IsVisible = true;
            return;
        }

        PART_More.IsVisible = false;
        if (_banners.Count <= MaxVisible) _expanded = false;
    }
}
