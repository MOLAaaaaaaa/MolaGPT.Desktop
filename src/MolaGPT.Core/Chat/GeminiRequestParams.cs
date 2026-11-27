using System.Text.Json;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat;

internal static class GeminiRequestParams
{
    private static readonly string[] DeprecatedKeys =
        ["temperature", "top_p", "topP", "top_k", "topK", "thinking_budget", "thinkingBudget"];
    private static readonly string[] ConfigKeys =
        ["generationConfig", "generation_config", "thinkingConfig", "thinking_config", "extra_body", "google"];

    public static void Apply(
        IDictionary<string, object?> body, ThinkingConfig config, bool native,
        string? effort, bool? useThinking, IReadOnlyDictionary<string, JsonElement>? overrides)
    {
        var incomingLevel = Clean(body);
        var requested = ReadOverride(overrides, native)
            ?? (useThinking == false ? "off" : effort ?? (useThinking == true ? config.DefaultEffort : null))
            ?? (native ? incomingLevel : ReadString(body, "reasoning_effort") ?? incomingLevel);
        if (requested is null) return;

        var level = GeminiModelPolicy.NormalizeEffort(config, requested);
        if (native)
        {
            body.Remove("reasoning_effort");
            var generation = ReadConfig(body, "generationConfig");
            var thinking = ReadConfig(generation, "thinkingConfig");
            thinking["thinkingLevel"] = level.ToUpperInvariant();
            generation["thinkingConfig"] = thinking;
            body["generationConfig"] = generation;
        }
        else
            body["reasoning_effort"] = level;
    }

    private static string? ReadOverride(IReadOnlyDictionary<string, JsonElement>? overrides, bool native)
    {
        if (overrides is not { Count: > 0 }) return null;
        if (!native && overrides.TryGetValue("reasoning_effort", out var effort) && effort.ValueKind == JsonValueKind.String)
            return effort.GetString();
        return Clean(overrides.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal));
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
            && JsonSerializer.SerializeToElement(value) is { ValueKind: JsonValueKind.String } element
            ? element.GetString()
            : null;
}
