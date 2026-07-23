using System.Text.Json;
using MolaGPT.Core.Models;

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

    /// <summary>
    /// Maps the terminal Responses control events into a finish reason, token usage,
    /// or an error message. Returns true (and leaves text/thinking untouched) only for
    /// <c>response.completed</c> / <c>response.incomplete</c> / <c>response.failed</c> /
    /// <c>error</c>; content-delta events return false so the caller falls through to
    /// <see cref="TryMap"/>. Callers should invoke this BEFORE <see cref="TryMap"/>
    /// (which returns true for every <c>response.*</c> event).
    /// </summary>
    public bool TryMapControl(JsonElement root, out string? finish, out Usage? usage, out string? error)
    {
        finish = null;
        usage = null;
        error = null;

        var eventType = ReadString(root, "type");
        if (string.IsNullOrWhiteSpace(eventType)) return false;

        switch (eventType)
        {
            case "response.completed":
                finish = "stop";
                usage = ExtractUsage(root);
                return true;
            case "response.incomplete":
                finish = "incomplete";
                usage = ExtractUsage(root);
                return true;
            case "response.failed":
            case "error":
                error = ExtractResponseError(root) ?? "OpenAI Responses 请求失败";
                return true;
            default:
                return false;
        }
    }

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

        // Surface refusals as visible text so the user sees why generation stopped.
        if (eventType == "response.refusal.delta")
        {
            text = ReadString(root, "delta");
            return true;
        }

        return true;
    }

    private static Usage? ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            return null;
        if (!response.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
            return null;
        return new Usage(
            u.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number ? it.GetInt32() : null,
            u.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number ? ot.GetInt32() : null,
            u.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt32() : null);
    }

    private static string? ExtractResponseError(JsonElement root)
    {
        // response.failed carries response.error.{message}; a bare "error" event
        // carries message/error at the top level.
        if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("error", out var nestedError))
        {
            if (nestedError.ValueKind == JsonValueKind.String) return nestedError.GetString();
            if (nestedError.ValueKind == JsonValueKind.Object) return ReadString(nestedError, "message");
        }
        if (ReadString(root, "message") is { Length: > 0 } topMessage) return topMessage;
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String) return error.GetString();
            if (error.ValueKind == JsonValueKind.Object) return ReadString(error, "message");
        }
        return null;
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
