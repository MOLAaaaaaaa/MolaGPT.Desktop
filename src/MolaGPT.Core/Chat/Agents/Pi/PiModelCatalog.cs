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
    /// <summary>Pi requires a context window and an output cap; MolaGPT's model rows
    /// carry neither, and the real limits are enforced upstream anyway. These are
    /// deliberately generous placeholders rather than guesses at the truth.</summary>
    private const int DefaultContextWindow = 128000;
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
                ["reasoning"] = model.SupportsThinking,
                // Claimed for every model: Pi refuses to send an image to a model
                // that does not declare it, and MolaGPT has already decided whether
                // to send one by the time the turn reaches the sidecar.
                ["input"] = new[] { "text", "image" },
                // Billing is MolaGPT's, upstream of Pi. Zeroes keep Pi's own cost
                // display from inventing numbers we would then have to explain.
                ["cost"] = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 },
                ["contextWindow"] = DefaultContextWindow,
                ["maxTokens"] = DefaultMaxTokens,
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
