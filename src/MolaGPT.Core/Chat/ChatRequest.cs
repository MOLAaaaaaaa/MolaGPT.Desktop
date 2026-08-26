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

/// <summary>
/// Keys the app packs into <see cref="ChatRequest.ExtraBody"/> for its own
/// plumbing rather than for the upstream API. <c>enabled_tools</c> is the local
/// tool config (it carries the user's search/image/MCP credentials) and
/// <c>privacy_mode</c> is a MolaGPT proxy protocol field; both are meaningless to
/// a third-party endpoint, and strict gateways reject the unknown field outright.
/// Only <see cref="Providers.MolaGptProxyProvider"/> puts them on the wire — every
/// BYOK provider merges through <see cref="MergeSendable"/> instead.
/// </summary>
public static class InternalExtraBodyKeys
{
    public const string EnabledTools = "enabled_tools";
    public const string PrivacyMode = "privacy_mode";

    public static bool IsInternal(string key) => key is EnabledTools or PrivacyMode;

    /// <summary>Merges the wire-safe part of <paramref name="extraBody"/> into
    /// <paramref name="body"/>, dropping the internal keys.</summary>
    public static void MergeSendable(
        Dictionary<string, object?> body,
        IReadOnlyDictionary<string, object>? extraBody)
    {
        if (extraBody is null) return;
        foreach (var kv in extraBody)
            if (!IsInternal(kv.Key)) body[kv.Key] = kv.Value;
    }
}
