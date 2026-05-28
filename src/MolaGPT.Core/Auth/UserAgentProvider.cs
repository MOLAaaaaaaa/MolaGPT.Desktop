using System.Reflection;
using System.Runtime.InteropServices;

namespace MolaGPT.Core.Auth;

/// <summary>
/// CRITICAL: MolaGPT account tokens are bound to the User-Agent used during
/// login. The client must therefore send the same User-Agent on every
/// authenticated request.
///
/// We use a clearly-marked desktop UA. This makes the request easy to single
/// out in Cloudflare WAF / Bot Fight Mode rules; set up a "Skip" custom rule
/// that matches <c>http.user_agent contains "MolaGPT-Desktop"</c>, and the
/// desktop client never gets challenged.
///
/// IMPORTANT: do NOT bake real version numbers into the UA. Once a JWT is
/// issued for a specific UA hash, any change to that UA invalidates the
/// token and forces re-login. The version segment below is a marketing-style
/// "1.0" that we hold steady forever; protocol-breaking changes get a new
/// suffix that bumps the stored UA on purpose.
/// </summary>
public static class UserAgentProvider
{
    /// <summary>Recognizable, app-stable User-Agent. Easy to whitelist in Cloudflare.</summary>
    public const string FixedUa = "MolaGPT-Desktop/1.0 (Windows; .NET 8 WPF)";

    /// <summary>
    /// Custom header value clients send alongside the UA — gives operators a
    /// second matching dimension if they prefer a header-based CF rule
    /// (header values are slightly harder for abusers to spoof at scale).
    /// </summary>
    public const string ClientMarker = "MolaGPT-Desktop";
}
