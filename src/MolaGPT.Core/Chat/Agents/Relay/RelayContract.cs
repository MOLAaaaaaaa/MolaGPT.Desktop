using System.Text.Json.Serialization;

namespace MolaGPT.Core.Chat.Agents.Relay;

/// <summary>
/// Wire contract for the agent cloud relay — the coarse, phone-facing event set
/// the PHP relay stores and forwards. Granularity is deliberately NOT per-token
/// ("typewriter"): tool calls stream live (that is the progress signal), and the
/// answer text ships once per answer (or per tool boundary) as a snapshot. This
/// keeps wire traffic modest over PHP+SSE and makes reconnect replay idempotent
/// (snapshots are SET semantics, not append).
///
/// The desktop bridge's internal <c>AgentEventLog</c> is finer-grained (it keeps
/// every TextDelta for the local desktop transcript + harness). The
/// <see cref="AgentRelayClient"/> translates bridge events → these coarse relay
/// events. Seq is a per-session relay cursor: it is seeded from the originating
/// bridge seq, but every emitted envelope gets its own strictly increasing value
/// so simple relays can replay with <c>seq &gt; since</c> without missing events.
///
/// Polymorphism: <c>kind</c> discriminator (short kebab names) so the wire
/// shape is language-neutral — PHP just stores/forwards the JSON, and the
/// Kotlin phone dispatches on <c>kind</c> the same way the desktop does.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(UserPromptEvent), "userPrompt")]
[JsonDerivedType(typeof(ToolProgressEvent), "toolProgress")]
[JsonDerivedType(typeof(PermissionPromptEvent), "permissionPrompt")]
[JsonDerivedType(typeof(AnswerSnapshotEvent), "answerSnapshot")]
[JsonDerivedType(typeof(ThinkingSnapshotEvent), "thinkingSnapshot")]
[JsonDerivedType(typeof(TurnDoneEvent), "turnDone")]
[JsonDerivedType(typeof(TurnFailedEvent), "turnFailed")]
public abstract record RelayTranscriptEvent;

/// <summary>The user's prompt for this turn (optimistic local echo).</summary>
public sealed record UserPromptEvent(string Text) : RelayTranscriptEvent;

/// <summary>A tool-call lifecycle event, shipped live per tool — the "progress"
/// feed. Carries the normalized tool event; the phone renders/updates a tool card
/// by tool-id (idempotent on replay).</summary>
public sealed record ToolProgressEvent(AgentToolEvent Tool) : RelayTranscriptEvent;

/// <summary>A permission prompt raised mid-turn (Phase 4 wires approve/deny back).</summary>
public sealed record PermissionPromptEvent(AgentPermissionRequest Permission) : RelayTranscriptEvent;

/// <summary>Answer text accumulated so far, shipped at turn end (and optionally at
/// tool boundaries). SET semantics — the phone REPLACES its current answer text,
/// not appends — so reconnect replay is idempotent (re-applying the same snapshot
/// yields the same text, no duplication).</summary>
public sealed record AnswerSnapshotEvent(string Text) : RelayTranscriptEvent;

/// <summary>Optional reasoning/thinking snapshot, same SET semantics as the answer.</summary>
public sealed record ThinkingSnapshotEvent(string Text) : RelayTranscriptEvent;

/// <summary>The turn completed. Carries final usage when known.</summary>
public sealed record TurnDoneEvent(AgentUsage? Usage) : RelayTranscriptEvent;

/// <summary>The turn failed.</summary>
public sealed record TurnFailedEvent(string Message) : RelayTranscriptEvent;

/// <summary>One seq-stamped coarse relay event, shipped to the phone. Seq is the
/// relay cursor; the phone replays envelopes with seq > its last-seen seq on
/// reconnect.</summary>
public sealed record RelayEventEnvelope(string SessionId, long Seq, RelayTranscriptEvent Event);

/// <summary>The relay's stored projection cursor for one session. The desktop
/// owns the canonical local transcript; the relay reports the projection it
/// currently has so the desktop can publish a current one before advertising
/// session meta to the phone.</summary>
public sealed record RelaySessionCursor(string SessionId, long Seq, long ActivityAtMs);

/// <summary>One mobile client that recently touched the relay session list.</summary>
public sealed record RelayMobileDevice(string Id, string? Name, long LastSeenAtMs);

/// <summary>Desktop-side local history projection progress for the settings UI.</summary>
public sealed record AgentRelayProjectionStatus(int ProjectedSessions, int QueuedProjections, int ActiveProjections);

/// <summary>Operations the phone can command the bridge to perform.</summary>
public enum RelayCommandOp
{
    /// <summary>Send one user turn. Payload: {"text":"…","images":["…"]}.</summary>
    Send,
    /// <summary>Interrupt the in-flight turn. No payload.</summary>
    Interrupt,
    /// <summary>Approve a pending permission prompt. Payload: {"permissionId":"…","choice":"Once|Always|Deny"}.</summary>
    Approve,
    /// <summary>Create a new session. Payload: {"backendId":"…","workingDirectory":"…","title":"…","model":"…","reasoningEffort":"…","permissionMode":"…","approvalPolicy":"…"}.</summary>
    New,
    /// <summary>Switch model / reasoning effort / permission posture for a session.
    /// Payload: {"model":"…","reasoningEffort":"…","permissionMode":"…","approvalPolicy":"…"}.
    /// Any field may be omitted to leave it unchanged.</summary>
    SwitchOptions,
    /// <summary>Drop a session. No payload.</summary>
    Close,
    /// <summary>Ask the desktop to rebuild this session's relay transcript from
    /// the machine-local history file. The relay should be treated as a transient
    /// transport buffer, not the source of truth.</summary>
    RefreshHistory
}

/// <summary>A command from the phone, enqueued on the relay for the desktop.</summary>
public sealed record RelayCommand(string SessionId, string CmdId, RelayCommandOp Op, string? PayloadJson);

/// <summary>Point-in-time session state snapshot (no transcript) — phase and
/// attention are session-level, not transcript-level, so they sync via this
/// snapshot alongside the event stream rather than as replay events.</summary>
public sealed record RelaySessionMeta(
    string ConversationId,
    string BackendId,
    string Title,
    string WorkingDirectory,
    string? WorkspaceKey,
    string WorkspaceName,
    string? Model,
    string? ReasoningEffort,
    AgentPermissionMode PermissionMode,
    CodexApprovalPolicy? ApprovalPolicy,
    AgentSessionPhase Phase,
    bool NeedsAttention,
    long Seq,
    long UpdatedAtMs,
    long ActivityAtMs = 0,
    /// <summary>Models this session can switch to, discovered live from the CLI
    /// (Claude initialize.models / Codex model/list). Null/empty when not yet
    /// discovered — the phone then falls back to observed model ids.</summary>
    IReadOnlyList<AgentModelInfo>? AvailableModels = null);
