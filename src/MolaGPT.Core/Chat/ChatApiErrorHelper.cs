using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MolaGPT.Core.Chat;

public static class ChatApiErrorHelper
{
    public static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string context,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await ReadBodyAsync(response.Content, ct).ConfigureAwait(false);
        var message = ExtractErrorMessage(body);
        var status = (int)response.StatusCode;
        var reason = response.ReasonPhrase;
        var label = string.IsNullOrWhiteSpace(reason) ? status.ToString() : $"{status} {reason}";

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? $"{context}失败：HTTP {label}"
            : $"{context}失败：HTTP {label}，{message}");
    }

    public static bool TryExtractStreamingError(JsonElement root, out string message)
    {
        message = string.Empty;

        if (TryReadErrorNode(root, out message))
            return true;

        if (root.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "error", StringComparison.OrdinalIgnoreCase))
        {
            message = ReadString(root, "message") ?? root.ToString();
            return true;
        }

        return false;
    }

    public static string ExtractErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (TryReadErrorNode(doc.RootElement, out var message))
                return message;
            return ReadString(doc.RootElement, "message")
                   ?? ReadString(doc.RootElement, "detail")
                   ?? TrimForDisplay(body);
        }
        catch (JsonException)
        {
            return TrimForDisplay(StripHtml(body));
        }
    }

    private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static bool TryReadErrorNode(JsonElement root, out string message)
    {
        message = string.Empty;
        if (!root.TryGetProperty("error", out var error))
            return false;

        if (error.ValueKind == JsonValueKind.String)
        {
            message = error.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(message);
        }

        if (error.ValueKind == JsonValueKind.Object)
        {
            message = ReadString(error, "message")
                      ?? ReadString(error, "error")
                      ?? ReadString(error, "type")
                      ?? error.ToString();
            return !string.IsNullOrWhiteSpace(message);
        }

        message = error.ToString();
        return !string.IsNullOrWhiteSpace(message);
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string StripHtml(string text)
    {
        if (!text.Contains('<', StringComparison.Ordinal)) return text;

        var output = new StringBuilder(text.Length);
        var insideTag = false;
        foreach (var ch in text)
        {
            if (ch == '<')
            {
                insideTag = true;
                continue;
            }
            if (ch == '>')
            {
                insideTag = false;
                continue;
            }
            if (!insideTag) output.Append(ch);
        }
        return WebUtility.HtmlDecode(output.ToString());
    }

    private static string TrimForDisplay(string text)
    {
        var normalized = text
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return normalized.Length <= 800 ? normalized : normalized[..800] + "...";
    }
}
