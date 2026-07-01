namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// Normalized event emitted by an agent backend (Claude Code stream-json or
/// Codex app-server JSON-RPC). Both wire protocols are mapped onto this single
/// shape so the provider layer never has to know which CLI it is talking to.
///
/// A single turn produces a stream of these, terminated by exactly one
/// <see cref="AgentEventKind.TurnComplete"/> or <see cref="AgentEventKind.Error"/>.
/// </summary>
public sealed record AgentEvent(
    AgentEventKind Kind,
    string? Text = null,
    AgentToolEvent? Tool = null,
    AgentPermissionRequest? Permission = null,
    AgentUsage? Usage = null,
    string? ErrorMessage = null,
    string? RawJson = null)
{
    public static AgentEvent TextDelta(string text, string? raw = null) =>
        new(AgentEventKind.TextDelta, Text: text, RawJson: raw);

    public static AgentEvent ThinkingDelta(string text, string? raw = null) =>
        new(AgentEventKind.ThinkingDelta, Text: text, RawJson: raw);

    public static AgentEvent Complete(AgentUsage? usage = null, string? raw = null) =>
        new(AgentEventKind.TurnComplete, Usage: usage, RawJson: raw);

    public static AgentEvent Failure(string message, string? raw = null) =>
        new(AgentEventKind.Error, ErrorMessage: message, RawJson: raw);
}

public enum AgentEventKind
{
    /// <summary>Incremental assistant text.</summary>
    TextDelta,

    /// <summary>Incremental reasoning / thinking text.</summary>
    ThinkingDelta,

    /// <summary>A tool call started, updated, or finished (see <see cref="AgentToolEvent.Status"/>).</summary>
    ToolCall,

    /// <summary>The agent is asking permission to use a tool / edit a file.
    /// In P0 the backend auto-answers per the configured permission mode; this
    /// event is surfaced so the UI can show what was requested.</summary>
    PermissionRequest,

    /// <summary>The turn finished successfully. Carries final usage when known.</summary>
    TurnComplete,

    /// <summary>The turn failed. <see cref="AgentEvent.ErrorMessage"/> is set.</summary>
    Error
}

/// <summary>Normalized tool-call lifecycle event.</summary>
public sealed record AgentToolEvent(
    string Id,
    string Name,
    AgentToolStatus Status,
    string? Title = null,
    string? ArgumentsJson = null,
    string? ResultPreview = null);

public enum AgentToolStatus
{
    Started,
    Running,
    Completed,
    Failed
}

/// <summary>A permission prompt raised mid-turn by the agent.</summary>
public sealed record AgentPermissionRequest(
    string Id,
    string ToolName,
    string? Description = null,
    string? ArgumentsJson = null);

/// <summary>Token usage reported at the end of a turn (best-effort; fields null when unknown).</summary>
public sealed record AgentUsage(int? InputTokens, int? OutputTokens, int? TotalTokens);
