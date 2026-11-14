using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Net;

namespace MolaGPT.Core.Auth;

/// <summary>
/// Wraps the MolaGPT login flow:
///   POST https://chatgpt.wljay.cn/v2/api/auth/login.php
///        body { username, password = sha256(plaintext) }
///   resp { success, token, userInfo: { username, unlimited } }
///
/// Account sessions use short-lived access tokens and a rotating refresh token.
/// The persisted UA hash is retained for sessions issued by the legacy server.
/// </summary>
public sealed class MolaGptAuthService
{
    public const string JwtKey = "molagpt.jwt";
    public const string UsernameKey = "molagpt.username";
    public const string UaHashKey = "molagpt.ua_hash";
    public const string RefreshTokenKey = "molagpt.refresh_token";
    public const string ExpiresAtKey = "molagpt.expires_at";
    public const string DefaultLoginUrl = "https://chatgpt.wljay.cn/v2/api/auth/login.php";
    public const string DefaultOAuthExchangeUrl = "https://chatgpt.wljay.cn/v2/api/auth/oauth_exchange.php";
    public const string DefaultWarmupUrl = "https://chatgpt.wljay.cn/v2/";
    public const string DefaultLogoutUrl = "https://chatgpt.wljay.cn/v2/api/auth/logout.php";
    public const string DefaultRefreshUrl = "https://chatgpt.wljay.cn/v2/api/auth/session_refresh.php";

    private readonly HttpClient _http;
    private readonly CredentialStore _store;
    private readonly Action<string>? _log;
    private readonly string _loginUrl;
    private readonly string _oauthExchangeUrl;
    private readonly string _warmupUrl;
    private readonly object _sessionGate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private long _sessionGeneration;
    private long _loginAttemptGeneration;
    private OAuthAttempt? _pendingOAuth;
    private bool _warmedUp;

    private sealed record OAuthAttempt(string State, string Verifier, DateTimeOffset ExpiresAt);

    public event EventHandler? LoggedOut;

    public MolaGptAuthService(
        HttpClient http,
        CredentialStore store,
        string? loginUrl = null,
        string? warmupUrl = null,
        string? oauthExchangeUrl = null,
        Action<string>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log;
        _loginUrl = NetworkSecurity.RequireMolaGptAccountOrigin(new Uri(loginUrl ?? DefaultLoginUrl), "MolaGPT 登录").ToString();
        _oauthExchangeUrl = NetworkSecurity.RequireMolaGptAccountOrigin(
            new Uri(oauthExchangeUrl ?? DefaultOAuthExchangeUrl), "MolaGPT OAuth 兑换").ToString();
        _warmupUrl = NetworkSecurity.RequireMolaGptAccountOrigin(new Uri(warmupUrl ?? DefaultWarmupUrl), "MolaGPT 预热").ToString();
    }

    public string? CurrentJwt => _store.LoadSecret(JwtKey);
    public string? CurrentUsername => _store.LoadSecret(UsernameKey);
    public string? StoredUaHash => _store.LoadSecret(UaHashKey);
    public long SessionGeneration { get { lock (_sessionGate) return _sessionGeneration; } }

