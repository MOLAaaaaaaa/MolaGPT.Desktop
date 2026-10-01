using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MolaGPT.Core.Auth;

/// <summary>Which of OpenAI's two ChatGPT sign-ins the session came from. They
/// issue different tokens for different endpoints, so everything downstream —
/// where requests go, what they carry, how the token is renewed — follows it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChatGptLoginKind>))]
public enum ChatGptLoginKind
{
    /// <summary>Sign in with ChatGPT: a client registered for this app, calling
    /// the public Responses API.</summary>
    Official,

    /// <summary>The Codex sign-in that Pi, OpenCode and CC Switch use, calling
    /// the Codex backend at chatgpt.com.</summary>
    Codex
}

/// <summary>A signed-in ChatGPT account's tokens. Replaced as a whole on refresh:
/// the refresh token rotates, and reusing an old one revokes the grant.</summary>
/// <param name="AccountId">The ChatGPT account the Codex backend bills; every
/// Codex request names it.</param>
public sealed record ChatGptCredential(
    ChatGptLoginKind Kind,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string ClientId,
    IReadOnlyList<string> Scopes,
    string? IdToken,
    string? Email,
    string? Subject,
    string? AccountId = null,
    string? PlanType = null);

/// <summary>One model the account may use.</summary>
public sealed record ChatGptModel(
    string Id,
    string DisplayName,
    int? ContextWindow,
    bool Vision,
    IReadOnlyList<string> ReasoningEfforts);

/// <summary>
/// A ChatGPT plan as a model provider: the user signs in, and their plan pays
/// for the requests.
///
/// Two sign-ins, because OpenAI has two. <see cref="ChatGptLoginKind.Official"/>
/// is the documented flow for open-source and locally-run apps
/// (developers.openai.com/siwc). It launched on 2026-09-29 and does not yet
/// complete for every account — authorization succeeds, then the token exchange
/// answers invalid_grant, with Pi's own implementation as with this one — so it
/// is kept ready but not offered. <see cref="ChatGptLoginKind.Codex"/> is the
/// Codex CLI's sign-in, which OpenAI has said open-source clients may use, and is
/// what the settings page runs until the official one can replace it.
///
/// Everything stays on this machine. The token is the user's own, and the
/// requests it signs go from here straight to OpenAI; a token relayed through a
/// server and shared is the use OpenAI's fraud checks exist to stop.
/// </summary>
public sealed class ChatGptAccount
{
    public const string ApiBase = "https://api.openai.com/v1";

    private const string Issuer = "https://auth.openai.com";
    private const string CallbackPath = "/auth/callback";

    // Official — Sign in with ChatGPT.
    private const string AuthorizeUrl = Issuer + "/api/accounts/authorize";
    private const string TokenUrl = Issuer + "/api/accounts/oauth/token";
    private const string DirectScope = "chatgpt.tokens.use.direct";
    private const string Scope = "openid profile email offline_access resource.invoke " + DirectScope;
    private const string DynamicClientId = "dynamic_agent_client";
    private const string AgentName = "MolaGPT";

    /// <summary>The port OpenAI's own examples use. The flow allows any port on
    /// 127.0.0.1, so a busy one is not a failure.</summary>
    private const int PreferredPort = 1455;

    // Codex.
    private const string CodexClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string CodexAuthorizeUrl = Issuer + "/oauth/authorize";
    private const string CodexTokenUrl = Issuer + "/oauth/token";
    private const string CodexScope = "openid profile email offline_access";

    /// <summary>Registered for the Codex client, so neither host nor port may vary.
    /// The Codex CLI listens on the same one while it signs in.</summary>
    private const string CodexRedirectUri = "http://localhost:1455/auth/callback";

    public const string CodexEndpoint = "https://chatgpt.com/backend-api/codex/responses";
    private const string CodexModelsUrl = "https://chatgpt.com/backend-api/codex/models?client_version=0.155.0";
    private const string CodexUsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string JwtAuthClaim = "https://api.openai.com/auth";

