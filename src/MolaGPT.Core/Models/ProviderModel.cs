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

/// <param name="Mandatory">The provider says this model always reasons. Nothing may be
/// sent to switch it off — OpenRouter's own guidance is to hide the disable control and
/// never send <c>effort: "none"</c>, which such a model rejects.</param>
public sealed record ThinkingConfig(
    ThinkingParamKind Kind,
    string[]? EffortLevels = null,
    int? MinBudget = null,
    int? MaxBudget = null,
    int? DefaultBudget = null,
    string? DefaultEffort = null,
    bool Mandatory = false);

public static class ThinkingEffortLevels
{
    public static string[] ForKind(ThinkingParamKind kind) => kind switch
    {
        ThinkingParamKind.OpenAiReasoningEffort => ["minimal", "low", "medium", "high", "xhigh"],
        ThinkingParamKind.AnthropicAdaptive => ["low", "medium", "high", "xhigh", "max"],
        ThinkingParamKind.DeepSeekV4 => ["high", "max"],
        ThinkingParamKind.GeminiThinkingLevel => ["low", "medium", "high"],
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
        // Gateways prefix the vendor ("openai/gpt-5.6-luna"), so the family checks below
        // have to run on the leaf. Matching the whole id sent every OpenRouter-hosted
        // OpenAI model down the None path, where turning reasoning off wrote nothing at
        // all and the model quietly kept thinking.
        var leaf = lower.Contains('/', StringComparison.Ordinal)
            ? lower[(lower.LastIndexOf('/') + 1)..]
            : lower;
        if (lower.Contains("deepseek-v4", StringComparison.Ordinal)
            || lower.Contains("deepseek-reasoner", StringComparison.Ordinal)
            || lower.Contains("deepseek-r1", StringComparison.Ordinal))
            return ThinkingParamKind.DeepSeekV4;

        if (lower.Contains("qwen3", StringComparison.Ordinal)
            || lower.Contains("qwq", StringComparison.Ordinal))
            return ThinkingParamKind.QwenThinkingBudget;

        if (lower.Contains("gemini-3", StringComparison.Ordinal))
            return ThinkingParamKind.GeminiThinkingLevel;

        if (lower.Contains("gemini-2.5", StringComparison.Ordinal))
            return ThinkingParamKind.GeminiBudget;

        if (leaf.StartsWith("o1", StringComparison.Ordinal)
            || leaf.StartsWith("o3", StringComparison.Ordinal)
            || leaf.StartsWith("o4", StringComparison.Ordinal)
            || leaf.StartsWith("gpt-5", StringComparison.Ordinal)
            || leaf.StartsWith("gpt-6", StringComparison.Ordinal)
            || lower.Contains("reasoning", StringComparison.Ordinal))
            return ThinkingParamKind.OpenAiReasoningEffort;

        return ThinkingParamKind.None;
    }
}

/// <summary>
/// What one million tokens cost, in USD. The unit matches both models.dev's
/// <c>cost</c> block and the rates Pi's own <c>calculateCost</c> divides by
/// 1,000,000 — so a price collected from either source goes through unconverted.
/// </summary>
/// <param name="Source">Where the numbers came from: <c>endpoint</c>, <c>models.dev</c>
/// or <c>manual</c>. A manual price is never overwritten by a refresh.</param>
public sealed record ModelPricing(
    double Input,
    double Output,
    double? CacheRead = null,
    double? CacheWrite = null,
    string? Source = null)
{
    public const string SourceEndpoint = "endpoint";
    public const string SourceModelsDev = "models.dev";
    public const string SourceModelsDevPrefix = SourceModelsDev + ":";
    public const string SourceManual = "manual";

    public bool IsManual => string.Equals(Source, SourceManual, StringComparison.OrdinalIgnoreCase);
    public bool IsModelsDev => string.Equals(Source, SourceModelsDev, StringComparison.OrdinalIgnoreCase)
                               || Source?.StartsWith(SourceModelsDevPrefix, StringComparison.OrdinalIgnoreCase) == true;
    public string? ModelsDevProviderKey => Source?.StartsWith(SourceModelsDevPrefix, StringComparison.OrdinalIgnoreCase) == true
        ? Source[SourceModelsDevPrefix.Length..]
        : null;

    public static string ModelsDevSource(string providerKey) => SourceModelsDevPrefix + providerKey;
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
    IReadOnlyDictionary<string, JsonElement>? CustomBody = null,
    bool SupportsTemperature = true,
    bool SupportsTopP = true,
    ModelPricing? Pricing = null);
