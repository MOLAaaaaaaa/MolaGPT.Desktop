using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat;

/// <summary>
/// Provider-agnostic chat request. Concrete IChatProvider implementations
/// translate this into the upstream wire format (OpenAI / Anthropic / etc).
/// </summary>
public sealed record ChatRequest(
    string ModelId,
    IReadOnlyList<ChatMessage> Messages,
    double? Temperature = null,
    bool Stream = true,
    bool? UseThinking = null,
    string? ReasoningEffort = null,
    string? ConversationId = null,
    string? SessionId = null,
    int? MaxTokens = null,
    Dictionary<string, object>? ExtraBody = null,
    int? ThinkingBudgetTokens = null,
    Models.ThinkingParamKind? ThinkingParamKind = null);
