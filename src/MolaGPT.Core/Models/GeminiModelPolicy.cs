using MolaGPT.Core.Chat;

namespace MolaGPT.Core.Models;

/// <summary>
/// Gemini endpoint detection and model-specific thinking capabilities.
/// </summary>
public static class GeminiModelPolicy
{
    public const string ApiHost = "generativelanguage.googleapis.com";
    public const string NativeBaseUrl = "https://" + ApiHost + "/v1beta/";
    public const string OpenAiBaseUrl = NativeBaseUrl + "openai/";

    public static bool IsRoute(string? providerType, string? baseUrl, string? apiPath = null) =>
        string.Equals(providerType, "gemini", StringComparison.OrdinalIgnoreCase)
            ? !OpenRouterAttribution.IsOpenRouterHost(baseUrl)
            : IsOpenAiCompatRoute(providerType, baseUrl, apiPath);

    public static bool IsOpenAiCompatRoute(string? providerType, string? baseUrl, string? apiPath = null)
    {
        return string.Equals(providerType, "openai-compat", StringComparison.OrdinalIgnoreCase)
            && ResolveEndpoint(baseUrl, apiPath) is { } endpoint
            && IsOpenAiCompatEndpoint(endpoint);
    }

    private static Uri? ResolveEndpoint(string? baseUrl, string? apiPath)
    {
        if (string.IsNullOrWhiteSpace(apiPath))
            return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri : null;
        return Uri.TryCreate(baseUrl?.TrimEnd('/') + "/", UriKind.Absolute, out var root)
            && Uri.TryCreate(root, apiPath.Trim(), out var endpoint)
            ? endpoint : null;
    }

    public static bool IsOpenAiCompatEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && IsOpenAiCompatEndpoint(uri);

