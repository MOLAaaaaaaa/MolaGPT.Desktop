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
    MolaGptLocalTools,
    OpenAI,
    OpenAICompatible,
    Anthropic,
    Gemini,
    Custom
}

public static class MolaGptProviderIds
{
    public const string Proxy = "molagpt-proxy";
    public const string LocalTools = "molagpt-local-tools";

    public static bool IsMolaGptAccount(string? providerId) =>
        string.Equals(providerId, Proxy, StringComparison.OrdinalIgnoreCase)
        || string.Equals(providerId, LocalTools, StringComparison.OrdinalIgnoreCase);
}

public static class ProviderKindExtensions
{
    public static bool IsMolaGptAccount(this ProviderKind kind) =>
        kind is ProviderKind.MolaGptProxy or ProviderKind.MolaGptLocalTools;
}

/// <summary>
/// Explicit, user-facing app mode. This is the UI-layer truth source for the
/// three-way sidebar slider, derived from the active provider rather than
/// stored separately (so it can never disagree with <see cref="IChatProvider"/>):
/// <list type="bullet">
///   <item><see cref="Byok"/> — local-tools agent on the user's own API key (default).</item>
///   <item><see cref="Chat"/> — MolaGPT account cloud chat (<c>molagpt-proxy</c>), server-orchestrated, synced.</item>
///   <item><see cref="Work"/> — local-tools agent on the MolaGPT account's shared quota (<c>molagpt-local-tools</c>).</item>
/// </list>
/// </summary>
public enum AppMode
{
    Byok,
    Chat,
    Work
}

public static class AppModeExtensions
{
    /// <summary>Derive the explicit <see cref="AppMode"/> from a provider. Null → Byok
    /// (the welcome / signed-out state defaults to BYOK).</summary>
    public static AppMode ToAppMode(this IChatProvider? provider)
    {
        if (provider is null) return AppMode.Byok;
        if (provider.Kind == ProviderKind.MolaGptProxy) return AppMode.Chat;
        if (string.Equals(provider.Id, MolaGptProviderIds.LocalTools, StringComparison.OrdinalIgnoreCase))
            return AppMode.Work;
        return AppMode.Byok;
    }

    /// <summary>True for the two MolaGPT account modes (Chat / Work) where the
    /// quota chip and account identity apply. BYOK never qualifies.</summary>
    public static bool IsMolaGptAccount(this AppMode mode) => mode is AppMode.Chat or AppMode.Work;

    /// <summary>True for the two local-tools agent modes (BYOK / Work) that share
    /// the tool-call thread look and the local-tools composer toolbar.</summary>
    public static bool IsLocalAgent(this AppMode mode) => mode is AppMode.Byok or AppMode.Work;

    /// <summary>True when moving from <paramref name="from"/> to <paramref name="to"/>
    /// crosses the Chat ↔ local-agent boundary — the only mode change that can't
    /// continue the current conversation. Chat is cloud/server-orchestrated; Work and
    /// BYOK share the local agent thread, so switching between them keeps the chat.</summary>
    public static bool CrossesChatBoundary(this AppMode from, AppMode to) =>
        (from == AppMode.Chat) != (to == AppMode.Chat);
}
