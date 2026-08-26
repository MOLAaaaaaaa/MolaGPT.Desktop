using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Models;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Services;

/// <summary>
/// Rebuilds the BYOK provider registry from what the user saved.
///
/// This is entirely provider and credential logic with no UI dependency.
/// </summary>
public static class ProviderRestorer
{
    /// <summary>
    /// Registers every enabled, non-image provider row. Each row is guarded
    /// individually: one malformed saved provider must not cost the user all the
    /// others, which is why the try sits inside the loop.
    /// </summary>
    public static void Restore(IServiceProvider services, Action<string>? log = null)
    {
        try
        {
            var repo = services.GetRequiredService<ProviderRepository>();
            var registry = services.GetRequiredService<ProviderRegistry>();
            var creds = services.GetRequiredService<CredentialStore>();
            var http = services.GetRequiredService<IHttpClientFactory>();

            foreach (var row in repo.List())
            {
                try
                {
                    if (!row.Enabled) continue;
                    if (SettingsViewModel.IsImagePurpose(row.Purpose)) continue;

                    var apiKey = row.ApiKeyEnc is { Length: > 0 }
                        ? creds.Decrypt(row.ApiKeyEnc) ?? string.Empty
                        : string.Empty;

                    var models = TryDeserializeModels(row.Models);
                    var client = http.CreateClient(HttpClientNames.Byok);
                    var headers = CustomParamConverter.ToHeaderListFromJson(row.CustomHeaders);
                    var toolHost = services.GetService<IChatToolHost>();

                    IChatProvider? provider = CreateDirectProvider(
                        row.Type, row.Id, row.Name, row.BaseUrl, row.ApiPath,
                        apiKey, models, client, toolHost, headers);

                    // Opt-in (pi.byok.enabled): re-host eligible BYOK providers on
                    // the Pi harness under the same id. Falls through to the direct
                    // provider whenever that is not possible.
                    provider = services.GetService<PiByokProviderFactory>()
                                   ?.TryWrap(row.Type, row.Id, row.Name, row.BaseUrl, row.ApiPath,
                                       apiKey, models, headers)
                               ?? provider;

                    if (provider is not null) registry.Register(provider);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Restore provider '{row.Name}' failed: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"RestoreSavedProviders failed: {ex}");
        }
    }

    public static void ApplyEntry(
        ProviderEntry entry,
        ProviderRegistry registry,
        Func<HttpClient> httpFactory,
        IChatToolHost? toolHost = null,
        PiByokProviderFactory? pi = null)
    {
        RemoveEntry(entry.Id, registry, pi);
        if (!entry.Enabled || SettingsViewModel.IsImagePurpose(entry.Purpose)) return;

        var models = entry.Models.Select(ToProviderModel).ToList();
        var headers = CustomParamConverter.ToHeaderList(entry.CustomHeaders);
        var provider = CreateDirectProvider(
            entry.Type, entry.Id, entry.Name, entry.BaseUrl, entry.ApiPath,
            entry.ApiKey ?? string.Empty, models, httpFactory(), toolHost, headers);

        provider = pi?.TryWrap(
                       entry.Type, entry.Id, entry.Name, entry.BaseUrl, entry.ApiPath,
                       entry.ApiKey ?? string.Empty, models, headers)
                   ?? provider;
        if (provider is not null) registry.Register(provider);
    }

    public static void RemoveEntry(string id, ProviderRegistry registry, PiByokProviderFactory? pi = null)
    {
        registry.Unregister(id);
        pi?.Retire(id);
    }

    private static IChatProvider? CreateDirectProvider(
        string type,
        string id,
        string name,
        string? baseUrl,
        string? apiPath,
        string apiKey,
        IReadOnlyList<ProviderModel> models,
        HttpClient client,
        IChatToolHost? toolHost,
        IReadOnlyList<KeyValuePair<string, string>>? headers) => type switch
    {
        "openai" => OpenAIProvider.Create(id, name, apiKey, models, client, baseUrl, apiPath, headers),

        "openai-compat" => new OpenAICompatibleProvider(
            id, name, baseUrl ?? OpenAIProvider.DefaultBaseUrl, apiKey, models, client, toolHost)
        {
            ChatPath = OpenAICompatibleProvider.ResolveChatPath(apiPath),
            CustomHeaders = headers
        },

        "openai-response" => new OpenAICompatibleProvider(
            id, name, baseUrl ?? OpenAIProvider.DefaultBaseUrl, apiKey, models, client, toolHost)
        {
            WireApi = OpenAiWireApi.Responses,
            ChatPath = string.IsNullOrWhiteSpace(apiPath)
                ? OpenAICompatibleProvider.DefaultResponsesPath
                : apiPath.Trim(),
            CustomHeaders = headers
        },

        "anthropic" => new AnthropicProvider(id, name, apiKey, models, client, baseUrl)
        {
            MessagesPath = string.IsNullOrWhiteSpace(apiPath) ? "v1/messages" : apiPath.Trim(),
            CustomHeaders = headers
        },

        "gemini" => GeminiProvider.Create(id, name, apiKey, models, client, baseUrl, apiPath, headers),
        _ => null
    };

    public static List<ProviderModel> TryDeserializeModels(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var entries = JsonSerializer.Deserialize<List<ProviderModelEntry>>(json) ?? new();
            return entries.Select(ToProviderModel).ToList();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public static ProviderModel ToProviderModel(ProviderModelEntry entry)
    {
        ThinkingConfig? thinkingConfig = null;
        var kindStr = entry.ThinkingParamKind;

        if (entry.Thinking && string.IsNullOrWhiteSpace(kindStr))
        {
            var inferred = ThinkingParamKindInference.InferFromModelId(entry.Id);
            if (inferred != ThinkingParamKind.None) kindStr = inferred.ToString();
        }

        if (kindStr is { } && Enum.TryParse<ThinkingParamKind>(kindStr, true, out var kind))
        {
            thinkingConfig = new ThinkingConfig(
                kind,
                EffortLevels: ThinkingEffortLevels.Normalize(entry.EffortLevels) is { Length: > 0 } levels
                    ? levels
                    : null,
                MinBudget: entry.ThinkingBudgetMin,
                MaxBudget: entry.ThinkingBudgetMax,
                DefaultBudget: entry.ThinkingBudgetDefault,
                DefaultEffort: entry.DefaultEffort);
        }

        return new ProviderModel(
            entry.Id,
            NormalizeAutoModelDisplayName(entry.Id, entry.DisplayName),
            SupportsVision: entry.Vision,
            SupportsThinking: entry.Thinking,
            SupportsReasoningEffort: entry.ReasoningEffort,
            SupportsToolCalling: entry.Tools,
            ContextWindow: entry.ContextWindow,
            ThinkingConfig: thinkingConfig,
            CustomBody: CustomParamConverter.ToBodyDict(entry.CustomBody));
    }

    /// <summary>
    /// Keeps a hand-edited display name, but re-beautifies one that still matches
    /// the old auto-generated form — the legacy rule also replaced hyphens, which
    /// mangled ids like "gpt-4o" into "gpt 4o".
    /// </summary>
    private static string NormalizeAutoModelDisplayName(string id, string displayName)
    {
        var trimmed = (displayName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed)) return BeautifyModelName(id);

        return string.Equals(trimmed, LegacyBeautifyModelName(id), StringComparison.Ordinal)
            ? BeautifyModelName(id)
            : trimmed;
    }

    private static string BeautifyModelName(string id)
    {
        var name = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        return name.Replace('_', ' ');
    }

    private static string LegacyBeautifyModelName(string id)
    {
        var name = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        return name.Replace('-', ' ').Replace('_', ' ');
    }
}
