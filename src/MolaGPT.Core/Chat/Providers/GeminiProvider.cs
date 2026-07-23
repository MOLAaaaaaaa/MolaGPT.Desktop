namespace MolaGPT.Core.Chat.Providers;

/// <summary>
/// Google Gemini provider. Routes through Google's OpenAI-compatible endpoint,
/// so it can reuse <see cref="OpenAICompatibleProvider"/> with a custom base URL.
///
/// v1.5: switch to native streamGenerateContent for tool-use parity.
/// </summary>
public static class GeminiProvider
{
    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/";

    public static OpenAICompatibleProvider Create(
        string id,
        string displayName,
        string apiKey,
        IReadOnlyList<MolaGPT.Core.Models.ProviderModel> models,
        HttpClient http,
        string? baseUrl = null,
        string? chatPath = null,
        IReadOnlyList<KeyValuePair<string, string>>? customHeaders = null) =>
        new(id, displayName, baseUrl ?? DefaultBaseUrl, apiKey, models, http)
        {
            Kind = ProviderKind.Gemini,
            ChatPath = OpenAICompatibleProvider.ResolveChatPath(chatPath),
            CustomHeaders = customHeaders
        };
}
