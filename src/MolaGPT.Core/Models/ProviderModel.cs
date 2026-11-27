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
    QwenReasoningEffort,
    KimiToggle,
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
    public static IReadOnlyList<string> KnownLevels { get; } =
        ["minimal", "low", "medium", "high", "xhigh", "max", "ultra"];

    public static string[] ForKind(ThinkingParamKind kind) => kind switch
    {
        ThinkingParamKind.OpenAiReasoningEffort => ["minimal", "low", "medium", "high", "xhigh"],
        ThinkingParamKind.KimiToggle => [],
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

    public static string? MinimumEnabled(IEnumerable<string>? levels)
    {
        var enabled = Normalize(levels).Where(level => level is not ("off" or "none")).ToArray();
        return KnownLevels.FirstOrDefault(level => enabled.Contains(level, StringComparer.Ordinal))
               ?? enabled.FirstOrDefault();
    }
}

public static class ThinkingParamKindInference
{
    public static ThinkingParamKind InferFromModelId(string? modelId, ThinkingParamKind fallback = ThinkingParamKind.None)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return fallback;
        var raw = modelId.Trim().ToLowerInvariant();
        if (raw.Contains("-non-reasoning", StringComparison.Ordinal)) return fallback;
        var known = ModelCapabilityCatalog.Find(modelId);
        if (known is { Reasoning: false }) return fallback;
        var leaf = (known?.CanonicalId ?? raw).Split('/')[^1];
        var reasoning = known?.Reasoning == true ||
            System.Text.RegularExpressions.Regex.IsMatch(leaf, @"^(?:o[134](?:-|$)|gpt-[56](?:[.\-]|$)|gpt-oss|deepseek-(?:v(?:3\.\d|[4-9])|flash|reasoner|r1|chat)|kimi-k(?:2-thinking|2\.[5-9]|[3-9])|qwen[34](?:[.\-]|$)|qwq|gemini-(?:2\.5|[34])|claude-(?:3-7|3\.7|(?:sonnet|opus|haiku|fable|mythos)-[45])|grok-(?:3-mini|4)|glm-(?:5|4\.\d)|minimax-m[23]|mimo-v2|hunyuan-t1|magistral|seed-oss|gemma-4|pangu-pro-moe)") ||
            System.Text.RegularExpressions.Regex.IsMatch(leaf, @"(?:^|[-_])(?:reasoning|reasoner|thinking)(?:[-_]|$)");
        if (!reasoning) return fallback;

