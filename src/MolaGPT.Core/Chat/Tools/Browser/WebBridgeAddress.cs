using System.Text.Json;

namespace MolaGPT.Core.Chat.Tools.Browser;

/// <summary>
/// Where the Kimi WebBridge daemon is listening.
///
/// The port is not ours to assume. The daemon reads <c>addr</c> from
/// <c>~/.kimi-webbridge/config.json</c> and only falls back to
/// <c>127.0.0.1:10086</c> when that file says nothing — so a user who moved the
/// daemon has a working browser and a MolaGPT that cannot find it.
///
/// Resolution order: what the user typed in settings, then
/// <c>~/.kimi-webbridge/daemon.addr</c>, then the daemon's config, then the
/// documented default. <c>daemon.addr</c> comes before the config on purpose —
/// it is the address the running daemon actually bound to, and
/// <c>start --addr</c> is documented as "for this run only (not saved)", so the
/// config file can be stale in exactly the case where being wrong costs the most.
/// </summary>
public static class WebBridgeAddress
{
    public const string DefaultDaemonUrl = "http://127.0.0.1:10086";
    public const int DefaultPort = 10086;

    /// <summary>The daemon's config file, whether or not it exists.</summary>
    public static string ConfigPath =>
        Path.Combine(HomeDirectory(), ".kimi-webbridge", "config.json");

    /// <summary>Where the running daemon recorded that it bound, e.g. <c>127.0.0.1:10086</c>.</summary>
    public static string RuntimeAddressPath =>
        Path.Combine(HomeDirectory(), ".kimi-webbridge", "daemon.addr");

    /// <summary>The installed daemon binary, or null when WebBridge is not installed.</summary>
    public static string? InstalledDaemonPath()
    {
        var home = HomeDirectory();
        if (home.Length == 0) return null;
        var exe = Path.Combine(home, ".kimi-webbridge", "bin", "kimi-webbridge.exe");
        return File.Exists(exe) ? exe : null;
    }

    public static bool IsInstalled => InstalledDaemonPath() is not null;

    /// <summary>
    /// The base URL to talk to, given an optional explicit override.
    /// Never throws and never returns a non-loopback target.
    /// </summary>
    public static string Resolve(string? explicitUrl = null) =>
        NormalizeLoopbackUrl(explicitUrl)
        ?? NormalizeLoopbackUrl(ReadRuntimeAddress())
        ?? NormalizeLoopbackUrl(ReadConfiguredAddress())
        ?? DefaultDaemonUrl;

    /// <summary>
    /// The address the daemon wrote down when it started, or null when it has
    /// never run on this machine.
    /// </summary>
    public static string? ReadRuntimeAddress()
    {
        try
        {
            var path = RuntimeAddressPath;
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The <c>addr</c> the daemon is configured with, or null when there is no
    /// config file (the common case — the file is only written when the user
    /// moves the daemon).
    /// </summary>
    public static string? ReadConfiguredAddress()
    {
        try
        {
            var path = ConfigPath;
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("addr", out var addr)
                   && addr.ValueKind == JsonValueKind.String
                ? addr.GetString()
                : null;
        }
        catch
        {
            // A config we cannot read is the same as no config: use the default
            // rather than refusing to work.
            return null;
        }
    }

    /// <summary>
    /// Turns an address into a loopback base URL, or null if it is not usable.
    ///
    /// A bare <c>host:port</c> (the shape the daemon's own config uses) is
    /// accepted. <c>0.0.0.0</c> and <c>::</c> mean the daemon listens on every
    /// interface — a setting the daemon itself calls a security risk — but from
    /// this machine it is still reachable on loopback, so we keep the port and
    /// connect locally rather than dialling the wildcard. Anything genuinely
    /// remote is refused: the daemon drives the user's real browser, and that is
    /// not a capability to hand to another host.
    /// </summary>
    public static string? NormalizeLoopbackUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var text = url.Trim().TrimEnd('/');

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;

        var port = uri.IsDefaultPort ? DefaultPort : uri.Port;
        if (port is <= 0 or > 65535) return null;

        if (IsWildcardHost(uri.Host))
            return $"http://127.0.0.1:{port}";

        return IsLoopbackHost(uri.Host) ? $"{uri.Scheme}://127.0.0.1:{port}" : null;
    }

    private static bool IsWildcardHost(string host) =>
        host is "0.0.0.0" or "::" or "[::]";

    private static bool IsLoopbackHost(string host) =>
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.Ordinal)
        || host.Equals("[::1]", StringComparison.Ordinal);

    private static string HomeDirectory()
    {
        try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? string.Empty; }
        catch { return string.Empty; }
    }
}
