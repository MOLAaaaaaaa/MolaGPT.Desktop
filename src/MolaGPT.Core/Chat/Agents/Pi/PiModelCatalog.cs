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
    /// Pi requires an output cap and MolaGPT's model rows usually lack one; the real
    /// limit is enforced upstream anyway, so this is a floor rather than a claim.
    /// </summary>
    private const int DefaultMaxTokens = 8192;

    public static string BuildJson(
        IReadOnlyList<ProviderModel> models,
        string api,
        string displayName,
        string? endpoint)
    {
        var compat = ParseCompat(PiEndpointQuirks.CompatJsonFor(endpoint));

        var entries = models.Select(model =>
        {
            var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = model.Id,
                ["name"] = $"{displayName} · {model.DisplayName}",
                ["api"] = api,
                ["reasoning"] = model.SupportsThinking || model.SupportsReasoningEffort,
                // Claimed for every model: Pi refuses to send an image to a model
                // that does not declare it, and MolaGPT has already decided whether
                // to send one by the time the turn reaches the sidecar.
                ["input"] = new[] { "text", "image" },
                // Billing is MolaGPT's, upstream of Pi. Zeroes keep Pi's own cost
                // display from inventing numbers we would then have to explain.
                ["cost"] = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 },
                // Pi budgets auto-compaction off this number (it compacts once the
                // context passes contextWindow − 16,384), so a flat placeholder here
                // was compacting 1M-token models at about an eighth of their window.
                // The user's own setting wins; the table only covers the silence.
                ["contextWindow"] = ModelContextWindows.ResolveOrDefault(model.Id, model.ContextWindow),
                ["maxTokens"] = model.MaxOutputTokens is > 0 ? model.MaxOutputTokens.Value : DefaultMaxTokens,
            };
            if (compat is not null) entry["compat"] = compat;
            return entry;
        }).ToArray();

        return JsonSerializer.Serialize(entries, JsonOptions);
    }

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
