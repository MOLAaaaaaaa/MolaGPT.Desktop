using System.Text.Json;

namespace MolaGPT.Core.Chat.Providers;

/// <summary>
/// Durable, provider-scoped protocol history for one visible assistant turn.
/// A single UI message can represent several assistant/tool wire items, so the
/// ordinary role/content history is not sufficient for reasoning tool loops.
/// </summary>
internal static class OpenAiWireHistory
{
    private const int CurrentVersion = 1;

    public static string Serialize(
        OpenAiWireApi wireApi,
        string providerId,
        string modelId,
        IEnumerable<object> items) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = CurrentVersion,
            ["wire_api"] = WireApiName(wireApi),
            ["provider_id"] = providerId,
            ["model_id"] = modelId,
            ["items"] = items.ToArray()
        });

    public static bool TryRead(
        string? json,
        OpenAiWireApi expectedWireApi,
        string expectedProviderId,
        string expectedModelId,
        out IReadOnlyList<JsonElement> items)
    {
        items = Array.Empty<JsonElement>();
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || ReadInt(root, "version") != CurrentVersion
                || !string.Equals(ReadString(root, "wire_api"), WireApiName(expectedWireApi), StringComparison.Ordinal)
                || !string.Equals(ReadString(root, "provider_id"), expectedProviderId, StringComparison.Ordinal)
                || !string.Equals(ReadString(root, "model_id"), expectedModelId, StringComparison.Ordinal)
                || !root.TryGetProperty("items", out var itemArray)
                || itemArray.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsed = itemArray.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => item.Clone())
                .ToArray();
            if (parsed.Length == 0) return false;
            items = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string WireApiName(OpenAiWireApi wireApi) => wireApi switch
    {
        OpenAiWireApi.Responses => "responses",
        _ => "chat_completions"
    };

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Number
            ? node.GetInt32()
            : null;
}
