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
}
