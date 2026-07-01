using System.Collections.Concurrent;
using System.Text.Json;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// Headless owner of live Claude Code / Codex sessions — the desktop's agent
/// "bridge". It spawns CLI subprocesses through <see cref="AgentSessionManager"/>,
/// folds the normalized <see cref="AgentEvent"/> stream into serializable
/// <see cref="AgentSessionStateDto"/> state via <see cref="AgentTranscriptReducer"/>,
/// and exposes a process-local command surface (send / interrupt / approve /
/// new / switch-options / close / list) plus a change notification. No WPF, no
/// Dispatcher — it is the canonical state core both the desktop status surface
/// and (Phase 2) the cloud relay read from; the phone renders the same model.
///
/// Lifecycle mirrors <see cref="MolaGPT.Desktop.Services.CloudSyncService"/>: a DI
/// singleton started/stopped explicitly, a <see cref="System.Timers.Timer"/> for
/// batched state publication, and best-effort cleanup on stop. Turns are serial
/// per session (one in-flight turn at a time); concurrent sends on the same
/// session queue behind a per-session gate.
/// </summary>
public sealed partial class AgentBridgeService : IAsyncDisposable
{
    private readonly AgentSessionManager _manager;
    private readonly AgentHistoryReader _history;
    private readonly IAgentConfigProvider _config;

    private readonly ConcurrentDictionary<string, BridgeSession> _sessions = new(StringComparer.Ordinal);
    private readonly System.Timers.Timer _publishTimer;
    private bool _dirty;
    private bool _disposed;

    /// <summary>Raised on the timer thread whenever session state has changed
    /// since the last publication. Subscribers marshal to their own thread
    /// (the WPF status window posts to the Dispatcher).</summary>
    public event Action<IReadOnlyList<AgentSessionStateDto>>? SessionsChanged;

    /// <summary>Raised for every transcript event appended to a session's event
    /// log — the <b>incremental</b> feed the cloud relay ships to the phone
    /// (one envelope per event, with the log's monotonic seq), as opposed to the
    /// 100ms full-snapshot <see cref="SessionsChanged"/>. Fires on the turn
    /// thread; handlers must not block (the relay client posts to a transport).</summary>
    public event Action<string, AgentReplayEntry>? ReplayEventAppended;

    /// <summary>Raised when a session's phase / attention changes (point-in-time
    /// session state, synced as a snapshot alongside the event stream). Also
    /// fires on <see cref="NewSessionAsync"/> so the relay learns of new sessions.</summary>
    public event Action<AgentSessionStateDto>? SessionMetaChanged;

    public AgentBridgeService(
        AgentSessionManager manager,
        AgentHistoryReader history,
        IAgentConfigProvider config)
    {
        _manager = manager;
        _history = history;
        _config = config;
        _publishTimer = new System.Timers.Timer(100) { AutoReset = true };
        _publishTimer.Elapsed += (_, _) => PublishIfDirty();
    }

    /// <summary>Start the state-publication timer. Idempotent.</summary>
    public void Start() => _publishTimer.Start();

    /// <summary>Snapshot every live session now (bypasses the 100ms timer) — used
    /// right after a command so the caller sees the new state without waiting.</summary>
    public IReadOnlyList<AgentSessionStateDto> Snapshot() => BuildSnapshot();

    /// <summary>Snapshot one session, or null if unknown. The relay client reads
    /// this on <c>TurnComplete</c> to ship an <c>AnswerSnapshot</c> (the answer
    /// text accumulated by the reducer), folding per-token deltas into one event.</summary>
    public AgentSessionStateDto? GetSession(string conversationId)
        => _sessions.TryGetValue(conversationId, out var e) ? StateOf(e) : null;

