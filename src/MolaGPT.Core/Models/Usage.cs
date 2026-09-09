namespace MolaGPT.Core.Models;

/// <summary>
/// Token / call usage reported by the upstream model.
/// </summary>
public sealed record Usage(
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    int? CacheReadTokens = null,
    double? TokensPerSecond = null);
