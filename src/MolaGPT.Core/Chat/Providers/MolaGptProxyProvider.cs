using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Models;
using MolaGPT.Core.Net;

namespace MolaGPT.Core.Chat.Providers;

/// <summary>
/// Connects to existing MolaGPT PHP chat proxy endpoints (chator.php,
/// chatclaude.php, chatv4.php, dsCNchat.php, chatcli.php, chatvol.php,
/// chatkm.php, chatApiNL.php, chatgrok.php, chatAuto.php).
///
/// Model list is loaded from <c>api/auth/model_config_public.php</c> after
/// login. Each model in MolaGPT registry has its own <c>apiUrl</c> (relative
/// path); we resolve against <see cref="BaseUrl"/> per request.
///
/// Authenticated requests use the account HTTP handler to invalidate expired credentials.
/// </summary>
public sealed class MolaGptProxyProvider : IChatProvider
{
    private const int MaxPublicImageUploadBytes = 10 * 1024 * 1024;
    private const string AccountApiBasePath = "/v2/";

    public const string LocalToolsModelsPath = "api/auth/desktop_local_models.php";
    public const string LocalToolsChatPath = "api/auth/desktop_chat_completions.php";
    public const string LocalToolsDisplayName = "MolaGPT 本地工具";

    public string Id => MolaGptProviderIds.Proxy;
    public string DisplayName => "MolaGPT 账号";
    public ProviderKind Kind => ProviderKind.MolaGptProxy;
    public IReadOnlyList<ProviderModel> Models => _models;

    public string BaseUrl { get; init; } = "https://" + NetworkSecurity.MolaGptAccountHost + AccountApiBasePath;
    public IReadOnlyDictionary<string, string> ModelToApiUrl => _modelToApiUrl;

    private readonly HttpClient _http;
    private readonly MolaGptAuthService _auth;
    private List<ProviderModel> _models = new();
    private Dictionary<string, string> _modelToApiUrl = new();

