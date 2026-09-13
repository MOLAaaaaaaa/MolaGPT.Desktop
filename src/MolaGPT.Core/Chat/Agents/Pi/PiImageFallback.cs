using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MolaGPT.Core.Chat.Agents.Pi;

internal static class PiImageFallback
{
    private const string Marker = "[Unsupported Image]";

    internal enum ResponseKind { Other, EmptyStart, UnsupportedImage }

    internal static ResponseKind ClassifyResponse(string payload, bool httpError, string? eventName = null)
    {
        if (eventName == "ping") return ResponseKind.EmptyStart;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (httpError || eventName == "error") && IsUnsupportedError(root)
                    ? ResponseKind.UnsupportedImage : ResponseKind.Other;
            if (HasArrayItems(root, "choices") || HasArrayItems(root, "candidates")
                || root.TryGetProperty("response", out var completed)
                && completed.ValueKind == JsonValueKind.Object && HasArrayItems(completed, "output"))
                return ResponseKind.Other;
            var type = ReadString(root, "type");
            JsonElement errorValue = default;
            if (root.TryGetProperty("error", out var error))
                errorValue = error;
            else if (type == "response.failed"
                     && root.TryGetProperty("response", out var response)
                     && response.ValueKind == JsonValueKind.Object
                     && response.TryGetProperty("error", out error))
                errorValue = error;
            else if (httpError || type == "error" || eventName == "error")
                errorValue = root;

            if (errorValue.ValueKind != JsonValueKind.Undefined)
                return IsUnsupportedError(errorValue) ? ResponseKind.UnsupportedImage : ResponseKind.Other;

            if (type == "ping") return ResponseKind.EmptyStart;
            if (type is "response.created" or "response.in_progress"
                && HasEmptyArray(root, "response", "output"))
                return ResponseKind.EmptyStart;
            if (type == "message_start" && HasEmptyArray(root, "message", "content"))
                return ResponseKind.EmptyStart;
        }
        catch (JsonException)
        {
            if ((httpError || eventName == "error") && IsUnsupportedImage(payload))
                return ResponseKind.UnsupportedImage;
        }
        return ResponseKind.Other;
    }

    private static bool HasEmptyArray(JsonElement root, string property, string arrayProperty) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(arrayProperty, out var array) && array.ValueKind == JsonValueKind.Array
        && array.GetArrayLength() == 0;

    private static bool HasArrayItems(JsonElement value, string property) =>
        value.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
        && array.GetArrayLength() > 0;

    private static string? ReadMessage(JsonElement error) => error.ValueKind == JsonValueKind.String
        ? error.GetString()
        : error.ValueKind == JsonValueKind.Object ? ReadString(error, "message") : null;

    private static string? ReadString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() : null;

    private static bool IsUnsupportedError(JsonElement error)
    {
        if (ReadMessage(error) is { } message && IsUnsupportedImage(message)) return true;
        if (error.ValueKind != JsonValueKind.Object
            || !error.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object
            || ReadString(metadata, "raw") is not { } raw)
            return false;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var detail = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var nested)
                ? nested : root;
            return ReadMessage(detail) is { } rawMessage && IsUnsupportedImage(rawMessage);
        }
        catch (JsonException) { return IsUnsupportedImage(raw); }
    }

    private static bool IsUnsupportedImage(string message) => Regex.IsMatch(message,
        """
        (?>
            \b(?:(?:does|do)\s+not|doesn't|cannot|can't)\s+(?:support|accept|process)\s+(?:images?(?:\s+inputs?)?|vision)\b
            |\b(?:images?(?:\s+input(?:\s+modality)?)?|image_url|input_image|vision)\s+(?:is\s+|are\s+)?(?:not\s+supported|unsupported)\b
            |\bunsupported\s+image\s+inputs?\b
            |\bimage_url\s+is\s+only\s+supported\s+by\s+certain\s+models\b
            |模型不支持(?:图片|图像|视觉)(?:输入)?
            |不支持(?:图片|图像)输入
        )
        (?!\s*(?:[:,，：]\s*)?(?:
            /|(?:formats?|types?|sizes?|dimensions?|resolutions?|mime(?:\s+types?)?|generation|outputs?)\b
            |(?:for|of|with|in|at|due\s+to|because\s+of)\s+
                (?:(?!(?:any|all|models?|endpoint|api)\b)[^\s.;!?:,。；！？：，]{1,40}\s+){0,4}
                (?:formats?|types?|sizes?|dimensions?|resolutions?|mime(?:\s+types?)?)\b
            |格式|类型|大小|尺寸|分辨率|输出|生成
        ))
        """, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant);

    /// <summary>Only the outgoing copy is edited; Pi's session retains its images.</summary>
    internal static JsonObject? ReplaceImages(string body)
    {
        if (JsonNode.Parse(body) is not JsonObject root) return null;
        var changed = false;
        if (root["messages"] is JsonArray messages)
            foreach (var message in messages.OfType<JsonObject>())
                changed |= ReplaceContent(message["content"] as JsonArray);

        if (root["input"] is JsonArray input)
            foreach (var item in input.OfType<JsonObject>())
            {
                changed |= ReplaceContent(item["content"] as JsonArray);
                if (item["type"]?.GetValue<string>() is "function_call_output" or "custom_tool_call_output")
                    changed |= ReplaceContent(item["output"] as JsonArray);
            }

        if (root["contents"] is JsonArray contents)
            foreach (var content in contents.OfType<JsonObject>())
                changed |= ReplaceGoogleParts(content["parts"] as JsonArray);
        return changed ? root : null;
    }

    private static bool ReplaceContent(JsonArray? content)
    {
        if (content is null) return false;
        var changed = false;
        for (var i = 0; i < content.Count; i++)
        {
            if (content[i] is not JsonObject part) continue;
            var type = part["type"]?.GetValue<string>();
            if (type is "image_url" or "image" or "input_image")
            {
                var replacement = new JsonObject
                {
                    ["type"] = type == "input_image" ? "input_text" : "text",
                    ["text"] = Marker,
                };
                if (part["cache_control"] is { } cacheControl)
                    replacement["cache_control"] = cacheControl.DeepClone();
                content[i] = replacement;
                changed = true;
            }
            else if (type == "tool_result")
                changed |= ReplaceContent(part["content"] as JsonArray);
        }
        return changed;
    }

    private static bool ReplaceGoogleParts(JsonArray? parts)
    {
        if (parts is null) return false;
        var changed = false;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i] is not JsonObject part) continue;
            if (IsGoogleImage(part))
            {
                parts[i] = new JsonObject { ["text"] = Marker };
                changed = true;
            }
            else if (part["functionResponse"] is JsonObject response && response["parts"] is JsonArray results)
            {
                var removed = 0;
                for (var j = results.Count - 1; j >= 0; j--)
                    if (results[j] is JsonObject result && IsGoogleImage(result))
                    {
                        results.RemoveAt(j);
                        removed++;
                    }
                if (removed == 0) continue;
                if (results.Count == 0) response.Remove("parts");
                // FunctionResponsePart only accepts media; text belongs to the outer Parts.
                for (var j = 0; j < removed; j++)
                    parts.Insert(++i, new JsonObject { ["text"] = Marker });
                changed = true;
            }
        }
        return changed;
    }

    private static bool IsGoogleImage(JsonObject part) =>
        (part["inlineData"] ?? part["fileData"]) is JsonObject data
        && data["mimeType"]?.GetValue<string>() is { } mime
        && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
