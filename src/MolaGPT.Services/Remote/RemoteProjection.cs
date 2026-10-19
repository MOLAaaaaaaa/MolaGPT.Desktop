using System.Text.Json;
using System.Text.Json.Nodes;
using MolaGPT.Core.Chat.Tasks;
using MolaGPT.Storage;

namespace MolaGPT.Desktop.Services.Remote;

/// <summary>
/// Stored messages as a phone receives them: the desktop's own row and meta, with
/// what a phone cannot use dropped and what could be very large cut to a preview.
/// The phone asks for the rest of a cut message when the user wants it.
/// </summary>
internal static class RemoteProjection
{
    private const int PreviewThreshold = 64 * 1024;
    private const int PreviewHead = 48 * 1024;
    private const int PreviewTail = 8 * 1024;
    private const int CompactPreview = 2 * 1024;
    private const int FullLimit = 800 * 1024;
    private const int ThinkingLimit = 16 * 1024;
    private const int ArgumentsLimit = 2 * 1024;
    private const int ResultLimit = 4 * 1024;
    private const string HiddenHint = "✝";

    /// <summary>Total size a snapshot aims for; older messages beyond it go as previews.</summary>
    public const int SnapshotBudget = 400 * 1024;

    public enum Detail
    {
        /// <summary>Long text cut to a preview, as in a snapshot or a live update.</summary>
        Normal,
        /// <summary>Everything cut short, for old messages past the snapshot's budget.</summary>
        Compact,
        /// <summary>The whole message, for the phone's 「查看完整内容」.</summary>
        Full
    }

    public static JsonObject Message(MessageRow row, Detail detail = Detail.Normal)
    {
        var content = StripHiddenHints(row.Content ?? string.Empty);
        var result = new JsonObject
        {
            ["id"] = row.Id,
            ["parentId"] = row.ParentId,
            ["role"] = row.Role,
            ["createdAtMs"] = row.CreatedAt
        };
        if (row.Role == "user" && content.TrimStart().StartsWith(TaskTools.NotificationOpenTag, StringComparison.Ordinal))
            result["kind"] = "task_notification";

        var limit = detail switch { Detail.Full => FullLimit, Detail.Compact => CompactPreview, _ => PreviewThreshold };
        if (content.Length > limit)
        {
            result["truncated"] = content.Length;
            content = detail switch
            {
                Detail.Full => content[..FullLimit],
                Detail.Compact => content[..CompactPreview] + "\n\n…",
                _ => content[..PreviewHead] + "\n\n…\n\n" + content[^PreviewTail..]
            };
        }
        result["content"] = content;
        result["meta"] = Meta(row.Meta, detail);
        return result;
    }

    public static int Size(JsonNode node) => System.Text.Encoding.UTF8.GetByteCount(node.ToJsonString());

    private static string StripHiddenHints(string text)
    {
        var start = text.IndexOf(HiddenHint, StringComparison.Ordinal);
        while (start >= 0)
        {
            var end = text.IndexOf(HiddenHint, start + HiddenHint.Length, StringComparison.Ordinal);
            if (end < 0) break;
            text = text.Remove(start, end + HiddenHint.Length - start);
            start = text.IndexOf(HiddenHint, start, StringComparison.Ordinal);
        }
        return text.TrimEnd();
    }

    private static JsonObject? Meta(string? json, Detail detail)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonObject meta;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed) return null;
            meta = parsed;
        }
        catch (JsonException) { return null; }

        // Images inline as base64, and request plumbing: nothing a phone renders.
        meta.Remove("content_parts");
        if (meta["retry"] is JsonObject retry)
        {
            meta.Remove("retry");
            if (retry["attempts"] is JsonArray attempts) meta["retry_count"] = attempts.Count;
        }
        if (detail != Detail.Full && meta["thinking"]?.GetValueKind() == JsonValueKind.String)
            meta["thinking"] = Cut(meta["thinking"]!.GetValue<string>(), detail == Detail.Compact ? CompactPreview : ThinkingLimit);

        if (meta["tool_calls"] is JsonArray tools)
        {
            foreach (var tool in tools.OfType<JsonObject>())
            {
                if (detail == Detail.Full) continue;
                CutField(tool, "arguments_json", detail == Detail.Compact ? 0 : ArgumentsLimit);
                CutField(tool, "result_preview_json", detail == Detail.Compact ? 0 : ResultLimit);
                CutField(tool, "detail", detail == Detail.Compact ? 0 : ResultLimit);
            }
        }
        if (meta["thinking_segments"] is JsonArray segments && detail != Detail.Full)
            foreach (var segment in segments.OfType<JsonObject>())
                CutField(segment, "source", detail == Detail.Compact ? CompactPreview : ThinkingLimit);

        // Names are enough to draw a chip; local paths and inline thumbnails are not sent.
        if (meta["attachments"] is JsonArray attachments)
        {
            var kept = new JsonArray();
            foreach (var item in attachments.OfType<JsonObject>())
                kept.Add(new JsonObject
                {
                    ["filename"] = item["filename"]?.DeepClone(),
                    ["label"] = item["label"]?.DeepClone(),
                    ["kind"] = item["kind"]?.DeepClone(),
                    ["mime"] = item["mime"]?.DeepClone()
                });
            meta["attachments"] = kept;
        }
        return meta;
    }

    private static void CutField(JsonObject owner, string name, int limit)
    {
        if (owner[name]?.GetValueKind() != JsonValueKind.String) return;
        var value = owner[name]!.GetValue<string>();
        if (value.Length <= limit) return;
        if (limit == 0) owner.Remove(name);
        else owner[name] = Cut(value, limit);
    }

    private static string Cut(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";
}