    public MolaGptProxyProvider(HttpClient http, MolaGptAuthService auth)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
    }

    public long AccountSessionGeneration => _auth.SessionGeneration;

    public async Task RefreshModelsAsync(CancellationToken ct = default)
    {
        var generation = _auth.SessionGeneration;
        var baseUri = new Uri(NetworkSecurity.RequireHttpsBaseUrl(BaseUrl, "MolaGPT 云服务"));
        var url = NetworkSecurity.RequireHttps(new Uri(baseUri, "api/auth/model_config_public.php"), "MolaGPT 模型列表");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var root = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false);
        var modelsObj = root?["models"]?.AsObject();
        if (modelsObj is null) return;

        var list = new List<ProviderModel>();
        var apiMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (configKey, value) in modelsObj)
        {
            if (value is not JsonObject cfg) continue;
            if (cfg["show_in_frontend"]?.GetValue<bool>() != true) continue;

            var modelName = cfg["modelName"]?.GetValue<string>();
            if (string.IsNullOrEmpty(modelName)) continue;
            var tipText = cfg["tipText"]?.GetValue<string>() ?? modelName;
            var apiUrl = cfg["apiUrl"]?.GetValue<string>() ?? "api/auth/chator.php";
            var supportsThinking = cfg["supportsThinking"]?.GetValue<bool>() ?? false;
            var supportsReasoning = cfg["supportsReasoningEffort"]?.GetValue<bool>() ?? false;
            var showImageUpload = cfg["showImageUpload"]?.GetValue<bool>() ?? false;

            list.Add(new ProviderModel(
                Id: modelName,
                DisplayName: tipText,
                SupportsVision: showImageUpload,
                SupportsThinking: supportsThinking,
                SupportsReasoningEffort: supportsReasoning,
                SupportsToolCalling: true,
                ThinkingConfig: ReadQwenReasoningConfig(cfg)));
            apiMap[modelName] = apiUrl;
        }

        if (generation != _auth.SessionGeneration)
            throw new OperationCanceledException("账户已切换，原请求已取消。");
        _models = list;
        _modelToApiUrl = apiMap;
    }

    /// <summary>
    /// Loads the subset of MolaGPT account models that the server exposes via
    /// the desktop OpenAI-compatible gateway. These models still bill against
    /// the MolaGPT account, but the desktop client runs the tool loop locally.
    /// </summary>
    public async Task<IReadOnlyList<ProviderModel>> FetchLocalToolsModelsAsync(CancellationToken ct = default)
    {
        var generation = _auth.SessionGeneration;
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrEmpty(jwt)) return Array.Empty<ProviderModel>();

        var baseUri = new Uri(NetworkSecurity.RequireHttpsBaseUrl(BaseUrl, "MolaGPT 云服务"));
        var url = NetworkSecurity.RequireHttps(new Uri(baseUri, LocalToolsModelsPath), "MolaGPT 本地工具模型列表");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(resp, "MolaGPT 本地工具模型列表", ct, accountRequest: true).ConfigureAwait(false);

        var root = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false);
        if (generation != _auth.SessionGeneration)
            throw new OperationCanceledException("账户已切换，原请求已取消。");
        var modelsObj = root?["models"]?.AsObject();
        if (modelsObj is null) return Array.Empty<ProviderModel>();

        var list = new List<ProviderModel>();
        foreach (var (_, value) in modelsObj)
        {
            if (value is not JsonObject cfg) continue;
            if (cfg["show_in_frontend"]?.GetValue<bool>() == false) continue;

            var modelName = cfg["modelName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(modelName)) continue;
            var tipText = cfg["tipText"]?.GetValue<string>() ?? modelName!;
            var supportsThinking = cfg["supportsThinking"]?.GetValue<bool>() ?? false;
            var supportsReasoning = cfg["supportsReasoningEffort"]?.GetValue<bool>() ?? false;
            var showImageUpload = cfg["showImageUpload"]?.GetValue<bool>() ?? false;
            var supportsToolCalling = cfg["supportsToolCalling"]?.GetValue<bool>() ?? false;

            list.Add(new ProviderModel(
                Id: modelName!,
                DisplayName: tipText,
                SupportsVision: showImageUpload,
                SupportsThinking: supportsThinking,
                SupportsReasoningEffort: supportsReasoning,
                SupportsToolCalling: supportsToolCalling,
                ThinkingConfig: ReadQwenReasoningConfig(cfg) ?? BuildLocalToolsThinkingConfig(
                    modelName!, supportsThinking, supportsReasoning,
                    cfg["reasoningEffortAdapter"]?.GetValue<string>())));
        }

        return list;
    }

    private static ThinkingConfig? ReadQwenReasoningConfig(JsonObject cfg)
    {
        if (cfg["supportsReasoningEffort"]?.GetValue<bool>() != true
            || cfg["reasoningEffortAdapter"]?.GetValue<string>() != "openai_chat_reasoning_effort"
            || cfg["reasoningToggleAdapter"]?.GetValue<string>() != "qwen_enable_thinking")
            return null;

        // The thinking toggle already represents "none".
        var levels = cfg["reasoningEffortLevels"] is JsonArray configured
            ? ThinkingEffortLevels.Normalize(configured.Select(level => level?.GetValue<string>() ?? ""))
                .Where(level => level != "none").ToArray()
            : [];
        var defaultEffort = cfg["defaultReasoningEffort"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        return new ThinkingConfig(ThinkingParamKind.QwenReasoningEffort,
            EffortLevels: levels, DefaultEffort: levels.Contains(defaultEffort) ? defaultEffort : null);
    }

    private static ThinkingConfig? BuildLocalToolsThinkingConfig(
        string modelName,
        bool supportsThinking,
        bool supportsReasoningEffort,
        string? reasoningEffortAdapter)
    {
        if (!supportsThinking && !supportsReasoningEffort) return null;

        var inferred = reasoningEffortAdapter switch
        {
            "openai_chat_reasoning_effort" => ThinkingParamKind.OpenAiReasoningEffort,
            "qwen_thinking_budget" => ThinkingParamKind.QwenThinkingBudget,
            _ => ThinkingParamKindInference.InferFromModelId(modelName)
        };
        if (inferred == ThinkingParamKind.None && supportsReasoningEffort)
            inferred = ThinkingParamKind.OpenAiReasoningEffort;
        if (inferred == ThinkingParamKind.None) return null;

        return new ThinkingConfig(inferred);
    }

    /// <summary>
    /// Fetch the user's account state from <c>api/auth/status.php</c> (the
    /// account status endpoint. Returns null when no JWT
    /// is stored. Account errors retain the server's business classification.
    /// The response shape includes:
    /// <code>
    ///   { user: { logged_in, username, type, unlimited, is_donor,
    ///             usage:{model_id:int}, tokens_usage:{model_id:int},
    ///             credits:{enabled, enforcing, tier, used, allowance, remaining,
    ///                      window_days, recovers_at, next_refresh_at, by_model_tokens,
    ///                      base_tokens_per_credit} | null,
    ///             limits:{model_id:{daily_limit, daily_tokens_limit, display_name}}},
    ///     model_status:{model_id:{available, remaining, remaining_tokens,
    ///                             credit_multiplier, credit_symbol,...}},
    ///     config:{registered_user_limits:{...}} }
    /// </code>
    /// Since the credit rollout the per-model <c>daily_limit</c> /
    /// <c>daily_tokens_limit</c> are all <c>-1</c> and <c>remaining</c> is no
    /// longer emitted at all; the real budget is the shared credit pool under
    /// <c>user.credits</c>. <c>credits</c> is null when the server has the
    /// feature switched off, in which case callers fall back to the old
    /// per-model numbers.
    /// </summary>
    public async Task<MolaGptStatus?> FetchStatusAsync(CancellationToken ct = default)
    {
        var generation = _auth.SessionGeneration;
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrEmpty(jwt)) return null;

        var baseUri = new Uri(NetworkSecurity.RequireHttpsBaseUrl(BaseUrl, "MolaGPT 云服务"));
        var url = NetworkSecurity.RequireHttps(new Uri(baseUri, "api/auth/status.php"), "MolaGPT 账号状态");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(resp, "MolaGPT 账号状态", ct, accountRequest: true).ConfigureAwait(false);

        var root = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false);
        if (generation != _auth.SessionGeneration)
            throw new OperationCanceledException("账户已切换，原请求已取消。");
        if (root is null) return null;

        var user = root["user"]?.AsObject();
        if (user is null) return null;

        bool loggedIn = user["logged_in"]?.GetValue<bool>() ?? false;
        if (!loggedIn) return null;

        var username = user["username"]?.GetValue<string>() ?? "";
        var unlimited = user["unlimited"]?.GetValue<bool>() ?? false;
        var isDonor = user["is_donor"]?.GetValue<bool>() ?? false;
        // Server-side master toggle for personalized memory. Absent on older
        // builds of status.php (null = unknown); PDO may emit it as 0/1.
        var personalizedMemoryEnabled = ParseOptBool(user["settings"]?["personalized_memory_enabled"]);
        var usage = ParseIntDict(user["usage"]);
        var tokensUsage = ParseIntDict(user["tokens_usage"]);

        // Shared credit pool. Absent / enabled=false means the server is still
        // on the legacy per-model quotas, so leave this null and let the UI keep
        // its old rendering.
        MolaGptCredits? credits = null;
        if (user["credits"] is JsonObject cr && (cr["enabled"]?.GetValue<bool>() ?? false))
        {
            credits = new MolaGptCredits(
                Enforcing: cr["enforcing"]?.GetValue<bool>() ?? false,
                Tier: cr["tier"]?.GetValue<string>() ?? "registered",
                Used: ParseDouble(cr["used"]) ?? 0,
                Allowance: ParseDouble(cr["allowance"]) ?? 0,
                Remaining: ParseDouble(cr["remaining"]) ?? 0,
                BaseTokensPerCredit: (int)(ParseDouble(cr["base_tokens_per_credit"]) ?? 0),
                // Older builds of status.php omit these three; the defaults keep
                // the daily-reset wording those builds still imply. The window
                // breakdown has to stay null when absent rather than collapse to
                // an empty dict, or the same-day fallback never fires. An empty
                // object is a different thing — a window with nothing spent in it.
                WindowDays: (int)(ParseDouble(cr["window_days"]) ?? 1),
                RecoversAt: cr["recovers_at"]?.GetValue<string>(),
                ByModelTokens: cr["by_model_tokens"] is JsonObject bmt ? ParseIntDict(bmt) : null,
                WebSearch: cr["web_search"] is JsonObject ws && ws["name"]?.GetValue<string>() is { Length: > 0 } wsName
                    ? new MolaGptWebSearchUsage(
                        wsName,
                        (int)(ParseDouble(ws["calls"]) ?? 0),
                        ParseDouble(ws["uses"]) is { } wsUses ? (int)wsUses : null)
                    : null,
                NextRefreshAt: cr["next_refresh_at"]?.GetValue<long>(),
                ResetMode: cr["quota_reset_mode"]?.GetValue<string>());
        }

        // Per-model limits live under user.limits OR config.registered_user_limits
        // depending on the build of status.php; prefer the former, fall back.
        var limitsObj = user["limits"]?.AsObject()
            ?? root["config"]?["registered_user_limits"]?.AsObject();
        var limits = new Dictionary<string, MolaGptModelLimit>(StringComparer.Ordinal);
        if (limitsObj is not null)
        {
            foreach (var (modelId, val) in limitsObj)
            {
                if (val is not JsonObject lc) continue;
                limits[modelId] = new MolaGptModelLimit(
                    DisplayName: lc["display_name"]?.GetValue<string>() ?? modelId,
                    DailyRequests: lc["daily_limit"] is JsonValue dl && dl.TryGetValue<int>(out var dlv) ? dlv : (int?)null,
                    DailyTokens: lc["daily_tokens_limit"] is JsonValue dtl && dtl.TryGetValue<int>(out var dtlv) ? dtlv : (int?)null,
                    Enabled: lc["enabled"]?.GetValue<bool>() ?? true);
            }
        }

        var modelStatus = new Dictionary<string, MolaGptModelStatus>(StringComparer.Ordinal);
        if (root["model_status"] is JsonObject ms)
        {
            foreach (var (modelId, val) in ms)
            {
                if (val is not JsonObject s) continue;
                modelStatus[modelId] = new MolaGptModelStatus(
                    Available: s["available"]?.GetValue<bool>() ?? false,
                    Remaining: s["remaining"] is JsonValue r && r.TryGetValue<int>(out var rv) ? rv : (int?)null,
                    RemainingTokens: s["remaining_tokens"] is JsonValue rt && rt.TryGetValue<int>(out var rtv) ? rtv : (int?)null,
                    Reason: s["reason"]?.GetValue<string>(),
                    Message: s["message"]?.GetValue<string>(),
                    // null multiplier = the model has no price on file, which the
                    // server treats as unusable rather than free.
                    CreditMultiplier: ParseDouble(s["credit_multiplier"]),
                    CreditSymbol: s["credit_symbol"]?.GetValue<string>(),
                    PricingPeriod: s["pricing_period"]?.GetValue<string>());
            }
        }

        var security = user["account_security"];
        var permissions = security?["permissions"];
        var workModelBlocked = permissions?["blocked_model_scopes"] is JsonArray scopes
            && scopes.Any(scope => scope?.GetValue<string>() == "desktop_chat");
        return new MolaGptStatus(
            Username: username,
            Unlimited: unlimited,
            IsDonor: isDonor,
            Usage: usage,
            TokensUsage: tokensUsage,
            Limits: limits,
            ModelStatus: modelStatus,
            Credits: credits,
            PersonalizedMemoryEnabled: personalizedMemoryEnabled,
            Membership: user["membership"]?.Deserialize<AccountMembership>(),
            SecurityStatus: ReadSecurityStatus(security, desktopWork: false, workModelBlocked: workModelBlocked),
            WorkSecurityStatus: ReadSecurityStatus(security, desktopWork: true, workModelBlocked: workModelBlocked),
            CanResumeWork: permissions?["account_access"]?.GetValue<bool>() == true
                && permissions["official_models"]?.GetValue<bool>() == true
                && permissions["free_usage"]?.GetValue<bool>() == true
                && !workModelBlocked,
            ApiAccessState: user["api_access"]?["state"]?.GetValue<string>());
    }

    private static string? ReadSecurityStatus(JsonNode? security, bool desktopWork, bool workModelBlocked)
    {
        var permissions = security?["permissions"];
        var restrictions = (security?["restrictions"] as JsonArray)?.OfType<JsonObject>() ?? [];
        string? Recovery(IEnumerable<JsonObject> active, string field)
        {
            var dates = active.Select(r => r[field]?.GetValue<long>()).ToArray();
            if (dates.Length == 0 || dates.Any(t => t is null || t <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())) return null;
            return DateTimeOffset.FromUnixTimeSeconds(dates.Max()!.Value).ToLocalTime().ToString("M月d日 HH:mm");
        }
        if (permissions?["official_models"]?.GetValue<bool>() == false || (desktopWork && workModelBlocked))
        {
            var applicable = restrictions.Where(r => r["affects"] is JsonArray scopes && scopes.Any(s =>
                s?.GetValue<string>() is "account" or "official_models"
                || (desktopWork && s?.GetValue<string>() is ("desktop_chat" or "desktop_gateway"))));
            var label = permissions?["official_models"]?.GetValue<bool>() == false
                ? "官方模型服务暂时受限。" : "桌面 Work 暂时受限。";
            return label + (Recovery(applicable, "ends_at") is { } end ? $"预计于 {end} 恢复。" : "");
        }
        return permissions?["free_usage"]?.GetValue<bool>() == false
            ? "免费额度暂时受限。" + (Recovery(restrictions.Where(r => r["type"]?.GetValue<string>() == "free_cooldown"), "blocked_until")
                is { } freeEnd ? $"预计于 {freeEnd} 起逐步恢复。" : "") : null;
    }

    /// <summary>Reads a JSON number as a double. PHP emits credit values as
    /// either ints (<c>4</c>) or floats (<c>0.3</c>) depending on rounding, so
    /// neither int nor double parsing alone is enough.</summary>
    private static double? ParseDouble(JsonNode? node)
        => node is JsonValue jv && jv.TryGetValue<double>(out var d) ? d : (double?)null;

    /// <summary>Reads a JSON bool that PHP may have emitted as 0/1 or
    /// "true"/"1". Null when absent or unrecognized (older status.php builds).</summary>
    private static bool? ParseOptBool(JsonNode? node)
    {
        if (node is not JsonValue jv) return null;
        if (jv.TryGetValue<bool>(out var b)) return b;
        if (jv.TryGetValue<int>(out var i)) return i != 0;
        if (jv.TryGetValue<long>(out var l)) return l != 0;
        if (jv.TryGetValue<double>(out var d)) return d != 0;
        if (jv.TryGetValue<string>(out var s))
        {
            s = s.Trim();
            if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return null;
    }

    private static Dictionary<string, int> ParseIntDict(JsonNode? node)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (node is not JsonObject obj) return result;
        foreach (var (k, v) in obj)
        {
            if (v is JsonValue jv && jv.TryGetValue<int>(out var iv)) result[k] = iv;
            else if (v is JsonValue jv2 && jv2.TryGetValue<long>(out var lv)) result[k] = (int)Math.Min(lv, int.MaxValue);
        }
        return result;
    }

    public async Task<MolaGptPreparedAttachments> PrepareAttachmentsAsync(
        IReadOnlyList<Attachment> attachments,
        string conversationId,
        bool modelSupportsVision,
        CancellationToken ct = default)
    {
        if (attachments.Count == 0)
            return new MolaGptPreparedAttachments(Array.Empty<Attachment>(), null);

        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrEmpty(jwt))
            throw new MolaGptAuthExpiredException();

        var baseUri = new Uri(NetworkSecurity.RequireHttpsBaseUrl(BaseUrl, "MolaGPT 云服务"));
        var prepared = new List<Attachment>(attachments.Count);
        var sandboxEntries = new List<MolaGptSandboxEntry>();

        var imageIndexes = attachments
            .Select((attachment, index) => (attachment, index))
            .Where(item => item.attachment.Kind == AttachmentKind.Image
                           && modelSupportsVision
                           && IsMolaGptPublicImageSupported(item.attachment)
                           && string.IsNullOrWhiteSpace(item.attachment.RemoteUrl))
            .ToList();

        var uploadedImages = imageIndexes.Count == 0
            ? new Dictionary<int, MolaGptUploadResult>()
            : await UploadImagesBatchAsync(baseUri, jwt, imageIndexes, conversationId, ct).ConfigureAwait(false);

        for (var i = 0; i < attachments.Count; i++)
        {
            var attachment = attachments[i];
            Attachment preparedAttachment;
            if (uploadedImages.TryGetValue(i, out var imageUpload))
            {
                preparedAttachment = attachment with
                {
                    FileName = imageUpload.FileName,
                    RemoteUrl = imageUpload.Url,
                    SandboxPath = imageUpload.FilePathOnHost
                };
            }
            else if (string.IsNullOrWhiteSpace(attachment.SandboxPath))
            {
                var upload = await UploadSandboxFileAsync(baseUri, jwt, attachment, conversationId, ct).ConfigureAwait(false);
                preparedAttachment = attachment with
                {
                    FileName = upload.FileName,
                    SandboxPath = upload.FilePathOnHost
                };
            }
            else
            {
                preparedAttachment = attachment;
            }

            prepared.Add(preparedAttachment);
            if (!string.IsNullOrWhiteSpace(preparedAttachment.SandboxPath))
            {
                sandboxEntries.Add(new MolaGptSandboxEntry(
                    preparedAttachment.Kind == AttachmentKind.Image ? "图片文件" : "数据文件",
                    preparedAttachment.FileName ?? "附件",
                    preparedAttachment.SandboxPath!,
                    preparedAttachment.RemoteUrl));
            }
        }

        return new MolaGptPreparedAttachments(prepared, BuildSandboxHint(sandboxEntries));
    }

    public IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatRequest request, CancellationToken ct) =>
        StreamChatAsync(request, new MolaGptStreamDecoder(), ct);

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        ChatRequest request,
        MolaGptStreamDecoder decoder,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrEmpty(jwt))
        {
            // No token at all (logged out). Surface a friendly error rather
            // than the internal "Not logged in" InvalidOperationException.
            throw new MolaGptAuthExpiredException();
        }

        var baseUri = new Uri(NetworkSecurity.RequireHttpsBaseUrl(BaseUrl, "MolaGPT 云服务"));
        var apiUrlRel = _modelToApiUrl.TryGetValue(request.ModelId, out var url) ? url : "api/auth/chatAuto.php";

        // Pull tool flags out of ExtraBody; ComposerViewModel packs them
        // there as a single anonymous { network, steelBrowser } object so
        // the proxy API receives one stable enabled_tools object.
        object enabledTools = new { network = false, steelBrowser = false };
        if (request.ExtraBody is not null
            && request.ExtraBody.TryGetValue("enabled_tools", out var raw)
            && raw is not null)
        {
            enabledTools = raw;
        }

        var bodyDict = new Dictionary<string, object?>
        {
            ["session_id"] = request.SessionId ?? Guid.NewGuid().ToString("N"),
            ["messages"] = request.Messages.Select(m => new { role = m.Role, content = OpenAiMessageContentBuilder.Build(m) }).ToArray(),
            ["model"] = request.ModelId,
            ["stream"] = true,
            ["conversation_id"] = request.ConversationId ?? Guid.NewGuid().ToString("N"),
            ["use_thinking"] = request.UseThinking ?? false,
            ["reasoning_effort"] = request.ReasoningEffort ?? "medium",
            ["enabled_tools"] = enabledTools,
            ["client_features"] = new { subagents = 1 },
            ["mola_error_protocol"] = 1,
            ["privacy_mode"] = false
        };
        if (request.Temperature is { } temperature) bodyDict["temperature"] = temperature;

        // Unlike the BYOK providers this path keeps privacy_mode: it is MolaGPT's
        // own protocol field, already written above, and the composer's value
        // (Tracks off -> true) is meant to override the default.
        if (request.ExtraBody is not null)
            foreach (var kv in request.ExtraBody)
                if (kv.Key != InternalExtraBodyKeys.EnabledTools) bodyDict[kv.Key] = kv.Value;

        if (IsAutoRouteEndpoint(apiUrlRel, request.ModelId))
        {
            yield return new ChatChunk(Pending: new PendingStatusDelta("初始化模型", "分类用户问题", IsRoutes: true));
            var route = await ResolveAutoRouteAsync(baseUri, apiUrlRel, bodyDict, jwt, ct).ConfigureAwait(false);
            ApplyAutoRoute(bodyDict, route);
            apiUrlRel = route.ApiUrl;
            yield return new ChatChunk(Pending: new PendingStatusDelta(
                "已选择模型",
                BuildAutoRoutingPendingDetail(route.UseThinking, route.ReasoningEffort, route.EnabledTools),
                IsRoutes: true));
        }

        var fullUrl = ResolveEndpoint(baseUri, apiUrlRel, "MolaGPT 对话请求");
        request.ResolvedEndpoint?.Invoke(apiUrlRel);

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, fullUrl)
        {
            Content = JsonContent.Create(bodyDict)
        };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Conflict
            && await ChatApiErrorHelper.ReadAccountHttpErrorAsync(resp, ct).ConfigureAwait(false) is { } rejection)
        {
            if (rejection.Type != "request_already_accepted")
                throw new ChatRequestException(rejection);

            var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
            await Task.Delay(wait, ct).ConfigureAwait(false);
            await foreach (var chunk in ResumeStreamAsync((string)bodyDict["session_id"]!, decoder, apiUrlRel, ct, missingRunError: rejection)
                .ConfigureAwait(false))
                yield return chunk;
            yield break;
        }
        await ChatApiErrorHelper.EnsureSuccessAsync(resp, "MolaGPT 对话请求", ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        await foreach (var chunk in decoder.ReadAsync(stream, ct).ConfigureAwait(false))
            yield return chunk;
    }


    private async Task<Dictionary<int, MolaGptUploadResult>> UploadImagesBatchAsync(
        Uri baseUri,
        string jwt,
        IReadOnlyList<(Attachment Attachment, int Index)> images,
        string conversationId,
        CancellationToken ct)
    {
        var endpoint = ResolveEndpoint(baseUri, "api/=imgtemp/batchUpload.php", "MolaGPT 图片上传");
        var oversized = images
            .Where(item => item.Attachment.Bytes.Length > MaxPublicImageUploadBytes)
            .Select(item => $"{EnsureImageFileName(item.Attachment)} ({FormatBytes(item.Attachment.Bytes.Length)})")
            .ToList();
        if (oversized.Count > 0)
        {
            throw new InvalidOperationException(
                "图片超过 MolaGPT 公网图片上传 10MB 限制：" + string.Join("；", oversized));
        }

        var result = new Dictionary<int, MolaGptUploadResult>();
        foreach (var batch in images.Chunk(3))
        {
            using var form = new MultipartFormDataContent();
            foreach (var (attachment, _) in batch)
            {
                var content = new ByteArrayContent(attachment.Bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(attachment.MimeType);
                AddFilePart(form, content, "files[]", EnsureImageFileName(attachment));
            }
            form.Add(new StringContent(conversationId), "conversation_id");

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            using var response = await _http.SendAsync(req, ct).ConfigureAwait(false);
            await ChatApiErrorHelper.EnsureSuccessAsync(response, "MolaGPT 图片上传", ct, accountRequest: true).ConfigureAwait(false);

            var root = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("图片上传失败：服务器响应为空。");
            if (root["success"]?.GetValue<bool>() != true || root["files"] is not JsonArray files)
                throw new InvalidOperationException(BuildImageUploadErrorMessage(root, "图片上传失败。"));

            var uploadedBeforeBatch = result.Count;
            var fileNodes = files.OfType<JsonObject>().ToList();
            for (var i = 0; i < Math.Min(batch.Length, fileNodes.Count); i++)
            {
                var file = fileNodes[i];
                if (file["success"]?.GetValue<bool>() != true)
                    continue;
                var url = file["url"]?.GetValue<string>();
                var filename = file["filename"]?.GetValue<string>();
                var filePath = file["filePathOnHost"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(filename) || string.IsNullOrWhiteSpace(filePath))
                    continue;
                result[batch[i].Index] = new MolaGptUploadResult(filename!, url, filePath!);
            }

            if (result.Count - uploadedBeforeBatch != batch.Length)
                throw new InvalidOperationException(BuildImageUploadErrorMessage(root, "部分图片上传失败。"));
        }

        return result;
    }

    private async Task<MolaGptUploadResult> UploadSandboxFileAsync(
        Uri baseUri,
        string jwt,
        Attachment attachment,
        string conversationId,
        CancellationToken ct)
    {
        var endpoint = ResolveEndpoint(baseUri, "api/auth/sandboxUploader.php", "MolaGPT 沙箱文件上传");
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(attachment.Bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(attachment.MimeType);
        AddFilePart(form, content, "sandbox_file", attachment.FileName ?? "attachment");
        form.Add(new StringContent(conversationId), "conversation_id");

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        using var response = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(response, "MolaGPT 沙箱文件上传", ct, accountRequest: true).ConfigureAwait(false);

        var root = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("附件上传失败：服务器响应为空。");
        if (root["success"]?.GetValue<bool>() != true)
            throw new InvalidOperationException(root["message"]?.GetValue<string>() ?? "附件上传失败。");

        var filename = root["filename"]?.GetValue<string>();
        var filePath = root["filePathOnHost"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(filename) || string.IsNullOrWhiteSpace(filePath))
            throw new InvalidOperationException("附件上传成功但缺少沙箱路径。");

        return new MolaGptUploadResult(filename!, null, filePath!);
    }

    private static string? BuildSandboxHint(IReadOnlyList<MolaGptSandboxEntry> entries)
    {
        if (entries.Count == 0) return null;
        var lines = entries.Select((entry, index) =>
        {
            var urlInfo = string.IsNullOrWhiteSpace(entry.Url) ? string.Empty : $"\n   公网URL: {entry.Url}";
            return $"{index + 1}. {entry.TypeLabel}：{entry.FileName} → Python访问路径: {ToSandboxInputPath(entry.SandboxPath, entry.FileName)}{urlInfo}";
        });
        return "✝[系统提示: 用户已上传以下文件到沙箱：\n" + string.Join("\n", lines) + "]✝";
    }

    private static string ToSandboxInputPath(string? sandboxPath, string fileName)
    {
        var name = string.IsNullOrWhiteSpace(sandboxPath)
            ? fileName
            : sandboxPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? fileName;

        return "/input/" + name;
    }

    private static bool IsMolaGptPublicImageSupported(Attachment attachment)
    {
        var mime = attachment.MimeType.ToLowerInvariant();
        return mime is "image/png" or "image/jpeg" or "image/jpg" or "image/webp" or "image/gif";
    }

    private static string EnsureImageFileName(Attachment attachment)
    {
        var name = string.IsNullOrWhiteSpace(attachment.FileName) ? "image" : attachment.FileName!;
        if (!string.IsNullOrWhiteSpace(Path.GetExtension(name))) return name;

        var ext = attachment.MimeType.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".png"
        };
        return name + ext;
    }

    private static string BuildImageUploadErrorMessage(JsonObject root, string fallback)
    {
        var message = ReadString(root, "message") ?? fallback;
        if (root["files"] is not JsonArray files || files.Count == 0)
            return message;

        var details = files
            .OfType<JsonObject>()
            .Where(file => !IsUploadSuccess(file))
            .Select(file =>
            {
                var filename = ReadString(file, "filename") ?? "附件";
                var error = ReadString(file, "error") ?? "unknown_error";
                return $"{filename}: {DescribeUploadError(error)}";
            })
            .Where(detail => !string.IsNullOrWhiteSpace(detail))
            .ToList();

        return details.Count == 0 ? message : message + "：" + string.Join("；", details);
    }

    private static bool IsUploadSuccess(JsonObject file)
    {
        return file["success"] is JsonValue success
               && success.TryGetValue<bool>(out var ok)
               && ok;
    }

    private static string DescribeUploadError(string error) => error switch
    {
        "upload_error" => "文件上传过程中断",
        "file_too_large" => "文件超过 10MB",
        "invalid_type" => "图片类型不受支持或文件名缺少扩展名，仅支持 png/jpeg/jpg/webp/gif",
        _ => error
    };

    private static string FormatBytes(int bytes)
    {
        return bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.##} MB"
            : $"{bytes / 1024d:0.##} KB";
    }

    private static void AddFilePart(
        MultipartFormDataContent form,
        ByteArrayContent content,
        string fieldName,
        string fileName)
    {
        // Avoid MultipartFormDataContent.Add(content, name, fileName): .NET emits
        // filename*=utf-8''..., and the server-side WAF rejects that parameter.
        content.Headers.Remove("Content-Disposition");
        content.Headers.TryAddWithoutValidation(
            "Content-Disposition",
            $"form-data; name=\"{EscapeMultipartHeaderValue(fieldName)}\"; filename=\"{EscapeMultipartHeaderValue(fileName)}\"");
        form.Add(content);
    }

    private static string EscapeMultipartHeaderValue(string value)
    {
        var sanitized = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "_", StringComparison.Ordinal)
            .Replace("\n", "_", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(sanitized) ? "attachment" : sanitized;
    }

    private async Task<AutoRouteResult> ResolveAutoRouteAsync(
        Uri baseUri,
        string apiUrl,
        Dictionary<string, object?> body,
        string jwt,
        CancellationToken ct)
    {
        var url = ResolveEndpoint(baseUri, apiUrl, "MolaGPT Routes 路由请求");
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(resp, "MolaGPT Routes 路由请求", ct, accountRequest: true).ConfigureAwait(false);

        var root = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("MolaGPT Routes 未返回有效路由结果。");

        var modelName = ReadString(root, "model_name");
        var api = ReadString(root, "api_url");
        if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(api))
            throw new InvalidOperationException("MolaGPT Routes 路由结果缺少 model_name 或 api_url。");

        var controls = root["controls"] as JsonObject;
        var enabledTools = ReadEnabledTools(body.TryGetValue("enabled_tools", out var tools) ? tools : null);
        var thinkingMode = ReadString(controls, "thinking_mode");
        var useThinking = body.TryGetValue("use_thinking", out var currentThinking)
                          && currentThinking is bool currentThinkingValue
            ? currentThinkingValue
            : false;
        useThinking = thinkingMode?.ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => useThinking
        };

        var effort = ReadString(controls, "reasoning_effort")
                     ?? (body.TryGetValue("reasoning_effort", out var currentEffort) ? currentEffort as string : null)
                     ?? "medium";
        if (string.Equals(effort, "inherit", StringComparison.OrdinalIgnoreCase))
            effort = body.TryGetValue("reasoning_effort", out var inheritedEffort) ? inheritedEffort as string : "medium";

        var networkMode = ReadString(controls, "network_mode");
        if (string.Equals(networkMode, "on", StringComparison.OrdinalIgnoreCase))
            enabledTools["network"] = true;
        else if (string.Equals(networkMode, "off", StringComparison.OrdinalIgnoreCase))
            enabledTools["network"] = false;

        var steelMode = ReadString(controls, "steel_browser_mode");
        if (string.Equals(steelMode, "on", StringComparison.OrdinalIgnoreCase))
            enabledTools["steelBrowser"] = true;
        else if (string.Equals(steelMode, "off", StringComparison.OrdinalIgnoreCase))
            enabledTools["steelBrowser"] = false;

        return new AutoRouteResult(
            ModelName: modelName!,
            ModelKey: ReadString(root, "model_key"),
            DisplayName: ReadString(root, "display_name"),
            ApiUrl: api!,
            Reason: ReadString(root, "reason"),
            RouteSource: ReadString(root, "route_source"),
            RouteNote: ReadString(root, "route_note"),
            RouterModel: ReadString(root, "router_model"),
            ControlNote: ReadString(root, "control_note"),
            Confidence: TryReadDouble(root, "confidence"),
            UseThinking: useThinking,
            ReasoningEffort: effort,
            EnabledTools: enabledTools);
    }

    private static void ApplyAutoRoute(Dictionary<string, object?> body, AutoRouteResult route)
    {
        body["model"] = route.ModelName;
        if (route.UseThinking is { } useThinking)
            body["use_thinking"] = useThinking;
        if (!string.IsNullOrWhiteSpace(route.ReasoningEffort))
            body["reasoning_effort"] = route.ReasoningEffort;
        body["enabled_tools"] = route.EnabledTools;
        body["molagpt_routes"] = new Dictionary<string, object?>
        {
            ["model_key"] = route.ModelKey,
            ["model_name"] = route.ModelName,
            ["display_name"] = route.DisplayName,
            ["reason"] = route.Reason,
            ["route_source"] = route.RouteSource,
            ["confidence"] = route.Confidence,
            ["route_note"] = route.RouteNote,
            ["router_model"] = route.RouterModel,
            ["control_note"] = route.ControlNote
        };
    }

    private static Uri ResolveEndpoint(Uri baseUri, string apiUrl, string label)
    {
        var uri = Uri.TryCreate(apiUrl, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(baseUri, apiUrl);
        return NetworkSecurity.RequireMolaGptAccountOrigin(uri, label);
    }

    public static Uri RequireLocalToolsChatEndpoint(Uri uri)
    {
        NetworkSecurity.RequireMolaGptAccountOrigin(uri, "MolaGPT 本地工具服务");
        if (!string.Equals(uri.AbsolutePath, AccountApiBasePath + LocalToolsChatPath, StringComparison.Ordinal))
            throw new InvalidOperationException("MolaGPT 本地工具服务地址与官方请求路径不符。");
        return uri;
    }

    private static bool IsAutoRouteEndpoint(string apiUrl, string modelId)
    {
        return string.Equals(modelId, "autoLLM", StringComparison.OrdinalIgnoreCase)
            || string.Equals(modelId, "auto", StringComparison.OrdinalIgnoreCase)
            || apiUrl.Contains("chatAuto.php", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, bool> ReadEnabledTools(object? value)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["network"] = false,
            ["steelBrowser"] = false,
            ["code"] = true,
            ["deepResearch"] = false
        };

        try
        {
            var node = JsonSerializer.SerializeToNode(value) as JsonObject;
            if (node is null) return result;
            foreach (var key in result.Keys.ToList())
            {
                if (node[key] is JsonValue jv && jv.TryGetValue<bool>(out var enabled))
                    result[key] = enabled;
            }
        }
        catch { }

        return result;
    }

    private static string BuildAutoRoutingPendingDetail(bool? useThinking, string? reasoningEffort, IReadOnlyDictionary<string, bool> enabledTools)
    {
        var parts = new List<string>();
        if (useThinking == true)
            parts.Add("自动开启" + GetReasoningEffortPendingLabel(reasoningEffort));
        if (enabledTools.TryGetValue("network", out var network) && network
            || enabledTools.TryGetValue("steelBrowser", out var steelBrowser) && steelBrowser)
        {
            parts.Add("自动开启联网");
        }
        return string.Join("，", parts);
    }

    private static string GetReasoningEffortPendingLabel(string? effort)
    {
        return effort?.ToLowerInvariant() switch
        {
            "low" => "推理·低",
            "high" or "xhigh" => "推理·高",
            _ => "推理·中"
        };
    }

    private static string? ReadString(JsonObject? obj, string key)
    {
        if (obj?[key] is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        return null;
    }

    private static double? TryReadDouble(JsonObject obj, string key)
    {
        if (obj[key] is JsonValue value && value.TryGetValue<double>(out var number))
            return number;
        return null;
    }

    private sealed record AutoRouteResult(
        string ModelName,
        string? ModelKey,
        string? DisplayName,
        string ApiUrl,
        string? Reason,
        string? RouteSource,
        string? RouteNote,
        string? RouterModel,
        string? ControlNote,
        double? Confidence,
        bool? UseThinking,
        string? ReasoningEffort,
        IReadOnlyDictionary<string, bool> EnabledTools);

    // ─── Resume protocol ────────────────────────────────────────────────

    public async Task<StreamSessionStatus?> CheckStreamStatusAsync(string sessionId, CancellationToken ct)
    {
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrEmpty(jwt)) return null;

        var baseUri = new Uri(NetworkSecurity.RequireHttpsBaseUrl(BaseUrl, "MolaGPT 云服务"));
        var url = ResolveEndpoint(baseUri, "api/auth/check_stream_status.php", "MolaGPT 流状态检查");

        var body = new { session_ids = new[] { sessionId } };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);

        var root = doc.RootElement;
        if (!root.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Object)
        {
            if (!root.TryGetProperty("results", out sessions) || sessions.ValueKind != JsonValueKind.Object)
                return null;
        }

        if (!sessions.TryGetProperty(sessionId, out var session) || session.ValueKind != JsonValueKind.Object)
            return null;

        if (session.ValueKind == JsonValueKind.Null)
            return null;

        var status = session.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? "unknown"
            : "unknown";
        var chunksCount = session.TryGetProperty("chunks_count", out var cc) && cc.ValueKind == JsonValueKind.Number
            ? cc.GetInt32()
            : 0;
        var totalBytes = session.TryGetProperty("total_bytes", out var tb) && tb.ValueKind == JsonValueKind.Number
            ? tb.GetInt64()
            : 0L;

        return new StreamSessionStatus(status, chunksCount, totalBytes);
    }

    public async IAsyncEnumerable<ChatChunk> ResumeStreamAsync(
        string sessionId,
        MolaGptStreamDecoder decoder,
        string apiUrl,
        [EnumeratorCancellation] CancellationToken ct,
        ChatRequestError? missingRunError = null)
    {
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrEmpty(jwt))
            throw new MolaGptAuthExpiredException();

        var baseUri = new Uri(NetworkSecurity.RequireHttpsBaseUrl(BaseUrl, "MolaGPT 云服务"));
        var fullUrl = ResolveEndpoint(baseUri, apiUrl, "MolaGPT 流恢复");

        var bodyDict = new Dictionary<string, object?>
        {
            ["action"] = "resume",
            ["session_id"] = sessionId,
            ["offset"] = decoder.ResumeOffset,
            ["mola_error_protocol"] = 1
        };

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, fullUrl)
        {
            Content = JsonContent.Create(bodyDict)
        };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new ChatRequestException((await ChatApiErrorHelper.ReadAccountHttpErrorAsync(resp, ct).ConfigureAwait(false))!);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new ChatRequestException(missingRunError ?? new ChatRequestError("stream_unavailable", "暂时无法读取原有回答。"));
        await ChatApiErrorHelper.EnsureSuccessAsync(resp, "MolaGPT 流恢复", ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var chunk in decoder.ReadAsync(stream, ct).ConfigureAwait(false))
            yield return chunk;
    }


    public async Task<CompletedStreamData?> FetchCompletedStreamAsync(string sessionId, CancellationToken ct, bool cleanup = true)
    {
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrEmpty(jwt)) return null;

        var baseUri = new Uri(NetworkSecurity.RequireHttpsBaseUrl(BaseUrl, "MolaGPT 云服务"));
        var url = ResolveEndpoint(baseUri, "api/auth/fetch_completed_stream.php", "MolaGPT 完成流拉取");

        var body = new { session_id = sessionId, cleanup };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Conflict
                && await ChatApiErrorHelper.ReadAccountHttpErrorAsync(resp, ct).ConfigureAwait(false) is { } failure)
                return new CompletedStreamData(string.Empty, null, Error: failure);
            return null;
        }

        await using var responseStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (ChatApiErrorHelper.ReadBusinessError(root) is { } error)
            return new CompletedStreamData(string.Empty, null, Error: error);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success)
            || success.ValueKind != JsonValueKind.True)
            return null;

        var text = MolaGptStreamDecoder.ReadString(root, "content") ?? string.Empty;
        var sources = MolaGptStreamDecoder.ReadSources(root, "sources");
        var runStatus = MolaGptStreamDecoder.ReadString(root, "run_status");
        var terminalError = runStatus switch
        {
            "lost" => InterruptedStream(),
            "error" => new ChatRequestError("server_error", "请求暂时无法完成，请稍后重试。", Retryable: true),
            "stopped" => new ChatRequestError("stream_stopped", "回答已停止。"),
            _ => null
        };
        if (MolaGptStreamDecoder.ReadString(root, "raw_sse") is { Length: > 0 } raw)
        {
            var parsed = await ParseCompletedRawSseAsync(raw, sources, ct).ConfigureAwait(false);
            return parsed with
            {
                Text = string.IsNullOrEmpty(parsed.Text) ? text : parsed.Text,
                Error = parsed.Error ?? terminalError
                    ?? (parsed.FinishReason is null && runStatus != "completed" ? InterruptedStream() : null)
            };
        }

        terminalError ??= ChatApiErrorHelper.ReadLegacySharedQuota(text);
        return new CompletedStreamData(ChatApiErrorHelper.WithoutSharedQuotaTail(text, terminalError), sources, Error: terminalError);
    }

    private static ChatRequestError InterruptedStream() =>
        new("interrupted", "网络中断，回答没有完成。", Retryable: true);

    private static async Task<CompletedStreamData> ParseCompletedRawSseAsync(
        string rawSse,
        IReadOnlyList<SourceReference>? sources,
        CancellationToken ct)
    {
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var segments = new List<RecoveredThinkingSegment>();
        var tools = new Dictionary<string, ToolCallDelta>(StringComparer.Ordinal);
        var sequence = 0;
        JsonObject? subagents = null;
        Usage? usage = null;
        string? finishReason = null;
        ChatRequestError? error = null;

        void FinishThinking()
        {
            if (thinking.Length == 0) return;
            segments.Add(new RecoveredThinkingSegment(thinking.ToString(), text.Length, sequence++));
            thinking.Clear();
        }

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rawSse));
        try
        {
            await foreach (var chunk in new MolaGptStreamDecoder().ReadAsync(stream, ct).ConfigureAwait(false))
            {
                if (chunk.Tool is { } tool)
                {
                    if (tools.TryGetValue(tool.Id, out var previous))
                        tools[tool.Id] = tool with { ContentOffset = previous.ContentOffset, TimelineIndex = previous.TimelineIndex };
                    else
                    {
                        FinishThinking();
                        tools.Add(tool.Id, tool with { ContentOffset = text.Length, TimelineIndex = sequence++ });
                    }
                }
                thinking.Append(chunk.DeltaThinking);
                if (chunk.DeltaText is { Length: > 0 } visible)
                {
                    FinishThinking();
                    text.Append(visible);
                }
                if (chunk.Subagents is { } frame)
                    subagents = ServerSubagents.Merge(subagents, frame);
                sources = chunk.Sources ?? sources;
                usage = chunk.Usage ?? usage;
                finishReason = chunk.FinishReason ?? finishReason;
            }
        }
        catch (ChatRequestException failure) { error = failure.Error; }

        FinishThinking();
        var content = text.ToString();
        error ??= ChatApiErrorHelper.ReadLegacySharedQuota(content);
        return new CompletedStreamData(ChatApiErrorHelper.WithoutSharedQuotaTail(content, error), sources,
            ServerSubagents.Finish(subagents), error, segments, usage, finishReason, tools.Values.ToArray());
    }
}

