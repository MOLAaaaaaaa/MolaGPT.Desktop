using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MolaGPT.Core.Net;

namespace MolaGPT.Core.Auth;

/// <summary>
/// Wraps the MolaGPT login flow:
///   POST https://chatgpt.wljay.cn/v2/api/auth/login.php
///        body { username, password = sha256(plaintext) }
///   resp { success, token, userInfo: { username, unlimited } }
///
/// MolaGPT account tokens compare
/// <c>JWT.ua === sha256($_SERVER['HTTP_USER_AGENT'])</c> on every chat
/// request. If the desktop client's UA changes between login and chat (e.g.
/// because we shipped a new version), every stored JWT goes stale and chat
/// returns 401.
///
/// To recover automatically, this service ALSO stores the SHA-256 hash of the
/// User-Agent that was active at login time. <see cref="IsJwtValidForUa"/>
/// lets the host detect a UA mismatch at startup and clear the bad token
/// silently; no infinite-loop 401s, no mysterious "登录中" hang.
/// </summary>
public sealed class MolaGptAuthService
{
    public const string JwtKey = "molagpt.jwt";
    public const string UsernameKey = "molagpt.username";
    public const string UaHashKey = "molagpt.ua_hash";
    public const string DefaultLoginUrl = "https://chatgpt.wljay.cn/v2/api/auth/login.php";
    public const string DefaultOAuthExchangeUrl = "https://chatgpt.wljay.cn/v2/api/auth/oauth_exchange.php";
    public const string DefaultWarmupUrl = "https://chatgpt.wljay.cn/v2/";

    private readonly HttpClient _http;
    private readonly CredentialStore _store;
    private readonly string _loginUrl;
    private readonly string _oauthExchangeUrl;
    private readonly string _warmupUrl;
    private bool _warmedUp;

    public MolaGptAuthService(
        HttpClient http,
        CredentialStore store,
        string? loginUrl = null,
        string? warmupUrl = null,
        string? oauthExchangeUrl = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _loginUrl = NetworkSecurity.RequireHttps(new Uri(loginUrl ?? DefaultLoginUrl), "MolaGPT 登录").ToString();
        _oauthExchangeUrl = NetworkSecurity.RequireHttps(
            new Uri(oauthExchangeUrl ?? DefaultOAuthExchangeUrl), "MolaGPT OAuth 兑换").ToString();
        _warmupUrl = NetworkSecurity.RequireHttps(new Uri(warmupUrl ?? DefaultWarmupUrl), "MolaGPT 预热").ToString();
    }

    public string? CurrentJwt => _store.LoadSecret(JwtKey);
    public string? CurrentUsername => _store.LoadSecret(UsernameKey);
    public string? StoredUaHash => _store.LoadSecret(UaHashKey);

