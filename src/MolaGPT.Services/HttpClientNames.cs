namespace MolaGPT.Desktop.Services;

/// <summary>
/// Named-HttpClient keys shared by every front end.
///
/// These were consts on the WPF App class, which meant a service that only
/// needed a string could not be compiled outside a WPF assembly. They live here
/// so the same two clients are addressed by the same names from either shell.
/// </summary>
public static class HttpClientNames
{
    /// <summary>
    /// Bound to a shared CookieContainer so Cloudflare's __cf_bm and the
    /// backend's mola_did cookie survive warmup → login → chat. Its User-Agent
    /// is hashed into JWT.ua and must stay constant for the process lifetime.
    /// </summary>
    public const string MolaGpt = "molagpt";

    /// <summary>BYOK calls: no sticky cookies needed.</summary>
    public const string Byok = "byok";
}
