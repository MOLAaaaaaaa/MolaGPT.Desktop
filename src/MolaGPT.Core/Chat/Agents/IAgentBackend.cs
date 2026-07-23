namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// Permission / sandbox posture for an agent session. Maps onto each CLI's
/// own permission flags (Claude Code <c>--permission-mode</c>, Codex sandbox
/// presets). Mirrors the four-way choice exposed in settings.
/// </summary>
public enum AgentPermissionMode
{
    /// <summary>Read-only / planning. No file writes or command execution without asking.</summary>
    Plan,

    /// <summary>Auto-accept file edits, still gate risky commands. The P0 default.</summary>
    AcceptEdits,

    /// <summary>Default interactive posture: ask for anything non-trivial.</summary>
    Default,

    /// <summary>Fully autonomous. Only safe in trusted workspaces / containers.</summary>
    BypassPermissions
}

/// <summary>
/// Codex-only approval axis (<c>--ask-for-approval</c>). Orthogonal to the
/// sandbox preset that <see cref="AgentPermissionMode"/> maps to: the sandbox
/// decides what a command <em>can</em> touch, this decides when Codex pauses to
/// ask the user before running one. Claude Code has no equivalent — it folds
/// both axes into its single <c>--permission-mode</c>.
/// </summary>
public enum CodexApprovalPolicy
{
    /// <summary>Ask before anything outside the trusted set (most cautious).</summary>
    Untrusted,

    /// <summary>Ask only when the model itself requests escalation (the Codex default).</summary>
    OnRequest,

    /// <summary>Never pause for approval (only safe with a tight sandbox).</summary>
    Never
}

/// <summary>Options for spawning a single agent session.</summary>
public sealed record AgentSessionOptions(
    string ResolvedExecutablePath,
    string WorkingDirectory,
    AgentPermissionMode PermissionMode = AgentPermissionMode.AcceptEdits,
    string? Model = null,
    string? ReasoningEffort = null,
    string? SystemPromptAppend = null,
    /// <summary>When set, resume this existing CLI session (Claude <c>--resume</c>,
    /// Codex <c>thread/resume</c>) instead of starting a fresh one.</summary>
    string? ResumeSessionId = null,
    /// <summary>Codex-only approval policy. Ignored by Claude Code.</summary>
    CodexApprovalPolicy? ApprovalPolicy = null,
    /// <summary>Stable conversation id to bind the CLI session to when starting
    /// fresh (Claude <c>--session-id</c>, so the on-disk <c>.jsonl</c> uses our id
    /// instead of a self-assigned one). Must be a valid UUID for Claude to accept
    /// it; ignored when resuming or when not a UUID. Codex assigns its own thread id.</summary>
    string? SessionId = null);

/// <summary>One user turn, including optional image references from the relay.</summary>
public sealed record AgentTurnInput(string Text, IReadOnlyList<string> Images)
{
    public static AgentTurnInput TextOnly(string text) => new(text, Array.Empty<string>());
}

/// <summary>
/// A model the backend advertises as switchable, discovered live from the CLI
/// (Claude <c>initialize.models</c>, Codex <c>model/list</c>). <see cref="Id"/> is
/// the exact value passed back to switch to it (Claude alias/full name, Codex
/// model id); the rest is display metadata for the phone's picker.
/// </summary>
public sealed record AgentModelInfo(
    string Id,
    string DisplayName,
    string? Description = null,
    bool IsDefault = false,
    IReadOnlyList<string>? ReasoningEfforts = null);

/// <summary>
/// Backend-specific protocol driver. One implementation per CLI
/// (Claude Code stream-json, Codex app-server JSON-RPC). Hides all wire-format
/// differences behind <see cref="StartSessionAsync"/>, which hands back an
/// <see cref="IAgentSession"/> the provider layer drives turn-by-turn.
/// </summary>
public interface IAgentBackend
{
    /// <summary>Stable backend id ("claude-code", "codex").</summary>
    string Id { get; }

    /// <summary>Friendly name shown in the UI / model selector.</summary>
    string DisplayName { get; }

    /// <summary>Spawn a persistent agent process and return a live session.</summary>
    Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken ct);
}

/// <summary>
/// A live, stateful agent session backed by a persistent CLI subprocess.
/// The process retains conversation context across turns, so callers send only
/// the new user text per turn (not the whole history).
/// </summary>
public interface IAgentSession : IAsyncDisposable
{
    /// <summary>Backend that owns this session.</summary>
    string BackendId { get; }

    /// <summary>True while the underlying process is alive.</summary>
    bool IsAlive { get; }

    /// <summary>True while a turn is being streamed on this session — regardless
    /// of which owner (bridge command, desktop chat UI) started it. The bridge
    /// consults this before restart-style operations (e.g. option respawn) so a
    /// turn it did not dispatch is never killed mid-flight.</summary>
    bool IsTurnActive => false;

    /// <summary>
    /// The CLI's own session/thread id for the live conversation, captured from
    /// the backend handshake (Claude <c>system/init.session_id</c>, Codex
    /// <c>threadId</c>). Null until known. The bridge follows this to resume the
    /// correct conversation after a respawn, even if the CLI rotated the id.
    /// </summary>
    string? CurrentSessionId { get; }

    /// <summary>
    /// Send one user turn and stream normalized events until the turn completes.
    /// The stream ends after exactly one TurnComplete or Error event.
    /// </summary>
    IAsyncEnumerable<AgentEvent> SendTurnAsync(AgentTurnInput input, CancellationToken ct);

    /// <summary>Request the current turn be interrupted (best-effort).</summary>
    Task InterruptAsync(CancellationToken ct);

    /// <summary>Resolve a pending backend permission prompt.</summary>
    Task ApproveAsync(string permissionId, AgentPermissionChoice choice, CancellationToken ct);

    /// <summary>
    /// Models this session can switch to, discovered from the backend handshake
    /// (Claude <c>initialize.models</c>, Codex <c>model/list</c>). Empty until
    /// discovered, or when the backend can't enumerate. Best-effort — the phone
    /// falls back to observed model ids when this is empty.
    /// </summary>
    IReadOnlyList<AgentModelInfo> AvailableModels => Array.Empty<AgentModelInfo>();

    /// <summary>
    /// Switch the model of the <em>running</em> session without restarting the
    /// process or losing context (Claude <c>set_model</c> control request; Codex
    /// re-parametrizes the thread). Passing null resets to the backend default.
    /// Backends that can't hot-switch fall back to a respawn at the bridge layer.
    /// Returns true when the switch was applied live; false when the caller should
    /// respawn instead.
    /// </summary>
    Task<bool> SetModelAsync(string? model, CancellationToken ct) => Task.FromResult(false);
}