        if (leaf.StartsWith("deepseek-")) return ThinkingParamKind.DeepSeekV4;
        if (QwenEffortFor(leaf) is not null) return ThinkingParamKind.QwenReasoningEffort;
        if (leaf.StartsWith("qwen") || leaf.StartsWith("qwq")) return ThinkingParamKind.QwenThinkingBudget;
        if (leaf.StartsWith("gemini-"))
            return GeminiModelPolicy.ThinkingFor(modelId)?.Kind ?? ThinkingParamKind.GeminiBudget;
        if (leaf.StartsWith("claude-"))
            return known?.NativeEfforts.Length > 0 ||
                System.Text.RegularExpressions.Regex.IsMatch(leaf, @"claude-(?:sonnet|opus|haiku|fable|mythos)-(?:[5-9]|4-[6-9])")
                ? ThinkingParamKind.AnthropicAdaptive : ThinkingParamKind.AnthropicBudget;
        if (leaf.StartsWith("kimi-k2") || leaf.StartsWith("moonshot-"))
            return ThinkingParamKind.KimiToggle;
        return ThinkingParamKind.OpenAiReasoningEffort;
    }

    private static ThinkingConfig? QwenEffortFor(string leaf)
    {
        if (leaf == "qwen4-max" || leaf.StartsWith("qwen4-max-", StringComparison.Ordinal))
            return new(ThinkingParamKind.QwenReasoningEffort, ["low", "high", "xhigh"], DefaultEffort: "high");
        if (new[] { "qwen3.8-flash", "qwen3.8-max", "qwen3.8-omni-flash", "qwen3.8-2.4t-a95b", "qwen3.8-27b" }
            .Any(id => leaf == id || leaf.StartsWith(id + "-", StringComparison.Ordinal)))
            return new(ThinkingParamKind.QwenReasoningEffort, ["low", "medium", "xhigh"], DefaultEffort: "xhigh");
        return null;
    }

    // Only official endpoints may constrain saved native model configurations.
    public static ThinkingConfig? OfficialPresetFor(string modelId, string? baseUrl, string? apiPath)
    {
        if (!Uri.TryCreate(baseUrl?.TrimEnd('/') + "/", UriKind.Absolute, out var root)
            || !Uri.TryCreate(root, apiPath?.Trim() ?? "", out var uri)
            || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0)
            return null;
        if (modelId.Contains("-non-reasoning", StringComparison.OrdinalIgnoreCase)) return null;
        var leaf = (ModelCapabilityCatalog.Find(modelId)?.CanonicalId ?? modelId).ToLowerInvariant().Split('/')[^1];
        if (uri.Host == "api.deepseek.com" && (modelId.Equals("deepseek-flash", StringComparison.OrdinalIgnoreCase)
                || leaf.StartsWith("deepseek-v4.1-flash", StringComparison.Ordinal)))
            return new(ThinkingParamKind.DeepSeekV4, ["low", "high", "max"], DefaultEffort: "high");
        if (uri.Host == "api.moonshot.cn" || uri.Host == "api.moonshot.ai" || uri.Host == "api.kimi.ai")
            return leaf.StartsWith("kimi-k3", StringComparison.Ordinal)
                ? new(ThinkingParamKind.OpenAiReasoningEffort, ["low", "high", "max"], DefaultEffort: "max", Mandatory: true)
                : null;
        if (uri.Host == "dashscope.aliyuncs.com" || uri.Host == "dashscope-intl.aliyuncs.com"
            || uri.Host.EndsWith(".dashscope.aliyuncs.com", StringComparison.Ordinal)
            || uri.Host.EndsWith(".dashscope-intl.aliyuncs.com", StringComparison.Ordinal))
            return QwenEffortFor(leaf);
        return null;
    }

    /// <summary>Defaults for a newly detected model, never for a manually saved configuration.</summary>
    public static ThinkingConfig? DefaultConfigFor(string? modelId, string? baseUrl = null)
    {
        var kind = InferFromModelId(modelId);
        if (kind == ThinkingParamKind.None) return null;
        var known = ModelCapabilityCatalog.Find(modelId);
        var leaf = (known?.CanonicalId ?? modelId ?? "").ToLowerInvariant().Split('/')[^1];
        var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : "";
        if (QwenEffortFor(leaf) is { } qwen) return qwen;
        if (kind is ThinkingParamKind.GeminiThinkingLevel or ThinkingParamKind.GeminiBudget)
            return GeminiModelPolicy.DefaultThinkingFor(modelId) ?? new(kind);
        var nativeLevels = kind is ThinkingParamKind.OpenAiReasoningEffort or ThinkingParamKind.AnthropicAdaptive
            or ThinkingParamKind.DeepSeekV4 ? known?.NativeEfforts : null;
        var levels = nativeLevels is { Length: > 0 } ? nativeLevels : null;
        var kimiK3 = leaf.StartsWith("kimi-k3");
        var mandatory = kimiK3 ||
            (host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
                && known?.CanonicalId.StartsWith("openai/") == true
                && levels is { Length: > 0 } && !levels.Contains("none", StringComparer.Ordinal)) ||
            leaf.StartsWith("claude-fable-") || leaf.StartsWith("claude-mythos-") ||
            leaf.StartsWith("claude-opus-5-5") || leaf.StartsWith("claude-sonnet-5-5");
        var defaultEffort = kimiK3 ? "max" : levels?.Contains("high") == true ? "high" : ThinkingEffortLevels.MinimumEnabled(levels);
        if (kimiK3 && levels is null) levels = ["low", "high", "max"];
        return new(kind, levels, DefaultEffort: defaultEffort, Mandatory: mandatory);
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
    ModelPricing? Pricing = null,
    bool SupportsBuiltinSearch = false)
{
    public ThinkingConfig? ResolveThinkingConfig(bool googleRoute = false)
    {
        if (ThinkingConfig is not null) return ThinkingConfig;
        if (googleRoute && GeminiModelPolicy.DefaultThinkingFor(Id) is { } google) return google;
        if (ThinkingParamKindInference.DefaultConfigFor(Id) is { } preset) return preset;
        var kind = ThinkingParamKindInference.InferFromModelId(
            Id, SupportsReasoningEffort ? ThinkingParamKind.OpenAiReasoningEffort : ThinkingParamKind.None);
        return !SupportsThinking && kind == ThinkingParamKind.None ? null : new ThinkingConfig(kind);
    }
}
