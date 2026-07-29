using System.Collections.Concurrent;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// Owns the lifecycle of persistent agent sessions, keyed by
/// (backendId, conversationId). Sessions are created lazily on first turn and
/// reused across turns of the same conversation — mirroring the
/// <c>McpClientManager</c> lazy-cache pattern. The persistent process retains
/// context, so callers (the bridge) send only the newest user turn per request.
/// </summary>
public sealed class AgentSessionManager : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, IAgentBackend> _backends;
    private readonly AgentCliResolver _resolver;
    private readonly IAgentConfigProvider _config;

    private readonly ConcurrentDictionary<string, Lazy<Task<IAgentSession>>> _sessions = new();

    public AgentSessionManager(
        IEnumerable<IAgentBackend> backends,
        AgentCliResolver resolver,
        IAgentConfigProvider config)
    {
        _backends = backends.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);
        _resolver = resolver;
        _config = config;
    }

    /// <summary>
    /// Get the live session for a (backend, conversation), creating it on first
    /// use; a dead session is transparently replaced. Pins the working directory,
    /// resume target, model, reasoning effort, permission/sandbox posture, and
    /// (Codex) approval policy for this session. These are baked into the process
    /// at spawn time, so the bridge closes a live session before reusing this with
    /// new values — the next call rebuilds the process under the new options.
    /// </summary>
    public async Task<IAgentSession> GetOrCreateAsync(
        string backendId, string? conversationId, string? workingDirectory, string? resumeSessionId,
        string? model, string? reasoningEffort,
        AgentPermissionMode? permissionMode, CodexApprovalPolicy? approvalPolicy, CancellationToken ct)
    {
        var key = $"{backendId}|{conversationId ?? "draft"}";

        while (true)
        {
            var lazy = _sessions.GetOrAdd(key, _ =>
                new Lazy<Task<IAgentSession>>(() => CreateSessionAsync(
                    backendId, conversationId, workingDirectory, resumeSessionId,
                    model, reasoningEffort, permissionMode, approvalPolicy, ct)));

            IAgentSession session;
            try
            {
                session = await lazy.Value.ConfigureAwait(false);
            }
            catch
            {
                // Failed init — evict so the next attempt can retry cleanly.
                _sessions.TryRemove(new KeyValuePair<string, Lazy<Task<IAgentSession>>>(key, lazy));
                throw;
            }

            if (session.IsAlive)
                return session;

            // Stale (process exited). Evict and recreate.
            if (_sessions.TryRemove(new KeyValuePair<string, Lazy<Task<IAgentSession>>>(key, lazy)))
                await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<IAgentSession> CreateSessionAsync(
        string backendId, string? conversationId, string? workingDirectory, string? resumeSessionId,
        string? model, string? reasoningEffort,
        AgentPermissionMode? permissionMode, CodexApprovalPolicy? approvalPolicy, CancellationToken ct)
    {
        if (!_backends.TryGetValue(backendId, out var backend))
            throw new InvalidOperationException($"Unknown agent backend '{backendId}'.");

        var (configuredPath, exeName) = backendId switch
        {
            ClaudeCodeBackend.BackendId => (_config.ClaudeCodePath, "claude"),
            CodexBackend.BackendId => (_config.CodexPath, "codex"),
            _ => (null, backendId)
        };

        var resolved = await _resolver.ResolveAsync(exeName, configuredPath, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"找不到 {backend.DisplayName} CLI。请确认已安装（npm i -g），或在设置中指定可执行文件路径。");

        var cwd = workingDirectory
            ?? _config.GetWorkingDirectory(conversationId)
            ?? throw new InvalidOperationException(
                $"{backend.DisplayName} 会话需要一个工作目录。请先为本会话选择一个项目文件夹。");

        var options = new AgentSessionOptions(
            resolved,
            cwd,
            permissionMode ?? _config.PermissionMode,
            Model: model,
            ReasoningEffort: reasoningEffort,
            ResumeSessionId: resumeSessionId,
            ApprovalPolicy: approvalPolicy,
            // Bind a fresh CLI session to our stable conversation id so the on-disk
            // session matches what the bridge/phone track. Ignored when resuming.
            SessionId: conversationId);

        return await backend.StartSessionAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Peek the live session for a conversation without creating one. Returns
    /// null when none was ever created, creation is still in flight (or failed),
    /// or the process has died. Lets the bridge check for an existing process
    /// before restart-style operations without spawning one as a side effect.
    /// </summary>
    public IAgentSession? TryGetLive(string backendId, string? conversationId)
    {
        var key = $"{backendId}|{conversationId ?? "draft"}";
        if (!_sessions.TryGetValue(key, out var lazy) || !lazy.IsValueCreated)
            return null;
        var task = lazy.Value;
        if (!task.IsCompletedSuccessfully)
            return null;
        var session = task.Result;
        return session.IsAlive ? session : null;
    }

    /// <summary>Tear down the session for a conversation (e.g. when it is deleted/closed).</summary>
    public async Task CloseAsync(string backendId, string? conversationId)
    {
        var key = $"{backendId}|{conversationId ?? "draft"}";
        if (_sessions.TryRemove(key, out var lazy) && lazy.IsValueCreated)
        {
            try { await (await lazy.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Tear down every backend's live session for a conversation. Used when a
    /// conversation is deleted and the caller doesn't know which backend owned
    /// it (or both did) — releases the CLI process and its working-directory
    /// lock. Keys are "{backendId}|{conversationId}", so we match on the suffix.
    /// </summary>
    public async Task CloseConversationAsync(string? conversationId)
    {
        var suffix = $"|{conversationId ?? "draft"}";
        foreach (var key in _sessions.Keys.ToArray())
        {
            if (!key.EndsWith(suffix, StringComparison.Ordinal)) continue;
            if (_sessions.TryRemove(key, out var lazy) && lazy.IsValueCreated)
            {
                try { await (await lazy.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _sessions.Values)
        {
            if (!lazy.IsValueCreated) continue;
            try { await (await lazy.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
        _sessions.Clear();
    }
}
