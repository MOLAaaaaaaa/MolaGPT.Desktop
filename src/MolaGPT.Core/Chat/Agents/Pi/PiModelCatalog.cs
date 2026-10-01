using System.Text.Json;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat.Agents.Pi;

/// <summary>
/// Renders a provider's models into the shape Pi's <c>registerProvider</c> takes.
///
/// The whole list goes in at spawn rather than one model at a time, so switching
/// model mid-conversation is a <c>set_model</c> against an already-registered
/// entry instead of a reason to start another Node process — which, at ~2.7s and
/// ~95 MB each, is the difference the sidecar pool exists to protect.
/// </summary>
public static class PiModelCatalog
{
    /// <summary>
    /// Pi requires an output cap and MolaGPT's model rows usually lack one. Pi sends
    /// whatever is here as the request's cap, so on its own this would be a real
    /// limit rather than a floor; <see cref="PiWorkLlmShim.RemoveOutputCap"/> strips
    /// it before the request leaves. Only Anthropic, whose API requires the field,
    /// still receives it.
    /// </summary>
    private const int DefaultMaxTokens = 8192;

    /// <param name="multiplexedRelay">The endpoint is a relay standing in front of
    /// several vendors rather than one vendor's API, so the host says nothing
    /// about the dialect. See <see cref="PiEndpointQuirks.MultiplexedRelayCompatJson"/>.</param>
    public static string BuildJson(
        IReadOnlyList<ProviderModel> models,
        string api,
        string displayName,
        string? endpoint,
        bool multiplexedRelay = false)
    {
        var compat = ParseCompat(multiplexedRelay
            ? PiEndpointQuirks.MultiplexedRelayCompatJson
            : PiEndpointQuirks.CompatJsonFor(endpoint));

        var entries = models.Select(model =>
        {
            var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = model.Id,
                ["name"] = $"{displayName} · {model.DisplayName}",
                ["api"] = api,
                ["reasoning"] = model.SupportsThinking || model.SupportsReasoningEffort,
                ["input"] = model.SupportsVision ? new[] { "text", "image" } : new[] { "text" },
                // Pi's calculateCost divides these by 1,000,000, which is the unit
                // ModelPricing already stores. Handing it the real rates is what
                // makes the turn's cost come back computed — including the cache
                // read/write split and Anthropic's 2x long-cache-write rule, none
                // of which is worth reimplementing on this side. A model with no
                // price keeps the zeroes; the caller tells "free" from "unknown"
                // by looking at Pricing, never at a cost of 0.
                ["cost"] = BuildCost(model.Pricing),
                // Pi budgets auto-compaction off this number (it compacts once the
                // context passes contextWindow − 16,384), so a flat placeholder here
                // was compacting 1M-token models at about an eighth of their window.
                // The user's own setting wins; the table only covers the silence.
                ["contextWindow"] = ModelContextWindows.ResolveOrDefault(model.Id, model.ContextWindow),
                ["maxTokens"] = model.MaxOutputTokens is > 0 ? model.MaxOutputTokens.Value : DefaultMaxTokens,
            };
            if (BuildThinkingLevelMap(model, api) is { } levelMap) entry["thinkingLevelMap"] = levelMap;
            if (compat is not null) entry["compat"] = compat;
            return entry;
        }).ToArray();

        return JsonSerializer.Serialize(entries, JsonOptions);
    }

    /// <summary>
    /// Pi treats an explicit null off mapping as "off is unavailable" and clamps it
    /// to minimal. Completions needs a real off level: Pi then omits its effort field,
    /// and <see cref="ThinkingParams"/> supplies the endpoint's off expression.
    /// Responses keeps the existing mapping because its default off expression is
    /// effort: none, which some endpoints reject.
    /// </summary>
    private static Dictionary<string, object?>? BuildThinkingLevelMap(ProviderModel model, string api)
    {
        if (api != "openai-responses") return null;
        var map = new Dictionary<string, object?>(StringComparer.Ordinal) { ["off"] = null };
        foreach (var level in ThinkingEffortLevels.Normalize(model.ThinkingConfig?.EffortLevels))
            if (level is "xhigh" or "max") map[level] = level;
        return map;
    }

    /// <summary>Pi reads all four rates unconditionally, so a missing cache price
    /// falls back to the matching base rate rather than to zero — charging nothing
    /// for cache traffic would understate every cached turn.</summary>
    private static object BuildCost(ModelPricing? pricing) => pricing is null
        ? new { input = 0d, output = 0d, cacheRead = 0d, cacheWrite = 0d }
        : new
        {
            input = pricing.Input,
            output = pricing.Output,
            cacheRead = pricing.CacheRead ?? pricing.Input,
            cacheWrite = pricing.CacheWrite ?? pricing.Input
        };

    private static JsonElement? ParseCompat(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
