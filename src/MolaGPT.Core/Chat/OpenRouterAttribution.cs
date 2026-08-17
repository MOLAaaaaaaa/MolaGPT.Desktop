using System.Net.Http;

namespace MolaGPT.Core.Chat;

/// <summary>
/// OpenRouter optional ranking headers (<c>HTTP-Referer</c> / <c>X-Title</c>).
/// Applied automatically for BYOK requests whose base URL hosts openrouter.ai.
/// </summary>
public static class OpenRouterAttribution
{
    /// <summary>Unified app title across Desktop / Mobile / Web.</summary>
    public const string AppTitle = "MolaGPT";

    /// <summary>Desktop site URL reported to OpenRouter rankings.</summary>
    public const string RefererUrl = "https://github.com/MOLAaaaaaaa/MolaGPT.Desktop";

    public static bool IsOpenRouterHost(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.Host.EndsWith("openrouter.ai", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds OpenRouter ranking headers when <paramref name="baseUrl"/> is OpenRouter,
    /// skipping any name already present on the request or in <paramref name="customHeaders"/>.
    /// </summary>
    public static void Apply(
        HttpRequestMessage req,
        string? baseUrl,
        IReadOnlyList<KeyValuePair<string, string>>? customHeaders = null)
    {
        if (!IsOpenRouterHost(baseUrl)) return;

        if (!HasHeader(req, customHeaders, "HTTP-Referer") && !HasHeader(req, customHeaders, "Referer"))
            req.Headers.TryAddWithoutValidation("HTTP-Referer", RefererUrl);

        if (!HasHeader(req, customHeaders, "X-Title"))
            req.Headers.TryAddWithoutValidation("X-Title", AppTitle);
    }

    private static bool HasHeader(
        HttpRequestMessage req,
        IReadOnlyList<KeyValuePair<string, string>>? customHeaders,
        string name)
    {
        if (req.Headers.Contains(name)) return true;
        if (customHeaders is null) return false;
        foreach (var (n, _) in customHeaders)
        {
            if (!string.IsNullOrWhiteSpace(n) && n.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
