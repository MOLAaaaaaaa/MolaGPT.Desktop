using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MolaGPT.Core.Auth;

[JsonConverter(typeof(JsonStringEnumConverter<ChatGptLoginKind>))]
public enum ChatGptLoginKind { Official, Codex }

/// <summary>A signed-in ChatGPT account's tokens. Replaced as a whole on refresh:
/// the refresh token rotates.</summary>
public sealed record ChatGptCredential(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string ClientId,
    IReadOnlyList<string> Scopes,
    string? IdToken,
    string? Email,
    string? Subject,
    ChatGptLoginKind Kind = ChatGptLoginKind.Official,
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
/// ChatGPT sign-ins for a locally run client. Each credential retains its login
/// kind so endpoints, headers and refreshes follow the authorized session.
/// </summary>
public sealed class ChatGptAccount
{
    public const string ApiBase = "https://api.openai.com/v1";

    private const string Issuer = "https://auth.openai.com";
    private const string CallbackPath = "/auth/callback";

    private const string AuthorizeUrl = Issuer + "/api/accounts/authorize";
    private const string TokenUrl = Issuer + "/api/accounts/oauth/token";
    private const string DirectScope = "chatgpt.tokens.use.direct";
    private const string Scope = "openid profile email offline_access resource.invoke " + DirectScope;
    private const string DynamicClientId = "dynamic_agent_client";
    private const string AgentName = "MolaGPT";
    private const string CodexClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string CodexAuthorizeUrl = Issuer + "/oauth/authorize";
    private const string CodexTokenUrl = Issuer + "/oauth/token";
    private const string CodexScope = "openid profile email offline_access";
    private const string CodexRedirectUri = "http://localhost:1455/auth/callback";
    public const string CodexEndpoint = "https://chatgpt.com/backend-api/codex/responses";
    private const string CodexModelsUrl = "https://chatgpt.com/backend-api/codex/models?client_version=0.155.0";
    private const string JwtAuthClaim = "https://api.openai.com/auth";
    private const string Originator = "molagpt";

    /// <summary>The port OpenAI's own examples use. The flow allows any port on
    /// 127.0.0.1, so a busy one is not a failure.</summary>
    private const int PreferredPort = 1455;

    /// <summary>Refresh this long before expiry, so no request starts with a token
    /// about to lapse.</summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(10);

    private const string CredentialKey = "chatgpt.credential";
    private const string RegistrationKey = "chatgpt.registration";
    private const string PendingRegistrationKey = "chatgpt.pending-registration";

    /// <summary>Refresh errors after which the stored grant is dead and only a new
    /// sign-in helps. Anything else — a network failure, a 5xx — is weather.</summary>
    private static readonly HashSet<string> DeadGrantErrors = new(StringComparer.Ordinal)
    {
        "invalid_grant", "invalid_refresh_token", "token_expired", "refresh_token_expired",
        "refresh_token_invalidated", "refresh_token_reused"
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
    private Registration _registration;
    private Registration? _pendingRegistration;
    private JsonElement? _configuration;

    public ChatGptAccount(CredentialStore store, Func<HttpClient> http, Action<string>? log = null)
    {
        _store = store;
        _http = http;
        _log = log;
        _registration = Load<Registration>(RegistrationKey) ?? new Registration(Guid.NewGuid().ToString("D"), null);
        // Persist the host before any browser authorization, including failed attempts.
        Save(RegistrationKey, _registration);
        _pendingRegistration = Load<Registration>(PendingRegistrationKey);
        _credential = Load<ChatGptCredential>(CredentialKey);
        if (_credential is { } credential) PreferredLoginKind = credential.Kind;
    }

    /// <summary>Signed in, signed out, or anything in between.</summary>
    public event EventHandler? Changed;

    public bool IsSignedIn => Current is not null;

    public ChatGptLoginKind? Kind => Current?.Kind;

    public ChatGptLoginKind PreferredLoginKind
    {
        get { lock (_gate) return _registration.PreferredLoginKind; }
        set
        {
            lock (_gate)
            {
                if (_registration.PreferredLoginKind == value) return;
                _registration = _registration with { PreferredLoginKind = value };
                Save(RegistrationKey, _registration);
            }
        }
    }

    /// <summary>Identity sign-in alone does not authorize inference.</summary>
    public bool CanUsePlan => Current is { } current
                              && (current.Kind == ChatGptLoginKind.Codex
                                  || current.Scopes.Contains(DirectScope, StringComparer.Ordinal));

    public string? Email => Current?.Email;

    /// <summary>Why the last session ended without the user signing out, if it did.</summary>
    public string? LastError { get; private set; }

    public string UsageUrl => Kind == ChatGptLoginKind.Codex
        ? "https://chatgpt.com/codex/settings/usage" : "https://chatgpt.com/settings/usage";

    /// <summary>Where requests go for the current session.</summary>
    public string Endpoint => Kind == ChatGptLoginKind.Codex ? CodexEndpoint : ApiBase + "/responses";

    private ChatGptCredential? Current
    {
        get { lock (_gate) return _credential; }
    }

    public async Task SignInAsync(Action<string> openBrowser, CancellationToken ct, bool useDifferentAccount = false)
    {
        var current = Current is { Kind: ChatGptLoginKind.Official } official ? official : null;
        var registration = useDifferentAccount
            ? new Registration(_registration.HostId, null)
            : current is not null
                ? _registration with { ClientId = current.ClientId, Subject = current.Subject }
                : _pendingRegistration ?? _registration;
        var requestConsent = !useDifferentAccount && current is not null && !CanUsePlan;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);
        for (var attempt = 0; ; attempt++)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var pkce = Pkce.Create();
            var state = RandomToken(32);
            var nonce = RandomToken(32);
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
            if (registration.ClientId is null) query.Add(new("agent_name_hint", AgentName));
            else if (current is { IdToken: { } hint } && current.ClientId == registration.ClientId)
                query.Add(new("id_token_hint", hint));
            if (requestConsent) query.Add(new("prompt", "consent"));
            openBrowser(BuildUrl(AuthorizeUrl, query));
            _log?.Invoke($"等待授权回调：{redirectUri}");

            try
            {
                await CompleteCallbackAsync(listener, state, async callback =>
                {
                    var issued = callback["client_id"];
                    if (registration.ClientId is { } saved && issued is not null && issued != saved)
                        throw new InvalidOperationException("OpenAI 返回的 client_id 与本次登录不符。");
                    var clientId = registration.ClientId ?? issued;
                    if (clientId is null || !clientId.StartsWith("oaiapp_", StringComparison.Ordinal))
                        throw new InvalidOperationException("OpenAI 没有返回有效的应用 client_id。");
                    registration = registration with { ClientId = clientId };
                    // A failed exchange must not replace the active account's registration.
                    _pendingRegistration = registration;
                    Save(PendingRegistrationKey, registration);
                    var token = await RequestTokenAsync(new Dictionary<string, string>
                    {
                        ["grant_type"] = "authorization_code",
                        ["client_id"] = clientId,
                        ["code"] = callback["code"]!,
                        ["code_verifier"] = pkce.Verifier,
                        ["redirect_uri"] = redirectUri,
                        ["resource"] = ApiBase,
                    }, timeout.Token).ConfigureAwait(false);
                    var credential = await ToCredentialAsync(token, clientId, nonce, previous: null,
                        expectedSubject: registration.Subject, timeout.Token).ConfigureAwait(false);
                    await _refreshGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                    try
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        lock (_gate)
                        {
                            _registration = registration with
                            {
                                Subject = credential.Subject,
                                PreferredLoginKind = ChatGptLoginKind.Official,
                            };
                            Save(RegistrationKey, _registration);
                            Save(CredentialKey, credential);
                            _credential = credential;
                            LastError = null;
                            _pendingRegistration = null;
                            _store.RemoveSecret(PendingRegistrationKey);
                        }
                    }
                    finally { _refreshGate.Release(); }
                    Changed?.Invoke(this, EventArgs.Empty);
                }, timeout.Token).ConfigureAwait(false);
                return;
            }
            catch (TokenRequestException ex) when (attempt == 0 && ex.Code == "invalid_grant")
            {
                _log?.Invoke("授权码交换失败，使用 issued client_id 重新授权一次。");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("等待 ChatGPT 授权超时，请重试。");
            }
        }
    }

    public async Task SignInWithCodexAsync(Action<string> openBrowser, CancellationToken ct)
    {
        var pkce = Pkce.Create();
        var state = RandomToken(32);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:1455/");
        try { listener.Start(); }
        catch (HttpListenerException)
        {
            throw new InvalidOperationException("端口 1455 被占用，可能是 Codex CLI 正在登录。结束后重试。");
        }
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
        _log?.Invoke("等待 Codex OAuth 授权回调。");
        await CompleteCallbackAsync(listener, state, async callback =>
        {
            var token = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = CodexClientId,
                ["code"] = callback["code"]!,
                ["code_verifier"] = pkce.Verifier,
                ["redirect_uri"] = CodexRedirectUri,
            }, timeout.Token, CodexTokenUrl).ConfigureAwait(false);
            var credential = ToCodexCredential(token, previous: null);
            await _refreshGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    Save(CredentialKey, credential);
                    _credential = credential;
                    PreferredLoginKind = ChatGptLoginKind.Codex;
                    LastError = null;
                    _pendingRegistration = null;
                    _store.RemoveSecret(PendingRegistrationKey);
                }
            }
            finally { _refreshGate.Release(); }
            Changed?.Invoke(this, EventArgs.Empty);
        }, timeout.Token).ConfigureAwait(false);
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var current = Current;
            lock (_gate) _credential = null;
            Changed?.Invoke(this, EventArgs.Empty);
            var revoked = current is null || current.Kind == ChatGptLoginKind.Codex;
            try
            {
                if (current is { Kind: ChatGptLoginKind.Official })
                {
                    var configuration = await GetConfigurationAsync(timeout.Token).ConfigureAwait(false);
                    var endpoint = RequiredString(configuration, "revocation_endpoint");
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        try
                        {
                            using var content = new FormUrlEncodedContent(
                                new Dictionary<string, string>
                                {
                                    ["token"] = current.RefreshToken,
                                    ["token_type_hint"] = "refresh_token",
                                    ["client_id"] = current.ClientId,
                                });
                            using var response = await _http().PostAsync(endpoint, content, timeout.Token).ConfigureAwait(false);
                            ObserveResponse(response);
                            revoked = response.StatusCode == HttpStatusCode.OK;
                            if (revoked || (int)response.StatusCode < 500 || attempt == 1) break;
                        }
                        catch (HttpRequestException) when (attempt == 0) { }
                        await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                _log?.Invoke("远程会话撤销未确认：" + ex.GetType().Name);
            }
            finally
            {
                lock (_gate)
                {
                    _pendingRegistration = null;
                    _store.RemoveSecret(CredentialKey);
                    _store.RemoveSecret(PendingRegistrationKey);
                    LastError = revoked ? null : "已退出登录，但未确认撤销远程会话。可在 ChatGPT 设置中断开应用。";
                }
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally { _refreshGate.Release(); }
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
                token = await RequestTokenAsync(form, ct,
                    current.Kind == ChatGptLoginKind.Codex ? CodexTokenUrl : TokenUrl).ConfigureAwait(false);
            }
            catch (TokenRequestException ex) when (ex.Code is { } code && DeadGrantErrors.Contains(code))
            {
                Expire("ChatGPT 登录已失效，请重新登录。", current);
                return null;
            }

            var refreshed = current.Kind == ChatGptLoginKind.Codex
                ? ToCodexCredential(token, current)
                : await ToCredentialAsync(token, current.ClientId, nonce: null, previous: current,
                    expectedSubject: current.Subject, ct).ConfigureAwait(false);
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

    public IReadOnlyList<KeyValuePair<string, string>> RequestHeaders()
    {
        if (Current is not { Kind: ChatGptLoginKind.Codex } current) return [];
        return
        [
            new("originator", Originator),
            new("OpenAI-Beta", "responses=experimental"),
            new("chatgpt-account-id", current.AccountId!),
        ];
    }

    /// <summary>Apply the selected session's HTTP contract after Pi and custom parameters.</summary>
    public static void AdaptBody(JsonObject body, ChatGptLoginKind kind = ChatGptLoginKind.Official)
    {
        if (body["reasoning"] is JsonObject reasoning
            && reasoning["effort"]?.GetValue<string>() is "minimal" or "none")
            reasoning["effort"] = "low";
        body["store"] = false;
        body["stream"] = true;
        var input = body["input"] as JsonArray ?? throw new JsonException("ChatGPT 请求的 input 必须是数组。");
        if (kind == ChatGptLoginKind.Codex)
        {
            var instructions = new List<string>();
            if (body["instructions"]?.GetValue<string>() is { Length: > 0 } existing) instructions.Add(existing);
            while (input.Count > 0 && input[0] is JsonObject first
                && first["role"]?.GetValue<string>() is "system" or "developer")
            {
                var content = first["content"];
                instructions.Add(content is JsonArray parts
                    ? string.Join("\n", parts.OfType<JsonObject>().Select(part => part["text"]?.GetValue<string>()))
                    : content?.GetValue<string>() ?? string.Empty);
                input.RemoveAt(0);
            }
            body["instructions"] = instructions.Count > 0 ? string.Join("\n\n", instructions) : "You are a helpful assistant.";
            return;
        }
        foreach (var item in input.OfType<JsonObject>())
            if (item["role"]?.GetValue<string>() == "system") item["role"] = "developer";
        if (body["tools"] is not JsonArray tools) return;
        var functions = new JsonArray();
        for (var i = 0; i < tools.Count;)
        {
            if (tools[i]?["type"]?.GetValue<string>() is "function" or "custom")
            {
                var tool = tools[i];
                tools.RemoveAt(i);
                functions.Add(tool);
            }
            else i++;
        }
        if (tools.Count == 0) body.Remove("tools");
        if (functions.Count > 0)
            input.Insert(0, new JsonObject { ["type"] = "additional_tools", ["role"] = "developer", ["tools"] = functions });
    }

    /// <summary>The API refused the token outright. Ends the session so the next
    /// turn asks for a sign-in instead of failing the same way again.</summary>
    public void Expire(string reason, ChatGptCredential? expected = null)
    {
        lock (_gate)
        {
            if (_credential is null || (expected is not null && !ReferenceEquals(_credential, expected))) return;
            _credential = null;
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
        if (!CanUsePlan) throw new InvalidOperationException("请先授权使用 ChatGPT 套餐。");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            Kind == ChatGptLoginKind.Codex ? CodexModelsUrl : ApiBase + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var (name, value) in RequestHeaders()) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await _http().SendAsync(request, ct).ConfigureAwait(false);
        ObserveResponse(response);
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

    public void ObserveResponse(HttpResponseMessage response) =>
        _log?.Invoke($"HTTP {(int)response.StatusCode}，request id: {RequestId(response) ?? "未提供"}");

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
            if (query["state"] != state)
            {
                await RespondAsync(context, 400, "登录请求已过期，请回到 MolaGPT 重试。").ConfigureAwait(false);
                continue;
            }
            if (query["error"] is { } error)
            {
                await RespondAsync(context, 400, "未连接 ChatGPT，可以关闭此页面。").ConfigureAwait(false);
                throw new InvalidOperationException(error == "access_denied"
                    ? "已取消授权。"
                    : "ChatGPT 授权失败：" + (query["error_description"] ?? error));
            }

            // A stale tab from an earlier attempt can still hit this port; it is not
            // this sign-in, so it is answered and ignored rather than failing it.
            if (query["code"] is not { Length: > 0 })
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

    private async Task<JsonElement> RequestTokenAsync(Dictionary<string, string> form, CancellationToken ct, string url = TokenUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http().SendAsync(request, ct).ConfigureAwait(false);
        ObserveResponse(response);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonElement json;
        try { json = JsonDocument.Parse(body).RootElement.Clone(); }
        catch (JsonException)
        {
            throw new TokenRequestException(null, $"OpenAI 登录服务返回了无法解析的内容（HTTP {(int)response.StatusCode}，request id: {RequestId(response)}）。");
        }
        if (!response.IsSuccessStatusCode || json.ValueKind != JsonValueKind.Object)
        {
            var code = String(json, "error")
                ?? (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var error) ? String(error, "code") : null);
            _log?.Invoke($"令牌请求被拒绝：HTTP {(int)response.StatusCode} {code}，request id: {RequestId(response)}");
            throw new TokenRequestException(code,
                $"OpenAI 登录服务拒绝了请求（HTTP {(int)response.StatusCode}，{code}，request id: {RequestId(response)}）。");
        }
        return json;
    }

    private static ChatGptCredential ToCodexCredential(JsonElement token, ChatGptCredential? previous)
    {
        var access = RequiredString(token, "access_token");
        var claims = DecodeJwtPayload(access);
        var auth = claims.GetProperty(JwtAuthClaim);
        var accountId = RequiredString(auth, "chatgpt_account_id");
        var idToken = String(token, "id_token") ?? previous?.IdToken;
        var identity = String(token, "id_token") is { } fresh ? DecodeJwtPayload(fresh) : default;
        return new ChatGptCredential(
            access,
            String(token, "refresh_token") ?? previous?.RefreshToken
                ?? throw new InvalidOperationException("OpenAI 没有返回 refresh_token。"),
            DateTimeOffset.UtcNow.AddSeconds(token.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600),
            CodexClientId,
            String(token, "scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? previous?.Scopes.ToArray() ?? [],
            idToken,
            String(identity, "email") ?? previous?.Email,
            String(identity, "sub") ?? String(claims, "sub") ?? previous?.Subject,
            ChatGptLoginKind.Codex,
            accountId,
            String(auth, "chatgpt_plan_type") ?? previous?.PlanType);
    }

    private static JsonElement DecodeJwtPayload(string token)
    {
        using var document = JsonDocument.Parse(DecodeBase64Url(token.Split('.')[1]));
        return document.RootElement.Clone();
    }

    private async Task<ChatGptCredential> ToCredentialAsync(
        JsonElement token, string clientId, string? nonce, ChatGptCredential? previous,
        string? expectedSubject, CancellationToken ct)
    {
        var access = RequiredString(token, "access_token");
        var refresh = String(token, "refresh_token") ?? previous?.RefreshToken
            ?? throw new InvalidOperationException("OpenAI 没有返回 refresh_token。");
        var expiresIn = token.GetProperty("expires_in").GetInt32();
        var scopes = String(token, "scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                     ?? previous?.Scopes.ToArray() ?? [];

        var idToken = String(token, "id_token") ?? previous?.IdToken;
        var email = previous?.Email;
        var subject = previous?.Subject;
        if (String(token, "id_token") is { } fresh)
        {
            var parts = fresh.Split('.');
            if (parts.Length != 3) throw new InvalidOperationException("OpenAI 返回了无效的 ID token。");
            using var headerDocument = JsonDocument.Parse(DecodeBase64Url(parts[0]));
            var header = headerDocument.RootElement;
            if (RequiredString(header, "alg") != "RS256")
                throw new InvalidOperationException("ID token 使用了不支持的签名算法。");
            var keyId = RequiredString(header, "kid");
            var configuration = await GetConfigurationAsync(ct).ConfigureAwait(false);
            var jwks = await _http().GetFromJsonAsync<JsonElement>(RequiredString(configuration, "jwks_uri"), ct).ConfigureAwait(false);
            var key = jwks.GetProperty("keys").EnumerateArray().FirstOrDefault(k =>
                String(k, "kid") == keyId && String(k, "kty") == "RSA");
            if (key.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("未找到 ID token 的 OpenAI 签名密钥。");
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = DecodeBase64Url(RequiredString(key, "n")),
                Exponent = DecodeBase64Url(RequiredString(key, "e")),
            });
            if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), DecodeBase64Url(parts[2]),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new InvalidOperationException("ID token 的 OpenAI 签名无效。");
            using var claimsDocument = JsonDocument.Parse(DecodeBase64Url(parts[1]));
            var claims = claimsDocument.RootElement;
            if (RequiredString(claims, "iss") != Issuer)
                throw new InvalidOperationException("ID token 的签发方不是 OpenAI。");
            if (!AudienceIncludes(claims, clientId))
                throw new InvalidOperationException("ID token 不是签发给本应用的。");
            if (String(claims, "azp") is { } authorizedParty && authorizedParty != clientId)
                throw new InvalidOperationException("ID token 的授权客户端不符。");
            if (claims.GetProperty("exp").GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                throw new InvalidOperationException("ID token 已过期。");
            if (nonce is not null && String(claims, "nonce") != nonce)
                throw new InvalidOperationException("ID token 与本次登录不符。");
            var sub = RequiredString(claims, "sub");
            if (expectedSubject is not null && sub != expectedSubject)
                throw new InvalidOperationException("登录得到的是另一个 ChatGPT 账号，请使用其他账号登录入口。");
            email = String(claims, "email") ?? email;
            subject = sub;
        }
        if (subject is null || idToken is null)
            throw new InvalidOperationException("OpenAI 没有返回有效的 ID token。");

        return new ChatGptCredential(
            access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn), clientId, scopes,
            idToken, email, subject);
    }

    private static bool AudienceIncludes(JsonElement claims, string clientId)
    {
        if (!claims.TryGetProperty("aud", out var aud)) return false;
        return aud.ValueKind switch
        {
            JsonValueKind.String => aud.GetString() == clientId,
            JsonValueKind.Array => aud.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == clientId),
            _ => false
        };
    }

    private async Task<JsonElement> GetConfigurationAsync(CancellationToken ct)
    {
        if (_configuration is { } cached) return cached;
        var configuration = await _http().GetFromJsonAsync<JsonElement>(Issuer + "/.well-known/openid-configuration", ct).ConfigureAwait(false);
        if (RequiredString(configuration, "issuer") != Issuer)
            throw new InvalidOperationException("OpenAI 登录配置的签发方不符。");
        _configuration = configuration;
        return configuration;
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }

    private static string RequiredString(JsonElement element, string name) =>
        String(element, name) is { Length: > 0 } value ? value
            : throw new InvalidOperationException($"OpenAI 返回的 {name} 为空或缺失。");

    private static string? RequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault()
            : response.Headers.TryGetValues("openai-request-id", out ids) ? ids.FirstOrDefault() : null;

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
    private sealed record Registration(string HostId, string? ClientId, string? Subject = null,
        ChatGptLoginKind PreferredLoginKind = ChatGptLoginKind.Official);

    private sealed class TokenRequestException(string? code, string message) : Exception(message)
    {
        public string? Code { get; } = code;
    }
}