    /// <summary>List the union of live in-memory sessions and recent on-disk
    /// history sessions, de-duplicated by conversation id, newest first. History
    /// sessions are surfaced as <see cref="AgentSessionPhase.Idle"/> and primed
    /// to resume on first send.</summary>
    public async Task<IReadOnlyList<AgentSessionStateDto>> ListSessionsAsync(CancellationToken ct = default)
    {
        // Ensure history sessions are registered as idle bridge entries so a
        // later SendAsync can resume them. Cheap; de-dups by conversationId.
        try
        {
            var recent = await _history.ListRecentAsync(max: 40, ct: ct).ConfigureAwait(false);
            foreach (var e in recent)
            {
                // Skip the throwaway model-discovery session (warm-up spawns a CLI in a
                // temp dir just to read the catalog) so it never shows in the phone's list.
                if (IsModelDiscoveryPath(e.WorkingDirectory)) continue;
                var fileMs = e.LastModified.ToUnixTimeMilliseconds();
                var entry = GetOrCreateEntry(e.SessionId, e.BackendId, e.WorkingDirectory, e.Title,
                    resumeId: e.SessionId,
                    updatedAtMs: fileMs);
                // GetOrCreateEntry's GetOrAdd only sets UpdatedAtMs on first insert, so a
                // session first discovered mid-write (e.g. before the assistant answer
                // was flushed) would freeze its timestamp there and never re-project the
                // finished file. For an entry the bridge has only seen on disk (no live
                // turn), the .jsonl is the source of truth — refresh it to the current
                // file mtime + latest title each listing so the relay re-projects on growth.
                RefreshHistoryEntry(entry, fileMs, e.Title);
            }
        }
        catch { /* history is best-effort */ }

        // Re-register remotely-created sessions that have no on-disk transcript yet
        // (created from the phone but never messaged): without this they live only
        // in the in-memory map and vanish on restart. resumeId stays null — a
        // never-started session binds a fresh CLI session on its first send.
        try
        {
            foreach (var stub in _config.ListPersistedSessions())
            {
                if (_sessions.ContainsKey(stub.ConversationId)) continue;
                var entry = GetOrCreateEntry(stub.ConversationId, stub.BackendId, stub.WorkingDirectory,
                    stub.Title, resumeId: null, updatedAtMs: stub.CreatedAtMs);
                if (!string.IsNullOrWhiteSpace(stub.Model)) entry.Model = stub.Model;
            }
        }
        catch { /* stubs are best-effort */ }

        // Warm up each present backend's model catalog (once) so history-loaded
        // sessions — which have no live process until their first turn — still show
        // the real model list in the phone's picker.
        foreach (var backendId in _sessions.Values.Select(e => e.BackendId).Distinct(StringComparer.Ordinal))
            BeginWarmUpModelCatalog(backendId);

        return BuildSnapshot();
    }

    /// <summary>Load coarse transcript turns for a discovered on-disk history
    /// session. The relay uses this only to backfill sessions that have no relay
    /// event history yet; live sessions keep using the bridge event log.</summary>
    public async Task<IReadOnlyList<AgentHistoryTurn>> LoadHistoryTurnsAsync(
        string conversationId,
        int maxTurns = 30,
        CancellationToken ct = default)
    {
        try
        {
            var recent = await _history.ListRecentAsync(max: 120, ct: ct).ConfigureAwait(false);
            var entry = recent.FirstOrDefault(e => string.Equals(e.SessionId, conversationId, StringComparison.Ordinal));
            return entry is null
                ? Array.Empty<AgentHistoryTurn>()
                : await _history.LoadTurnsAsync(entry, maxTurns, ct).ConfigureAwait(false);
        }
        catch
        {
            return Array.Empty<AgentHistoryTurn>();
        }
    }

    /// <summary>Create a brand-new agent session (does not spawn the CLI yet —
    /// that happens lazily on the first send). Returns the initial state.</summary>
    public Task<AgentSessionStateDto> NewSessionAsync(
        string backendId, string workingDirectory,
        string? model, string? reasoningEffort, AgentPermissionMode? permissionMode, CodexApprovalPolicy? approvalPolicy,
        string? title = null, CancellationToken ct = default)
        => NewSessionAsync(null, backendId, workingDirectory, model, reasoningEffort, permissionMode, approvalPolicy, title, ct);