    /// <summary>Names this app to the Codex backend, as Pi and OpenCode name
    /// themselves, rather than passing as the Codex CLI.</summary>
    public const string Originator = "molagpt";

    /// <summary>Refresh this long before expiry, so no request starts with a token
    /// about to lapse.</summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(10);

    private const string CredentialKey = "chatgpt.credential";
    private const string RegistrationKey = "chatgpt.registration";

    /// <summary>Refresh errors after which the stored grant is dead and only a new
    /// sign-in helps. Anything else — a network failure, a 5xx — is weather.</summary>
    private static readonly HashSet<string> DeadGrantErrors = new(StringComparer.Ordinal)
    {
        "invalid_grant", "invalid_refresh_token", "token_expired", "refresh_token_expired",
        "refresh_token_invalidated", "refresh_token_reused", "invalid_client"
    };

    private static readonly JsonSerializerOptions StoreJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly CredentialStore _store;
    private readonly Func<HttpClient> _http;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _gate = new();
    private ChatGptCredential? _credential;
    private (ChatGptUsage Usage, DateTimeOffset ReadAt)? _usage;
    private Registration _registration;

    public ChatGptAccount(CredentialStore store, Func<HttpClient> http, Action<string>? log = null)
    {
        _store = store;
        _http = http;
        _log = log;
        _credential = Load<ChatGptCredential>(CredentialKey);
        _registration = Load<Registration>(RegistrationKey) ?? new Registration(Guid.NewGuid().ToString("D"), null, null);
    }

    /// <summary>Signed in, signed out, or anything in between.</summary>
    public event EventHandler? Changed;

    public bool IsSignedIn => Current is not null;

    public ChatGptLoginKind? Kind => Current?.Kind;

    /// <summary>Signed in and allowed to use the plan. An official sign-in can
    /// decline the plan permission on the consent page; a Codex one has no such
    /// choice.</summary>
    public bool CanUsePlan => Current is { } current
                              && (current.Kind == ChatGptLoginKind.Codex
                                  || current.Scopes.Contains(DirectScope, StringComparer.Ordinal));

    public string? Email => Current?.Email;

    /// <summary>The plan the Codex token names (plus, pro, team…), when it does.</summary>
    public string? PlanType => Current?.PlanType;

    /// <summary>Why the last session ended without the user signing out, if it did.</summary>
    public string? LastError { get; private set; }

    /// <summary>Where the user sees, and limits, what this session spends. Codex
    /// usage is metered on its own page.</summary>
    public string UsageUrl => Current?.Kind == ChatGptLoginKind.Codex
        ? "https://chatgpt.com/codex/settings/usage"
        : "https://chatgpt.com/settings/usage";

    /// <summary>Where requests go for the current session.</summary>
    public string Endpoint => Current?.Kind == ChatGptLoginKind.Codex ? CodexEndpoint : ApiBase + "/responses";

    /// <summary>The plan's limits as last read, null until read. Only the Codex
    /// backend reports them.</summary>
    public ChatGptUsage? Usage
    {
        get { lock (_gate) return _usage?.Usage; }
    }

    /// <summary>When <see cref="Usage"/> was last read.</summary>
    public DateTimeOffset? UsageReadAt
    {
        get { lock (_gate) return _usage?.ReadAt; }
    }

    /// <summary>A new reading of <see cref="Usage"/>.</summary>
    public event EventHandler? UsageChanged;

    private ChatGptCredential? Current
    {
        get { lock (_gate) return _credential; }
    }

