using System.Collections.Generic;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat;

/// <summary>
/// Streaming-incremental delta from a chat provider. All fields nullable —
/// a single chunk usually carries only one of DeltaText / DeltaThinking /
/// FinishReason / Usage / Tool.
/// </summary>
public sealed record ChatChunk(
    string? DeltaText = null,
    string? DeltaThinking = null,
    string? FinishReason = null,
    Usage? Usage = null,
    IReadOnlyList<SourceReference>? Sources = null,
    PendingStatusDelta? Pending = null,
    ToolCallDelta? Tool = null,
    string? RawJson = null,
    CompactionDelta? Compaction = null,
    ContextUsageDelta? ContextUsage = null);

/// <summary>
/// How full the model's context is after this turn.
///
/// Deliberately derived from the same quantity the agent's own auto-compaction
/// decides on — the newest assistant message's reported token total — so the gauge
/// and the threshold can never tell the user different stories. It is a per-turn
/// reading, not a live one: the number only exists once a reply has been counted,
/// which is also why nothing here needs re-tokenizing as the user types.
/// </summary>
/// <param name="Tokens">Null when there is nothing honest to report yet — before
/// the first reply, and in the gap after a compaction where the freshest usage
/// still describes the context that was just discarded. Render that as unknown,
/// never as zero.</param>
/// <param name="ContextWindow">The window the model was catalogued with. 0 when
/// unknown, in which case only the raw token count can be shown.</param>
public sealed record ContextUsageDelta(int? Tokens, int ContextWindow)
{
    public bool IsKnown => Tokens is > 0 && ContextWindow > 0;

    public double? Percent => IsKnown
        ? Math.Min(100d, Tokens!.Value * 100d / ContextWindow)
        : null;
}

/// <summary>
/// The agent summarized its own history mid-turn to stay inside the context
/// window. Surfaced as a chunk because it is something that <em>happened to the
/// conversation</em> — silently dropping it is how a user ends up asking why the
/// model forgot something they can still see on screen.
/// </summary>
/// <param name="Completed">False on the way in, true on the way out. Compaction is
/// a model call and can take seconds, so the two edges are distinct events.</param>
/// <param name="Reason">Pi's own word for why — <c>manual</c> when the user asked,
/// otherwise the automatic trigger.</param>
/// <param name="TokensBefore">Context size at the cut, 0 when not reported.</param>
/// <param name="TokensAfter">The agent's <em>estimate</em> of what the history
/// weighs now that it is a summary — a character-count heuristic, not a count from
/// the model, and deliberately a conservative one. 0 when not reported. Show it as
/// an approximation or not at all; presenting it as measured would make the next
/// turn's real reading look like a regression.</param>
public sealed record CompactionDelta(
    bool Completed,
    string? Reason = null,
    int TokensBefore = 0,
    bool Aborted = false,
    string? ErrorMessage = null,
    int TokensAfter = 0);

public sealed record PendingStatusDelta(
    string Label,
    string? Detail = null,
    bool IsRoutes = false);

public sealed record ToolCallDelta(
    string Id,
    string Name,
    string Status,
    string? Label = null,
    string? Summary = null,
    string? Detail = null,
    string? ArgumentsJson = null,
    string? ResultPreviewJson = null,
    string? Provider = null,
    int? ContentOffset = null,
    int? TimelineIndex = null);
