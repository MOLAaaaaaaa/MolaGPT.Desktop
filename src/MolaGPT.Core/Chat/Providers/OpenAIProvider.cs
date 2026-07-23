namespace MolaGPT.Core.Chat.Providers;

/// <summary>
/// OpenAI canonical provider — thin sugar over <see cref="OpenAICompatibleProvider"/>
/// with the official base URL pre-filled.
/// </summary>
public sealed class OpenAIProvider
{
    public const string DefaultBaseUrl = "https://api.openai.com/";

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
            Kind = ProviderKind.OpenAI,
            ChatPath = OpenAICompatibleProvider.ResolveChatPath(chatPath),
            CustomHeaders = customHeaders
        };
}
