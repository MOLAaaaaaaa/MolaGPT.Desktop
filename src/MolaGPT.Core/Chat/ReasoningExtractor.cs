using System.Text;
using System.Text.Json;

namespace MolaGPT.Core.Chat;

/// <summary>
/// Pulls all three "thinking / reasoning" channels out of an OpenAI-style
/// streaming <c>delta</c> object and concatenates them, in order, into a
/// single string suitable for <see cref="ChatChunk.DeltaThinking"/>.
///
/// Supported reasoning channels:
///   - <c>delta.reasoning</c>            (string, OpenRouter / O1-mini / Grok)
///   - <c>delta.reasoning_content</c>    (string, DeepSeek / GLM)
///   - <c>delta.reasoning_details[]</c>  (array of {text|content|summary},
///                                        Claude thinking / OpenRouter
///                                        reasoning_details extension)
/// The extractor de-duplicates within a single event so models that report the same
/// chunk through multiple channels don't surface it three times.
/// </summary>
public static class ReasoningExtractor
{
    public static string? Extract(JsonElement delta)
    {
        if (delta.ValueKind != JsonValueKind.Object) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();

        if (delta.TryGetProperty("reasoning", out var reasoning)
            && reasoning.ValueKind == JsonValueKind.String)
        {
            AppendUnique(sb, seen, reasoning.GetString());
        }

        if (delta.TryGetProperty("reasoning_content", out var reasoningContent)
            && reasoningContent.ValueKind == JsonValueKind.String)
        {
            AppendUnique(sb, seen, reasoningContent.GetString());
        }

        if (delta.TryGetProperty("reasoning_details", out var details)
            && details.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in details.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                AppendUnique(sb, seen, ReadStringField(item, "text"));
                AppendUnique(sb, seen, ReadStringField(item, "content"));
                AppendUnique(sb, seen, ReadStringField(item, "summary"));
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static void AppendUnique(StringBuilder sb, HashSet<string> seen, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (!seen.Add(value)) return;
        sb.Append(value);
    }

    private static string? ReadStringField(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
