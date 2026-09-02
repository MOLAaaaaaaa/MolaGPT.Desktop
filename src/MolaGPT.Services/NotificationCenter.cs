namespace MolaGPT.Desktop.Services;

/// <summary>
/// Severity of a notification. Drives the icon and accent colour, and decides
/// how long the banner stays: <see cref="Progress"/> and <see cref="Error"/>
/// are sticky, the rest expire on their own.
/// </summary>
public enum NotifyKind
{
    Info,
    Success,
    Warning,
    Error,
    Progress
}

/// <summary>
/// One thing that just happened. Deliberately not a status: anything that
/// describes what the app <em>is</em> (signed in, update available, Python
/// installed) belongs on a chip or in a settings row, not here.
/// </summary>
public sealed record AppNotification
{
    /// <summary>
    /// Notifications sharing a key replace each other in place instead of
    /// stacking, so a download can report 0% → 100% → done as a single banner.
    /// Null means "always add a new one".
    /// </summary>
    public string? Key { get; init; }

    public NotifyKind Kind { get; init; } = NotifyKind.Info;

    public string Title { get; init; } = "MolaGPT";

    public string? Body { get; init; }

    /// <summary>
    /// 0..1 for a determinate bar. Null on a <see cref="NotifyKind.Progress"/>
    /// notification means the work has no measurable total.
    /// </summary>
    public double? Progress { get; init; }

    public string? ActionText { get; init; }

    public Action? Action { get; init; }

    /// <summary>Where clicking the notification should take the user, if anywhere.</summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// The only category allowed to escalate to a Windows toast, and only while
    /// the app is in the background. Everything else stays in-app.
    /// </summary>
    public bool IsAnswerCompleted { get; init; }

    /// <summary>Overrides the per-kind default; ignored when <see cref="Sticky"/> is set.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Stays until replaced by the same key, or dismissed by hand.</summary>
    public bool Sticky { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public TimeSpan? EffectiveDuration =>
        Sticky ? null : Duration ?? DefaultDuration(Kind);

    /// <summary>
    /// Errors stay because the user has to act on them; progress stays because
    /// a terminal state is coming to replace it.
    /// </summary>
    public static TimeSpan? DefaultDuration(NotifyKind kind) => kind switch
    {
        NotifyKind.Success => TimeSpan.FromSeconds(4),
        NotifyKind.Info => TimeSpan.FromSeconds(5),
        NotifyKind.Warning => TimeSpan.FromSeconds(6),
        _ => null
    };
}

/// <summary>
/// The single entry point for transient notifications. Publishers never decide
/// where a notification is shown — that is the router's job, which is what
/// keeps "banner or Windows toast?" from being re-litigated at each call site.
/// </summary>
public sealed class NotificationCenter
{
    public event EventHandler<AppNotification>? Published;

    public event EventHandler<string>? Dismissed;

    public void Notify(AppNotification notification) =>
        Published?.Invoke(this, notification);

    /// <summary>Retracts a notification early, by key. No-op if it is already gone.</summary>
    public void Dismiss(string key)
    {
        if (!string.IsNullOrWhiteSpace(key)) Dismissed?.Invoke(this, key);
    }

    public void Info(string title, string? body = null, string? key = null) =>
        Notify(new AppNotification { Key = key, Kind = NotifyKind.Info, Title = title, Body = body });

    public void Success(string title, string? body = null, string? key = null) =>
        Notify(new AppNotification { Key = key, Kind = NotifyKind.Success, Title = title, Body = body });

    public void Warning(string title, string? body = null, string? key = null) =>
        Notify(new AppNotification { Key = key, Kind = NotifyKind.Warning, Title = title, Body = body });

    public void Error(string title, string? body = null, string? key = null) =>
        Notify(new AppNotification { Key = key, Kind = NotifyKind.Error, Title = title, Body = body });

    public void Progress(string key, string title, string? body = null, double? progress = null) =>
        Notify(new AppNotification
        {
            Key = key,
            Kind = NotifyKind.Progress,
            Title = title,
            Body = body,
            Progress = progress
        });
}
