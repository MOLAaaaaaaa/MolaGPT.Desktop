using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MolaGPT.Core.Chat;

public sealed record ChatRequestError(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable = false,
    [property: JsonPropertyName("reauth")] bool Reauth = false,
    [property: JsonPropertyName("retry_after")] int? RetryAfter = null,
    [property: JsonPropertyName("next_action")] string? NextAction = null,
    [property: JsonPropertyName("retry_at")] long? RetryAt = null,
    [property: JsonPropertyName("work_resume_entry_id")] string? WorkResumeEntryId = null)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Details { get; init; }

    [JsonIgnore]
    public bool NeedsSignIn => Reauth || NextAction == "sign_in";

    [JsonIgnore]
    public bool IsPausedWork => Type is "work_account_paused" or "work_paused";
}

public sealed class ChatRequestException(ChatRequestError error) : InvalidOperationException(error.Message)
{
    public ChatRequestError Error { get; } = error;
}

public static class ChatApiErrorHelper
{
    public static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string context,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await ReadBodyAsync(response.Content, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (ReadHttpError(body, status) is { } error)
            throw new ChatRequestException(error);
        // 服务端 nginx 的长连接并发上限触发时回的是一页 HTML，剥完标签只剩「429 Too Many Requests nginx」
        if (status == 429 && body.TrimStart().StartsWith('<'))
            throw new InvalidOperationException($"{context}失败：当前使用人数较多，请稍后再试。");
        var message = ExtractErrorMessage(body);
        var reason = response.ReasonPhrase;
        var label = string.IsNullOrWhiteSpace(reason) ? status.ToString() : $"{status} {reason}";

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? $"{context}失败：HTTP {label}"
            : $"{context}失败：HTTP {label}，{message}");
    }

    public static async Task<ChatRequestError?> ReadAccountHttpErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        return ReadHttpError(await ReadBodyAsync(response.Content, ct).ConfigureAwait(false), status, accountRequest: true)
            ?? (status == 401 ? AccountAuthExpired() : null);
    }

    private static ChatRequestError? ReadHttpError(string body, int status, bool accountRequest = false)
    {
        if (!body.TrimStart().StartsWith('{')) return null;
        try
        {
            using var json = JsonDocument.Parse(body);
            return ReadBusinessError(json.RootElement, httpStatus: status, accountRequest: accountRequest);
        }
        catch (JsonException) { return null; }
    }

    private static ChatRequestError AccountAuthExpired() =>
        new("auth_expired", "登录状态已失效，请重新登录。", Reauth: true, NextAction: "sign_in");

    public const string LegacySharedQuotaNotice = "该供应商共享额度已用完，请稍后再试。";

    public static ChatRequestError? ReadLegacySharedQuota(string? text) =>
        string.Equals(text?.Trim(), LegacySharedQuotaNotice, StringComparison.Ordinal)
            ? new ChatRequestError("shared_quota_exhausted", LegacySharedQuotaNotice, NextAction: "switch_model")
            : null;

    public static string WithoutSharedQuotaTail(string text, ChatRequestError? error)
    {
        if (error?.Type != "shared_quota_exhausted") return text;
        var trimmed = text.TrimEnd();
        return trimmed.EndsWith(LegacySharedQuotaNotice, StringComparison.Ordinal)
            ? trimmed[..^LegacySharedQuotaNotice.Length].TrimEnd() : text;
    }

    public static ChatRequestError ReadProviderError(string? message, int httpStatus = 0, string? code = null)
    {
        var probe = $"{code} {message}";
        if (probe.Contains("共享额度已用完", StringComparison.Ordinal))
            return ReadLegacySharedQuota(LegacySharedQuotaNotice)!;
        if (new[] { "insufficient_quota", "quota_exceeded", "quota exceeded", "billing_hard_limit",
                "insufficient balance", "额度不足", "配额已用完", "余额不足" }
            .Any(s => probe.Contains(s, StringComparison.OrdinalIgnoreCase)))
            return new ChatRequestError("upstream_quota_exhausted",
                "当前模型的服务额度不足，请切换模型或稍后重试。", NextAction: "switch_model");
        if (httpStatus == 429 || new[] { "rate_limit_exceeded", "rate limit exceeded", "too many requests", "resource_exhausted" }
            .Any(s => probe.Contains(s, StringComparison.OrdinalIgnoreCase)))
            return new ChatRequestError("rate_limited", "当前模型请求较多，请稍后重试。", Retryable: true);
        return new ChatRequestError("upstream_unavailable", "模型暂时无法响应，请稍后重试。", Retryable: true);
    }

    public static ChatRequestError? ReadBusinessError(JsonElement root, string? eventName = null, int httpStatus = 0,
        bool accountRequest = false)
    {
        var namedError = eventName is "error" or "mola_error" or "response.failed";
        if (root.ValueKind != JsonValueKind.Object)
            return namedError ? ReadProviderError(root.ValueKind == JsonValueKind.String ? root.GetString() : null, httpStatus) : null;

        if (root.TryGetProperty("mola_error", out var explicitError) && ReadTypedError(explicitError) is { } typed)
            return typed;

        var hasError = root.TryGetProperty("error", out var node)
            && node.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False);
        if (hasError && node.ValueKind == JsonValueKind.Object && node.TryGetProperty("retryable", out _)
            && ReadTypedError(node) is { } business)
            return business;

        if (accountRequest && httpStatus == 401) return AccountAuthExpired();
        var conflict = node.ValueKind == JsonValueKind.String ? node.GetString() : ReadString(node, "code");
        if (httpStatus == 409 && conflict is "request_already_accepted" or "idempotency_conflict")
            return new ChatRequestError(conflict, ReadString(root, "message") ?? ReadString(node, "message")
                ?? "请求已经接收，请读取原有结果。");

        var failed = namedError || ReadString(root, "type") is "error" or "response.failed"
            || (ReadString(root, "object") == "response" && ReadString(root, "status") == "failed");
        if (!failed && root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
            failed = ReadString(choices[0], "finish_reason") == "error";

        if (!hasError && !failed) return null;
        if (!hasError)
        {
            node = root;
            if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
                && response.TryGetProperty("error", out var nested) && nested.ValueKind is not JsonValueKind.Null)
                node = nested;
        }
        var message = node.ValueKind == JsonValueKind.String ? node.GetString() : ReadString(node, "message");
        var code = ReadString(node, "code") ?? ReadString(node, "type") ?? ReadString(node, "status");
        if (httpStatus == 0 && node.ValueKind == JsonValueKind.Object && node.TryGetProperty("code", out var numeric)
            && numeric.ValueKind == JsonValueKind.Number && numeric.TryGetInt32(out var status))
            httpStatus = status;
        return ReadProviderError(message, httpStatus, code);
    }

    private static ChatRequestError? ReadTypedError(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(ReadString(value, "message"))
            || string.IsNullOrWhiteSpace(ReadString(value, "type")))
            return null;
        ChatRequestError? error;
        try { error = value.Deserialize<ChatRequestError>(); }
        catch (JsonException) { return null; }
        return error is { RetryAt: null, RetryAfter: > 0 }
            ? error with { RetryAt = DateTimeOffset.UtcNow.AddSeconds(error.RetryAfter.Value).ToUnixTimeMilliseconds() }
            : error;
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
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
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
