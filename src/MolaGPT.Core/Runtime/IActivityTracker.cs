namespace MolaGPT.Core.Runtime;

/// <summary>
/// Tracks live application work that must keep the desktop host out of idle power
/// throttling. Lower layers acquire leases before work starts and dispose them
/// idempotently in <c>finally</c>; the host app provides the concrete implementation.
/// </summary>
public interface IActivityTracker
{
    /// <summary>
    /// Acquire a ref-counted activity lease. When the host is currently idle, this
    /// synchronously restores normal execution before returning. Disposing the
    /// returned handle is idempotent.
    /// </summary>
    IDisposable Acquire(string source);

    /// <summary>
    /// Mark brief interactive activity and keep the host awake for at least
    /// <paramref name="hold"/>. Synchronously restores normal execution before returning.
    /// </summary>
    void Pulse(string source, TimeSpan hold);
}

/// <summary>No-op fallback used by standalone tests and callers without DI.</summary>
public sealed class NullActivityTracker : IActivityTracker
{
    public static readonly NullActivityTracker Instance = new();
    private NullActivityTracker() { }

    public IDisposable Acquire(string source) => NoopLease.Instance;
    public void Pulse(string source, TimeSpan hold) { }

    private sealed class NoopLease : IDisposable
    {
        public static readonly NoopLease Instance = new();
        public void Dispose() { }
    }
}
