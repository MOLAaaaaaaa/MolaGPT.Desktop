using Avalonia.Controls;
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

    public NotificationHost()
    {
        InitializeComponent();
        PART_More.Click += (_, _) =>
        {
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
                return;
            }
        }

        var banner = new NotificationBanner();
        banner.DismissRequested += (_, b) => Remove(b);
        banner.Apply(notification);

        _banners.Add(banner);
        PART_Stack.Children.Add(banner);
        Reflow();
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

        PART_Stack.Children.Remove(banner);
        Reflow();
    }

    /// <summary>
    /// Three at a time; the rest collapse behind a count. A stack tall enough to
    /// reach the composer stops being a notification and becomes a panel.
    /// </summary>
    private void Reflow()
    {
        for (var i = 0; i < _banners.Count; i++)
            _banners[i].IsVisible = _expanded || i < MaxVisible;

        var hidden = _banners.Count - MaxVisible;
        if (!_expanded && hidden > 0)
        {
            PART_More.Content = $"还有 {hidden} 条";
            PART_More.IsVisible = true;
            return;
        }

        PART_More.IsVisible = false;
        if (_banners.Count <= MaxVisible) _expanded = false;
    }
}
