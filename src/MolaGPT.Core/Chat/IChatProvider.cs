using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat;

/// <summary>
/// Unified chat-provider abstraction (cf. Chatbox's src/shared/providers/index.ts).
/// MolaGptProxyProvider wraps the existing PHP proxy; the BYOK providers
/// (OpenAI / Anthropic / Gemini / OpenAI-compatible) call upstream APIs directly.
/// </summary>
public interface IChatProvider
{
    /// <summary>Stable provider id ("openai", "anthropic", "molagpt-proxy", or a user-assigned GUID).</summary>
    string Id { get; }

    /// <summary>Friendly name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>Provider kind for grouping / icon selection.</summary>
    ProviderKind Kind { get; }

    /// <summary>Available models. May be refreshed at runtime (e.g. MolaGPT registry).</summary>
    IReadOnlyList<ProviderModel> Models { get; }

    /// <summary>Open a streaming chat completion. Implementations MUST yield ChatChunks as soon as available.</summary>
    IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatRequest request, CancellationToken ct);
}

public enum ProviderKind
{
    MolaGptProxy,
    OpenAI,
    OpenAICompatible,
    Anthropic,
    Gemini,
    Custom
}