    /// <summary>
    /// Sign in with ChatGPT. Completes once the token exchange has, or throws when
    /// the user cancels, declines, or lets it time out — or when OpenAI refuses
    /// the exchange, which is what sends the user to <see cref="SignInWithCodexAsync"/>.
    /// </summary>
    public async Task SignInAsync(Action<string> openBrowser, CancellationToken ct)
    {
        var pkce = Pkce.Create();
        var state = RandomToken(32);
        var nonce = RandomToken(32);
        var registration = _registration;

        using var listener = StartListener(out var redirectUri);
        var query = new List<KeyValuePair<string, string>>
        {
            new("client_id", registration.ClientId ?? DynamicClientId),
            new("ext_agent_host_id", "urn:uuid:" + registration.HostId),
            new("response_type", "code"),
            new("redirect_uri", redirectUri),
            new("scope", Scope),
            new("resource", ApiBase),
            new("state", state),
            new("nonce", nonce),
            new("code_challenge", pkce.Challenge),
            new("code_challenge_method", "S256"),
        };
        // The name is only asked for when registering; a returning client already has one.
        if (registration.ClientId is null) query.Add(new("agent_name_hint", AgentName));
        else if (registration.LastIdToken is { } hint) query.Add(new("id_token_hint", hint));

        openBrowser(BuildUrl(AuthorizeUrl, query));
        _log?.Invoke($"[chatgpt] 等待授权回调：{redirectUri}，client {query[0].Value}");

        await CompleteCallbackAsync(listener, state, async callback =>
        {
            var issued = callback.GetValues("client_id")?.FirstOrDefault(id => id.StartsWith("oaiapp_", StringComparison.Ordinal));
            var clientId = issued ?? registration.ClientId
                ?? throw new InvalidOperationException("OpenAI 没有返回应用的 client_id。");
            _log?.Invoke($"[chatgpt] 收到授权码，client {clientId}");
            var token = await RequestTokenAsync(TokenUrl, new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = callback.GetValues("code")![0],
                ["code_verifier"] = pkce.Verifier,
                ["redirect_uri"] = redirectUri,
                ["resource"] = ApiBase,
            }, ct).ConfigureAwait(false);

            var credential = ToCredential(ChatGptLoginKind.Official, token, clientId, nonce, previous: null);
            lock (_gate) _registration = registration with { ClientId = clientId, LastIdToken = credential.IdToken };
            Save(RegistrationKey, _registration);
            Store(credential);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The Codex sign-in, for accounts the official exchange refuses.</summary>
    public async Task SignInWithCodexAsync(Action<string> openBrowser, CancellationToken ct)
    {
        var pkce = Pkce.Create();
        var state = RandomToken(32);

        HttpListener listener;
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:1455/");
            listener.Start();
        }
        catch (HttpListenerException)
        {
            throw new InvalidOperationException("端口 1455 被占用，可能是 Codex CLI 正在登录。结束后重试。");
        }

        using (listener)
        {
            openBrowser(BuildUrl(CodexAuthorizeUrl,
            [
                new("response_type", "code"),
                new("client_id", CodexClientId),
                new("redirect_uri", CodexRedirectUri),
                new("scope", CodexScope),
                new("code_challenge", pkce.Challenge),
                new("code_challenge_method", "S256"),
                new("state", state),
                new("id_token_add_organizations", "true"),
                new("codex_cli_simplified_flow", "true"),
                new("originator", Originator),
            ]));
            _log?.Invoke("[chatgpt] 等待 Codex 授权回调");

            await CompleteCallbackAsync(listener, state, async callback =>
            {
                var token = await RequestTokenAsync(CodexTokenUrl, new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = CodexClientId,
                    ["code"] = callback.GetValues("code")![0],
                    ["code_verifier"] = pkce.Verifier,
                    ["redirect_uri"] = CodexRedirectUri,
                }, ct).ConfigureAwait(false);
                Store(ToCredential(ChatGptLoginKind.Codex, token, CodexClientId, nonce: null, previous: null));
            }, ct).ConfigureAwait(false);
        }
    }

