using System;

namespace MolaGPT.Core.Net;

public static class NetworkSecurity
{
    public static Uri RequireHttps(Uri uri, string context)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{context} 必须使用 HTTPS 加密传输。当前地址: {uri}");
        }

        return uri;
    }

    public static string RequireHttpsBaseUrl(string baseUrl, string context)
    {
        var normalized = baseUrl.TrimEnd('/') + "/";
        RequireHttps(new Uri(normalized, UriKind.Absolute), context);
        return normalized;
    }

    public static string RequireHttpOrHttpsBaseUrl(string baseUrl, string context)
    {
        var normalized = baseUrl.TrimEnd('/') + "/";
        var uri = new Uri(normalized, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{context} 必须使用 HTTP 或 HTTPS。当前地址: {uri}");
        return normalized;
    }

    /// <summary>
    /// Joins a base URL with an explicit, user-editable endpoint path. The
    /// convention across the app is: base URL = host root up to (but not
    /// including) the version segment; path carries the version, e.g.
    /// "v1/chat/completions", "v1/images/generations", "v1/models". No path
    /// is auto-inferred — what the user/preset configures is what we POST to.
    /// </summary>
    public static Uri CombineEndpoint(string baseUrl, string path, string context)
    {
        var normalizedBase = RequireHttpOrHttpsBaseUrl(baseUrl, context);
        var trimmedPath = path.Trim().TrimStart('/');
        return new Uri(normalizedBase + trimmedPath, UriKind.Absolute);
    }
}