    /// <summary>Create a brand-new agent session with a caller-supplied id.
    /// The phone uses this for optimistic routing: it can enqueue <c>New</c>
    /// under the same id it will subscribe to, then wait for the bridge's meta
    /// snapshot to confirm creation.</summary>
    public Task<AgentSessionStateDto> NewSessionAsync(
        string? conversationId, string backendId, string workingDirectory,
        string? model, string? reasoningEffort, AgentPermissionMode? permissionMode, CodexApprovalPolicy? approvalPolicy,
        string? title = null, CancellationToken ct = default)
    {
        conversationId = NormalizeRequestedConversationId(conversationId);
        workingDirectory = ResolveInitialWorkingDirectory(workingDirectory, title);
        _config.SetWorkingDirectory(conversationId, workingDirectory);

        var entry = GetOrCreateEntry(conversationId, backendId, workingDirectory,
            title ?? DefaultTitle(backendId), resumeId: null);
        Mutate(entry, e =>
        {
            e.Model = ResolveModel(model);
            e.ReasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort;
            e.PermissionMode = permissionMode ?? _config.PermissionMode;
            e.ApprovalPolicy = backendId == CodexBackend.BackendId
                ? approvalPolicy ?? CodexApprovalPolicy.OnRequest
                : null;
        });

        // Persist a durable stub so a remotely-created session that is never messaged
        // (in-memory only until its first send spawns the CLI + writes a transcript)
        // survives a desktop restart instead of vanishing.
        _config.SaveSession(new AgentPersistedSession(
            conversationId, backendId, entry.Title, workingDirectory, ResolveModel(model), NowMs()));

        // Announce the new session so the relay learns of it (the phase didn't
        // change from Idle, so Mutate wouldn't fire — new-session discovery is
        // also surfaced via the session-list endpoint, but this gives a prompt
        // meta snapshot to live subscribers).
        SessionMetaChanged?.Invoke(StateOf(entry));
        MarkDirty();
        // Warm up this backend's model catalog (once) so the phone's picker shows the
        // real list even before the first turn spawns the session's own process.
        BeginWarmUpModelCatalog(backendId);
        return Task.FromResult(StateOf(entry));
    }

