using System.Collections.Concurrent;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// Per-backend model catalog for the bridge. The switchable-model list (Claude
/// <c>initialize.models</c> / Codex <c>model/list</c>) is account/env-global, not
/// per-session — but a session only exposes it once its CLI process is live, which
/// doesn't happen for a brand-new or history-loaded session until its first turn.
///
/// So the bridge caches the catalog per backend and, the first time it's needed,
/// warms it up with a throwaway discovery process (spawn → initialize / model-list
/// → cache → dispose). Every session of that backend then reports the cached
/// catalog in its meta, so the phone's picker is populated even before any turn.
/// </summary>
public sealed partial class AgentBridgeService
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<AgentModelInfo>> _modelCatalog =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _catalogWarming = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _catalogFingerprint = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _catalogStampMs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _catalogCts = new();

    /// <summary>Re-discover a backend's catalog at least this often even when the
    /// config fingerprint looks unchanged (covers provider-side model changes).</summary>
    private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>The models to advertise for a session: its own live catalog when the
    /// process is up (also refreshes the per-backend cache), else the cached catalog.</summary>
    private IReadOnlyList<AgentModelInfo> CatalogFor(string backendId, IReadOnlyList<AgentModelInfo>? liveModels)
    {
        if (liveModels is { Count: > 0 })
        {
            // A real session's catalog is authoritative AND current — stamp it so
            // the staleness check doesn't immediately schedule a throwaway re-scan.
            _modelCatalog[backendId] = liveModels;
            _catalogFingerprint[backendId] = ComputeConfigFingerprint(backendId);
            _catalogStampMs[backendId] = NowMs();
            return liveModels;
        }
        return _modelCatalog.TryGetValue(backendId, out var cached) ? cached : Array.Empty<AgentModelInfo>();
    }

    /// <summary>Kick off a one-time catalog warm-up for <paramref name="backendId"/> if
    /// it isn't cached yet. Fire-and-forget; re-pushes meta for that backend's sessions
    /// once discovered so the phone's picker fills in without a turn.</summary>
    private void BeginWarmUpModelCatalog(string backendId)
    {
        if (string.IsNullOrWhiteSpace(backendId)) return;
        if (_modelCatalog.ContainsKey(backendId) && !IsCatalogStale(backendId)) return;
        if (!_catalogWarming.TryAdd(backendId, 0)) return; // a warm-up is already in flight
        _ = Task.Run(() => WarmUpModelCatalogAsync(backendId));
    }

    /// <summary>
    /// True when a cached catalog can no longer be trusted. The cache used to be
    /// permanent for the process lifetime, so switching the CLI's provider config
    /// (e.g. CC Switch rewriting <c>~/.claude/settings.json</c>) left the phone's
    /// model picker showing the OLD provider's models until the desktop restarted.
    /// Staleness is decided by a cheap config fingerprint plus a coarse max age.
    /// </summary>
    private bool IsCatalogStale(string backendId)
    {
        if (!_catalogStampMs.TryGetValue(backendId, out var stampedAt)) return true;
        if (NowMs() - stampedAt > (long)CatalogMaxAge.TotalMilliseconds) return true;
        var current = ComputeConfigFingerprint(backendId);
        return !_catalogFingerprint.TryGetValue(backendId, out var cached)
            || !string.Equals(cached, current, StringComparison.Ordinal);
    }

    /// <summary>Cheap signature of everything that can change a backend's model
    /// catalog: the config files a switcher rewrites, plus the env overrides.
    /// Only stats files (no parsing, no secrets read into the fingerprint).</summary>
    internal static string ComputeConfigFingerprint(string backendId, string? homeOverride = null)
    {
        var sb = new System.Text.StringBuilder();
        var home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var relative in backendId == CodexBackend.BackendId
            ? new[] { Path.Combine(".codex", "config.toml"), Path.Combine(".codex", "auth.json") }
            : new[] { Path.Combine(".claude", "settings.json"), Path.Combine(".claude", "settings.local.json"), ".claude.json" })
        {
            var path = Path.Combine(home, relative);
            try
            {
                var info = new FileInfo(path);
                sb.Append(relative).Append('=')
                  .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append(':')
                  .Append(info.Exists ? info.Length : 0).Append(';');
            }
            catch { sb.Append(relative).Append("=err;"); }
        }

        foreach (var name in backendId == CodexBackend.BackendId
            ? new[] { "OPENAI_BASE_URL", "OPENAI_API_KEY", "CODEX_MODEL" }
            : new[] { "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY", "ANTHROPIC_MODEL" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            // Hash rather than embed: the fingerprint only needs to CHANGE when the
            // credential changes, never to carry it.
            sb.Append(name).Append('=')
              .Append(string.IsNullOrEmpty(value) ? "0" : value.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(';');
        }

        return sb.ToString();
    }

    private async Task WarmUpModelCatalogAsync(string backendId)
    {
        var discoId = $"__models__-{backendId}";
        // Snapshot the fingerprint BEFORE discovery so a config edit that lands
        // mid-warm-up isn't recorded as "already covered" (next pass re-runs).
        var fingerprint = ComputeConfigFingerprint(backendId);
        try
        {
            var cwd = ModelDiscoveryDir();
            // Plan/read-only posture, no model override, no session binding (the id
            // isn't a UUID, so Claude won't --session-id-bind it) — a throwaway process
            // purely to run the discovery handshake.
            var disco = await _manager.GetOrCreateAsync(
                backendId, discoId, cwd, resumeSessionId: null,
                model: null, reasoningEffort: null,
                AgentPermissionMode.Plan,
                backendId == CodexBackend.BackendId ? CodexApprovalPolicy.OnRequest : null,
                _catalogCts.Token).ConfigureAwait(false);

            // Discovery is kicked off on spawn (Claude BeginInitialize / Codex model/list
            // after handshake); poll briefly for it to land.
            for (var i = 0; i < 40 && disco.AvailableModels.Count == 0; i++)
                await Task.Delay(150, _catalogCts.Token).ConfigureAwait(false);

            if (disco.AvailableModels.Count > 0)
            {
                var previous = _modelCatalog.TryGetValue(backendId, out var old) ? old : null;
                _modelCatalog[backendId] = disco.AvailableModels;
                _catalogFingerprint[backendId] = fingerprint;
                _catalogStampMs[backendId] = NowMs();

                // Only re-publish metas when the catalog actually changed — the
                // refresh now runs periodically, and an unchanged list shouldn't
                // churn every session's meta on the relay.
                if (previous is null || !previous.Select(m => m.Id).SequenceEqual(disco.AvailableModels.Select(m => m.Id), StringComparer.Ordinal))
                {
                    foreach (var e in _sessions.Values.Where(e => e.BackendId == backendId))
                        SessionMetaChanged?.Invoke(StateOf(e));
                    MarkDirty();
                }
            }
        }
        catch { /* best-effort — the phone just falls back to observed ids until a real turn */ }
        finally
        {
            _catalogWarming.TryRemove(backendId, out _);
            try { await _manager.CloseAsync(backendId, discoId).ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private static string ModelDiscoveryDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MolaGPT", "model-discovery");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>True when a working directory is (under) the throwaway discovery dir,
    /// so history listing can hide the warm-up's stray empty session.</summary>
    private static bool IsModelDiscoveryPath(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return false;
        var marker = Path.Combine("MolaGPT", "model-discovery");
        return workingDirectory.Replace('/', Path.DirectorySeparatorChar)
            .Contains(marker, StringComparison.OrdinalIgnoreCase);
    }
}