    public void SignOut()
    {
        lock (_gate)
        {
            _credential = null;
            _usage = null;
            LastError = null;
            _store.RemoveSecret(CredentialKey);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A current access token, refreshed first when it is about to expire. Null
    /// when nobody is signed in or the grant has died; a refresh that fails for
    /// any other reason throws, since the session may still be fine.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        var current = Current;
        if (current is null) return null;
        if (current.ExpiresAt - ExpiryMargin > DateTimeOffset.UtcNow) return current.AccessToken;

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while this one waited.
            current = Current;
            if (current is null) return null;
            if (current.ExpiresAt - ExpiryMargin > DateTimeOffset.UtcNow) return current.AccessToken;

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = current.ClientId,
                ["refresh_token"] = current.RefreshToken,
            };
            if (current.Kind == ChatGptLoginKind.Official) form["resource"] = ApiBase;

            JsonElement token;
            try
            {
                token = await RequestTokenAsync(
                    current.Kind == ChatGptLoginKind.Codex ? CodexTokenUrl : TokenUrl, form, ct).ConfigureAwait(false);
            }
            catch (TokenRequestException ex) when (ex.Code is { } code && DeadGrantErrors.Contains(code))
            {
                Expire("ChatGPT 登录已失效，请重新登录。", current);
                return null;
            }

            var refreshed = ToCredential(current.Kind, token, current.ClientId, nonce: null, previous: current);
            lock (_gate)
            {
                if (!ReferenceEquals(_credential, current)) return null;
                _credential = refreshed;
                Save(CredentialKey, refreshed);
            }
            return refreshed.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Headers every request of this session carries besides the bearer.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> RequestHeaders()
    {
        if (Current is not { Kind: ChatGptLoginKind.Codex } current) return [];
        var headers = new List<KeyValuePair<string, string>>
        {
            new("originator", Originator),
            new("OpenAI-Beta", "responses=experimental"),
        };
        if (current.AccountId is { } account) headers.Add(new("chatgpt-account-id", account));
        return headers;
    }

    /// <summary>
    /// Last fixes to a Responses body for a ChatGPT plan.
    ///
    /// Plan models reason at <c>low</c> or above and refuse <c>minimal</c> and
    /// <c>none</c>, which is what Pi sends when reasoning is switched off, so the
    /// floor is used instead.
    ///
    /// The Codex backend also wants the system prompt as <c>instructions</c>.
    /// Without it, it falls back to the model's own base instructions — the Codex
    /// CLI's prompt — so the leading system and developer messages are moved
    /// there. It refuses stored and unstreamed responses as well.
    /// </summary>
    public static void AdaptBody(JsonObject body, ChatGptLoginKind kind)
    {
        if (body["reasoning"] is JsonObject reasoning
            && reasoning["effort"]?.GetValue<string>() is "minimal" or "none")
            reasoning["effort"] = "low";
        if (kind != ChatGptLoginKind.Codex) return;

        var instructions = new List<string>();
        if (body["instructions"]?.GetValue<string>() is { Length: > 0 } existing) instructions.Add(existing);
        if (body["input"] is JsonArray input)
        {
            while (input.Count > 0
                   && input[0] is JsonObject first
                   && first["role"]?.GetValue<string>() is "system" or "developer")
            {
                instructions.Add(MessageText(first["content"]));
                input.RemoveAt(0);
            }
        }
        body["instructions"] = instructions.Count > 0 ? string.Join("\n\n", instructions) : "You are a helpful assistant.";
        body["store"] = false;
        body["stream"] = true;
    }

    private static string MessageText(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray parts => string.Join("\n", parts
            .OfType<JsonObject>()
            .Select(part => part["text"]?.GetValue<string>())
            .Where(text => !string.IsNullOrEmpty(text))),
        _ => string.Empty
    };

    /// <summary>The API refused the token outright. Ends the session so the next
    /// turn asks for a sign-in instead of failing the same way again.</summary>
    public void Expire(string reason, ChatGptCredential? expected = null)
    {
        lock (_gate)
        {
            if (_credential is null || (expected is not null && !ReferenceEquals(_credential, expected))) return;
            _credential = null;
            _usage = null;
            LastError = reason;
            _store.RemoveSecret(CredentialKey);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The models this account may use, in the order OpenAI lists them.</summary>
    public async Task<IReadOnlyList<ChatGptModel>> ListModelsAsync(CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("未登录 ChatGPT。");
        var codex = Kind == ChatGptLoginKind.Codex;
        using var request = new HttpRequestMessage(HttpMethod.Get, codex ? CodexModelsUrl : ApiBase + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var (name, value) in RequestHeaders()) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await _http().SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"获取 ChatGPT 模型失败（HTTP {(int)response.StatusCode}）：{Trim(body)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var list = root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array ? models
            : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data
            : default;
        if (list.ValueKind != JsonValueKind.Array) return [];

        var result = new List<ChatGptModel>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            // Only what OpenAI means to be picked; the rest are internal or retired.
            if (String(item, "visibility") is { } visibility && visibility != "list") continue;
            var id = String(item, "slug") ?? String(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var efforts = item.TryGetProperty("supported_reasoning_levels", out var levels)
                          && levels.ValueKind == JsonValueKind.Array
                ? levels.EnumerateArray()
                    .Select(level => level.ValueKind == JsonValueKind.String ? level.GetString() : String(level, "effort"))
                    .Where(effort => !string.IsNullOrWhiteSpace(effort))
                    .Select(effort => effort!)
                    .ToArray()
                : [];
            var vision = item.TryGetProperty("input_modalities", out var modalities)
                         && modalities.ValueKind == JsonValueKind.Array
                         && modalities.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.String && m.GetString() == "image");
            var context = item.TryGetProperty("context_window", out var window) && window.TryGetInt32(out var size)
                ? size : (int?)null;
            result.Add(new ChatGptModel(id!, String(item, "display_name") ?? id!, context, vision, efforts));
        }
        return result;
    }

    /// <summary>Reads the plan's limits. Does nothing for an official sign-in,
    /// whose API has no such report.</summary>
    public async Task RefreshUsageAsync(CancellationToken ct)
    {
        if (Kind != ChatGptLoginKind.Codex) return;
        var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        if (token is null) return;

        using var request = new HttpRequestMessage(HttpMethod.Get, CodexUsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var (name, value) in RequestHeaders()) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await _http().SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"获取 ChatGPT 用量失败（HTTP {(int)response.StatusCode}）：{Trim(body)}");

        using var doc = JsonDocument.Parse(body);
        if (ChatGptUsage.FromStatus(doc.RootElement) is { } usage) SetUsage(usage);
    }

    /// <summary>Takes the limits from a Codex response's headers, so every turn
    /// keeps them current without a separate request.</summary>
    public void ReadUsage(HttpResponseMessage response)
    {
        if (ChatGptUsage.FromHeaders(response.Headers) is { } usage) SetUsage(usage);
    }

    private void SetUsage(ChatGptUsage usage)
    {
        lock (_gate)
        {
            if (_credential is null) return;
            _usage = (usage, DateTimeOffset.UtcNow);
        }
        UsageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Store(ChatGptCredential credential)
    {
        lock (_gate)
        {
            _credential = credential;
            // A sign-in may be another account; its limits are its own.
            _usage = null;
            LastError = null;
            Save(CredentialKey, credential);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static HttpListener StartListener(out string redirectUri)
    {
        foreach (var port in new[] { PreferredPort, FreePort() })
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                redirectUri = $"http://127.0.0.1:{port}{CallbackPath}";
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }
        throw new InvalidOperationException("无法在本机监听登录回调。");
    }

    /// <summary>
    /// Waits for this sign-in's callback and hands its query to
    /// <paramref name="exchange"/>. The browser is answered only after the
    /// exchange, so its page says what actually happened rather than "connected"
    /// ahead of a refusal.
    /// </summary>
    private static async Task CompleteCallbackAsync(
        HttpListener listener,
        string state,
        Func<System.Collections.Specialized.NameValueCollection, Task> exchange,
        CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(SignInTimeout);
        await using var stop = limit.Token.Register(listener.Stop);
        while (true)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (limit.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    ct.IsCancellationRequested ? "登录已取消。" : "等待授权超时，请重试。", ct);
            }

            var path = context.Request.RawUrl?.Split('?', 2)[0];
            if (path != CallbackPath)
            {
                await RespondAsync(context, 404, "页面不存在。").ConfigureAwait(false);
                continue;
            }

            var query = ParseQuery(context.Request.RawUrl);
            if (query["error"] is { } error)
            {
                await RespondAsync(context, 400, "未连接 ChatGPT，可以关闭此页面。").ConfigureAwait(false);
                throw new InvalidOperationException(error == "access_denied"
                    ? "已取消授权。"
                    : "ChatGPT 授权失败：" + (query["error_description"] ?? error));
            }

            // A stale tab from an earlier attempt can still hit this port; it is not
            // this sign-in, so it is answered and ignored rather than failing it.
            if (query.GetValues("state")?.FirstOrDefault() != state
                || query.GetValues("code")?.FirstOrDefault() is not { Length: > 0 })
            {
                await RespondAsync(context, 400, "登录请求已过期，请回到 MolaGPT 重试。").ConfigureAwait(false);
                continue;
            }

            try
            {
                await exchange(query).ConfigureAwait(false);
            }
            catch
            {
                await RespondAsync(context, 400, "连接 ChatGPT 失败，请回到 MolaGPT 查看原因。").ConfigureAwait(false);
                throw;
            }
            await RespondAsync(context, 200, "已连接 ChatGPT，可以关闭此页面。").ConfigureAwait(false);
            return;
        }
    }

    /// <summary>
    /// The callback query exactly as the browser sent it. Not <c>Request.Url</c>,
    /// which HttpListener rebuilds by un-escaping and re-escaping the target; the
    /// code has to reach the token endpoint byte for byte.
    /// </summary>
    private static System.Collections.Specialized.NameValueCollection ParseQuery(string? rawUrl)
    {
        var mark = rawUrl?.IndexOf('?') ?? -1;
        return System.Web.HttpUtility.ParseQueryString(mark < 0 ? string.Empty : rawUrl![(mark + 1)..]);
    }

    private static async Task RespondAsync(HttpListenerContext context, int status, string message)
    {
        try
        {
            var html = "<!doctype html><meta charset=\"utf-8\"><title>MolaGPT</title>"
                       + "<body style=\"font-family:system-ui,sans-serif;display:grid;place-items:center;height:90vh;margin:0\">"
                       + "<p>" + WebUtility.HtmlEncode(message) + "</p></body>";
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
        {
            // The tab was closed; the sign-in's outcome does not depend on it.
        }
    }

    private static string BuildUrl(string baseUrl, IEnumerable<KeyValuePair<string, string>> query) =>
        // A string, never a Uri: Uri.ToString() un-escapes the query on the way out.
        baseUrl + "?" + string.Join("&", query.Select(kv =>
            Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

    private async Task<JsonElement> RequestTokenAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http().SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonElement json;
        try { json = JsonDocument.Parse(body).RootElement.Clone(); }
        catch (JsonException)
        {
            throw new TokenRequestException(null, $"OpenAI 登录服务返回了无法解析的内容（HTTP {(int)response.StatusCode}）。");
        }
        if (!response.IsSuccessStatusCode || json.ValueKind != JsonValueKind.Object)
        {
            var code = String(json, "error") ?? (json.TryGetProperty("error", out var error) ? String(error, "code") : null);
            _log?.Invoke($"[chatgpt] 令牌请求被拒绝：HTTP {(int)response.StatusCode} {code}");
            throw new TokenRequestException(code, code == "invalid_grant" && form["grant_type"] == "authorization_code"
                ? "OpenAI 拒绝了这次登录（invalid_grant）。"
                : $"OpenAI 登录服务拒绝了请求（HTTP {(int)response.StatusCode}）：{Trim(body)}");
        }
        return json;
    }

    /// <summary>
    /// Builds the stored credential from a token response.
    ///
    /// The ID token's signature is not checked: it arrives straight from OpenAI's
    /// token endpoint over TLS, which OpenID Connect accepts in place of a
    /// signature check (Core §3.1.3.7). Its claims are still held to this sign-in —
    /// issuer, audience, nonce — and only identify the account for display.
    /// </summary>
    private static ChatGptCredential ToCredential(
        ChatGptLoginKind kind, JsonElement token, string clientId, string? nonce, ChatGptCredential? previous)
    {
        var access = String(token, "access_token") ?? throw new InvalidOperationException("OpenAI 没有返回 access_token。");
        var refresh = String(token, "refresh_token") ?? previous?.RefreshToken
            ?? throw new InvalidOperationException("OpenAI 没有返回 refresh_token。");
        var expiresIn = token.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? seconds : 3600;
        var scopes = String(token, "scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                     ?? previous?.Scopes.ToArray() ?? [];

        var idToken = String(token, "id_token") ?? previous?.IdToken;
        string? email = previous?.Email, subject = previous?.Subject;
        if (String(token, "id_token") is { } fresh && DecodeJwtPayload(fresh) is { } claims)
        {
            if (String(claims, "iss") is { } iss && iss.TrimEnd('/') != Issuer)
                throw new InvalidOperationException("ID token 的签发方不是 OpenAI。");
            if (!AudienceIncludes(claims, clientId))
                throw new InvalidOperationException("ID token 不是签发给本应用的。");
            if (nonce is not null && String(claims, "nonce") != nonce)
                throw new InvalidOperationException("ID token 与本次登录不符。");
            var sub = String(claims, "sub");
            if (previous?.Subject is { } known && sub is not null && sub != known)
                throw new InvalidOperationException("刷新得到的是另一个 ChatGPT 账号。");
            email = String(claims, "email") ?? email;
            subject = sub ?? subject;
        }

        // The Codex backend bills the account named in the access token's own claims.
        string? accountId = previous?.AccountId, plan = previous?.PlanType;
        if (kind == ChatGptLoginKind.Codex
            && DecodeJwtPayload(access) is { } accessClaims
            && accessClaims.TryGetProperty(JwtAuthClaim, out var auth))
        {
            accountId = String(auth, "chatgpt_account_id") ?? accountId;
            plan = String(auth, "chatgpt_plan_type") ?? plan;
        }
        if (kind == ChatGptLoginKind.Codex && accountId is null)
            throw new InvalidOperationException("Codex 登录的令牌里没有 ChatGPT 账号。");

        return new ChatGptCredential(
            kind, access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn), clientId, scopes,
            idToken, email, subject, accountId, plan);
    }

    private static bool AudienceIncludes(JsonElement claims, string clientId)
    {
        if (!claims.TryGetProperty("aud", out var aud)) return true;
        return aud.ValueKind switch
        {
            JsonValueKind.String => aud.GetString() == clientId,
            JsonValueKind.Array => aud.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == clientId),
            _ => false
        };
    }

    private static JsonElement? DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private T? Load<T>(string key) where T : class
    {
        try
        {
            return _store.LoadSecret(key) is { Length: > 0 } json ? JsonSerializer.Deserialize<T>(json, StoreJson) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Save<T>(string key, T value) => _store.SaveSecret(key, JsonSerializer.Serialize(value, StoreJson));

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Trim(string body) => body.Length <= 300 ? body : body[..300] + "…";

    private sealed record Pkce(string Verifier, string Challenge)
    {
        public static Pkce Create()
        {
            var verifier = RandomToken(32);
            return new Pkce(verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        }
    }

    /// <param name="HostId">This installation, as OpenAI's <c>ext_agent_host_id</c>.</param>
    /// <param name="ClientId">The client OpenAI issued on first sign-in.</param>
    /// <param name="LastIdToken">Lets a later sign-in skip the account picker.</param>
    private sealed record Registration(string HostId, string? ClientId, string? LastIdToken);

    private sealed class TokenRequestException(string? code, string message) : Exception(message)
    {
        public string? Code { get; } = code;
    }
}
