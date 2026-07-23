namespace MolaGPT.Core.Chat.Agents.Relay;

/// <summary>
/// The desktop bridge's view of the relay: it produces transcript events and
/// session-meta snapshots, and it consumes commands targeting this machine's
/// sessions. The real implementation posts over HTTPS+Bearer-JWT to the PHP
/// endpoints and SSE-subscribes the command stream; an in-memory implementation
/// backs the protocol tests.
/// </summary>
public interface IRelayProducer
{
    /// <summary>Read the relay's stored projection cursors per session. Used by
    /// the desktop to decide whether the relay already has the transcript for a
    /// local history session before publishing the session meta snapshot.</summary>
    Task<IReadOnlyDictionary<string, RelaySessionCursor>> ListSessionCursorsAsync(CancellationToken ct);

    /// <summary>Read mobile devices that recently fetched relay sessions.</summary>
    Task<IReadOnlyList<RelayMobileDevice>> ListMobileDevicesAsync(CancellationToken ct);

    /// <summary>Push one seq'd transcript event to the relay (desktop → phone).</summary>
    Task PostEventAsync(RelayEventEnvelope envelope, CancellationToken ct);

    /// <summary>Clear transient transcript events for one session before the
    /// desktop republishes a fresh local-history view.</summary>
    Task ResetSessionEventsAsync(string sessionId, CancellationToken ct);

    /// <summary>Atomically replace ALL transcript events for a session with the
    /// given ordered set in a SINGLE request. History projection uses this so the
    /// phone never reads a half-rebuilt transcript: the prior approach reset then
    /// re-posted events one-by-one, which for a large session both exposed partial
    /// state for the whole backfill and outran the snapshot loop (re-triggering the
    /// projection endlessly).</summary>
    Task ReplaceSessionEventsAsync(string sessionId, IReadOnlyList<RelayEventEnvelope> events, CancellationToken ct);

    /// <summary>Push a session-meta snapshot (phase / attention change).</summary>
    Task PostMetaAsync(RelaySessionMeta meta, CancellationToken ct);

    /// <summary>Refresh online TTL for already-published sessions. This is
    /// intentionally separate from metadata and history projection so a slow
    /// transcript backfill cannot make the desktop look disconnected.</summary>
    Task PostHeartbeatAsync(IReadOnlyList<string> sessionIds, CancellationToken ct);

    /// <summary>Mark this desktop bridge offline. Used on graceful app exit; the
    /// relay also applies a heartbeat TTL for crash/kill cases.</summary>
    Task MarkMachineOfflineAsync(CancellationToken ct);

    /// <summary>
    /// Atomically claim a queued command for this bridge. The relay only delivers
    /// an unexpired lease to one eligible machine at a time.
    /// </summary>
    Task<RelayCommandLease> LeaseCommandAsync(string sessionId, string cmdId, CancellationToken ct);

    /// <summary>Extend a still-owned lease while a long-running command executes.</summary>
    Task<RelayCommandLease> RenewCommandLeaseAsync(string sessionId, string cmdId, CancellationToken ct);

    /// <summary>
    /// Record the terminal dispatch result. A completed command is no longer
    /// delivered; a failed command remains inspectable at the relay instead of
    /// being silently dropped.
    /// </summary>
    Task<bool> CompleteCommandAsync(
        string sessionId,
        string cmdId,
        bool succeeded,
        string? error,
        CancellationToken ct);

    /// <summary>Long-lived stream of commands addressed to this bridge. Completes
    /// on disconnect; the client reconnects with backoff.</summary>
    IAsyncEnumerable<RelayCommand> SubscribeCommandsAsync(CancellationToken ct);
}

/// <summary>
/// The phone's view of the relay: it posts commands and subscribes to the seq'd
/// event stream for a session, replaying from <paramref name="sinceSeq"/> on
/// (re)connect. Mirrors <see cref="IRelayProducer"/> from the other side; one
/// in-memory transport implements both for tests.
/// </summary>
public interface IRelayConsumer
{
    /// <summary>Enqueue a command for the desktop bridge.</summary>
    Task PostCommandAsync(RelayCommand command, CancellationToken ct);

    /// <summary>Replay history (seq > sinceSeq) then stream live events for a
    /// session. Pass 0 to rebuild the whole transcript.</summary>
    IAsyncEnumerable<RelayEventEnvelope> SubscribeEventsAsync(string sessionId, long sinceSeq, CancellationToken ct);
}
