namespace MolaGPT.Core.Chat.Tools.Browser;

/// <summary>
/// Per-turn options for the local Kimi WebBridge daemon client.
/// The daemon is a third-party local service the user installs; MolaGPT never
/// ships the extension and only talks to loopback.
/// </summary>
public sealed record BrowserControlOptions(
    bool Enabled = false,
    string DaemonUrl = WebBridgeAddress.DefaultDaemonUrl,
    int SnapshotMaxCharacters = 20000,
    int RequestTimeoutSeconds = 45,
    string? AllowedHosts = null,
    string? BlockedHosts = null,
    string? SensitiveHosts = null)
{
    /// <summary>Hosts the browser tool may open. Empty means "no allow-list":
    /// everything except <see cref="BlockedHostList"/> is permitted.</summary>
    public IReadOnlyList<string> AllowedHostList => HostRules.Split(AllowedHosts);

    /// <summary>Hosts the browser tool must never open. Wins over the allow-list.</summary>
    public IReadOnlyList<string> BlockedHostList => HostRules.Split(BlockedHosts);

    /// <summary>网银、邮箱、政务这类站点：可以去，但每一次写操作都要重新确认，
    /// 「始终允许」对它们无效。见 <see cref="BrowserGuard.ProtectedReason"/>。</summary>
    public IReadOnlyList<string> SensitiveHostList => HostRules.Split(SensitiveHosts);

    /// <summary>是否需要为了执行名单而先去问 daemon「当前在哪个站点」。</summary>
    public bool HasHostRules =>
        AllowedHostList.Count > 0 || BlockedHostList.Count > 0 || SensitiveHostList.Count > 0;
}

/// <summary>
/// Host matching for the browser allow / block lists.
///
/// A rule matches the host itself and its subdomains — <c>example.com</c> covers
/// <c>www.example.com</c> — because that is what someone typing a site name
/// means. It never matches a suffix mid-label, so <c>example.com</c> does not
/// cover <c>notexample.com</c>. A leading dot or <c>*.</c> is accepted and
/// ignored, since both are common ways to write the same intent.
/// </summary>
public static class HostRules
{
    public static IReadOnlyList<string> Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw
            .Split([',', ';', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(rule => rule.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool Matches(IReadOnlyList<string> rules, string? host)
    {
        if (rules.Count == 0 || string.IsNullOrWhiteSpace(host)) return false;
        var target = Normalize(host);
        return rules.Any(rule =>
            target.Equals(rule, StringComparison.OrdinalIgnoreCase)
            || target.EndsWith("." + rule, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string value)
    {
        var text = value.Trim().TrimEnd('/');
        if (text.StartsWith("*.", StringComparison.Ordinal)) text = text[2..];
        text = text.TrimStart('.');

        // Tolerate a pasted URL: keep the host, drop scheme, path and port.
        if (text.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            text = uri.Host;
        }
        else
        {
            var slash = text.IndexOf('/');
            if (slash >= 0) text = text[..slash];
            var colon = text.LastIndexOf(':');
            if (colon > 0 && int.TryParse(text[(colon + 1)..], out _)) text = text[..colon];
        }

        return text.Trim().ToLowerInvariant();
    }
}
