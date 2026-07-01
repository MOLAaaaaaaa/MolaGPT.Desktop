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
/// interrupt / switch-options to the bridge and acking each command so the relay
/// drops it (no redelivery on reconnect).
///
/// Turns are dispatched fire-and-forget: a command is acked as soon as the bridge
/// has started handling it, not after the (possibly long) turn finishes. The
/// bridge's per-session gate serializes concurrent sends on the same session.
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
    private const int HistoryBackfillMaxTurns = 30;

    private readonly AgentBridgeService _bridge;
    private readonly IRelayProducer _producer;
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _awaitingTerminal = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastRelaySeq = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _relayActivityAtMs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingAnswerText = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingThinkingText = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _historyProjectionQueue = new();
    private readonly ConcurrentDictionary<string, byte> _queuedHistoryProjections = new(StringComparer.Ordinal);
    private Task _eventPostTail = Task.CompletedTask;
    private CancellationTokenSource? _cts;
    private int _activeHistoryProjection;
    private bool _relayCursorsSeeded;

    public AgentRelayClient(AgentBridgeService bridge, IRelayProducer producer)
    {
        _bridge = bridge;
        _producer = producer;
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
                AppendThinking(sessionId, ev.Text);
                return null;

            case AgentEventKind.TextDelta:
                FlushThinking(sessionId, seq);
                AppendAnswer(sessionId, ev.Text);
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

    private void AppendThinking(string sessionId, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
            _pendingThinkingText[sessionId] = _pendingThinkingText.GetValueOrDefault(sessionId) + text;
    }

    private void AppendAnswer(string sessionId, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
            _pendingAnswerText[sessionId] = _pendingAnswerText.GetValueOrDefault(sessionId) + text;
    }

    private void FlushTurnBuffers(string sessionId, long bridgeSeq)
    {
        FlushThinking(sessionId, bridgeSeq);
        FlushAnswer(sessionId, bridgeSeq);
    }

    private void FlushThinking(string sessionId, long bridgeSeq)
    {
        string? text;
        lock (_gate)
        {
            if (!_pendingThinkingText.TryGetValue(sessionId, out text) || string.IsNullOrWhiteSpace(text))
                return;
            _pendingThinkingText.Remove(sessionId);
        }
        Post(Envelope(sessionId, bridgeSeq, new ThinkingSnapshotEvent(text)));
    }

    private void FlushAnswer(string sessionId, long bridgeSeq)
    {
        string? text;
        lock (_gate)
        {
            if (!_pendingAnswerText.TryGetValue(sessionId, out text) || string.IsNullOrWhiteSpace(text))
                return;
            _pendingAnswerText.Remove(sessionId);
        }
        Post(Envelope(sessionId, bridgeSeq, new AnswerSnapshotEvent(text)));
    }

    private void ResetTurnBuffers(string sessionId)
    {
        lock (_gate)
        {
            _pendingAnswerText.Remove(sessionId);
            _pendingThinkingText.Remove(sessionId);
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
            if (LastRelaySeqOrBridgeSeq(s.ConversationId, s.Seq) > 0)
                _ = PostMetaSafeAsync(BuildMeta(s), CancellationToken.None);
            EnqueueHistoryProjection(s.ConversationId);
            return;
        }

        _ = PostMetaSafeAsync(BuildMeta(s), CancellationToken.None);
    }

    private async Task<IReadOnlyList<AgentSessionStateDto>> PublishInitialSessionsAsync(CancellationToken ct)
    {
        return await PublishMachineSnapshotAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<AgentSessionStateDto>> PublishMachineSnapshotAsync(CancellationToken ct)
    {
        IReadOnlyList<AgentSessionStateDto> sessions;
        try { sessions = await _bridge.ListSessionsAsync(ct).ConfigureAwait(false); }
        catch { return Array.Empty<AgentSessionStateDto>(); }

        foreach (var s in sessions)
        {
            ct.ThrowIfCancellationRequested();
            if (NeedsHistoryProjection(s))
            {
                if (LastRelaySeqOrBridgeSeq(s.ConversationId, s.Seq) > 0)
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
            if (ids.Count > 0)
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

        await PublishHistoryProjectionAsync(
            state.ConversationId,
            state.UpdatedAtMs,
            turns,
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

        await _producer.ReplaceSessionEventsAsync(sessionId, envelopes, ct).ConfigureAwait(false);

        // Advance the cursor only AFTER the atomic replace succeeds, so a concurrent
        // snapshot never sees a reset cursor and re-enqueues another projection.
        lock (_gate)
        {
            _lastRelaySeq[sessionId] = seq;
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
            var previous = _lastRelaySeq.GetValueOrDefault(sessionId);
            var relaySeq = Math.Max(bridgeSeq, previous + 1);
            _lastRelaySeq[sessionId] = relaySeq;
            return new RelayEventEnvelope(sessionId, relaySeq, ev);
        }
    }

    private long LastRelaySeqOrBridgeSeq(string sessionId, long bridgeSeq)
    {
        lock (_gate)
            return Math.Max(bridgeSeq, _lastRelaySeq.GetValueOrDefault(sessionId));
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
            _relayActivityAtMs[sessionId] = 0;
        }
    }

    private static bool IsHistoryProjection(AgentSessionStateDto s)
        => s.ResumeSessionId is not null
           && s.Seq <= 0
           && s.Phase == AgentSessionPhase.Idle;

    private bool NeedsHistoryProjection(AgentSessionStateDto s)
        => IsHistoryProjection(s)
           && (LastRelaySeqOrBridgeSeq(s.ConversationId, s.Seq) <= 0
               || LastRelayActivity(s.ConversationId) < s.UpdatedAtMs);

    private bool ShouldHeartbeat(AgentSessionStateDto s)
        => !IsHistoryProjection(s) || LastRelaySeqOrBridgeSeq(s.ConversationId, s.Seq) > 0;

    private RelaySessionMeta BuildMeta(AgentSessionStateDto s)
    {
        var seq = LastRelaySeqOrBridgeSeq(s.ConversationId, s.Seq);
        var workspace = WorkspaceOf(s.WorkingDirectory);
        // updatedAtMs is the machine snapshot heartbeat used for online/offline;
        // activityAtMs is the real session activity time used for sorting.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new RelaySessionMeta(
            s.ConversationId, s.BackendId, s.Title, s.WorkingDirectory,
            workspace.Key, workspace.Name,
            s.Model, s.ReasoningEffort, s.PermissionMode, s.ApprovalPolicy,
            s.Phase, s.NeedsAttention, seq, nowMs, s.UpdatedAtMs,
            s.AvailableModels);
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
        try { await _producer.PostEventAsync(envelope, CancellationToken.None).ConfigureAwait(false); }
        catch { /* best-effort — relay drops this event; reconnect replay resends from seq */ }
    }

    private async Task PostMetaSafeAsync(RelaySessionMeta meta, CancellationToken ct = default)
    {
        try
        {
            await _producer.PostMetaAsync(meta, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _lastRelaySeq[meta.ConversationId] = Math.Max(meta.Seq, _lastRelaySeq.GetValueOrDefault(meta.ConversationId));
                // Deliberately do NOT advance _relayActivityAtMs here. That cursor must
                // track content we have actually PROJECTED (set only by
                // PublishHistoryProjectionAsync / seeded from the server). Bumping it on
                // a mere meta post would mark a history session "up to date" before — or
                // instead of — its projection, so a pending or failed projection would
                // never retry and the phone would be stuck on a stale transcript.
            }
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
            // Dispatch on the threadpool and ack immediately — the turn may run
            // long, and the bridge gate serializes per-session sends.
            _ = DispatchAsync(cmd, ct);
            try { await _producer.AckCommandAsync(cmd.SessionId, cmd.CmdId, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
    }

    private async Task DispatchAsync(RelayCommand cmd, CancellationToken ct)
    {
        try
        {
            switch (cmd.Op)
            {
                case RelayCommandOp.Send:
                    if (TryParseSend(cmd.PayloadJson, out var text, out var images))
                        await _bridge.SendAsync(cmd.SessionId, new AgentTurnInput(text, images), CancellationToken.None).ConfigureAwait(false);
                    break;
                case RelayCommandOp.Interrupt:
                    await _bridge.InterruptAsync(cmd.SessionId, CancellationToken.None).ConfigureAwait(false);
                    break;
                case RelayCommandOp.SwitchOptions:
                    if (TryParseSwitchOptions(cmd.PayloadJson, out var sw))
                        await _bridge.SwitchOptionsAsync(cmd.SessionId,
                            sw.Model, sw.ReasoningEffort, sw.PermissionMode, sw.ApprovalPolicy,
                            CancellationToken.None).ConfigureAwait(false);
                    break;
                case RelayCommandOp.New:
                    if (TryParseNew(cmd.PayloadJson, out var nw))
                        await _bridge.NewSessionAsync(
                            cmd.SessionId, nw.BackendId, nw.WorkingDirectory,
                            nw.Model, nw.ReasoningEffort, nw.PermissionMode, nw.ApprovalPolicy,
                            nw.Title, CancellationToken.None).ConfigureAwait(false);
                    break;
                case RelayCommandOp.Close:
                    await _bridge.CloseAsync(cmd.SessionId, CancellationToken.None).ConfigureAwait(false);
                    await ResetRelayEventsAsync(cmd.SessionId, CancellationToken.None).ConfigureAwait(false);
                    break;
                case RelayCommandOp.RefreshHistory:
                    await RefreshHistoryAsync(cmd.SessionId, CancellationToken.None).ConfigureAwait(false);
                    break;
                case RelayCommandOp.Approve:
                    if (TryParseApprove(cmd.PayloadJson, out var approval))
                        await _bridge.ApproveAsync(
                            cmd.SessionId,
                            approval.PermissionId,
                            approval.Choice,
                            CancellationToken.None).ConfigureAwait(false);
                    break;
            }
        }
        catch
        {
            // The bridge records turn failures as Failed phase + error events,
            // which the relay already forwards. Swallow so the dispatch task
            // never surfaces an unobserved exception.
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
