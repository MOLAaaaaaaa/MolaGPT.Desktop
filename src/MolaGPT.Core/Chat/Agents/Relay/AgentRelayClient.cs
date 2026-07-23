using System.Collections.Concurrent;
using System.Text.Json;

namespace MolaGPT.Core.Chat.Agents.Relay;

/// <summary>
/// Glues the headless <see cref="AgentBridgeService"/> to a relay
/// <see cref="IRelayProducer"/>: it translates the bridge's fine-grained events
/// into the phone-facing <b>coarse</b> <see cref="RelayTranscriptEvent"/> stream
/// (tool cards live = progress; answer text once per answer via
/// <see cref="AnswerSnapshotEvent"/> — not per-token), forwards session-meta
/// snapshots, and consumes the relay's command stream — dispatching send /
/// interrupt / switch-options to the bridge under a renewable lease, then
/// recording a terminal result so failed commands are not silently dropped.
///
/// Turns dispatch concurrently so an interrupt can still reach a running turn.
/// Each command lease is renewed until its dispatch task has a terminal result;
/// the bridge's per-session gate serializes concurrent sends on the same session.
///
/// Transport-agnostic, lives in Core (no WPF). The real HTTP producer
/// (Bearer-JWT + SSE command stream) is a Desktop-layer adapter; tests use an
/// in-memory transport.
/// </summary>
public sealed class AgentRelayClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan MachineSnapshotInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MachineHeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CommandLeaseRenewInterval = TimeSpan.FromSeconds(30);
    private const int HistoryBackfillMaxTurns = 30;

    /// <summary>How long after the last transcript-file activity an externally
    /// driven session (open projected tail, never bridge-driven) still reports
    /// Running. Bounds the "stuck Running" window when a CLI dies mid-turn
    /// without ever writing its terminal marker.</summary>
    // How long after a projected transcript's last write we still treat its open
    // tail as a live turn. Within the window: keep the tail open and report
    // Running (the phone shows 运行中). Past it: the turn stopped writing without
    // a terminal marker (finished, interrupted, or — for Claude, whose interactive
    // ~/.claude/projects transcripts never contain a `result` line — simply done),
    // so synthesize a TurnDone to close it and fall back to Idle. Kept short so a
    // completed external turn self-heals quickly instead of hanging on 运行中.
    internal static TimeSpan ExternalTurnActiveWindow { get; set; } = TimeSpan.FromSeconds(60);

    // Upper bound for the one-time startup re-projection that closes open tails a
    // previous process left behind. Only sessions active within this window are
    // swept, so a large idle history isn't re-projected on every launch.
    private static readonly TimeSpan StartupTailHealWindow = TimeSpan.FromHours(24);

    /// <summary>Minimum time between mid-segment "growing" snapshots of the same
    /// answer/thinking segment — the pseudo-streaming cadence. Internal-settable
    /// so tests can shrink it without waiting wall-clock seconds.</summary>
    internal static TimeSpan StreamFlushInterval { get; set; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Minimum growth (chars) since the last shipped snapshot before a
    /// mid-segment flush is worth a wire round-trip.</summary>
    internal static int StreamFlushMinGrowth { get; set; } = 80;

    /// <summary>Stop mid-segment flushing once a segment exceeds this size — each
    /// growing snapshot re-ships the full segment text, so very large answers
    /// would otherwise bloat the relay's append-only event store. The segment
    /// still ships once, complete, at its boundary flush.</summary>
    private const int StreamFlushMaxSegmentChars = 64_000;

    private readonly AgentBridgeService _bridge;
    private readonly IRelayProducer _producer;
    private readonly string _machineId;
    private readonly string _machineName;
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _awaitingTerminal = new(StringComparer.Ordinal);
    // _nextRelaySeq allocates local envelopes; _lastRelaySeq advances only after
    // the relay has durably accepted an event or an atomic history replacement.
    private readonly Dictionary<string, long> _nextRelaySeq = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastRelaySeq = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _relayActivityAtMs = new(StringComparer.Ordinal);
    // Whether the last history projection of a session ended in an open turn
    // (transcript still being written by an externally driven CLI process).
    private readonly Dictionary<string, bool> _projectedTailOpen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TurnStreamState> _turnStreams = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _historyProjectionQueue = new();
    private readonly ConcurrentDictionary<string, byte> _queuedHistoryProjections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _executingCommands = new(StringComparer.Ordinal);
    private Task _eventPostTail = Task.CompletedTask;
    private CancellationTokenSource? _cts;
    private int _activeHistoryProjection;
    private bool _relayCursorsSeeded;
    private const int EventPostRetryBurst = 5;

    public AgentRelayClient(
        AgentBridgeService bridge,
        IRelayProducer producer,
        IAgentConfigProvider? config = null)
    {
        _bridge = bridge;
        _producer = producer;
        _machineId = (config?.MachineId ?? string.Empty).Trim();
        _machineName = string.IsNullOrWhiteSpace(config?.MachineName)
            ? Environment.MachineName
            : config!.MachineName.Trim();
    }

    /// <summary>Wire bridge events to the producer and start the command loop.
    /// Returns when the command stream ends (disconnect) or the token cancels;
    /// callers reconnect with backoff in production.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = linkedCts;
        Task? snapshotLoop = null;
        Task? heartbeatLoop = null;
        Task? historyProjectionLoop = null;
        _bridge.ReplayEventAppended += OnReplayEvent;
        _bridge.SessionMetaChanged += OnSessionMeta;
        try
        {
            await SeedRelayCursorsAsync(linkedCts.Token).ConfigureAwait(false);
            heartbeatLoop = RunMachineHeartbeatLoopAsync(linkedCts.Token);
            historyProjectionLoop = RunHistoryProjectionLoopAsync(linkedCts.Token);
            await PublishInitialSessionsAsync(linkedCts.Token).ConfigureAwait(false);
            snapshotLoop = RunMachineSnapshotLoopAsync(linkedCts.Token);
            await RunCommandLoopAsync(linkedCts.Token).ConfigureAwait(false);
        }
        finally
        {
            try { linkedCts.Cancel(); } catch { /* best-effort */ }
            _bridge.ReplayEventAppended -= OnReplayEvent;
            _bridge.SessionMetaChanged -= OnSessionMeta;
            if (snapshotLoop is not null)
            {
                try { await snapshotLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected on stop */ }
                catch { /* best-effort */ }
            }
            if (heartbeatLoop is not null)
            {
                try { await heartbeatLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected on stop */ }
                catch { /* best-effort */ }
            }
            if (historyProjectionLoop is not null)
            {
                try { await historyProjectionLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected on stop */ }
                catch { /* best-effort */ }
            }
            if (ReferenceEquals(_cts, linkedCts))
                _cts = null;
            linkedCts.Dispose();
        }
    }

    /// <summary>Stop forwarding and tear down the command loop. Safe to call once.</summary>
    public void Stop()
    {
        _bridge.ReplayEventAppended -= OnReplayEvent;
        _bridge.SessionMetaChanged -= OnSessionMeta;
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>Stop forwarding and mark the desktop bridge offline at the relay.
    /// This is best-effort and complements the relay's heartbeat TTL.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        Stop();
        try { await _producer.MarkMachineOfflineAsync(ct).ConfigureAwait(false); }
        catch { /* best-effort shutdown path */ }
    }

    public AgentRelayProjectionStatus GetProjectionStatus()
    {
        int projectedSessions;
        lock (_gate)
            projectedSessions = _lastRelaySeq.Count(kv => kv.Value > 0);
        return new AgentRelayProjectionStatus(
            projectedSessions,
            _queuedHistoryProjections.Count,
            Volatile.Read(ref _activeHistoryProjection));
    }

    public Task<IReadOnlyList<RelayMobileDevice>> ListMobileDevicesAsync(CancellationToken ct = default)
        => _producer.ListMobileDevicesAsync(ct);

    // ---- bridge → relay (coarse translation) ------------------------------

    private void OnReplayEvent(string sessionId, AgentReplayEntry entry)
    {
        var ev = entry.Event;
        // Coarse translation. Per-token TextDelta/ThinkingDelta are folded into an
        // AnswerSnapshot at turn end, never shipped individually. PendingShown is a
        // bridge-internal pacing marker the phone doesn't need.
        switch (ev)
        {
            case UserTurnSubmitted u:
                SetAwaitingTerminal(sessionId, true); // a turn is in flight until a terminal event
                ResetTurnBuffers(sessionId);
                Post(Envelope(sessionId, entry.Seq, new UserPromptEvent(u.Text)));
                break;

            case CliEventAppended c:
                if (TranslateCli(sessionId, entry.Seq, c.Event) is { } coarse)
                    Post(Envelope(sessionId, entry.Seq, coarse));
                break;

            case TurnFinalized:
                // The bridge emits TurnFinalized for every turn end — including
                // interrupts, where no TurnComplete/Error arrived. If no terminal
                // event was seen for this turn, ship the partial answer snapshot
                // + a usage-less TurnDone so the phone isn't left hanging.
                if (ConsumeAwaitingTerminal(sessionId))
                {
                    FlushTurnBuffers(sessionId, entry.Seq);
                    Post(Envelope(sessionId, entry.Seq, new TurnDoneEvent(null)));
                }
                ResetTurnBuffers(sessionId);
                break;
        }
    }

    private RelayTranscriptEvent? TranslateCli(string sessionId, long seq, AgentEvent ev)
    {
        switch (ev.Kind)
        {
            case AgentEventKind.ThinkingDelta:
                AppendThinking(sessionId, ev.Text, seq);
                return null;

            case AgentEventKind.TextDelta:
                FlushThinking(sessionId, seq);
                AppendAnswer(sessionId, ev.Text, seq);
                return null;

            case AgentEventKind.ToolCall when ev.Tool is not null:
                FlushTurnBuffers(sessionId, seq);
                return new ToolProgressEvent(ev.Tool);

            case AgentEventKind.PermissionRequest when ev.Permission is not null:
                FlushTurnBuffers(sessionId, seq);
                return new PermissionPromptEvent(ev.Permission);

            case AgentEventKind.TurnComplete:
                SetAwaitingTerminal(sessionId, false); // terminal: a normal completion
                FlushTurnBuffers(sessionId, seq);
                ResetTurnBuffers(sessionId);
                return new TurnDoneEvent(ev.Usage);

            case AgentEventKind.Error:
                SetAwaitingTerminal(sessionId, false); // terminal: a failure
                FlushTurnBuffers(sessionId, seq);
                ResetTurnBuffers(sessionId);
                return new TurnFailedEvent(ev.ErrorMessage ?? "出错了");

            // TextDelta / ThinkingDelta are folded into AnswerSnapshot at turn end.
            default:
                return null;
        }
    }

    /// <summary>Per-session accumulation state for the turn currently streaming.
    /// Answer/thinking deltas buffer here; segments (delimited by tool calls) get
    /// stable ids so mid-segment growing snapshots replace-in-place on the phone.</summary>
    private sealed class TurnStreamState
    {
        public string AnswerText = string.Empty;
        public string ThinkingText = string.Empty;
        public int AnswerSegment;
        public int ThinkingSegment;
        public long AnswerFlushedAtMs;
        public long ThinkingFlushedAtMs;
        public int AnswerFlushedLength;
        public int ThinkingFlushedLength;
    }

    private TurnStreamState StreamStateOf(string sessionId)
    {
        // Caller holds _gate.
        if (!_turnStreams.TryGetValue(sessionId, out var state))
            _turnStreams[sessionId] = state = new TurnStreamState();
        return state;
    }

    private void AppendThinking(string sessionId, string? text, long bridgeSeq)
    {
        if (string.IsNullOrEmpty(text)) return;
        RelayEventEnvelope? growing = null;
        lock (_gate)
        {
            var s = StreamStateOf(sessionId);
            s.ThinkingText += text;
            growing = MaybeGrowingSnapshot(
                sessionId, bridgeSeq, s.ThinkingText, s.ThinkingSegment, isThinking: true,
                ref s.ThinkingFlushedAtMs, ref s.ThinkingFlushedLength);
        }
        if (growing is not null) Post(growing);
    }

    private void AppendAnswer(string sessionId, string? text, long bridgeSeq)
    {
        if (string.IsNullOrEmpty(text)) return;
        RelayEventEnvelope? growing = null;
        lock (_gate)
        {
            var s = StreamStateOf(sessionId);
            s.AnswerText += text;
            growing = MaybeGrowingSnapshot(
                sessionId, bridgeSeq, s.AnswerText, s.AnswerSegment, isThinking: false,
                ref s.AnswerFlushedAtMs, ref s.AnswerFlushedLength);
        }
        if (growing is not null) Post(growing);
    }

    /// <summary>Pseudo-streaming: while a segment accumulates, periodically ship
    /// its full text so far under the SAME segment id. The phone upserts by that
    /// id, so each snapshot replaces the previous one in place — no reliance on
    /// per-token wire events, and replay stays idempotent (later snapshots simply
    /// win). Rate-limited by time and growth; disabled for huge segments.</summary>
    private RelayEventEnvelope? MaybeGrowingSnapshot(
        string sessionId, long bridgeSeq, string text, int segment, bool isThinking,
        ref long flushedAtMs, ref int flushedLength)
    {
        var nowMs = Environment.TickCount64;
        if (flushedAtMs == 0)
        {
            // Clock starts at the segment's FIRST delta, so the first growing
            // snapshot lands ~interval after streaming began — not "interval
            // after the text happened to clear the growth gate", which for fast
            // generations pushed the first ship past the end of the answer.
            flushedAtMs = nowMs;
            return null;
        }
        if (text.Length > StreamFlushMaxSegmentChars) return null;
        if (text.Length - flushedLength < StreamFlushMinGrowth) return null;
        if (nowMs - flushedAtMs < StreamFlushInterval.TotalMilliseconds) return null;

        flushedAtMs = nowMs;
        flushedLength = text.Length;
        RelayTranscriptEvent ev = isThinking
            ? new ThinkingSnapshotEvent(text, SegmentId(isThinking: true, segment))
            : new AnswerSnapshotEvent(text, SegmentId(isThinking: false, segment));
        return Envelope(sessionId, bridgeSeq, ev);
    }

    private static string SegmentId(bool isThinking, int segment)
        => (isThinking ? "t" : "a") + segment.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void FlushTurnBuffers(string sessionId, long bridgeSeq)
    {
        FlushThinking(sessionId, bridgeSeq);
        FlushAnswer(sessionId, bridgeSeq);
    }

    private void FlushThinking(string sessionId, long bridgeSeq)
    {
        string? text;
        string segmentId;
        lock (_gate)
        {
            if (!_turnStreams.TryGetValue(sessionId, out var s) || string.IsNullOrWhiteSpace(s.ThinkingText))
                return;
            text = s.ThinkingText;
            segmentId = SegmentId(isThinking: true, s.ThinkingSegment);
            s.ThinkingText = string.Empty;
            s.ThinkingSegment++;
            s.ThinkingFlushedAtMs = 0;
            s.ThinkingFlushedLength = 0;
        }
        Post(Envelope(sessionId, bridgeSeq, new ThinkingSnapshotEvent(text, segmentId)));
    }

    private void FlushAnswer(string sessionId, long bridgeSeq)
    {
        string? text;
        string segmentId;
        lock (_gate)
        {
            if (!_turnStreams.TryGetValue(sessionId, out var s) || string.IsNullOrWhiteSpace(s.AnswerText))
                return;
            text = s.AnswerText;
            segmentId = SegmentId(isThinking: false, s.AnswerSegment);
            s.AnswerText = string.Empty;
            s.AnswerSegment++;
            s.AnswerFlushedAtMs = 0;
            s.AnswerFlushedLength = 0;
        }
        Post(Envelope(sessionId, bridgeSeq, new AnswerSnapshotEvent(text, segmentId)));
    }

    private void ResetTurnBuffers(string sessionId)
    {
        lock (_gate)
        {
            _turnStreams.Remove(sessionId);
        }
    }

    private void Post(RelayEventEnvelope envelope)
    {
        // Fire-and-forget, but preserve envelope order. The HTTP producer writes
        // each envelope as a separate request, and the PHP relay persists in
        // arrival order, so unordered concurrent posts could scramble replay.
        lock (_gate)
        {
            _eventPostTail = _eventPostTail
                .ContinueWith(_ => PostSafeAsync(envelope), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default)
                .Unwrap();
        }
    }

    private void OnSessionMeta(AgentSessionStateDto s)
    {
        if (NeedsHistoryProjection(s))
        {
            if (LastConfirmedRelaySeq(s.ConversationId) > 0)
                _ = PostMetaSafeAsync(BuildMeta(s), CancellationToken.None);
            EnqueueHistoryProjection(s.ConversationId);
            return;
        }

        _ = PostMetaSafeAsync(BuildMeta(s), CancellationToken.None);
    }

    private async Task<IReadOnlyList<AgentSessionStateDto>> PublishInitialSessionsAsync(CancellationToken ct)
    {
        var sessions = await PublishMachineSnapshotAsync(ct).ConfigureAwait(false);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // If the relay cursor is behind a local session on reconnect, rebuild its
        // durable transcript projection before advertising a newer meta.seq. This
        // closes gaps left by a process stop during an event-post retry.
        foreach (var session in sessions)
        {
            if (NeedsRelayCatchup(session))
            {
                EnqueueHistoryProjection(session.ConversationId);
                continue;
            }
            // One-time heal for sessions a PREVIOUS process left with an open tail
            // on the relay (its in-memory tail-open flag doesn't survive restart).
            // A history session whose transcript has been idle a while is done, so
            // re-project it once: the stale-tail branch appends a synthetic TurnDone
            // if the relay tail is still open, and is a no-op if already closed.
            // Bounded to recently-active sessions so a large history isn't re-swept.
            if (IsHistoryProjection(session)
                && nowMs - session.UpdatedAtMs > (long)ExternalTurnActiveWindow.TotalMilliseconds
                && nowMs - session.UpdatedAtMs <= (long)StartupTailHealWindow.TotalMilliseconds)
            {
                EnqueueHistoryProjection(session.ConversationId);
            }
        }
        return sessions;
    }

    private async Task<IReadOnlyList<AgentSessionStateDto>> PublishMachineSnapshotAsync(CancellationToken ct)
    {
        IReadOnlyList<AgentSessionStateDto> sessions;
        try { sessions = await _bridge.ListSessionsAsync(ct).ConfigureAwait(false); }
        catch { return Array.Empty<AgentSessionStateDto>(); }

        foreach (var s in sessions)
        {
            ct.ThrowIfCancellationRequested();
            if (NeedsHistoryProjection(s) || NeedsTailClose(s))
            {
                if (LastConfirmedRelaySeq(s.ConversationId) > 0)
                    await PostMetaSafeAsync(BuildMeta(s), ct).ConfigureAwait(false);
                EnqueueHistoryProjection(s.ConversationId);
                continue;
            }

            await PostMetaSafeAsync(BuildMeta(s), ct).ConfigureAwait(false);
        }

        return sessions;
    }

    private async Task RunMachineSnapshotLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(MachineSnapshotInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            if (!Volatile.Read(ref _relayCursorsSeeded))
                await SeedRelayCursorsAsync(ct).ConfigureAwait(false);
            await PublishMachineSnapshotAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RunHistoryProjectionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_historyProjectionQueue.TryDequeue(out var sessionId))
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            _queuedHistoryProjections.TryRemove(sessionId, out _);
            Interlocked.Exchange(ref _activeHistoryProjection, 1);
            try { await RefreshHistoryAsync(sessionId, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* next snapshot can enqueue again */ }
            finally { Interlocked.Exchange(ref _activeHistoryProjection, 0); }
        }
    }

    private async Task RunMachineHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PublishHeartbeatSafeAsync(ct).ConfigureAwait(false);

            try { await Task.Delay(MachineHeartbeatInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PublishHeartbeatSafeAsync(CancellationToken ct)
    {
        try
        {
            var sessions = await _bridge.ListSessionsAsync(ct).ConfigureAwait(false);
            var ids = sessions
                .Where(ShouldHeartbeat)
                .Select(s => s.ConversationId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            // Always heartbeat — even with zero sessions — so an idle/new machine
            // still registers in the relay's machines table and can be chosen as
            // the target for a phone "New" command.
            await _producer.PostHeartbeatAsync(ids, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* the next heartbeat retries */ }
    }

    private async Task SeedRelayCursorsAsync(CancellationToken ct)
    {
        try
        {
            var cursors = await _producer.ListSessionCursorsAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                foreach (var cursor in cursors.Values)
                {
                    _lastRelaySeq[cursor.SessionId] = Math.Max(cursor.Seq, _lastRelaySeq.GetValueOrDefault(cursor.SessionId));
                    _nextRelaySeq[cursor.SessionId] = Math.Max(cursor.Seq, _nextRelaySeq.GetValueOrDefault(cursor.SessionId));
                    _relayActivityAtMs[cursor.SessionId] = Math.Max(cursor.ActivityAtMs, _relayActivityAtMs.GetValueOrDefault(cursor.SessionId));
                }
            }
            Volatile.Write(ref _relayCursorsSeeded, true);
        }
        catch { /* next snapshot loop retries */ }
    }

    private async Task RefreshHistoryAsync(
        string sessionId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<AgentSessionStateDto> sessions;
        try { sessions = await _bridge.ListSessionsAsync(ct).ConfigureAwait(false); }
        catch { return; }

        var state = sessions.FirstOrDefault(s => string.Equals(s.ConversationId, sessionId, StringComparison.Ordinal));
        if (state is null)
            return;

        var turns = await _bridge.LoadHistoryTurnsAsync(
            sessionId,
            HistoryBackfillMaxTurns,
            ct).ConfigureAwait(false);
        if (turns.Count == 0)
            return;

        // Freshness decides whether the open tail is a live turn or a finished one.
        // Claude's interactive transcripts never carry a terminal marker, so their
        // last turn is ALWAYS open — without this, every completed Claude session
        // would hang on 运行中 in the phone's detail view forever.
        var tailOpen = turns[^1].IsOpen;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fileFresh = nowMs - state.UpdatedAtMs <= (long)ExternalTurnActiveWindow.TotalMilliseconds;
        var liveTail = tailOpen && fileFresh;
        // A stale open tail = the writer stopped without a terminal; close it.
        var closeStaleTail = tailOpen && !fileFresh;

        // Record BEFORE the meta post below so the phone sees Running while the
        // external turn's tail is still live, and flips back once it closes.
        SetProjectedTailOpen(sessionId, liveTail);

        await PublishHistoryProjectionAsync(
            state.ConversationId,
            state.UpdatedAtMs,
            turns,
            closeStaleTail,
            ct).ConfigureAwait(false);

        var fresh = _bridge.GetSession(sessionId) ?? state;
        await PostMetaSafeAsync(BuildMeta(fresh), ct).ConfigureAwait(false);
    }

    private void EnqueueHistoryProjection(string sessionId)
    {
        if (_queuedHistoryProjections.TryAdd(sessionId, 0))
            _historyProjectionQueue.Enqueue(sessionId);
    }

    private async Task PublishHistoryProjectionAsync(
        string sessionId,
        long activityAtMs,
        IReadOnlyList<AgentHistoryTurn> turns,
        bool closeStaleTail,
        CancellationToken ct)
    {
        if (turns.Count == 0)
            return;

        // Build the full transcript with fresh seqs 1..N, then ship it as ONE atomic
        // replace. The old approach (reset + post each event individually) exposed a
        // half-rebuilt transcript to the phone for the entire backfill — and for a
        // large session that backfill outran the 10s snapshot loop, which observed the
        // reset cursor and re-triggered the projection endlessly (so every re-entry on
        // the phone showed a different, incomplete tail).
        await DrainEventPostsAsync().ConfigureAwait(false);

        var envelopes = new List<RelayEventEnvelope>();
        long seq = 0;
        foreach (var turn in turns)
        {
            foreach (var ev in turn.Events)
            {
                ct.ThrowIfCancellationRequested();
                envelopes.Add(new RelayEventEnvelope(sessionId, ++seq, ev));
            }
        }

        // Terminate a stale open tail so the phone's detail view doesn't derive a
        // perpetual Running from a transcript that stopped mid-turn. The file is no
        // longer changing, so this synthetic TurnDone lands at a stable seq on every
        // re-projection — the phone's since-cursor sees it once and does not re-fire.
        if (closeStaleTail && envelopes.Count > 0 && envelopes[^1].Event is not TurnDoneEvent)
            envelopes.Add(new RelayEventEnvelope(sessionId, ++seq, new TurnDoneEvent(null)));

        await _producer.ReplaceSessionEventsAsync(sessionId, envelopes, ct).ConfigureAwait(false);

        // Advance the cursor only AFTER the atomic replace succeeds, so a concurrent
        // snapshot never sees a reset cursor and re-enqueues another projection.
        lock (_gate)
        {
            _lastRelaySeq[sessionId] = seq;
            _nextRelaySeq[sessionId] = seq;
            _relayActivityAtMs[sessionId] = Math.Max(activityAtMs, _relayActivityAtMs.GetValueOrDefault(sessionId));
        }
    }

    private async Task ResetRelayEventsAsync(string sessionId, CancellationToken ct)
    {
        try { await _producer.ResetSessionEventsAsync(sessionId, ct).ConfigureAwait(false); }
        catch { /* best-effort */ }
        ResetRelayCursor(sessionId);
    }

    private RelayEventEnvelope Envelope(string sessionId, long bridgeSeq, RelayTranscriptEvent ev)
    {
        lock (_gate)
        {
            var previous = _nextRelaySeq.GetValueOrDefault(sessionId);
            var relaySeq = Math.Max(bridgeSeq, previous + 1);
            _nextRelaySeq[sessionId] = relaySeq;
            return new RelayEventEnvelope(sessionId, relaySeq, ev);
        }
    }

    private long LastConfirmedRelaySeq(string sessionId)
    {
        lock (_gate)
            return _lastRelaySeq.GetValueOrDefault(sessionId);
    }

    private long LastRelayActivity(string sessionId)
    {
        lock (_gate)
            return _relayActivityAtMs.GetValueOrDefault(sessionId);
    }

    private void ResetRelayCursor(string sessionId)
    {
        lock (_gate)
        {
            _lastRelaySeq[sessionId] = 0;
            _nextRelaySeq[sessionId] = 0;
            _relayActivityAtMs[sessionId] = 0;
        }
    }

    private static bool IsHistoryProjection(AgentSessionStateDto s)
        => s.ResumeSessionId is not null
           && s.Seq <= 0
           && s.Phase == AgentSessionPhase.Idle;

    private bool NeedsHistoryProjection(AgentSessionStateDto s)
        => IsHistoryProjection(s)
           && (LastConfirmedRelaySeq(s.ConversationId) <= 0
               || LastRelayActivity(s.ConversationId) < s.UpdatedAtMs);

    // A projected tail we left OPEN (external turn was live) has since gone stale —
    // the writer stopped without a terminal. The file is no longer growing, so
    // NeedsHistoryProjection won't re-fire; enqueue one final projection so the
    // stale-tail branch can append a synthetic TurnDone and unstick the phone's
    // detail view from a perpetual 运行中.
    private bool NeedsTailClose(AgentSessionStateDto s)
        => IsHistoryProjection(s)
           && IsProjectedTailOpen(s.ConversationId)
           && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - s.UpdatedAtMs
               > (long)ExternalTurnActiveWindow.TotalMilliseconds;

    private bool ShouldHeartbeat(AgentSessionStateDto s)
        => !IsHistoryProjection(s) || LastConfirmedRelaySeq(s.ConversationId) > 0;

    private bool NeedsRelayCatchup(AgentSessionStateDto s)
        => s.Seq > LastConfirmedRelaySeq(s.ConversationId);

    private RelaySessionMeta BuildMeta(AgentSessionStateDto s)
    {
        var seq = LastConfirmedRelaySeq(s.ConversationId);
        var workspace = WorkspaceOf(s.WorkingDirectory);
        // updatedAtMs is the machine snapshot heartbeat used for online/offline;
        // activityAtMs is the real session activity time used for sorting.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // An externally driven turn (desktop chat UI or a raw CLI in a terminal)
        // never passes through the bridge, so bridge phase stays Idle. If the
        // projected transcript ends in an open turn and the file is still fresh,
        // surface Running so the phone doesn't show 空闲 mid-turn. Gated on
        // Seq<=0: once the bridge drives the session live, its phase wins.
        var phase = s.Phase;
        if (phase == AgentSessionPhase.Idle
            && s.Seq <= 0
            && IsProjectedTailOpen(s.ConversationId)
            && nowMs - s.UpdatedAtMs <= (long)ExternalTurnActiveWindow.TotalMilliseconds)
        {
            phase = AgentSessionPhase.Running;
        }
        return new RelaySessionMeta(
            s.ConversationId, s.BackendId, s.Title, s.WorkingDirectory,
            workspace.Key, workspace.Name,
            s.Model, s.ReasoningEffort, s.PermissionMode, s.ApprovalPolicy,
            phase, s.NeedsAttention, seq, nowMs, s.UpdatedAtMs,
            s.AvailableModels,
            string.IsNullOrWhiteSpace(_machineId) ? null : _machineId,
            string.IsNullOrWhiteSpace(_machineName) ? null : _machineName);
    }

    private void SetProjectedTailOpen(string sessionId, bool open)
    {
        lock (_gate) _projectedTailOpen[sessionId] = open;
    }

    private bool IsProjectedTailOpen(string sessionId)
    {
        lock (_gate) return _projectedTailOpen.GetValueOrDefault(sessionId);
    }

    private void SetAwaitingTerminal(string sessionId, bool value)
    {
        lock (_gate) _awaitingTerminal[sessionId] = value;
    }

    private bool ConsumeAwaitingTerminal(string sessionId)
    {
        lock (_gate)
        {
            if (!_awaitingTerminal.GetValueOrDefault(sessionId)) return false;
            _awaitingTerminal[sessionId] = false;
            return true;
        }
    }

    private async Task PostSafeAsync(RelayEventEnvelope envelope)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                await _producer.PostEventAsync(envelope, CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    _lastRelaySeq[envelope.SessionId] = Math.Max(
                        envelope.Seq,
                        _lastRelaySeq.GetValueOrDefault(envelope.SessionId));
                }

                // A meta snapshot may have been posted before this event completed.
                // Refresh it after confirmed delivery so the relay never advances a
                // list cursor beyond the transcript it actually stores.
                if (_bridge.GetSession(envelope.SessionId) is { } session)
                    _ = PostMetaSafeAsync(BuildMeta(session), CancellationToken.None);
                return;
            }
            catch
            {
                var stop = _cts;
                if (stop is null || stop.IsCancellationRequested) return;

                // Keep requests strictly ordered through _eventPostTail. Retry a
                // short exponential burst, then a bounded 30s cadence until the
                // bridge reconnects; no envelope is skipped or acknowledged early.
                var seconds = attempt < EventPostRetryBurst
                    ? Math.Min(1 << attempt, 16)
                    : 30;
                attempt++;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task PostMetaSafeAsync(RelaySessionMeta meta, CancellationToken ct = default)
    {
        try
        {
            await _producer.PostMetaAsync(meta, ct).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    private async Task DrainEventPostsAsync()
    {
        Task tail;
        lock (_gate) tail = _eventPostTail;
        try { await tail.ConfigureAwait(false); }
        catch { /* individual event posts are already best-effort */ }
    }

    // ---- relay → bridge (command dispatch) --------------------------------

    private async Task RunCommandLoopAsync(CancellationToken ct)
    {
        await foreach (var cmd in _producer.SubscribeCommandsAsync(ct).ConfigureAwait(false))
        {
            if (!CanDispatchToThisMachine(cmd)) continue;

            RelayCommandLease lease;
            try
            {
                lease = await _producer.LeaseCommandAsync(
                    cmd.SessionId, cmd.CmdId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The command remains queued (or its previous lease expires), so a
                // subsequent command-stream reconnect can safely retry the claim.
                continue;
            }

            if (!lease.Acquired) continue;
            if (!_executingCommands.TryAdd(cmd.CmdId, 0)) continue;

            // A Send may run for minutes. Keep the command loop free for
            // Interrupt/Approve while this task renews its lease and later records
            // an explicit terminal result.
            _ = ExecuteLeasedCommandAsync(cmd, ct);
        }
    }

    private bool CanDispatchToThisMachine(RelayCommand cmd)
    {
        var target = cmd.MachineId?.Trim();
        if (cmd.Op == RelayCommandOp.New)
        {
            // New commands must always be explicitly routed. This also prevents a
            // legacy subscriber (which has no machine id) from creating a duplicate
            // session when another desktop is the intended target.
            return !string.IsNullOrWhiteSpace(target)
                && !string.IsNullOrWhiteSpace(_machineId)
                && string.Equals(target, _machineId, StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(target)
            || (!string.IsNullOrWhiteSpace(_machineId)
                && string.Equals(target, _machineId, StringComparison.Ordinal));
    }

    private async Task ExecuteLeasedCommandAsync(RelayCommand cmd, CancellationToken stopCt)
    {
        // The renew loop is deliberately NOT linked to the relay lifetime: a Send
        // turn survives a relay reconnect (see DispatchAsync), and keeping its
        // lease renewed across that reconnect prevents the relay from offering
        // the command to another machine while the turn is still running here.
        using var renewCts = new CancellationTokenSource();
        var renewTask = RenewCommandLeaseUntilCompleteAsync(cmd, renewCts.Token);
        try
        {
            await DispatchAsync(cmd, stopCt).ConfigureAwait(false);
            await _producer.CompleteCommandAsync(
                cmd.SessionId, cmd.CmdId, succeeded: true, error: null, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopCt.IsCancellationRequested)
        {
            // Shutdown is intentionally not terminal: allow the lease to expire so
            // the command can be recovered by the bridge after it reconnects.
        }
        catch (Exception ex)
        {
            try
            {
                await _producer.CompleteCommandAsync(
                    cmd.SessionId,
                    cmd.CmdId,
                    succeeded: false,
                    error: CommandError(ex),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve at-least-once delivery: if the result post fails, the
                // lease expires and the relay will offer the command again.
            }
        }
        finally
        {
            try { renewCts.Cancel(); } catch { /* best-effort */ }
            try { await renewTask.ConfigureAwait(false); } catch { /* best-effort */ }
            _executingCommands.TryRemove(cmd.CmdId, out _);
        }
    }

    private async Task RenewCommandLeaseUntilCompleteAsync(RelayCommand cmd, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(CommandLeaseRenewInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try
            {
                var lease = await _producer.RenewCommandLeaseAsync(
                    cmd.SessionId, cmd.CmdId, CancellationToken.None).ConfigureAwait(false);
                if (!lease.Acquired) break;
            }
            catch
            {
                // A transient result/lease failure must not terminate the bridge
                // task. Retry on the next interval while the original lease lasts.
            }
        }
    }

    private static string CommandError(Exception ex)
    {
        var text = ex.Message.Trim();
        if (text.Length == 0) text = ex.GetType().Name;
        return text.Length <= 240 ? text : text[..240];
    }

    private async Task DispatchAsync(RelayCommand cmd, CancellationToken ct)
    {
        switch (cmd.Op)
        {
            case RelayCommandOp.Send:
                if (!TryParseSend(cmd.PayloadJson, out var text, out var images)
                    || (string.IsNullOrWhiteSpace(text) && images.Count == 0))
                    throw new InvalidOperationException("Invalid Send command payload.");
                // Deliberately NOT the relay-lifetime token: a turn can run for
                // minutes, and the relay connection restarting (network blip,
                // bridge toggle, app-level reconnect loop) must not cancel it.
                // The turn's lifecycle is bounded by an explicit Interrupt
                // command (bridge TurnCts) and bridge disposal on app exit.
                await _bridge.SendAsync(cmd.SessionId, new AgentTurnInput(text, images), CancellationToken.None).ConfigureAwait(false);
                break;
            case RelayCommandOp.Interrupt:
                await _bridge.InterruptAsync(cmd.SessionId, ct).ConfigureAwait(false);
                break;
            case RelayCommandOp.SwitchOptions:
                if (!TryParseSwitchOptions(cmd.PayloadJson, out var sw))
                    throw new InvalidOperationException("Invalid SwitchOptions command payload.");
                await _bridge.SwitchOptionsAsync(cmd.SessionId,
                    sw.Model, sw.ReasoningEffort, sw.PermissionMode, sw.ApprovalPolicy,
                    ct).ConfigureAwait(false);
                break;
            case RelayCommandOp.New:
                if (!CanDispatchToThisMachine(cmd))
                    throw new InvalidOperationException("New command target does not match this desktop.");
                if (!TryParseNew(cmd.PayloadJson, out var nw))
                    throw new InvalidOperationException("Invalid New command payload.");
                await _bridge.NewSessionAsync(
                    cmd.SessionId, nw.BackendId, nw.WorkingDirectory,
                    nw.Model, nw.ReasoningEffort, nw.PermissionMode, nw.ApprovalPolicy,
                    nw.Title, ct, requireExistingWorkingDirectory: true).ConfigureAwait(false);
                break;
            case RelayCommandOp.Close:
                await _bridge.CloseAsync(cmd.SessionId, ct).ConfigureAwait(false);
                await ResetRelayEventsAsync(cmd.SessionId, ct).ConfigureAwait(false);
                break;
            case RelayCommandOp.RefreshHistory:
                await RefreshHistoryAsync(cmd.SessionId, ct).ConfigureAwait(false);
                break;
            case RelayCommandOp.Approve:
                if (!TryParseApprove(cmd.PayloadJson, out var approval))
                    throw new InvalidOperationException("Invalid Approve command payload.");
                await _bridge.ApproveAsync(
                    cmd.SessionId,
                    approval.PermissionId,
                    approval.Choice,
                    ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unsupported relay command: {cmd.Op}.");
        }
    }

    private static bool TryParseSend(string? payloadJson, out string text, out IReadOnlyList<string> images)
    {
        text = string.Empty;
        images = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (doc.RootElement.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                images = imgs.EnumerateArray().Select(i => i.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList();
            return true;
        }
        catch { return false; }
    }

    private static bool TryParseSwitchOptions(string? payloadJson, out SwitchPayload sw)
    {
        sw = default;
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            string? model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            string? effort = root.TryGetProperty("reasoningEffort", out var e) ? e.GetString() : null;
            var perm = TryReadEnum<AgentPermissionMode>(root, "permissionMode")
                ?? TryReadEnum<AgentPermissionMode>(root, "permission_mode");
            var appr = TryReadEnum<CodexApprovalPolicy>(root, "approvalPolicy")
                ?? TryReadEnum<CodexApprovalPolicy>(root, "approval_policy");
            sw = new SwitchPayload(model, effort, perm, appr);
            return true;
        }
        catch { return false; }
    }

    private static bool TryParseApprove(string? payloadJson, out ApprovePayload approval)
    {
        approval = default;
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var permissionId = ReadString(root, "permissionId") ?? ReadString(root, "permission_id");
            var choice = TryReadEnum<AgentPermissionChoice>(root, "choice");
            if (string.IsNullOrWhiteSpace(permissionId) || choice is null)
                return false;
            approval = new ApprovePayload(permissionId, choice.Value);
            return true;
        }
        catch { return false; }
    }

    private static bool TryParseNew(string? payloadJson, out NewPayload nw)
    {
        nw = default;
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var backendId = ReadString(root, "backendId") ?? ReadString(root, "backend_id");
            if (string.IsNullOrWhiteSpace(backendId)) return false;

            var workingDirectory = ReadString(root, "workingDirectory")
                ?? ReadString(root, "working_directory")
                ?? ReadString(root, "cwd")
                ?? string.Empty;
            var title = ReadString(root, "title") ?? ReadString(root, "name");
            var model = ReadString(root, "model");
            var effort = ReadString(root, "reasoningEffort") ?? ReadString(root, "reasoning_effort");
            var perm = TryReadEnum<AgentPermissionMode>(root, "permissionMode")
                ?? TryReadEnum<AgentPermissionMode>(root, "permission_mode");
            var appr = TryReadEnum<CodexApprovalPolicy>(root, "approvalPolicy")
                ?? TryReadEnum<CodexApprovalPolicy>(root, "approval_policy");

            nw = new NewPayload(backendId, workingDirectory, title, model, effort, perm, appr);
            return true;
        }
        catch { return false; }
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static T? TryReadEnum<T>(JsonElement root, string name) where T : struct
    {
        if (!root.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (Enum.TryParse<T>(text, ignoreCase: true, out var parsed))
                return parsed;
            if (int.TryParse(text, out var numeric) && Enum.IsDefined(typeof(T), numeric))
                return (T)Enum.ToObject(typeof(T), numeric);
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            && Enum.IsDefined(typeof(T), number))
        {
            return (T)Enum.ToObject(typeof(T), number);
        }

        return null;
    }

    private static (string? Key, string Name) WorkspaceOf(string workingDirectory)
    {
        var trimmed = (workingDirectory ?? string.Empty).Trim();
        if (trimmed.Length == 0) return (null, "Quick Chat");

        string normalized;
        try { normalized = Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { normalized = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }

        if (normalized.Length == 0) normalized = trimmed;
        var key = OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
        var name = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(name)) name = normalized;
        return (key, name);
    }

    private readonly record struct SwitchPayload(string? Model, string? ReasoningEffort, AgentPermissionMode? PermissionMode, CodexApprovalPolicy? ApprovalPolicy);
    private readonly record struct NewPayload(string BackendId, string WorkingDirectory, string? Title, string? Model, string? ReasoningEffort, AgentPermissionMode? PermissionMode, CodexApprovalPolicy? ApprovalPolicy);
    private readonly record struct ApprovePayload(string PermissionId, AgentPermissionChoice Choice);
}