public sealed record StreamSessionStatus(string Status, int ChunksCount, long TotalBytes);
public sealed record RecoveredThinkingSegment(string Text, int ContentOffset, int TimelineIndex);
public sealed record CompletedStreamData(string Text, IReadOnlyList<SourceReference>? Sources,
    JsonObject? Subagents = null, ChatRequestError? Error = null,
    IReadOnlyList<RecoveredThinkingSegment>? Thinking = null, Usage? Usage = null, string? FinishReason = null,
    IReadOnlyList<ToolCallDelta>? ToolCalls = null);
public sealed record MolaGptPreparedAttachments(IReadOnlyList<Attachment> Attachments, string? SystemHint);
internal sealed record MolaGptUploadResult(string FileName, string? Url, string FilePathOnHost);
internal sealed record MolaGptSandboxEntry(string TypeLabel, string FileName, string SandboxPath, string? Url);

/// <summary>
/// Snapshot of an authenticated user's quota / usage from
/// <c>api/auth/status.php</c>. Maps onto the <c>userInfo</c> +
/// <c>model_status</c> + <c>config.registered_user_limits</c> blocks used by
/// the account panel.
/// </summary>
public sealed record MolaGptStatus(
    string Username,
    bool Unlimited,
    bool IsDonor,
    IReadOnlyDictionary<string, int> Usage,
    IReadOnlyDictionary<string, int> TokensUsage,
    IReadOnlyDictionary<string, MolaGptModelLimit> Limits,
    IReadOnlyDictionary<string, MolaGptModelStatus> ModelStatus,
    MolaGptCredits? Credits = null,
    bool? PersonalizedMemoryEnabled = null,
    AccountMembership? Membership = null,
    string? SecurityStatus = null,
    string? WorkSecurityStatus = null,
    bool CanResumeWork = false,
    // Null until the server announces user-API access for the account; the API-key
    // entry points stay hidden instead of guessing at availability.
    string? ApiAccessState = null);