    private static string NormalizeRequestedConversationId(string? value)
    {
        var id = (value ?? string.Empty).Trim();
        if (id.Length is > 0 and <= 128
            && id.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'))
            return id;
        // Dashed ("D") form so it is a valid UUID for Claude's --session-id binding.
        return Guid.NewGuid().ToString("D");
    }

    private static string ResolveInitialWorkingDirectory(string? workingDirectory, string? title)
    {
        var trimmed = (workingDirectory ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(trimmed))
            return trimmed;

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var root = string.IsNullOrWhiteSpace(documents)
            ? Path.Combine(Path.GetTempPath(), "MolaGPT", "Agent Chats")
            : Path.Combine(documents, "MolaGPT", "Agent Chats");
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        var slug = SlugForDirectory(title) ?? "new-chat";
        var path = Path.Combine(root, date, $"{slug}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string? SlugForDirectory(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0) return null;

        var chars = new List<char>(Math.Min(text.Length, 48));
        var lastWasDash = false;
        foreach (var ch in text)
        {
            if (chars.Count >= 48) break;
            if (char.IsLetterOrDigit(ch))
            {
                chars.Add(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                chars.Add('-');
                lastWasDash = true;
            }
        }

        var slug = new string(chars.ToArray()).Trim('-');
        return slug.Length == 0 ? null : slug;
    }

    /// <summary>Send one user turn to a session, streaming events into its state.
    /// Serial per session: a second send on the same session awaits the first.
    /// Returns when the turn completes (or fails / is interrupted).</summary>
    public Task SendAsync(string conversationId, string text, CancellationToken ct = default)
        => SendAsync(conversationId, AgentTurnInput.TextOnly(text), ct);

    public async Task SendAsync(string conversationId, AgentTurnInput input, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(conversationId, out var entry))
            throw new InvalidOperationException($"未知 agent 会话：{conversationId}");

        using var gate = await entry.Gate.AcquireAsync(ct).ConfigureAwait(false);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        entry.TurnCts = cts; // remember so InterruptAsync can cancel this turn
        var token = cts.Token;

        var reducer = entry.Reducer;
        reducer.AddUser(input.Text);
        LogAndPublish(entry, new UserTurnSubmitted(input.Text));
        reducer.BeginPending("连接中…");
        LogAndPublish(entry, new PendingShown("连接中…"));
        Mutate(entry, e => { e.Phase = AgentSessionPhase.Spawning; e.NeedsAttention = false; });
        MarkDirty();

        IAgentSession? live = null;
        try
        {
            // A mid-turn option change deferred the respawn (so it wouldn't kill the
            // previous in-flight turn). Apply it now, at this turn boundary, before
            // spawning — the new process picks up the new model/mode and --resume.
            if (entry.PendingRespawn)
            {
                entry.PendingRespawn = false;
                entry.LiveSession = null;
                try { await _manager.CloseAsync(entry.BackendId, conversationId).ConfigureAwait(false); }
                catch { /* best-effort */ }
            }

            live = await _manager.GetOrCreateAsync(
                entry.BackendId, conversationId, entry.WorkingDirectory, entry.ResumeId,
                entry.Model, entry.ReasoningEffort,
                entry.PermissionMode, entry.ApprovalPolicy, token).ConfigureAwait(false);
            entry.LiveSession = live;
            Mutate(entry, e => e.Phase = AgentSessionPhase.Running);
            MarkDirty();

            var lastFlush = Environment.TickCount64;
            await foreach (var ev in live.SendTurnAsync(input, token).ConfigureAwait(false))
            {
                // Apply to the reducer BEFORE logging/publishing, so an event whose
                // translation reads the reducer's snapshot (e.g. TurnComplete →
                // AnswerSnapshot) sees the just-applied state, including any
                // previously-buffered deltas we flush here.
                reducer.Apply(ev);
                if (ev.Kind is AgentEventKind.TurnComplete or AgentEventKind.Error)
                    reducer.Flush();
                LogAndPublish(entry, new CliEventAppended(ev));

                if (ev.Kind == AgentEventKind.PermissionRequest)
                    Mutate(entry, e => { e.Phase = AgentSessionPhase.Waiting; e.NeedsAttention = true; });

                // Batched flush — same cadence idea as the WPF reducer (33ms),
                // coarsened since headless state publication is timer-driven.
                var now = Environment.TickCount64;
                if (now - lastFlush >= 100)
                {
                    reducer.Flush();
                    lastFlush = now;
                    MarkDirty();
                }
            }

            reducer.EndTurn();
            LogAndPublish(entry, new TurnFinalized());
            Mutate(entry, e => e.Phase = AgentSessionPhase.Completed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Interrupted by the user (our linked cts, not the caller's). The
            // turn ends quietly; state already reflects what was streamed.
            reducer.EndTurn();
            LogAndPublish(entry, new TurnFinalized());
            Mutate(entry, e => e.Phase = e.NeedsAttention ? AgentSessionPhase.Waiting : AgentSessionPhase.Completed);
        }
        catch (Exception ex)
        {
            var failure = AgentEvent.Failure(ex.Message);
            LogAndPublish(entry, new CliEventAppended(failure));
            reducer.Apply(failure);
            Mutate(entry, e => e.Phase = AgentSessionPhase.Failed);
        }
        finally
        {
            // Follow the CLI's real session id so a later respawn resumes THIS
            // conversation. Claude self-assigns an id when started without
            // --session-id and can rotate it; the bridge keeps conversationId
            // stable (what the phone subscribes to) but tracks the CLI id for
            // the next --resume. Codex returns its stable threadId.
            if (live?.CurrentSessionId is { } realId && !string.IsNullOrWhiteSpace(realId))
                entry.ResumeId = realId;
            entry.TurnCts = null;
            MarkDirty();
        }
    }

    /// <summary>Append a replay event to the session's log and publish it to
    /// <see cref="ReplayEventAppended"/> subscribers (the relay client). The seq
    /// is assigned by the log and carried in the entry — bridge-authoritative.</summary>
    private void LogAndPublish(BridgeSession e, AgentReplayEvent ev)
    {
        var entry = e.EventLog.Append(ev);
        ReplayEventAppended?.Invoke(e.ConversationId, entry);
    }

    /// <summary>Interrupt the in-flight turn for a session (best-effort).</summary>
    public async Task InterruptAsync(string conversationId, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(conversationId, out var entry)) return;
        try { entry.TurnCts?.Cancel(); } catch { /* best-effort */ }
        if (entry.LiveSession is { } live)
        {
            try { await live.InterruptAsync(ct).ConfigureAwait(false); } catch { /* best-effort */ }
        }
    }

    /// <summary>Resolve a pending permission prompt by sending the choice back
    /// into the live backend protocol.</summary>
    public async Task ApproveAsync(string conversationId, string permissionId, AgentPermissionChoice choice, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(conversationId, out var entry))
            return;
        if (entry.LiveSession is null)
            throw new InvalidOperationException("没有可处理权限回传的活动 CLI 会话。");

        await entry.LiveSession.ApproveAsync(permissionId, choice, ct).ConfigureAwait(false);

        Mutate(entry, e =>
        {
            e.NeedsAttention = false;
            if (e.Phase == AgentSessionPhase.Waiting) e.Phase = AgentSessionPhase.Running;
        });
        MarkDirty();
    }

    /// <summary>Change a session's model / reasoning effort / permission / approval.
    /// <para>Model switches go through the live session's <see cref="IAgentSession.SetModelAsync"/>
    /// (Claude <c>set_model</c> control request; Codex per-turn override) so the running
    /// process keeps its context — no restart. Effort / permission / approval are baked
    /// in at spawn, so those still close the session and let the next send respawn under
    /// the new options (deferred to a turn boundary if a turn is in flight).</para></summary>
    public async Task SwitchOptionsAsync(string conversationId,
        string? model, string? reasoningEffort, AgentPermissionMode? permissionMode, CodexApprovalPolicy? approvalPolicy,
        CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(conversationId, out var entry)) return;

        // Try to switch the model on the RUNNING process first (keeps context).
        var modelSwitchedLive = false;
        if (model is not null && entry.LiveSession is { IsAlive: true } live)
        {
            try { modelSwitchedLive = await live.SetModelAsync(ResolveModel(model), ct).ConfigureAwait(false); }
            catch { modelSwitchedLive = false; }
        }

        var changed = false;
        var needsRespawn = false;
        Mutate(entry, e =>
        {
            if (model is not null)
            {
                e.Model = ResolveModel(model);
                changed = true;
                if (!modelSwitchedLive) needsRespawn = true; // no live session (or backend can't hot-switch)
            }
            if (reasoningEffort is not null) { e.ReasoningEffort = reasoningEffort; changed = true; needsRespawn = true; }
            if (permissionMode is not null) { e.PermissionMode = permissionMode.Value; changed = true; needsRespawn = true; }
            if (approvalPolicy is not null && e.BackendId == CodexBackend.BackendId)
            { e.ApprovalPolicy = approvalPolicy; changed = true; needsRespawn = true; }
        });
        if (!changed) return;

        if (needsRespawn)
        {
            // Don't kill an in-flight turn — disposing the live process mid-turn surfaces
            // on the phone as "Claude Code process ended unexpectedly". If a turn is
            // running, defer: the NEXT send respawns under the new options at a turn
            // boundary. Otherwise drop the idle process now. Keep ResumeId either way so
            // the respawn RESUMES the same conversation (nulling it was an earlier bug).
            if (entry.TurnCts is not null)
            {
                entry.PendingRespawn = true;
            }
            else
            {
                entry.LiveSession = null;
                try { await _manager.CloseAsync(entry.BackendId, conversationId).ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
        }
        // else: the live model switch was enough — the process stays up, context intact.

        SessionMetaChanged?.Invoke(StateOf(entry));
        MarkDirty();
    }

    /// <summary>Drop a session from the bridge (does not delete on-disk history).</summary>
    public async Task CloseAsync(string conversationId, CancellationToken ct = default)
    {
        _config.ForgetSession(conversationId);
        if (!_sessions.TryRemove(conversationId, out var entry)) return;
        try { entry.TurnCts?.Cancel(); } catch { /* best-effort */ }
        try { await _manager.CloseAsync(entry.BackendId, conversationId).ConfigureAwait(false); }
        catch { /* best-effort */ }
        MarkDirty();
    }

    // ---- internals --------------------------------------------------------

    private BridgeSession GetOrCreateEntry(
        string conversationId, string backendId, string workingDirectory,
        string title, string? resumeId, long? updatedAtMs = null)
        => _sessions.GetOrAdd(conversationId, id => new BridgeSession(id, backendId, workingDirectory, title)
        {
            ResumeId = resumeId,
            PermissionMode = _config.PermissionMode,
            ApprovalPolicy = backendId == CodexBackend.BackendId ? CodexApprovalPolicy.OnRequest : null,
            UpdatedAtMs = updatedAtMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

    /// <summary>Keep a discovered on-disk (history) entry's <c>UpdatedAtMs</c> and
    /// title in sync with the live file. Only touches entries the bridge has never
    /// run a live turn on (no <see cref="BridgeSession.LiveSession"/>, empty event
    /// log) — a bridge-owned live session keeps its own monotonically-bumped
    /// timestamp. UpdatedAtMs only moves forward. This is what lets the relay's
    /// history projection re-fire after the underlying .jsonl grows.</summary>
    private static void RefreshHistoryEntry(BridgeSession entry, long fileMs, string title)
    {
        lock (entry.StateLock)
        {
            if (entry.LiveSession is not null || entry.EventLog.Seq > 0) return;
            if (fileMs > entry.UpdatedAtMs) entry.UpdatedAtMs = fileMs;
            if (!string.IsNullOrWhiteSpace(title) && title != entry.Title) entry.Title = title;
        }
    }

    private static string? ResolveModel(string? model) =>
        string.IsNullOrWhiteSpace(model) || model == DefaultModelLabel ? null : model;

    /// <summary>Sentinel that means "leave the CLI on its own default model".</summary>
    public const string DefaultModelLabel = "默认（CLI 配置）";

    private static string DefaultTitle(string backendId) =>
        backendId == CodexBackend.BackendId ? "新 Codex 会话" : "新 Claude Code 会话";

    private IReadOnlyList<AgentSessionStateDto> BuildSnapshot()
        => _sessions.Values
            .OrderByDescending(e => e.UpdatedAtMs)
            .Select(StateOf)
            .ToList();

    private AgentSessionStateDto StateOf(BridgeSession e)
    {
        // Hold the per-session lock for the whole read so a concurrent turn
        // thread cannot publish a half-mutated combination (e.g. Phase updated
        // but Model still mid-switch). EventLog.Seq and Reducer.Blocks each take
        // their own locks underneath — no lock-ordering hazard since those never
        // reach back for StateLock.
        lock (e.StateLock)
        {
            var model = e.Model ?? ResolveConfiguredModel(e.BackendId);
            return new AgentSessionStateDto(
                ConversationId: e.ConversationId,
                BackendId: e.BackendId,
                Title: e.Title,
                WorkingDirectory: e.WorkingDirectory,
                ResumeSessionId: e.ResumeId,
                Model: model,
                ReasoningEffort: e.ReasoningEffort,
                PermissionMode: e.PermissionMode,
                ApprovalPolicy: e.ApprovalPolicy,
                Phase: e.Phase,
                NeedsAttention: e.NeedsAttention,
                Seq: e.EventLog.Seq,
                ModeLabel: ComputeModeLabel(e, model),
                UpdatedAtMs: e.UpdatedAtMs,
                Transcript: e.Reducer.Blocks.ToList(),
                AvailableModels: CatalogFor(e.BackendId, e.LiveSession?.AvailableModels));
        }
    }

    /// <summary>Run a state mutation under the per-session lock and bump
    /// <c>UpdatedAtMs</c> so registry ordering / reconnect ordering stays fresh
    /// on every change, not just at turn end. If phase or attention changed, fires
    /// <see cref="SessionMetaChanged"/> (outside the lock) so the relay ships a
    /// fresh snapshot without blocking the turn thread on the handler.</summary>
    private void Mutate(BridgeSession e, Action<BridgeSession> fn)
    {
        AgentSessionStateDto? toPublish = null;
        lock (e.StateLock)
        {
            var oldPhase = e.Phase;
            var oldAttention = e.NeedsAttention;
            fn(e);
            e.UpdatedAtMs = NowMs();
            if (e.Phase != oldPhase || e.NeedsAttention != oldAttention)
                toPublish = StateOf(e); // StateOf re-enters e.StateLock — Monitor is reentrant
        }
        if (toPublish is not null)
            SessionMetaChanged?.Invoke(toPublish);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Reproduces the old <c>AgentSessionItem.ModeLabel</c> so the status
    /// surface and the phone show the same short label, e.g. "opus · 自动改文件".</summary>
    private static string ComputeModeLabel(BridgeSession e, string? resolvedModel = null)
    {
        var isCodex = e.BackendId == CodexBackend.BackendId;
        var model = resolvedModel ?? e.Model ?? "默认";
        var posture = e.PermissionMode switch
        {
            AgentPermissionMode.Plan => isCodex ? "只读" : "计划",
            AgentPermissionMode.AcceptEdits => isCodex ? "可写" : "自动改文件",
            AgentPermissionMode.BypassPermissions => "完全放行",
            _ => isCodex ? "可写" : "逐项询问"
        };
        if (!isCodex) return $"{model} · {posture}";
        var approval = e.ApprovalPolicy ?? CodexApprovalPolicy.OnRequest;
        var approvalLabel = approval switch
        {
            CodexApprovalPolicy.Untrusted => "严格审批",
            CodexApprovalPolicy.Never => "免审批",
            _ => "按需审批"
        };
        return $"{model} · {posture} · {approvalLabel}";
    }

    private static string? ResolveConfiguredModel(string backendId)
    {
        try
        {
            return backendId switch
            {
                CodexBackend.BackendId => ReadCodexConfiguredModel(),
                ClaudeCodeBackend.BackendId => ReadClaudeConfiguredModel(),
                _ => null
            };
        }
        catch { return null; }
    }

    private static string? ReadCodexConfiguredModel()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "config.toml");
        if (!File.Exists(path)) return null;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (!line.StartsWith("model", StringComparison.Ordinal) || line.StartsWith("model_", StringComparison.Ordinal))
                continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            return Unquote(line[(eq + 1)..].Trim());
        }
        return null;
    }

    private static string? ReadClaudeConfiguredModel()
    {
        var envModel = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL");
        if (!string.IsNullOrWhiteSpace(envModel)) return envModel;

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "settings.json");
        if (!File.Exists(path)) return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
            return model.GetString();
        if (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object
            && env.TryGetProperty("ANTHROPIC_MODEL", out var envJsonModel)
            && envJsonModel.ValueKind == JsonValueKind.String)
            return envJsonModel.GetString();
        return null;
    }

    private static string? Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private void MarkDirty() => Volatile.Write(ref _dirty, true);

    private void PublishIfDirty()
    {
        if (!Volatile.Read(ref _dirty) || _disposed) return;
        _dirty = false;
        try { SessionsChanged?.Invoke(BuildSnapshot()); }
        catch { /* subscribers must not throw the timer */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _publishTimer.Stop();
        _publishTimer.Dispose();
        try { _catalogCts.Cancel(); } catch { /* best-effort */ }
        _catalogCts.Dispose();
        foreach (var entry in _sessions.Values)
        {
            try { entry.TurnCts?.Cancel(); } catch { /* best-effort */ }
        }
        await _manager.DisposeAsync().ConfigureAwait(false);
        _sessions.Clear();
    }

    /// <summary>Per-session state held by the bridge.</summary>
    private sealed class BridgeSession
    {
        public BridgeSession(string id, string backendId, string cwd, string title)
        {
            ConversationId = id;
            BackendId = backendId;
            WorkingDirectory = cwd;
            Title = title;
            Reducer = new AgentTranscriptReducer();
            EventLog = new AgentEventLog();
            Gate = new AsyncSemaphore();
            StateLock = new object();
        }

        public string ConversationId { get; }
        public string BackendId { get; }
        public string WorkingDirectory { get; set; }
        public string Title { get; set; }
        public string? ResumeId { get; set; }
        public string? Model { get; set; }
        public string? ReasoningEffort { get; set; }
        public AgentPermissionMode PermissionMode { get; set; }
        public CodexApprovalPolicy? ApprovalPolicy { get; set; }
        public AgentSessionPhase Phase { get; set; } = AgentSessionPhase.Idle;
        public bool NeedsAttention { get; set; }
        public long UpdatedAtMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public AgentTranscriptReducer Reducer { get; }
        public AgentEventLog EventLog { get; }
        public AsyncSemaphore Gate { get; }
        public IAgentSession? LiveSession { get; set; }
        public CancellationTokenSource? TurnCts { get; set; }

        /// <summary>An option change arrived mid-turn; respawn under the new options
        /// at the next turn boundary instead of killing the running process.</summary>
        public bool PendingRespawn { get; set; }

        /// <summary>Serializes reads (StateOf) against writes (turn loop / option
        /// switch) of the snapshot fields above. The reducer and event log guard
        /// themselves; this protects the bridge-level session state.</summary>
        public object StateLock { get; }
    }

    /// <summary>Minimal awaitable per-session lock so concurrent sends serialize
    /// without pulling in a heavier primitive. Single-writer; the bridge issues
    /// one gate per session and releases on dispose.</summary>
    private sealed class AsyncSemaphore : IDisposable
    {
        private readonly SemaphoreSlim _sem = new(1, 1);
        public Task<Releaser> AcquireAsync(CancellationToken ct)
        {
            var t = _sem.WaitAsync(ct);
            return t.IsCompletedSuccessfully
                ? Task.FromResult(new Releaser(_sem))
                : AcquireAsyncCore(t);
        }
        private async Task<Releaser> AcquireAsyncCore(Task wait)
        {
            await wait.ConfigureAwait(false);
            return new Releaser(_sem);
        }
        public void Dispose() => _sem.Dispose();
        public readonly struct Releaser : IDisposable
        {
            private readonly SemaphoreSlim _sem;
            public Releaser(SemaphoreSlim sem) => _sem = sem;
            public void Dispose() => _sem.Release();
        }
    }
}
