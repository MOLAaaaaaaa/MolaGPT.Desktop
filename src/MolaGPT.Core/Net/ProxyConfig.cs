using System.Text;

namespace MolaGPT.Core.Net;

public enum ProxyMode
{
    /// <summary>Whatever Windows says (including auto-detect and PAC) — what .NET does on its own.</summary>
    System,
    Manual,
    /// <summary>Connect directly, ignoring the system setting.</summary>
    None
}

public enum ProxyScheme
{
    Http,
    Socks5
}

/// <summary>
/// 设置 → 网络代理, as stored. Only <see cref="ProxyMode.Manual"/> reads the rest.
/// </summary>
public sealed record ProxyConfig
{
    /// <summary>What a fresh install starts with. Private ranges go direct: a proxy
    /// that cannot see 192.168.x.x would otherwise break a LAN model server.</summary>
    public const string DefaultBypass = "10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, *.local, <local>";

    public ProxyMode Mode { get; init; } = ProxyMode.System;
    public ProxyScheme Scheme { get; init; } = ProxyScheme.Http;
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string Bypass { get; init; } = DefaultBypass;

    /// <summary>Whether molagpt.* traffic (login, official models, cloud sync, remote
    /// control) follows the proxy. Off sends it direct whatever the mode: the account
    /// risk check is tied to the device and the exit IP changing mid-session is exactly
    /// what it looks for.</summary>
    public bool ApplyToOfficial { get; init; } = true;

    public static ProxyConfig Default { get; } = new();

    /// <summary>True when manual mode has a server it can actually dial.</summary>
    public bool IsManualUsable => Mode == ProxyMode.Manual && TryGetServerUri(out _);

    public bool TryGetServerUri(out Uri uri)
    {
        uri = null!;
        var host = Host.Trim();
        if (host.Length == 0 || Port is < 1 or > 65535) return false;
        if (host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@')) return false;
        try
        {
            uri = new UriBuilder(Scheme == ProxyScheme.Socks5 ? "socks5" : "http", host, Port).Uri;
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The variables a child process (an MCP server started through npx or uvx, say)
    /// needs to reach the same proxy. Direct mode clears inherited proxy settings;
    /// system mode leaves the child's environment unchanged.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToEnvironment()
    {
        if (Mode == ProxyMode.None)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["HTTP_PROXY"] = string.Empty,
                ["HTTPS_PROXY"] = string.Empty,
                ["ALL_PROXY"] = string.Empty,
                ["NO_PROXY"] = "*"
            };

        if (!IsManualUsable || !TryGetServerUri(out var server)) return new Dictionary<string, string>();

        // socks5h: the proxy resolves names, which is what .NET's own SOCKS support does
        // and the only form most tools accept for it.
        var scheme = Scheme == ProxyScheme.Socks5 ? "socks5h" : "http";
        var credentials = Username.Length == 0
            ? string.Empty
            : Uri.EscapeDataString(Username) + (Password.Length == 0 ? string.Empty : ":" + Uri.EscapeDataString(Password)) + "@";
        var url = $"{scheme}://{credentials}{server.Authority}";

        var noProxy = new StringBuilder("localhost,127.0.0.1,::1");
        foreach (var entry in ProxyBypassList.Entries(Bypass))
        {
            // Wildcards and <local> are WinINet notation; the NO_PROXY dialect is suffixes and CIDR.
            var converted = entry.StartsWith("*.", StringComparison.Ordinal) ? entry[1..] : entry;
            if (converted.Contains('*') || converted.StartsWith('<')) continue;
            noProxy.Append(',').Append(converted);
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HTTP_PROXY"] = url,
            ["HTTPS_PROXY"] = url,
            ["ALL_PROXY"] = url,
            ["NO_PROXY"] = noProxy.ToString()
        };
    }
}
