using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MolaGPT.App.Views;
using MolaGPT.Desktop.Services;
using MolaGPT.ViewModels;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.App.Infrastructure;

/// <summary>
/// Decides where a notification goes. Publishers say what happened; this says
/// whether it becomes an in-app banner, a Windows toast, or waits in a queue —
/// which is what stops every call site from re-inventing the answer.
/// </summary>
internal sealed class NotificationRouter : IDisposable
{
    private const string BackgroundSummaryKey = "background-summary";

    private readonly NotificationCenter _center;
    private readonly BackgroundStreamService _backgroundStreams;
    private readonly SettingsViewModel _settings;
    private readonly NotificationHost _host;
    private readonly AppNotificationService _system;
    private readonly Window _window;
    private readonly Func<string?> _currentConversationId;
    private readonly List<AppNotification> _queued = new();

    public NotificationRouter(
        NotificationCenter center,
        BackgroundStreamService backgroundStreams,
        SettingsViewModel settings,
        NotificationHost host,
        AppNotificationService system,
        Window window,
        Func<string?> currentConversationId)
    {
        _center = center;
        _backgroundStreams = backgroundStreams;
        _settings = settings;
        _host = host;
        _system = system;
        _window = window;
        _currentConversationId = currentConversationId;

        _center.Published += OnPublished;
        _center.Dismissed += OnDismissed;
        _backgroundStreams.TaskCompleted += OnStreamCompleted;
        _backgroundStreams.TaskFailed += OnStreamFailed;
        MolaWindow.AnyWindowActivated += OnWindowActivated;
    }

    private void OnPublished(object? sender, AppNotification notification) =>
        Dispatcher.UIThread.Post(() => Route(notification));

    private void OnDismissed(object? sender, string key) =>
        Dispatcher.UIThread.Post(() => _host.Dismiss(key));

    private void Route(AppNotification notification)
    {
        if (notification.IsAnswerCompleted && !_settings.EnableCompletionNotification)
        {
            Retire(notification);
            return;
        }

        if (IsForeground())
        {
            // Returning to the app by way of another window never fires the main
            // window's Activated, so the backlog also drains on the next thing
            // that happens while we are back in front.
            FlushQueue();

            // The user is already looking at the conversation that finished —
            // but only if the main window is the one they are actually in.
            // Settings and dialogs keep the app in the foreground while hiding
            // the transcript, and a completion swallowed there is lost.
            if (notification.IsAnswerCompleted
                && _window.IsActive
                && !string.IsNullOrEmpty(notification.ConversationId)
                && string.Equals(_currentConversationId(), notification.ConversationId, StringComparison.Ordinal))
            {
                Retire(notification);
                return;
            }

            _host.Show(WithNavigation(notification));
            return;
        }

        if (notification.IsAnswerCompleted)
        {
            Retire(notification);
            _system.ShowSystemToast(notification.Title, notification.Body, notification.ConversationId);
            return;
        }

        // Everything else waits. A runtime finishing its download in the
        // background is worth knowing about, but not worth a toast.
        if (!string.IsNullOrEmpty(notification.Key))
            _queued.RemoveAll(q => string.Equals(q.Key, notification.Key, StringComparison.Ordinal));
        _queued.Add(notification);
    }

    /// <summary>
    /// Takes down whatever banner this key is still showing, for the paths that
    /// deliver a notification somewhere other than the banner stack.
    ///
    /// Without this, a terminal state that gets suppressed or sent to a Windows
    /// toast leaves its own progress banner standing: the image workbench puts
    /// up a sticky 「正在后台生成」 card, and if the completion is swallowed —
    /// notifications turned off, the user already on that conversation, or the
    /// app in the background — that card never comes down.
    /// </summary>
    private void Retire(AppNotification notification)
    {
        // Progress is not an outcome; retiring it here would erase a banner that
        // is still telling the truth.
        if (notification.Kind == NotifyKind.Progress) return;
        if (!string.IsNullOrEmpty(notification.Key)) _host.Dismiss(notification.Key);
    }

