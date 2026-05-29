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
/// NO VERSION NUMBER, ON PURPOSE. Once a JWT is signed against a UA hash,
/// changing that UA invalidates the token and forces re-login. Embedding
/// the assembly version turns every release into a forced logout, which
/// is bad UX. The constant below is intentionally version-less so we can
/// ship as many releases as we want without disturbing existing logins;
/// CF / WAF rules still match by the literal "MolaGPT-Desktop" prefix.
/// If we ever truly need a hard re-login (security wipe, protocol break),
/// bump the suffix manually — that's the only situation that warrants it.
/// </summary>
public static class UserAgentProvider
{
    /// <summary>Recognizable, app-stable User-Agent. Easy to whitelist in Cloudflare.</summary>
    public const string FixedUa = "MolaGPT-Desktop (Windows; .NET 8 WPF)";

    /// <summary>
    /// Custom header value clients send alongside the UA — gives operators a
    /// second matching dimension if they prefer a header-based CF rule
    /// (header values are slightly harder for abusers to spoof at scale).
    /// </summary>
    public const string ClientMarker = "MolaGPT-Desktop";
}
