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
    private readonly CancellationTokenSource _catalogCts = new();

    /// <summary>The models to advertise for a session: its own live catalog when the
    /// process is up (also refreshes the per-backend cache), else the cached catalog.</summary>
    private IReadOnlyList<AgentModelInfo> CatalogFor(string backendId, IReadOnlyList<AgentModelInfo>? liveModels)
    {
        if (liveModels is { Count: > 0 })
        {
            _modelCatalog[backendId] = liveModels; // keep the cache fresh from real sessions
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
        if (_modelCatalog.ContainsKey(backendId)) return;
        if (!_catalogWarming.TryAdd(backendId, 0)) return; // a warm-up is already in flight
        _ = Task.Run(() => WarmUpModelCatalogAsync(backendId));
    }

    private async Task WarmUpModelCatalogAsync(string backendId)
    {
        var discoId = $"__models__-{backendId}";
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
                _modelCatalog[backendId] = disco.AvailableModels;
                foreach (var e in _sessions.Values.Where(e => e.BackendId == backendId))
                    SessionMetaChanged?.Invoke(StateOf(e));
                MarkDirty();
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
