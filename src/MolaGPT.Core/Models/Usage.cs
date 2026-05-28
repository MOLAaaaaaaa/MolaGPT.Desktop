namespace MolaGPT.Core.Models;

/// <summary>
/// Token / call usage reported by the upstream model (parsed from the
/// final SSE chunk's "usage" field when present).
/// </summary>
public sealed record Usage(int? PromptTokens, int? CompletionTokens, int? TotalTokens);
