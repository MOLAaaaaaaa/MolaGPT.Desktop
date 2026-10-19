namespace MolaGPT.Core.Models;

/// <summary>
/// The native Gemini 3/4 thinking-level capabilities. Gemini 2.5 is deliberately
/// excluded so its existing thinking-budget behavior remains unchanged.
/// </summary>
public static class GeminiModelPolicy
{
    public static bool UsesThinkingLevel(string? modelId) => ThinkingFor(modelId) is not null;

    public static ThinkingConfig? ThinkingFor(string? modelId)
    {
        var model = NormalizeModelId(modelId);
        if (model.Length == 0 || model.Contains("-non-reasoning", StringComparison.Ordinal)) return null;

        return model switch
        {
            _ when model.StartsWith("gemini-4-argon", StringComparison.Ordinal)
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
            _ when model.StartsWith("gemini-4", StringComparison.Ordinal)
                => Level(["low", "medium", "high"], "medium"),
            _ when model.StartsWith("gemini-3", StringComparison.Ordinal)
                => Level(["low", "medium", "high"], "medium"),
            _ => null
        };
    }

    /// <summary>
    /// Maps the common UI vocabulary to one of this model's supported native
    /// thinking levels. Unknown model IDs are left untouched for callers that
    /// use this helper outside the known Gemini 3/4 families.
    /// </summary>
    public static string NormalizeEffort(string modelId, string effort)
    {
        var config = ThinkingFor(modelId);
        if (config is null) return (effort ?? string.Empty).Trim().ToLowerInvariant();

        var value = (effort ?? string.Empty).Trim().ToLowerInvariant();
        var levels = config.EffortLevels ?? [];
        if (value is "off" or "none") return levels[0];
        if (value == "minimal" && !levels.Contains(value, StringComparer.Ordinal))
            return levels.Contains("low", StringComparer.Ordinal) ? "low" : config.DefaultEffort ?? levels[^1];
        if (value is "xhigh" or "max" or "ultra") value = "high";
        return levels.Contains(value, StringComparer.Ordinal)
            ? value
            : config.DefaultEffort ?? levels[^1];
    }

    public static (string[] Levels, string DefaultEffort) ResolveEfforts(
        ThinkingConfig policy,
        IEnumerable<string>? storedLevels,
        string? storedDefault)
    {
        var supported = ThinkingEffortLevels.Normalize(policy.EffortLevels);
        var stored = ThinkingEffortLevels.Normalize(storedLevels)
            .Where(level => supported.Contains(level, StringComparer.Ordinal))
            .ToArray();
        var levels = stored.Length > 0 ? stored : supported;
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
        if (value.StartsWith("models/", StringComparison.Ordinal)) value = value[7..];
        return value.Contains('/')
            ? value[(value.LastIndexOf('/') + 1)..]
            : value;
    }
}