    public static string ComputeUaHash(string ua) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ua ?? string.Empty))).ToLowerInvariant();

    /// <summary>
    /// True iff a JWT is stored AND it was issued for the same UA we're about
    /// to send. Call at startup to decide whether to keep the token or wipe it.
    /// </summary>
    public bool IsJwtValidForUa(string currentUa)
    {
        if (string.IsNullOrEmpty(CurrentJwt)) return false;
        var stored = StoredUaHash;
        if (string.IsNullOrEmpty(stored)) return false;
        return string.Equals(stored, ComputeUaHash(currentUa), StringComparison.OrdinalIgnoreCase);
    }

    public async Task WarmupAsync(CancellationToken ct = default)
    {
        if (_warmedUp) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _warmupUrl);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            _ = resp.IsSuccessStatusCode;
        }
        catch
        {
            // Non-fatal.
        }
        finally
        {
            _warmedUp = true;
        }
    }

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        await WarmupAsync(ct).ConfigureAwait(false);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();

        using var req = new HttpRequestMessage(HttpMethod.Post, _loginUrl);
        req.Content = JsonContent.Create(new { username, password = hash });
        req.Headers.TryAddWithoutValidation("Origin", "https://chatgpt.wljay.cn");
        req.Headers.TryAddWithoutValidation("Referer", "https://chatgpt.wljay.cn/v2/");
        req.Headers.TryAddWithoutValidation("X-MolaGPT-Client", UserAgentProvider.ClientMarker);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new LoginResult(false, null, $"HTTP {(int)resp.StatusCode}: {SummarizeLoginBody(body)}");
        }

        var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        LoginPayload? payload;
        try { payload = JsonSerializer.Deserialize<LoginPayload>(content); }
        catch (JsonException) { return new LoginResult(false, null, SummarizeLoginBody(content)); }

        if (payload is null || !payload.Success || string.IsNullOrEmpty(payload.Token))
            return new LoginResult(false, null, payload?.Message ?? "Empty response");

        // Persist the JWT alongside the UA hash that was used to mint it.
        // If the app's UA ever changes, IsJwtValidForUa will return false and
        // App.xaml.cs will silently wipe the stale token at next startup.
        _store.SaveSecret(JwtKey, payload.Token);
        _store.SaveSecret(UaHashKey, ComputeUaHash(UserAgentProvider.FixedUa));
        if (!string.IsNullOrEmpty(payload.UserInfo?.Username))
            _store.SaveSecret(UsernameKey, payload.UserInfo.Username);

        return new LoginResult(true, payload.UserInfo, null);
    }

    public void Logout()
    {
        _store.RemoveSecret(JwtKey);
        _store.RemoveSecret(UsernameKey);
        _store.RemoveSecret(UaHashKey);
    }

    /// <summary>
    /// Redeems a short-lived OAuth handoff code (from
    /// <c>molagpt://oauth_callback?code=...</c>) for the session JWT and
    /// persists it. The code is single-use and expires in about five minutes.
    /// </summary>
    public async Task<LoginResult> ExchangeOAuthCodeAsync(string? code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new LoginResult(false, null, "授权码为空");

        await WarmupAsync(ct).ConfigureAwait(false);

        using var req = new HttpRequestMessage(HttpMethod.Post, _oauthExchangeUrl);
        req.Content = JsonContent.Create(new { code });
        req.Headers.TryAddWithoutValidation("Origin", "https://chatgpt.wljay.cn");
        req.Headers.TryAddWithoutValidation("Referer", "https://chatgpt.wljay.cn/v2/");
        req.Headers.TryAddWithoutValidation("X-MolaGPT-Client", UserAgentProvider.ClientMarker);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return new LoginResult(false, null, $"HTTP {(int)resp.StatusCode}: {SummarizeLoginBody(content)}");

        LoginPayload? payload;
        try { payload = JsonSerializer.Deserialize<LoginPayload>(content); }
        catch (JsonException) { return new LoginResult(false, null, SummarizeLoginBody(content)); }

        if (payload is null || !payload.Success || string.IsNullOrEmpty(payload.Token))
            return new LoginResult(false, null, payload?.Message ?? "授权码兑换失败");

        if (!ApplyExternalToken(payload.Token))
            return new LoginResult(false, null, "兑换成功但令牌无法解析，请重试");

        return new LoginResult(true, payload.UserInfo, null);
    }

    /// <summary>
    /// Persists a JWT obtained out-of-band. Prefer
    /// <see cref="ExchangeOAuthCodeAsync"/> for OAuth; this remains for
    /// direct JWT handoff during migration.
    /// </summary>
    public bool ApplyExternalToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.');
        if (parts.Length < 2) return false;

        string? username = null;
        try
        {
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("username", out var u) &&
                u.ValueKind == JsonValueKind.String)
                username = u.GetString();
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(username)) return false;

        _store.SaveSecret(JwtKey, token);
        _store.SaveSecret(UaHashKey, ComputeUaHash(UserAgentProvider.FixedUa));
        _store.SaveSecret(UsernameKey, username);
        return true;
    }

    private static byte[] Base64UrlDecode(string segment)
    {
        var s = segment.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private sealed class LoginPayload
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("userInfo")] public UserInfo? UserInfo { get; set; }
    }

    private static string SummarizeLoginBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "服务器没有返回可读的错误信息。";

        if (body.Contains("__cf_chl", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
        {
            return "请求被 Cloudflare 拦截。在 chatgpt.wljay.cn 的 Cloudflare → Security → WAF → Custom Rules 加一条 Skip 规则:When (http.user_agent contains \"MolaGPT-Desktop\")  Then (Skip → All remaining custom rules / Bot Fight Mode / Managed Rules) 即可放行。";
        }

        if (body.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("<!doctype", StringComparison.OrdinalIgnoreCase))
        {
            return "服务器返回了 HTML 而不是 JSON,登录接口可能被中间层改写,或被 WAF/Nginx 拦截。";
        }

        var compact = body.Replace("\r", " ").Replace("\n", " ").Trim();
        return compact.Length <= 180 ? compact : compact[..180] + "...";
    }
}

public sealed record UserInfo(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("unlimited")] bool Unlimited);

public sealed record LoginResult(bool Success, UserInfo? UserInfo, string? ErrorMessage);

/// <summary>
/// Thrown when chat / model-list calls receive 401. The caller should display
/// the message to the user and prompt re-login.
/// </summary>
public sealed class MolaGptAuthExpiredException : Exception
{
    public MolaGptAuthExpiredException()
        : base("MolaGPT 登录态已失效 — JWT 与当前客户端 UA 不匹配,可能是更新过版本。请点击右上角的用户图标重新登录。") { }
}