public sealed record MolaGptModelLimit(
    string DisplayName,
    int? DailyRequests,
    int? DailyTokens,
    bool Enabled);

/// <param name="CreditMultiplier">Credits burned per
/// <c>BaseTokensPerCredit</c> tokens. 0 = free, null = the model has no price
/// on file, which the server treats as unusable rather than free.</param>
/// <param name="CreditSymbol">Price tier as a run of <c>$</c>. Empty string =
/// free, null = unpriced. Rendered verbatim; the server owns the thresholds.</param>
/// <param name="PricingPeriod"><c>peak</c> / <c>off_peak</c> for models with
/// time-of-day pricing; the multiplier above is already the current period's.
/// Null for flat-priced models.</param>
public sealed record MolaGptModelStatus(
    bool Available,
    int? Remaining,
    int? RemainingTokens,
    string? Reason,
    string? Message,
    double? CreditMultiplier = null,
    string? CreditSymbol = null,
    string? PricingPeriod = null)
{
    /// <summary>「峰时」/「谷时」, or null when the model has one flat price.</summary>
    public string? PricingPeriodLabel => PricingPeriod switch
    {
        "peak" => "峰时",
        "off_peak" => "谷时",
        _ => null
    };
}

/// <summary>
/// MolaGPT Web Search draws from the same pool as the models but is not one, so it
/// has no entry in the per-model limits. <paramref name="Uses"/> is how many more
/// calls the balance buys (null when searches are free); <paramref name="Calls"/>
/// is what this window has already spent. Points themselves are never exposed.
/// </summary>
public sealed record MolaGptWebSearchUsage(string Name, int Calls, int? Uses);

