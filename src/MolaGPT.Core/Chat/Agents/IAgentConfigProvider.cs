namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// Supplies per-conversation agent configuration to the provider/manager layer.
/// Implemented in the Desktop layer (backed by settings + the conversation row)
/// so Core stays free of any storage dependency.
/// </summary>
public interface IAgentConfigProvider
{
    /// <summary>Configured absolute path to the Claude Code CLI, or null to auto-resolve.</summary>
    string? ClaudeCodePath { get; }

    /// <summary>Configured absolute path to the Codex CLI, or null to auto-resolve.</summary>
    string? CodexPath { get; }

    /// <summary>Default permission / sandbox posture for new agent sessions.</summary>
    AgentPermissionMode PermissionMode { get; }

    /// <summary>
    /// Stable install-scoped id for this desktop bridge. Used by the cloud relay
    /// to route phone commands to the correct machine when the same account has
    /// multiple MolaGPT installs. Empty means legacy single-machine behaviour.
    /// </summary>
    string MachineId => string.Empty;

    /// <summary>Human-readable machine label (typically <c>Environment.MachineName</c>).</summary>
    string MachineName => Environment.MachineName;

    /// <summary>
    /// Resolve the working directory for a conversation. Returns null when the
    /// user has not chosen one yet (the provider then surfaces a clear error).
    /// </summary>
    string? GetWorkingDirectory(string? conversationId);

    /// <summary>Persist the working directory chosen for a conversation.</summary>
    void SetWorkingDirectory(string conversationId, string directory);

    // ---- Durable session stubs --------------------------------------------
    // A session created remotely (from the phone) lives only in the bridge's
    // in-memory map until its first message spawns the CLI and writes an on-disk
    // transcript. Persisting a small stub lets such a not-yet-used session survive
    // a desktop restart instead of vanishing. Default no-ops: only the desktop
    // implementation persists; tests/harness inherit the no-ops.

    /// <summary>Remember a created session so it can be re-registered after restart.</summary>
    void SaveSession(AgentPersistedSession session) { }

    /// <summary>Drop a persisted session stub (on close).</summary>
    void ForgetSession(string conversationId) { }

    /// <summary>All persisted session stubs, for re-registration on startup.</summary>
    IReadOnlyList<AgentPersistedSession> ListPersistedSessions() => Array.Empty<AgentPersistedSession>();
}

/// <summary>Minimal durable record of a created agent session — enough to
/// re-register it in the bridge after a restart, before its CLI has ever run.</summary>
public sealed record AgentPersistedSession(
    string ConversationId,
    string BackendId,
    string Title,
    string WorkingDirectory,
    string? Model,
    long CreatedAtMs,
    /// <summary>The CLI's own session id for this conversation, when it differs
    /// from <see cref="ConversationId"/>. Codex self-assigns a threadId, so its
    /// on-disk transcript is named after that instead of our id — persisting the
    /// link keeps a restarted bridge from re-registering that transcript as a
    /// second, phantom conversation. Null for old stubs and for Claude (bound
    /// via --session-id, so the two ids are equal).</summary>
    string? ResumeSessionId = null);
