using System.Text.Json;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat;

internal static class GeminiRequestParams
{
    private static readonly string[] DeprecatedKeys =
        ["temperature", "top_p", "topP", "top_k", "topK", "thinking_budget", "thinkingBudget"];
    private static readonly string[] ConfigKeys =
        ["generationConfig", "generation_config", "thinkingConfig", "thinking_config", "extra_body", "google"];

    public static bool Applies(string? modelId, bool native, string? endpoint)
    {
        if (!GeminiModelPolicy.UsesThinkingLevel(modelId)) return false;
        if (native) return true;
        var uri = new Uri(endpoint!, UriKind.Absolute);
        return uri.Host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/v1beta/openai/", StringComparison.OrdinalIgnoreCase);
    }

    public static void Apply(
        IDictionary<string, object?> body, string modelId, bool native,
        string? effort = null, bool? useThinking = null)
    {
        var customLevel = Clean(body);
        if (native)
        {
            var generation = ReadConfig(body, "generationConfig");
            var thinking = ReadConfig(generation, "thinkingConfig");
            var requested = customLevel ?? effort
                ?? (useThinking == false ? "off" : null);
            if (requested is not null)
            {
                thinking["thinkingLevel"] = GeminiModelPolicy.NormalizeEffort(modelId, requested).ToUpperInvariant();
                generation["thinkingConfig"] = thinking;
                body["generationConfig"] = generation;
            }
        }
        else
        {
            var requested = ReadString(body, "reasoning_effort") ?? effort ?? customLevel
                ?? (useThinking == false ? "off" : null);
            if (requested is not null)
                body["reasoning_effort"] = GeminiModelPolicy.NormalizeEffort(modelId, requested);
        }
    }

    private static string? Clean(IDictionary<string, object?> body)
    {
        var level = ReadString(body, "thinkingLevel") ?? ReadString(body, "thinking_level");
        foreach (var key in DeprecatedKeys) body.Remove(key);
        body.Remove("thinking_level");
        body.Remove("thinkingLevel");
        foreach (var key in ConfigKeys)
        {
            if (!body.TryGetValue(key, out var value) || value is null) continue;
            var element = JsonSerializer.SerializeToElement(value);
            if (element.ValueKind != JsonValueKind.Object) continue;
            var config = element.Deserialize<Dictionary<string, object?>>()!;
            var nestedLevel = Clean(config);
            level ??= nestedLevel;
            if (config.Count == 0) body.Remove(key);
            else body[key] = config;
        }
        return level;
    }

    private static Dictionary<string, object?> ReadConfig(IDictionary<string, object?> body, string key) =>
        body.TryGetValue(key, out var value) && value is not null
            ? JsonSerializer.SerializeToElement(value).Deserialize<Dictionary<string, object?>>()!
            : new Dictionary<string, object?>();

    private static string? ReadString(IDictionary<string, object?> body, string key) =>
        body.TryGetValue(key, out var value) && value is not null
            ? JsonSerializer.SerializeToElement(value).GetString()
            : null;
}
