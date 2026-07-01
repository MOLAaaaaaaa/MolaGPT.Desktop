namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// Lifecycle phase of an agent session — drives the status surface's spinner /
/// state dot. Lives in Core (moved from the old WPF console) so the bridge and
/// the phone share one notion of session state. Independent of the attention
/// flag: a session can be <c>Waiting</c> (blocked on the user) without being
/// "running", and attention badges that distinctly from phase.
/// </summary>
public enum AgentSessionPhase { Idle, Spawning, Running, Waiting, Completed, Failed }

/// <summary>
/// Serializable snapshot of one agent session — the bridge's canonical state
/// model. The desktop status surface reads <see cref="Phase"/> /
/// <see cref="NeedsAttention"/> only (no transcript); the relay ships the seq'd
/// event stream and the phone rebuilds the transcript through its own reducer.
/// <see cref="Transcript"/> is carried for local verification (the integration
/// harness) and any future desktop transcript view, not for the wire.
/// </summary>
public sealed record AgentSessionStateDto(
    string ConversationId,
    string BackendId,
    string Title,
    string WorkingDirectory,
    string? ResumeSessionId,
    string? Model,                       // resolved model id; null = use the CLI's own default
    string? ReasoningEffort,             // "low"/"medium"/"high"/... or null = CLI default (backend wiring: Phase 2 slice)
    AgentPermissionMode PermissionMode,
    CodexApprovalPolicy? ApprovalPolicy,
    AgentSessionPhase Phase,
    bool NeedsAttention,
    long Seq,                            // last event seq folded into this snapshot
    string ModeLabel,                    // precomputed display, e.g. "opus · 自动改文件"
    long UpdatedAtMs,                    // epoch ms, for registry ordering
    IReadOnlyList<AgentBlockDto> Transcript,
    // Models this session can switch to, discovered live from the CLI (Claude
    // initialize.models / Codex model/list). Empty until discovered.
    IReadOnlyList<AgentModelInfo>? AvailableModels = null);