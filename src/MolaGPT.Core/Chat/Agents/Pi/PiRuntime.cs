using System.Net.Http;

namespace MolaGPT.Core.Chat.Agents.Pi;

/// <summary>
/// The one place Pi sidecars live.
///
/// Replaces "one process per conversation", which was measured to be a bad trade:
/// each process costs ~95 MB resident and ~2.7s to boot, they share nothing, and
/// the only thing the arrangement bought was skipping a ~60ms
/// <c>switch_session</c> when returning to a chat. Five open conversations meant
/// 441 MB of Node.
///
/// So sidecars are pooled and leased instead. A lease points a process at one
/// conversation's transcript, binds the loopback shim and tool bridge to that
/// turn, and releases the process when the turn ends. The pool is capped, which
/// is what actually bounds memory — idle reclaim only decides how long an unused
/// process lingers.
///
/// Concurrency is real here: a turn can still be streaming in the background
/// while the user sends another. That is why the shim and the bridge route by the
/// leased process's own token rather than holding a single "current" target.
/// </summary>
public sealed class PiRuntime : IAsyncDisposable
{
    /// <summary>Ceiling on live sidecars, and therefore on concurrent turns. Three
    /// is enough for "one streaming in the background while you work in another
    /// chat" without letting a busy session climb back to the old per-conversation
    /// footprint.</summary>
    public const int DefaultMaxSidecars = 3;

    /// <summary>How long an unused sidecar lingers before it is reclaimed. The pool
    /// cap bounds the worst case; this decides how quickly the common case falls
    /// back to zero.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromMinutes(1);

    private readonly PiWorkLlmShim _shim;
    private readonly PiWorkToolBridge _bridge;
    private readonly Action<string>? _log;
    private readonly int _maxSidecars;

    /// <summary>Admission control: holding a slot is what entitles a turn to a
    /// sidecar, so the pool can never exceed the cap and a fourth concurrent turn
    /// waits rather than spawning.</summary>
    private readonly SemaphoreSlim _slots;

    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Timer _idleSweep;
    private int _sidecarsCreated;

    /// <summary>How many sidecar processes have been started. The number to watch
    /// when asking whether something is churning them.</summary>
    public int SidecarsCreated => Volatile.Read(ref _sidecarsCreated);

    /// <summary>Live sidecar processes right now.</summary>
    public int LiveSidecars
    {
        get { lock (_gate) return _entries.Count; }
    }

    public PiRuntime(HttpClient http, Action<string>? log = null, int maxSidecars = DefaultMaxSidecars)
    {
        _log = log;
        _maxSidecars = Math.Max(1, maxSidecars);
        _slots = new SemaphoreSlim(_maxSidecars, _maxSidecars);
        _shim = new PiWorkLlmShim(http, log);
        _bridge = new PiWorkToolBridge(log);
        _idleSweep = new Timer(_ => SweepIdle(), null, IdleSweepInterval, IdleSweepInterval);
    }

    /// <summary>Loopback base URL the sidecar's LLM client talks to.</summary>
    public string ShimBaseUrl => _shim.BaseUrl;

    /// <summary>Loopback base URL the sidecar's tool callbacks talk to.</summary>
    public string BridgeUrl => _bridge.Url;

    /// <summary>
    /// Whether sidecars may summarize their own history once the context fills up.
    ///
    /// Application-wide, and kept here rather than on a provider for two reasons:
    /// this is the object that survives a provider being re-registered, and this is
    /// where the setting gets wiped — every <see cref="AcquireAsync"/> switches the
    /// session, which resets the sidecar to Pi's default. Re-applying anywhere else
    /// means some future caller forgets and the preference lasts one turn.
    /// </summary>
    public bool AutoCompactionEnabled { get; set; } = true;