    /// <summary>
    /// A notification that names a conversation gets a way to reach it, unless
    /// the publisher already supplied its own action.
    /// </summary>
    private AppNotification WithNavigation(AppNotification notification)
    {
        if (notification.Action is not null || string.IsNullOrEmpty(notification.ConversationId))
            return notification;

        var conversationId = notification.ConversationId;
        return notification with
        {
            ActionText = string.IsNullOrWhiteSpace(notification.ActionText) ? "查看" : notification.ActionText,
            Action = () => _system.NavigateTo(conversationId)
        };
    }

    private void OnStreamCompleted(object? sender, BackgroundStreamCompletedEventArgs e)
    {
        _center.Notify(new AppNotification
        {
            Key = "answer-" + e.ConversationId,
            Kind = NotifyKind.Success,
            Title = string.IsNullOrWhiteSpace(e.ConversationTitle)
                ? "回复已完成"
                : $"「{e.ConversationTitle}」回复已完成",
            Body = e.ModelLabel,
            ConversationId = e.ConversationId,
            IsAnswerCompleted = true
        });
    }

    /// <summary>
    /// Same key as the completion above, so a turn reports one outcome: a retry
    /// that succeeds replaces this banner instead of leaving a stale failure
    /// standing next to a finished answer.
    ///
    /// Deliberately <em>not</em> <see cref="AppNotification.IsAnswerCompleted"/>.
    /// That flag is the one road to a Windows toast, and a failure does not get to
    /// open a second one — an Error banner already stays put until it is replaced,
    /// and a failure that lands while the app is away waits in the queue and is
    /// shown on return.
    /// </summary>
    private void OnStreamFailed(object? sender, BackgroundStreamFailedEventArgs e)
    {
        _center.Notify(new AppNotification
        {
            Key = "answer-" + e.ConversationId,
            Kind = NotifyKind.Error,
            Title = string.IsNullOrWhiteSpace(e.ConversationTitle)
                ? "回复失败"
                : $"「{e.ConversationTitle}」回复失败",
            Body = string.IsNullOrWhiteSpace(e.ErrorMessage) ? e.ModelLabel : e.ErrorMessage,
            ConversationId = e.ConversationId
        });
    }

    private void OnWindowActivated(object? sender, EventArgs e) => FlushQueue();

    /// <summary>
    /// Replays what happened while the app was away as one line rather than a
    /// column of stale banners.
    /// </summary>
    private void FlushQueue()
    {
        if (_queued.Count == 0) return;

        var items = _queued.ToList();
        _queued.Clear();

        if (items.Count == 1)
        {
            _host.Show(WithNavigation(items[0]));
            return;
        }

        // Collapsing into one line means none of these keys get shown on their
        // own, so their standing banners have to be retired by hand.
        foreach (var item in items) Retire(item);

        _host.Show(new AppNotification
        {
            Key = BackgroundSummaryKey,
            Kind = NotifyKind.Info,
            Title = $"共 {items.Count} 个通知",
            Body = string.Join(" · ", items.Select(i => i.Title))
        });
    }

    /// <summary>
    /// "In the foreground" means the app has the user's attention, not that this
    /// particular window does — settings and dialogs are still the app.
    /// </summary>
    private bool IsForeground()
    {
        if (!_window.IsVisible || _window.WindowState == WindowState.Minimized) return false;

        return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.Any(w => w.IsActive)
            : _window.IsActive;
    }

    public void Dispose()
    {
        _center.Published -= OnPublished;
        _center.Dismissed -= OnDismissed;
        _backgroundStreams.TaskCompleted -= OnStreamCompleted;
        _backgroundStreams.TaskFailed -= OnStreamFailed;
        MolaWindow.AnyWindowActivated -= OnWindowActivated;
    }
}
