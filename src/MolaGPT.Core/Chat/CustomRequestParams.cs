using System.Net.Http;
using System.Text.Json;

namespace MolaGPT.Core.Chat;

/// <summary>
/// Shared application of user-defined parameter overrides (BYOK): per-provider
/// custom HTTP headers and per-model custom request-body fields. Used by the
/// OpenAI-compatible and Anthropic providers so the merge/guard rules stay in
/// one place.
/// </summary>
internal static class CustomRequestParams
{
    /// <summary>Body keys that a custom override must never replace — they carry the
    /// conversation payload / stream flag and overriding them would corrupt the request.</summary>
    public static bool IsProtectedBodyKey(string key) =>
        key is "messages" or "input" or "contents" or "stream";

    /// <summary>Overlays the model's custom body fields onto <paramref name="body"/>,
    /// skipping protected keys. Custom values are <see cref="JsonElement"/>s and
    /// serialize natively.</summary>
    public static void ApplyBody(IDictionary<string, object?> body, IReadOnlyDictionary<string, JsonElement>? custom)
    {
        if (custom is null) return;
        foreach (var (key, value) in custom)
        {
            if (IsProtectedBodyKey(key)) continue;
            body[key] = value;
        }
    }

    /// <summary>Appends the provider's custom headers after auth.
    /// TryAddWithoutValidation so arbitrary names/values never throw.</summary>
    public static void ApplyHeaders(HttpRequestMessage req, IReadOnlyList<KeyValuePair<string, string>>? headers)
    {
        if (headers is null) return;
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            req.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
