using System.Text.Json;
using MolaGPT.Core.Auth;

namespace MolaGPT.Core.Chat;

internal static class OpenAiRequestParams
{
    public static string? OfficialPath(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "api.openai.com" || !uri.IsDefaultPort)
            return null;

        return uri.AbsolutePath switch
        {
            "/v1/responses" or "/v1/responses/" => "/v1/responses",
            "/v1/chat/completions" or "/v1/chat/completions/" => "/v1/chat/completions",
            _ => null,
        };
    }

    public static void Apply(IDictionary<string, object?> body, string? officialPath)
    {
        if (officialPath is null) return;
        var model = body.TryGetValue("model", out var value) ? value switch
        {
            string id => id,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        } : null;

        // Ordinary requests omit sampling. A saved override can still contain fields this family rejects.
        if (!ChatGptAccount.IsGpt62Model(model)) return;
        body.Remove("temperature");
        body.Remove("top_p");
        body.Remove("top_logprobs");
        if (officialPath == "/v1/chat/completions")
        {
            body.Remove("logprobs");
        }
        else if (body.TryGetValue("include", out var include)
                 && JsonSerializer.SerializeToElement(include) is { ValueKind: JsonValueKind.Array } includes)
        {
            var remaining = includes.EnumerateArray()
                .Where(item => item.ValueKind != JsonValueKind.String || item.GetString() != "message.output_text.logprobs")
                .ToArray();
            if (remaining.Length == includes.GetArrayLength()) return;
            if (remaining.Length == 0) body.Remove("include");
            else body["include"] = remaining;
        }
    }
}
