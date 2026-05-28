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
    string? RawJson = null);

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