    /// <summary>
    /// Take a sidecar for one turn on <paramref name="conversationKey"/>.
    ///
    /// Blocks while every slot is busy, which is the intended back-pressure: three
    /// turns really are in flight and a fourth process would cost more than it
    /// saves. Dispose the lease to release the slot.
    /// </summary>
    public async Task<PiTurnLease> AcquireAsync(
        PiSidecarSpec spec,
        string conversationKey,
        PiWorkLlmShim.ForwardTarget target,
        PiWorkToolBridge.TurnBinding binding,
        CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        Entry entry;
        try
        {
            entry = Claim(spec);
        }
        catch
        {
            _slots.Release();
            throw;
        }

        var sessionPath = ResolveSessionPath(spec.SessionRoot, conversationKey);
        try
        {
            _shim.SetTarget(entry.Token, target);
            _bridge.SetBinding(entry.Token, binding);

            // Always switch, even when this process served the same conversation
            // last time: a turn that was cancelled mid-flight, or a transcript
            // rewritten underneath us by a retry, leaves the in-memory session and
            // the file disagreeing. 60ms is not worth the class of bug that
            // "optimising" it away would open.
            var wasWarm = await entry.Session.SwitchSessionAsync(sessionPath, ct).ConfigureAwait(false);

            // The switch just reset the sidecar's auto-compaction to Pi's default,
            // so the preference is re-sent here rather than anywhere the caller has
            // to remember. Sent every time and in both directions on purpose: it
            // costs one line on a live process, and skipping the "on" case would
            // make this silently wrong the day Pi's default changes.
            //
            // Best-effort: a runtime too old to know the command must not take the
            // turn down with it. Losing the preference means summarizing stays on,
            // which is the default and costs the user nothing they did not already
            // have; failing the lease would cost them the answer.
            try
            {
                await entry.Session
                    .SetAutoCompactionAsync(AutoCompactionEnabled, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Invoke("[pi-runtime] 自动压缩偏好未生效：" + ex.Message);
            }

            entry.ConversationKey = conversationKey;
            return new PiTurnLease(this, entry, wasWarm);
        }
        catch
        {
            Release(entry);
            throw;
        }
    }

    /// <summary>Drop whatever process is currently holding
    /// <paramref name="conversationKey"/>'s transcript, so the file on disk can be
    /// rewritten without a live session writing over the edit. Used by the retry
    /// path; a no-op when nothing is holding it.</summary>
    public async Task EvictConversationAsync(string? conversationKey)
    {
        var key = conversationKey ?? DraftKey;
        List<Entry> victims;
        lock (_gate)
        {
            victims = _entries
                .Where(e => !e.InUse && string.Equals(e.ConversationKey, key, StringComparison.Ordinal))
                .ToList();
            foreach (var victim in victims) _entries.Remove(victim);
        }

        foreach (var victim in victims)
        {
            _shim.SetTarget(victim.Token, null);
            _bridge.SetBinding(victim.Token, null);
            try { await victim.Session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log?.Invoke("[pi-runtime] 释放 sidecar 失败：" + ex.Message); }
        }
    }

    public const string DraftKey = "draft";

    /// <summary>Drop every idle sidecar shaped for <paramref name="spec"/>. Called
    /// when a provider is re-registered or removed, so a stale endpoint or key
    /// cannot survive in a warm process.</summary>
    public async Task RetireSpecAsync(PiSidecarSpec spec)
    {
        List<Entry> victims;
        lock (_gate)
        {
            victims = _entries.Where(e => !e.InUse && e.SpecKey == spec.Key).ToList();
            foreach (var victim in victims) _entries.Remove(victim);
        }

        foreach (var victim in victims)
        {
            _shim.SetTarget(victim.Token, null);
            _bridge.SetBinding(victim.Token, null);
            try { await victim.Session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log?.Invoke("[pi-runtime] 释放 sidecar 失败：" + ex.Message); }
        }
    }

    /// <summary>
    /// Where a conversation's transcript lives.
    ///
    /// An existing file wins whatever it is called: sessions created before the
    /// pool carry Pi's own <c>&lt;timestamp&gt;_&lt;id&gt;.jsonl</c> naming, and
    /// picking a fresh name for them would silently strand the history. New
    /// conversations get a deterministic name instead, so nothing has to be
    /// discovered later.
    /// </summary>
    internal static string ResolveSessionPath(string sessionRoot, string conversationKey)
    {
        var id = PiWorkProvider.SanitizeSessionId(conversationKey);
        var existing = FindExistingSessionFile(sessionRoot, id);
        return existing ?? Path.Combine(sessionRoot, id + ".jsonl");
    }

    private static string? FindExistingSessionFile(string sessionRoot, string sanitizedId)
    {
        if (string.IsNullOrWhiteSpace(sessionRoot) || !Directory.Exists(sessionRoot)) return null;

        try
        {
            return Directory.EnumerateFiles(sessionRoot, "*.jsonl", SearchOption.AllDirectories)
                .Where(f => Path.GetFileNameWithoutExtension(f).Contains(sanitizedId, StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Pick a free sidecar of the right shape, reviving or spawning one as
    /// needed. Runs under the slot semaphore, so at least one entry is always
    /// obtainable without waiting.</summary>
    private Entry Claim(PiSidecarSpec spec)
    {
        lock (_gate)
        {
            // Prefer a process already shaped for this provider…
            var reusable = _entries.FirstOrDefault(e =>
                !e.InUse && e.SpecKey == spec.Key && e.Session.IsAlive);
            if (reusable is not null)
            {
                reusable.InUse = true;
                return reusable;
            }

            // …then a dead one of the right shape, which respawns on first use.
            var stale = _entries.FirstOrDefault(e => !e.InUse && e.SpecKey == spec.Key);
            if (stale is not null)
            {
                stale.InUse = true;
                return stale;
            }

            if (_entries.Count >= _maxSidecars)
            {
                // The slot semaphore guarantees a free entry exists; it just belongs
                // to another provider. Trading it costs a boot, which is why the cap
                // is a ceiling rather than a target.
                var evictable = _entries
                    .Where(e => !e.InUse)
                    .OrderBy(e => e.LastUsedUtc)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException("Pi 运行时没有可用的 sidecar 槽位。");

                _entries.Remove(evictable);
                RetireDetached(evictable);
            }

            var token = PiWorkLlmShim.NewSidecarToken();
            var entry = new Entry
            {
                Token = token,
                SpecKey = spec.Key,
                Session = new PiSidecarSession(spec.ToLaunchOptions(_shim.BaseUrl, token, _bridge.Url), _log),
                InUse = true,
            };
            _entries.Add(entry);
            Interlocked.Increment(ref _sidecarsCreated);
            return entry;
        }
    }

    private void Release(Entry entry)
    {
        _shim.SetTarget(entry.Token, null);
        _bridge.SetBinding(entry.Token, null);
        lock (_gate)
        {
            entry.InUse = false;
            entry.LastUsedUtc = DateTime.UtcNow;
        }
        _slots.Release();
    }

    internal void ReleaseLease(Entry entry) => Release(entry);

    private void SweepIdle()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        List<Entry> victims;
        lock (_gate)
        {
            victims = _entries.Where(e => !e.InUse && e.LastUsedUtc <= cutoff).ToList();
            foreach (var victim in victims) _entries.Remove(victim);
        }

        foreach (var victim in victims)
        {
            _log?.Invoke($"[pi-runtime] 回收空闲 sidecar（{victim.ConversationKey ?? "未使用"}）");
            RetireDetached(victim);
        }
    }

    /// <summary>Tear a sidecar down without blocking the caller. Teardown must never
    /// hold up the turn that displaced it.</summary>
    private void RetireDetached(Entry entry)
    {
        _shim.SetTarget(entry.Token, null);
        _bridge.SetBinding(entry.Token, null);
        _ = Task.Run(async () =>
        {
            try { await entry.Session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log?.Invoke("[pi-runtime] 回收失败：" + ex.Message); }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _idleSweep.DisposeAsync().ConfigureAwait(false);

        List<Entry> all;
        lock (_gate)
        {
            all = [.. _entries];
            _entries.Clear();
        }
        foreach (var entry in all)
        {
            try { await entry.Session.DisposeAsync().ConfigureAwait(false); }
            catch { /* best effort */ }
        }

        _bridge.Dispose();
        _shim.Dispose();
        _slots.Dispose();
    }

    internal sealed class Entry
    {
        public required string Token { get; init; }
        public required string SpecKey { get; init; }
        public required PiSidecarSession Session { get; init; }
        public bool InUse { get; set; }
        public string? ConversationKey { get; set; }
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    }
}

/// <summary>
/// A sidecar held for the duration of one turn. Disposing it returns the process
/// to the pool and unbinds the loopback routes — which is what stops a late tool
/// callback from a cancelled turn reaching the next conversation's tool host.
/// </summary>
public sealed class PiTurnLease : IAsyncDisposable
{
    private readonly PiRuntime _runtime;
    private readonly PiRuntime.Entry _entry;
    private int _released;

    internal PiTurnLease(PiRuntime runtime, PiRuntime.Entry entry, bool wasWarm)
    {
        _runtime = runtime;
        _entry = entry;
        WasWarm = wasWarm;
    }

    public PiSidecarSession Session => _entry.Session;

    /// <summary>False when this turn had to boot the process. The caller surfaces
    /// that as a status line, because ~2.7s of Node startup looks like the model
    /// being slow otherwise.</summary>
    public bool WasWarm { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _runtime.ReleaseLease(_entry);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Everything baked into a sidecar process at spawn — that is, everything that
/// forces a new process rather than a <c>set_model</c> or a
/// <c>switch_session</c>. Two providers with the same <see cref="Key"/> can share
/// one process.
/// </summary>
public sealed record PiSidecarSpec(
    string ProviderId,
    string NodePath,
    string CliJsPath,
    string ExtensionPath,
    string WorkingDirectory,
    string SessionRoot,
    string ModelsJson,
    string DefaultModelId,
    string DefaultApi,
    bool AuthHeader,
    bool Reasoning)
{
    /// <summary>Pool partition. The endpoint and the credential are deliberately
    /// absent: the shim supplies both per turn, so a rotating account token or a
    /// re-keyed provider never costs a respawn.</summary>
    public string Key => $"{ProviderId}|{AuthHeader}|{Reasoning}|{ModelsJson.Length}";

    internal PiSidecarLaunchOptions ToLaunchOptions(string shimBaseUrl, string token, string bridgeUrl) =>
        new(NodePath,
            CliJsPath,
            ExtensionPath,
            WorkingDirectory,
            SessionRoot,
            shimBaseUrl,
            token,
            DefaultModelId,
            DefaultApi,
            AuthHeader,
            Reasoning,
            bridgeUrl,
            token,
            ModelsJson);
}
