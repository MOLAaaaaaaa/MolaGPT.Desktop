namespace MolaGPT.Core.Models;

/// <summary>
/// Token / call usage reported by the upstream model.
/// </summary>
/// <param name="CostUsd">What the turn cost in USD, or null when the model has no
/// price on file. Null and 0 are different answers — "unknown" versus "free" — so
/// this stays null rather than defaulting to zero.</param>
public sealed record Usage(
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    int? CacheReadTokens = null,
    double? TokensPerSecond = null,
    double? CostUsd = null);