/// <summary>
/// The shared credit pool that replaced the per-model request/token quotas.
/// Every model draws from the same balance, so switching models does not
/// restore anything — the only per-model difference is the burn rate
/// (<see cref="MolaGptModelStatus.CreditMultiplier"/>).
/// </summary>
/// <param name="Tier">guest | new | registered | donor.</param>
/// <param name="Allowance">Already multiplied out over <paramref name="WindowDays"/>,
/// so it is the whole window's budget, not one day's.</param>
/// <param name="WindowDays">Rolling settlement window. 1 = the old midnight
/// reset; above that the balance is "the last N days combined" and nothing is
/// zeroed at any point in the day.</param>
/// <param name="RecoversAt"><c>yyyy-MM-dd</c> when an exhausted balance becomes available.</param>
/// <param name="NextRefreshAt">Unix timestamp of the next change to the available balance.</param>
/// <param name="ByModelTokens">Per-model tokens over the whole window. The
/// per-model figures shown next to a "约 N 次" estimate must come from here —
/// <c>user.tokens_usage</c> only covers today and would mix two scopes.</param>
public sealed record MolaGptCredits(
    bool Enforcing,
    string Tier,
    double Used,
    double Allowance,
    double Remaining,
    int BaseTokensPerCredit,
    int WindowDays = 1,
    string? RecoversAt = null,
    IReadOnlyDictionary<string, int>? ByModelTokens = null,
    MolaGptWebSearchUsage? WebSearch = null,
    long? NextRefreshAt = null,
    string? ResetMode = null)
{
    public bool Exhausted => Remaining <= 0;

    public string? RefreshLabel(DateTimeOffset now)
    {
        if (NextRefreshAt is not { } at) return null;
        var seconds = at - now.ToUnixTimeSeconds();
        if (seconds <= 0) return null;
        var minutes = (seconds + 59) / 60;
        var days = minutes / 1440;
        var hours = minutes % 1440 / 60;
        var rest = minutes % 60;
        var action = IsFixed ? "后重置" : "后刷新";
        if (days > 0) return $"{days} 天{(hours > 0 ? $" {hours} 小时" : "")}{action}";
        if (hours > 0) return $"{hours} 小时{(rest > 0 ? $" {rest} 分钟" : "")}{action}";
        return $"{rest} 分钟{action}";
    }

    /// <summary>True once the pool settles over more than a single day, which
    /// rules out every "明日 0 点重置" phrasing.</summary>
    public bool IsFixed => ResetMode == "fixed";
    public bool IsRolling => !IsFixed && WindowDays > 1;

    /// <summary>Share of the window's allowance already spent, 0..1.</summary>
    public double UsedFraction => Allowance > 0 ? Math.Clamp(Used / Allowance, 0, 1) : 0;

    /// <summary>Percent of the allowance still available. Rounded up so that a
    /// sliver of balance never reads as 0% — only a truly empty pool shows 0.</summary>
    public int RemainingPercent =>
        Exhausted ? 0 : Math.Clamp((int)Math.Ceiling(100 - UsedFraction * 100), 1, 100);

    /// <summary>Roughly how many more turns this balance buys on a model with
    /// the given burn rate. null when the model is unpriced, int.MaxValue when
    /// it is free. Estimated off an average-length turn, same as the web panel.</summary>
    public int? EstimatedUses(double? multiplier) => multiplier switch
    {
        null => null,
        <= 0 => int.MaxValue,
        _ => (int)Math.Floor(Remaining / multiplier.Value)
    };

    /// <summary>Tier wording matches the web panel verbatim. The raw point
    /// balance is never shown to users on any surface — only this label, the
    /// remaining percentage, and the per-model "约 N 次" estimate.</summary>
    public string TierLabel => Tier switch
    {
        "premium" => "高级支持者",
        "donor" => "支持者",
        "new" => "新账号",
        "guest" => "访客",
        _ => "注册用户"
    };

    /// <summary>How the balance settles, for the line next to the tier label.</summary>
    public string WindowLabel => IsFixed ? $"{WindowDays} 天固定周期" : IsRolling ? $"近 {WindowDays} 天累计" : "明日 0 点重置";

    /// <summary>When an exhausted pool frees up again. A rolling window thaws one
    /// day at a time, so it never promises a full balance at a given moment.
    /// No trailing punctuation — callers compose it into their own sentence.</summary>
    public string RecoveryLabel
    {
        get
        {
            if (IsFixed) return DateTime.TryParseExact(RecoversAt, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var reset) ? $"{reset.Month} 月 {reset.Day} 日全部重置" : "周期结束时全部重置";
            if (!IsRolling) return "明日 0 点恢复";
            return DateTime.TryParseExact(RecoversAt, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                       DateTimeStyles.None, out var d)
                ? $"{d.Month} 月 {d.Day} 日起逐步恢复"
                : "额度会随时间逐步恢复";
        }
    }

    /// <summary>Prefix for a per-model token figure, matching <see cref="WindowLabel"/>'s scope.</summary>
    public string SpentLabel => IsFixed ? "本周期已用" : IsRolling ? $"近 {WindowDays} 天已用" : "今日已用";

    /// <summary>Tokens spent on one model over the window. Falls back to the
    /// caller's same-day figure only when the server did not send the window
    /// breakdown — on those builds the two are the same number anyway.</summary>
    public int TokensFor(string modelId, IReadOnlyDictionary<string, int> sameDayFallback)
        => ByModelTokens is { } w
            ? w.GetValueOrDefault(modelId, 0)
            : sameDayFallback.GetValueOrDefault(modelId, 0);

    /// <summary>Total tokens over the window, same fallback rule as <see cref="TokensFor"/>.</summary>
    public long TotalTokens(IReadOnlyDictionary<string, int> sameDayFallback)
        => (ByModelTokens ?? sameDayFallback).Values.Sum(v => (long)v);
}
