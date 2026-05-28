using System.Text.Json;

namespace MolaGPT.Core.Chat;

/// <summary>
/// Maps OpenAI Responses API SSE events into the same text/thinking channels
/// as chat-completions deltas.
/// </summary>
public sealed class ResponseStreamEventMapper
{
    private readonly HashSet<string> _outputTextSeen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reasoningSeen = new(StringComparer.Ordinal);
    private string? _lastReasoningSummaryBlockKey;

    public bool TryMap(JsonElement root, out string? text, out string? thinking)
    {
        text = null;
        thinking = null;

        var eventType = ReadString(root, "type");
        if (string.IsNullOrWhiteSpace(eventType) || !eventType!.StartsWith("response.", StringComparison.Ordinal))
            return false;

        if (eventType == "response.output_text.delta")
        {
            var key = ReadString(root, "item_id") ?? $"output_{ReadInt(root, "output_index") ?? 0}";
            text = ReadString(root, "delta");
            if (!string.IsNullOrEmpty(text)) _outputTextSeen.Add(key);
            return true;
        }

        if (eventType == "response.output_text.done")
        {
            var key = ReadString(root, "item_id") ?? $"output_{ReadInt(root, "output_index") ?? 0}";
            if (!_outputTextSeen.Contains(key))
            {
                text = ReadString(root, "text");
                if (!string.IsNullOrEmpty(text)) _outputTextSeen.Add(key);
            }
            return true;
        }

        if (eventType == "response.reasoning_summary_text.delta")
        {
            var key = BuildReasoningKey(root);
            thinking = WithReasoningSeparation(key, ReadString(root, "delta"));
            if (!string.IsNullOrEmpty(thinking)) _reasoningSeen.Add(key);
            return true;
        }

        if (eventType == "response.reasoning_summary_text.done")
        {
            var key = BuildReasoningKey(root);
            if (!_reasoningSeen.Contains(key))
            {
                thinking = WithReasoningSeparation(key, ReadString(root, "text"));
                if (!string.IsNullOrEmpty(thinking)) _reasoningSeen.Add(key);
            }
            return true;
        }

        return true;
    }

    private string? WithReasoningSeparation(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var needsBreak = _lastReasoningSummaryBlockKey is not null && _lastReasoningSummaryBlockKey != key;
        _lastReasoningSummaryBlockKey = key;
        return needsBreak ? "\n\n" + value : value;
    }

    private static string BuildReasoningKey(JsonElement root) =>
        $"{ReadString(root, "item_id") ?? "reasoning"}:{ReadInt(root, "summary_index") ?? 0}";

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetInt32(out var n) ? n : null;
    }
}
