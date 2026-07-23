namespace MolaGPT.Core.Models;

using System.Text.Json;

public enum ThinkingParamKind
{
    None,
    OpenAiReasoningEffort,
    AnthropicAdaptive,
    AnthropicBudget,
    DeepSeekV4,
    GeminiThinkingLevel,
    GeminiBudget,
    QwenThinkingBudget,
}

public sealed record ThinkingConfig(
    ThinkingParamKind Kind,
    string[]? EffortLevels = null,
    int? MinBudget = null,
    int? MaxBudget = null,
    int? DefaultBudget = null,
    string? DefaultEffort = null);

public static class ThinkingEffortLevels
{
    public static string[] ForKind(ThinkingParamKind kind) => kind switch
    {
        ThinkingParamKind.OpenAiReasoningEffort => ["minimal", "low", "medium", "high", "xhigh"],
        ThinkingParamKind.AnthropicAdaptive => ["low", "medium", "high", "xhigh", "max"],
        ThinkingParamKind.DeepSeekV4 => ["high", "max"],
        ThinkingParamKind.GeminiThinkingLevel => ["minimal", "low", "medium", "high"],
        ThinkingParamKind.AnthropicBudget or ThinkingParamKind.GeminiBudget or ThinkingParamKind.QwenThinkingBudget
            => ["low", "medium", "high"],
        _ => ["low", "medium", "high"]
    };

    public static string[] Resolve(ThinkingConfig? config, ThinkingParamKind kind)
    {
        var custom = Normalize(config?.EffortLevels);
        return custom.Length > 0 ? custom : ForKind(kind);
    }

    public static string[] Normalize(IEnumerable<string>? levels)
    {
        if (levels is null) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in levels)
        {
            var v = (raw ?? "").Trim().ToLowerInvariant();
            if (v.Length == 0 || !seen.Add(v)) continue;
            list.Add(v);
        }
        return list.ToArray();
    }
}

public static class ThinkingParamKindInference
{
    public static ThinkingParamKind InferFromModelId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return ThinkingParamKind.None;

        var lower = modelId.ToLowerInvariant();
        if (lower.Contains("deepseek-v4", StringComparison.Ordinal)
            || lower.Contains("deepseek-reasoner", StringComparison.Ordinal)
            || lower.Contains("deepseek-r1", StringComparison.Ordinal))
            return ThinkingParamKind.DeepSeekV4;

        if (lower.Contains("qwen3", StringComparison.Ordinal)
            || lower.Contains("qwq", StringComparison.Ordinal))
            return ThinkingParamKind.QwenThinkingBudget;

        if (lower.Contains("gemini-2.5", StringComparison.Ordinal)
            || lower.Contains("gemini-3", StringComparison.Ordinal))
            return ThinkingParamKind.GeminiBudget;

        if (lower.StartsWith("o1", StringComparison.Ordinal)
            || lower.StartsWith("o3", StringComparison.Ordinal)
            || lower.StartsWith("o4", StringComparison.Ordinal)
            || lower.StartsWith("gpt-5", StringComparison.Ordinal)
            || lower.Contains("reasoning", StringComparison.Ordinal))
            return ThinkingParamKind.OpenAiReasoningEffort;

        return ThinkingParamKind.None;
    }
}

/// <summary>
/// A model exposed by a provider. ProviderModel.Id is the wire-level model name
/// (what gets sent in the request body's "model" field, e.g. "gpt-4o-mini",
/// "claude-3-5-sonnet-20241022", or a MolaGPT routes key like "g3f").
/// </summary>
public sealed record ProviderModel(
    string Id,
    string DisplayName,
    bool SupportsVision = false,
    bool SupportsThinking = false,
    bool SupportsReasoningEffort = false,
    bool SupportsToolCalling = false,
    int? ContextWindow = null,
    int? MaxOutputTokens = null,
    string? Description = null,
    ThinkingConfig? ThinkingConfig = null,
    IReadOnlyDictionary<string, JsonElement>? CustomBody = null);