    private static bool IsOpenAiCompatEndpoint(Uri uri) =>
        !OpenRouterAttribution.IsOpenRouterHost(uri)
        && (uri.Host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase)
            || (uri.AbsolutePath.TrimEnd('/') + "/").Contains("/v1beta/openai/", StringComparison.OrdinalIgnoreCase));

    public static bool IsLegacyNativePreset(string? providerType, string? baseUrl, string? apiPath) =>
        string.Equals(providerType, "gemini", StringComparison.OrdinalIgnoreCase)
        && string.Equals(baseUrl?.Trim().TrimEnd('/'), OpenAiBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(apiPath) || string.Equals(apiPath.Trim(), "v1/chat/completions", StringComparison.OrdinalIgnoreCase));

    public static bool UsesThinkingLevel(string? modelId) => ThinkingFor(modelId) is not null;

    public static ThinkingConfig? OfficialLevelPolicy(
        string? modelId, string? providerType, string? baseUrl, string? apiPath = null)
    {
        var native = string.Equals(providerType, "gemini", StringComparison.OrdinalIgnoreCase);
        if (!native && !string.Equals(providerType, "openai-compat", StringComparison.OrdinalIgnoreCase))
            return null;
        // The native endpoint joins the path onto the configured base. The
        // OpenAI-compatible endpoint can instead supply its own absolute URI.
        var endpoint = ResolveEndpoint(baseUrl, native ? null : apiPath);
        return endpoint is { Scheme: "https", Port: 443, UserInfo.Length: 0 }
            && endpoint.Host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase)
            ? ThinkingFor(modelId, knownOnly: true) : null;
    }

    public static ThinkingConfig? DefaultThinkingFor(string? modelId) =>
        ThinkingFor(modelId) ?? (NormalizeModelId(modelId).StartsWith("gemini-2.5-pro", StringComparison.Ordinal)
            ? new ThinkingConfig(ThinkingParamKind.GeminiBudget, MinBudget: 128, Mandatory: true)
            : null);

    public static ThinkingConfig? ThinkingFor(string? modelId) => ThinkingFor(modelId, knownOnly: false);

    private static ThinkingConfig? ThinkingFor(string? modelId, bool knownOnly)
    {
        var model = NormalizeModelId(modelId);
        if (model.Length == 0 || ModelCapabilityCatalog.Find(modelId) is { Reasoning: false }) return null;
        if (ModelCapabilityCatalog.Find(modelId) is { } known && known.CanonicalId.StartsWith("google/gemini-"))
            model = NormalizeModelId(known.CanonicalId);

        return model switch
        {
            _ when model.StartsWith("gemini-4-neon", StringComparison.Ordinal)
                || model.StartsWith("gemini-4-hydrogen", StringComparison.Ordinal)
                => Level(["low", "medium", "high", "xhigh"], "high"),
            _ when model.StartsWith("gemini-4-argon", StringComparison.Ordinal)
                || model.StartsWith("gemini-4-carbon", StringComparison.Ordinal)
                => Level(["low", "medium", "high"], "medium"),
            _ when model.StartsWith("gemini-3.8-flash", StringComparison.Ordinal)
                => Level(["low", "medium", "high"], "medium"),
            _ when model.StartsWith("gemini-3.6-flash", StringComparison.Ordinal)
                => Level(["minimal", "low", "medium", "high"], "medium"),
            _ when model.StartsWith("gemini-3.1-flash-lite-image", StringComparison.Ordinal)
                => Level(["minimal", "high"], "minimal"),
            _ when model.StartsWith("gemini-3.5-flash-lite", StringComparison.Ordinal)
                || model.StartsWith("gemini-3.1-flash-lite", StringComparison.Ordinal)
                => Level(["minimal", "low", "medium", "high"], "minimal"),
            _ when model.StartsWith("gemini-3.1-pro", StringComparison.Ordinal)
                => Level(["low", "medium", "high"], "high"),
            _ when model.StartsWith("gemini-3-pro", StringComparison.Ordinal)
                => Level(["low", "high"], "high"),
            _ when model.StartsWith("gemini-3-flash", StringComparison.Ordinal)
                => Level(["minimal", "low", "medium", "high"], "high"),
            _ when model.Equals("gemini-flash-latest", StringComparison.Ordinal)
                => Level(["low", "medium", "high"], "medium"),
            _ when model.Equals("gemini-flash-lite-latest", StringComparison.Ordinal)
                => Level(["minimal", "low", "medium", "high"], "minimal"),
            _ when !knownOnly && model.StartsWith("gemini-4", StringComparison.Ordinal)
                => Level(["low", "medium", "high"], "medium"),
            _ when !knownOnly && model.StartsWith("gemini-3", StringComparison.Ordinal)
                => Level(["low", "medium", "high"], "medium"),
            _ => null
        };
    }

    /// <summary>
    /// Maps the requested level to the vocabulary resolved for this model.
    /// </summary>
    public static string NormalizeEffort(ThinkingConfig config, string? effort)
    {
        var value = (effort ?? string.Empty).Trim().ToLowerInvariant();
        var (levels, defaultEffort) = ResolveEfforts(config with { EffortLevels = null }, config.EffortLevels, config.DefaultEffort);
        if (value is "off" or "none") return ThinkingEffortLevels.MinimumEnabled(levels)!;
        if (levels.Contains(value, StringComparer.Ordinal)) return value;
        if (value == "minimal" && levels.Contains("low", StringComparer.Ordinal)) return "low";
        if (value is "xhigh" or "max" or "ultra") value = "high";
        return levels.Contains(value, StringComparer.Ordinal) ? value : defaultEffort;
    }

    public static (string[] Levels, string DefaultEffort) ResolveEfforts(
        ThinkingConfig policy,
        IEnumerable<string>? storedLevels,
        string? storedDefault,
        IReadOnlyList<string>? allowedLevels = null)
    {
        var stored = ThinkingEffortLevels.Normalize(storedLevels)
            .Where(level => level is not ("off" or "none"))
            .Where(level => allowedLevels is null || allowedLevels.Contains(level, StringComparer.Ordinal))
            .ToArray();
        var levels = stored.Length > 0 ? stored : policy.EffortLevels
            ?? ThinkingEffortLevels.ForKind(ThinkingParamKind.GeminiThinkingLevel);
        var defaultEffort = (storedDefault ?? string.Empty).Trim().ToLowerInvariant();
        if (!levels.Contains(defaultEffort, StringComparer.Ordinal))
        {
            var preferred = policy.DefaultEffort ?? string.Empty;
            defaultEffort = levels.Contains(preferred, StringComparer.Ordinal)
                ? preferred
                : levels.Contains("high", StringComparer.Ordinal) ? "high" : levels[0];
        }
        return (levels, defaultEffort);
    }

    private static ThinkingConfig Level(string[] levels, string defaultEffort) =>
        new(
            ThinkingParamKind.GeminiThinkingLevel,
            EffortLevels: levels,
            DefaultEffort: defaultEffort,
            Mandatory: true);

    private static string NormalizeModelId(string? modelId)
    {
        var value = (modelId ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Contains("-non-reasoning", StringComparison.Ordinal)) return string.Empty;
        if (value.StartsWith("models/", StringComparison.Ordinal)) value = value[7..];
        return value.Contains('/')
            ? value[(value.LastIndexOf('/') + 1)..]
            : value;
    }
}