    public static string ComputeUaHash(string ua) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ua ?? string.Empty))).ToLowerInvariant();

    /// <summary>
    /// True iff a JWT is stored AND it was issued for the same UA we're about
    /// to send. Call at startup to decide whether to keep the token or wipe it.
    /// </summary>
    public bool IsJwtValidForUa(string currentUa)
    {
        if (string.IsNullOrEmpty(CurrentJwt)) return false;
        if (!string.IsNullOrEmpty(_store.LoadSecret(RefreshTokenKey))) return true;
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
        long generation;
        lock (_sessionGate)
        {
            _pendingOAuth = null;
            generation = ++_loginAttemptGeneration;
        }
        await WarmupAsync(ct).ConfigureAwait(false);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();

        using var req = new HttpRequestMessage(HttpMethod.Post, _loginUrl);
        req.Content = JsonContent.Create(new { username, password = hash, session_protocol = 2 });
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

        lock (_sessionGate)
        {
            if (generation != _loginAttemptGeneration)
                return new LoginResult(false, null, "登录状态已变化，请重试。");
            if (!SaveSessionLocked(payload))
                return new LoginResult(false, null, "服务器未返回完整的登录会话，请稍后重试。");
            _sessionGeneration++;
        }

        return new LoginResult(true, payload.UserInfo, null);
    }

    public void Logout()
    {
        bool hadAuthState;
        lock (_sessionGate)
        {
            _sessionGeneration++;
            _loginAttemptGeneration++;
            _pendingOAuth = null;
            hadAuthState = ClearAuthLocked();
        }

        if (hadAuthState)
            LoggedOut?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> LogoutRemoteAsync(CancellationToken ct = default)
    {
        string? token;
        long generation;
        lock (_sessionGate)
        {
            token = CurrentJwt;
            generation = _sessionGeneration;
        }

        var revoked = string.IsNullOrEmpty(token);
        if (!string.IsNullOrEmpty(token))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                token = await GetAccessTokenAsync(token, timeout.Token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, DefaultLogoutUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                revoked = response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or MolaGptAuthExpiredException)
            {
                revoked = false;
            }
        }

        var hadAuthState = false;
        lock (_sessionGate)
        {
            if (generation == _sessionGeneration)
            {
                _sessionGeneration++;
                _loginAttemptGeneration++;
                _pendingOAuth = null;
                hadAuthState = ClearAuthLocked();
            }
        }
        if (hadAuthState) LoggedOut?.Invoke(this, EventArgs.Empty);
        return revoked;
    }

    public bool LogoutIfCurrent(string? expectedJwt)
    {
        bool hadAuthState;
        lock (_sessionGate)
        {
            if (string.IsNullOrEmpty(expectedJwt) ||
                !string.Equals(CurrentJwt, expectedJwt, StringComparison.Ordinal)) return false;
            _sessionGeneration++;
            _loginAttemptGeneration++;
            _pendingOAuth = null;
            hadAuthState = ClearAuthLocked();
        }
        if (hadAuthState) LoggedOut?.Invoke(this, EventArgs.Empty);
        return hadAuthState;
    }

    public string BeginOAuth(string url)
    {
        var target = NetworkSecurity.RequireMolaGptAccountOrigin(new Uri(url), "MolaGPT OAuth");
        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var state = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        lock (_sessionGate)
        {
            _loginAttemptGeneration++;
            _pendingOAuth = new OAuthAttempt(state, verifier, DateTimeOffset.UtcNow.AddMinutes(10));
        }

        var builder = new UriBuilder(target);
        var query = builder.Query.TrimStart('?');
        builder.Query = (query.Length == 0 ? "" : query + "&") +
            $"code_challenge={challenge}&code_challenge_method=S256&client_state={state}";
        return builder.Uri.ToString();
    }

    public void CancelOAuth()
    {
        lock (_sessionGate)
        {
            _pendingOAuth = null;
            _loginAttemptGeneration++;
        }
    }

    private bool ClearAuthLocked()
    {
        var hadAuthState = !string.IsNullOrEmpty(CurrentJwt)
            || !string.IsNullOrEmpty(CurrentUsername)
            || !string.IsNullOrEmpty(StoredUaHash);
        _store.RemoveSecret(JwtKey);
        _store.RemoveSecret(UsernameKey);
        _store.RemoveSecret(UaHashKey);
        _store.RemoveSecret(RefreshTokenKey);
        _store.RemoveSecret(ExpiresAtKey);
        return hadAuthState;
    }

    /// <summary>
    /// Redeems a short-lived OAuth handoff code (from
    /// <c>molagpt://oauth_callback?code=...</c>) for the session JWT and
    /// persists it. The code is single-use and expires in about five minutes.
    /// </summary>
    public async Task<LoginResult> ExchangeOAuthCodeAsync(string? code, string? state, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new LoginResult(false, null, "授权码为空");

        long generation;
        string verifier;
        lock (_sessionGate)
        {
            if (_pendingOAuth is not { } pending || pending.ExpiresAt <= DateTimeOffset.UtcNow
                || !string.Equals(pending.State, state, StringComparison.Ordinal))
                return new LoginResult(false, null, "登录请求已失效，请重新发起。");
            verifier = pending.Verifier;
            _pendingOAuth = null;
            generation = ++_loginAttemptGeneration;
        }
        await WarmupAsync(ct).ConfigureAwait(false);

        using var req = new HttpRequestMessage(HttpMethod.Post, _oauthExchangeUrl);
        req.Content = JsonContent.Create(new { code, code_verifier = verifier, session_protocol = 2 });
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

        lock (_sessionGate)
        {
            if (generation != _loginAttemptGeneration)
                return new LoginResult(false, null, "登录状态已变化，请重试。");
            if (!SaveSessionLocked(payload))
                return new LoginResult(false, null, "服务器未返回完整的登录会话，请重新登录。");
            _sessionGeneration++;
        }

        return new LoginResult(true, payload.UserInfo, null);
    }

    private bool SaveSessionLocked(LoginPayload payload)
    {
        if (payload.SessionProtocol != 2 || string.IsNullOrEmpty(payload.Token)
            || string.IsNullOrEmpty(payload.RefreshToken)
            || payload.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
        var username = payload.UserInfo?.Username ?? ReadJwtString(payload.Token, "username");
        if (string.IsNullOrEmpty(username)) return false;
        // Store the rotated refresh credential before replacing the access token.
        _store.SaveSecret(RefreshTokenKey, payload.RefreshToken);
        _store.SaveSecret(ExpiresAtKey, payload.ExpiresAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _store.SaveSecret(JwtKey, payload.Token);
        _store.SaveSecret(UaHashKey, ComputeUaHash(UserAgentProvider.FixedUa));
        _store.SaveSecret(UsernameKey, username);
        return true;
    }

    /// <summary>Refreshes before dispatch; callers must not replay an already sent operation.</summary>
    public async Task<string> GetAccessTokenAsync(string expectedToken, CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string currentToken;
            string? refreshToken;
            long generation;
            lock (_sessionGate)
            {
                currentToken = CurrentJwt ?? throw new MolaGptAuthExpiredException();
                if (!IsSameSession(expectedToken, currentToken))
                    throw new OperationCanceledException("账户已切换，原请求已取消。");
                generation = _sessionGeneration;
                refreshToken = _store.LoadSecret(RefreshTokenKey);
                if (string.IsNullOrEmpty(refreshToken)
                    || (long.TryParse(_store.LoadSecret(ExpiresAtKey), out var expiresAt)
                        && expiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60))
                    return currentToken;
            }

            _log?.Invoke("refresh started");
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            using var request = new HttpRequestMessage(HttpMethod.Post, DefaultRefreshUrl)
            {
                Content = JsonContent.Create(new { refresh_token = refreshToken })
            };
            request.Headers.TryAddWithoutValidation("Origin", "https://chatgpt.wljay.cn");
            request.Headers.TryAddWithoutValidation("X-MolaGPT-Client", UserAgentProvider.ClientMarker);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            _log?.Invoke($"refresh HTTP={(int)response.StatusCode} elapsed_ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            lock (_sessionGate)
            {
                if (generation != _sessionGeneration)
                    throw new OperationCanceledException("账户已切换，原请求已取消。");
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var cleared = LogoutIfCurrent(currentToken);
                _log?.Invoke($"refresh unauthorized session_cleared={cleared}");
                throw new MolaGptAuthExpiredException();
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(SummarizeLoginBody(content), null, response.StatusCode);
            var payload = JsonSerializer.Deserialize<LoginPayload>(content);
            lock (_sessionGate)
            {
                if (generation != _sessionGeneration)
                    throw new OperationCanceledException("账户已切换，原请求已取消。");
                if (payload is null || !payload.Success
                    || !IsSameSession(currentToken, payload.Token)
                    || !SaveSessionLocked(payload))
                    throw new InvalidOperationException("服务器返回的刷新会话无效，请重新登录。");
                return payload.Token!;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"refresh failed type={ex.GetType().Name}");
            throw;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static bool IsSameSession(string expectedToken, string? currentToken)
    {
        if (string.Equals(expectedToken, currentToken, StringComparison.Ordinal)) return true;
        var sid = ReadJwtString(expectedToken, "sid");
        return !string.IsNullOrEmpty(sid)
            && string.Equals(sid, ReadJwtString(currentToken, "sid"), StringComparison.Ordinal);
    }

    private static string? ReadJwtString(string? token, string name)
    {
        var parts = token?.Split('.');
        if (parts is null || parts.Length != 3) return null;
        try
        {
            using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            return doc.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { return null; }
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

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class LoginPayload
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("userInfo")] public UserInfo? UserInfo { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_at")] public long ExpiresAt { get; set; }
        [JsonPropertyName("session_protocol")] public int SessionProtocol { get; set; }
    }

    private static string SummarizeLoginBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "服务器没有返回可读的错误信息。";

        if (body.Contains("__cf_chl", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
        {
            return "登录请求被站点防护拦截，请稍后重试或联系管理员。";
        }

        if (body.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("<!doctype", StringComparison.OrdinalIgnoreCase))
        {
            return "服务器返回了 HTML 而不是 JSON,登录接口可能被中间层改写,或被 WAF/Nginx 拦截。";
        }

        // 限频（429）、账号服务不可用（503）等都带 {success:false, message}，只显示那一句
        var message = ChatApiErrorHelper.ExtractErrorMessage(body);
        if (!string.IsNullOrWhiteSpace(message) && !message.StartsWith('{'))
            return message;

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
        : base("MolaGPT 登录已过期或已撤销，请重新登录。") { }
}
