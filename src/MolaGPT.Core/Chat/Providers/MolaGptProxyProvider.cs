using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Models;
using MolaGPT.Core.Net;
using MolaGPT.Core.Sse;

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
/// On 401 we treat the JWT as permanently dead; clear it and throw a
/// <see cref="MolaGptAuthExpiredException"/> so the chat surface can prompt
/// the user to re-login instead of retrying a doomed request.
/// </summary>
public sealed class MolaGptProxyProvider : IChatProvider
{
    private const int MaxPublicImageUploadBytes = 10 * 1024 * 1024;

    public string Id => "molagpt-proxy";
    public string DisplayName => "MolaGPT (账号代理)";
    public ProviderKind Kind => ProviderKind.MolaGptProxy;
    public IReadOnlyList<ProviderModel> Models => _models;

    public string BaseUrl { get; init; } = "https://chatgpt.wljay.cn/v2/";
    public IReadOnlyDictionary<string, string> ModelToApiUrl => _modelToApiUrl;
    public string? LastResolvedApiUrl { get; private set; }

    private readonly HttpClient _http;
    private readonly MolaGptAuthService _auth;
    private List<ProviderModel> _models = new();
    private Dictionary<string, string> _modelToApiUrl = new();

    public MolaGptProxyProvider(HttpClient http, MolaGptAuthService auth)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
    }

    public async Task RefreshModelsAsync(CancellationToken ct = default)
    {
        var baseUri = new Uri(NetworkSecurity.RequireHttpsBaseUrl(BaseUrl, "MolaGPT 云服务"));
        var url = NetworkSecurity.RequireHttps(new Uri(baseUri, "api/auth/model_config_public.php"), "MolaGPT 模型列表");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _auth.Logout();
            throw new MolaGptAuthExpiredException();
        }
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
                SupportsToolCalling: true));
            apiMap[modelName] = apiUrl;
        }

        _models = list;
        _modelToApiUrl = apiMap;
    }

    /// <summary>
    /// Fetch the user's account state from <c>api/auth/status.php</c> (the
    /// account status endpoint. Returns null when no JWT
    /// is stored; throws <see cref="MolaGptAuthExpiredException"/> on 401
    /// so the caller can re-prompt login. The response shape includes:
    /// <code>
    ///   { user: { logged_in, username, type, unlimited, is_donor,
    ///             usage:{model_id:int}, tokens_usage:{model_id:int},
    ///             limits:{model_id:{daily_limit, daily_tokens_limit, display_name}}},
    ///     model_status:{model_id:{available, remaining, remaining_tokens,...}},
    ///     config:{registered_user_limits:{...}} }
    /// </code>
    /// </summary>
    public async Task<MolaGptStatus?> FetchStatusAsync(CancellationToken ct = default)
    {
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrEmpty(jwt)) return null;

        var baseUri = new Uri(NetworkSecurity.RequireHttpsBaseUrl(BaseUrl, "MolaGPT 云服务"));
        var url = NetworkSecurity.RequireHttps(new Uri(baseUri, "api/auth/status.php"), "MolaGPT 账号状态");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _auth.Logout();
            throw new MolaGptAuthExpiredException();
        }
        resp.EnsureSuccessStatusCode();

        var root = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false);
        if (root is null) return null;

        var user = root["user"]?.AsObject();
        if (user is null) return null;

        bool loggedIn = user["logged_in"]?.GetValue<bool>() ?? false;
        if (!loggedIn) return null;

        var username = user["username"]?.GetValue<string>() ?? "";
        var unlimited = user["unlimited"]?.GetValue<bool>() ?? false;
        var isDonor = user["is_donor"]?.GetValue<bool>() ?? false;
        var usage = ParseIntDict(user["usage"]);
        var tokensUsage = ParseIntDict(user["tokens_usage"]);

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
                    Message: s["message"]?.GetValue<string>());
            }
        }

        return new MolaGptStatus(
            Username: username,
            Unlimited: unlimited,
            IsDonor: isDonor,
            Usage: usage,
            TokensUsage: tokensUsage,
            Limits: limits,
            ModelStatus: modelStatus);
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
            : await UploadImagesBatchAsync(baseUri, imageIndexes, conversationId, ct).ConfigureAwait(false);

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
                    preparedAttachment.RemoteUrl));
            }
        }

        return new MolaGptPreparedAttachments(prepared, BuildSandboxHint(sandboxEntries));
    }

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        ChatRequest request,
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
            ["temperature"] = request.Temperature ?? 0.7,
            ["model"] = request.ModelId,
            ["stream"] = true,
            ["conversation_id"] = request.ConversationId ?? Guid.NewGuid().ToString("N"),
            ["use_thinking"] = request.UseThinking ?? false,
            ["reasoning_effort"] = request.ReasoningEffort ?? "medium",
            ["enabled_tools"] = enabledTools,
            ["privacy_mode"] = false
        };
        if (request.ExtraBody is not null)
            foreach (var kv in request.ExtraBody)
                if (kv.Key != "enabled_tools") bodyDict[kv.Key] = kv.Value;

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
        LastResolvedApiUrl = apiUrlRel;

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, fullUrl)
        {
            Content = JsonContent.Create(bodyDict)
        };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            // JWT was rejected; almost always a UA-binding
            // mismatch (the UA hashed into the token differs from the UA we
            // just sent). Dropping the token forces the user to re-login,
            // which will also re-issue the UA hash for the new value.
            _auth.Logout();
            throw new MolaGptAuthExpiredException();
        }
        await ChatApiErrorHelper.EnsureSuccessAsync(resp, "MolaGPT 对话请求", ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        // Per-stream <think>...</think> splitter; needed for models like
        // DeepSeek-R1 / QwQ that emit reasoning inline in delta.content
        // instead of via the dedicated reasoning_content channel. Allocated
        // once per request because state must persist across SSE chunks.
        var thinkSplitter = new InlineThinkSplitter();
        var toolSynthesizer = new ToolCallContentSynthesizer();
        var responseMapper = new ResponseStreamEventMapper();

        await foreach (var ev in SseStreamReader.ReadAsync(stream, ct))
        {
            if (ev.IsDone)
            {
                var toolTail = toolSynthesizer.FinalizeOpenBlocks();
                if (!string.IsNullOrEmpty(toolTail))
                    yield return new ChatChunk(DeltaText: toolTail);
                // Surface any trailing buffered bytes from the splitter so we
                // don't lose content if the model never closed a <think>.
                var tail = thinkSplitter.Flush();
                if (!string.IsNullOrEmpty(tail.Visible) || !string.IsNullOrEmpty(tail.Thinking))
                    yield return new ChatChunk(
                        DeltaText: string.IsNullOrEmpty(tail.Visible) ? null : tail.Visible,
                        DeltaThinking: string.IsNullOrEmpty(tail.Thinking) ? null : tail.Thinking,
                        FinishReason: "stop");
                yield break;
            }
            if (string.IsNullOrEmpty(ev.Data)) continue;

            ChatChunk? chunk = null;
            try
            {
                using var doc = JsonDocument.Parse(ev.Data);
                var root = doc.RootElement;
                if (ChatApiErrorHelper.TryExtractStreamingError(root, out var streamError))
                    throw new InvalidOperationException(streamError);
                if (TryParseSources(root, out var sources))
                {
                    chunk = new ChatChunk(Sources: sources, RawJson: ev.Data);
                }
                if (responseMapper.TryMap(root, out var responseText, out var responseThinking))
                {
                    if (!string.IsNullOrEmpty(responseText))
                    {
                        var split = thinkSplitter.Feed(responseText);
                        responseText = string.IsNullOrEmpty(split.Visible) ? null : split.Visible;
                        if (!string.IsNullOrEmpty(split.Thinking))
                            responseThinking = string.IsNullOrEmpty(responseThinking) ? split.Thinking : responseThinking + split.Thinking;
                    }

                    chunk = string.IsNullOrEmpty(responseText) && string.IsNullOrEmpty(responseThinking)
                        ? null
                        : new ChatChunk(DeltaText: responseText, DeltaThinking: responseThinking, RawJson: ev.Data);
                }
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        var toolText = toolSynthesizer.HandleToolCalls(delta);
                        if (toolText is not null)
                        {
                            chunk = string.IsNullOrEmpty(toolText) ? null : new ChatChunk(DeltaText: toolText, RawJson: ev.Data);
                        }
                        else
                        {
                            string? text = ExtractContentText(delta);
                            // Aggregate all supported reasoning channels with
                            // cross-channel de-duplication.
                            string? thinking = ReasoningExtractor.Extract(delta);
                            string? finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                            var finalizeToolUi = finish == "tool_calls" || (toolSynthesizer.HasOpenBlocks && (!string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(thinking)));
                            if (finalizeToolUi)
                            {
                                var toolTail = toolSynthesizer.FinalizeOpenBlocks();
                                if (!string.IsNullOrEmpty(toolTail))
                                    text = string.IsNullOrEmpty(text) ? toolTail : toolTail + text;
                            }

                            // Run the visible text through the inline <think>
                            // splitter. Any thinking bytes it pulls out are
                            // merged with the structured reasoning channels.
                            if (!string.IsNullOrEmpty(text))
                            {
                                var split = thinkSplitter.Feed(text!);
                                text = string.IsNullOrEmpty(split.Visible) ? null : split.Visible;
                                if (!string.IsNullOrEmpty(split.Thinking))
                                    thinking = string.IsNullOrEmpty(thinking) ? split.Thinking : thinking + split.Thinking;
                            }

                            chunk = new ChatChunk(DeltaText: text, DeltaThinking: thinking, FinishReason: finish, Usage: TryParseUsage(root), RawJson: ev.Data);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                chunk = new ChatChunk(RawJson: ev.Data);
            }
            if (chunk is not null) yield return chunk;
        }
    }

    private async Task<Dictionary<int, MolaGptUploadResult>> UploadImagesBatchAsync(
        Uri baseUri,
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

        using var form = new MultipartFormDataContent();
        foreach (var (attachment, _) in images)
        {
            var content = new ByteArrayContent(attachment.Bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(attachment.MimeType);
            AddFilePart(form, content, "files[]", EnsureImageFileName(attachment));
        }
        form.Add(new StringContent(conversationId), "conversation_id");

        using var response = await _http.PostAsync(endpoint, form, ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(response, "MolaGPT 图片上传", ct).ConfigureAwait(false);

        var root = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("图片上传失败：服务器响应为空。");
        if (root["success"]?.GetValue<bool>() != true || root["files"] is not JsonArray files)
            throw new InvalidOperationException(BuildImageUploadErrorMessage(root, "图片上传失败。"));

        var result = new Dictionary<int, MolaGptUploadResult>();
        var fileNodes = files.OfType<JsonObject>().ToList();
        for (var i = 0; i < Math.Min(images.Count, fileNodes.Count); i++)
        {
            var file = fileNodes[i];
            if (file["success"]?.GetValue<bool>() != true)
                continue;
            var url = file["url"]?.GetValue<string>();
            var filename = file["filename"]?.GetValue<string>();
            var filePath = file["filePathOnHost"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(filename) || string.IsNullOrWhiteSpace(filePath))
                continue;
            result[images[i].Index] = new MolaGptUploadResult(filename!, url, filePath!);
        }

        if (result.Count != images.Count)
            throw new InvalidOperationException(BuildImageUploadErrorMessage(root, "部分图片上传失败。"));

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
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _auth.Logout();
            throw new MolaGptAuthExpiredException();
        }
        await ChatApiErrorHelper.EnsureSuccessAsync(response, "MolaGPT 沙箱文件上传", ct).ConfigureAwait(false);

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
            return $"{index + 1}. {entry.TypeLabel}：{entry.FileName} → Python访问路径: /input/{entry.FileName}{urlInfo}";
        });
        return "✝[系统提示: 用户已上传以下文件到沙箱：\n" + string.Join("\n", lines) + "]✝";
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
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _auth.Logout();
            throw new MolaGptAuthExpiredException();
        }
        await ChatApiErrorHelper.EnsureSuccessAsync(resp, "MolaGPT Routes 路由请求", ct).ConfigureAwait(false);

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
        return NetworkSecurity.RequireHttps(uri, label);
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

    private static string? ExtractContentText(JsonElement delta)
    {
        if (!delta.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return content.ToString();

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                parts.Add(item.GetString() ?? string.Empty);
            else if (item.ValueKind != JsonValueKind.Null && item.ValueKind != JsonValueKind.Undefined)
                parts.Add(item.ToString());
        }

        return parts.Count == 0 ? null : string.Concat(parts);
    }

    private static Usage? TryParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        return new Usage(
            TryGetInt(usage, "prompt_tokens"),
            TryGetInt(usage, "completion_tokens"),
            TryGetInt(usage, "total_tokens"));
    }

    private static int? TryGetInt(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static bool TryParseSources(JsonElement root, out IReadOnlyList<SourceReference> sources)
    {
        sources = Array.Empty<SourceReference>();
        if (!root.TryGetProperty("molagpt_sources", out var node) || node.ValueKind != JsonValueKind.Array)
            return false;

        var list = new List<SourceReference>();
        var fallbackId = 1;
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetInt(item, "id") ?? fallbackId;
            var title = item.TryGetProperty("title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String
                ? titleNode.GetString() ?? string.Empty
                : string.Empty;
            var url = item.TryGetProperty("url", out var urlNode) && urlNode.ValueKind == JsonValueKind.String
                ? urlNode.GetString() ?? string.Empty
                : string.Empty;
            list.Add(new SourceReference(id, title, url));
            fallbackId++;
        }

        sources = list;
        return list.Count > 0;
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
            return null;

        if (!sessions.TryGetProperty(sessionId, out var session) || session.ValueKind != JsonValueKind.Object)
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
        int offset,
        string apiUrl,
        [EnumeratorCancellation] CancellationToken ct)
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
            ["offset"] = offset
        };

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, fullUrl)
        {
            Content = JsonContent.Create(bodyDict)
        };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _auth.Logout();
            throw new MolaGptAuthExpiredException();
        }
        await ChatApiErrorHelper.EnsureSuccessAsync(resp, "MolaGPT 流恢复", ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var thinkSplitter = new InlineThinkSplitter();
        var toolSynthesizer = new ToolCallContentSynthesizer();
        var responseMapper = new ResponseStreamEventMapper();

        await foreach (var ev in SseStreamReader.ReadAsync(stream, ct))
        {
            if (ev.IsDone)
            {
                var toolTail = toolSynthesizer.FinalizeOpenBlocks();
                if (!string.IsNullOrEmpty(toolTail))
                    yield return new ChatChunk(DeltaText: toolTail);
                var tail = thinkSplitter.Flush();
                if (!string.IsNullOrEmpty(tail.Visible) || !string.IsNullOrEmpty(tail.Thinking))
                    yield return new ChatChunk(
                        DeltaText: string.IsNullOrEmpty(tail.Visible) ? null : tail.Visible,
                        DeltaThinking: string.IsNullOrEmpty(tail.Thinking) ? null : tail.Thinking,
                        FinishReason: "stop");
                yield break;
            }
            if (string.IsNullOrEmpty(ev.Data)) continue;

            ChatChunk? chunk = null;
            try
            {
                using var doc = JsonDocument.Parse(ev.Data);
                var root = doc.RootElement;
                if (ChatApiErrorHelper.TryExtractStreamingError(root, out var streamError))
                    throw new InvalidOperationException(streamError);
                if (TryParseSources(root, out var sources))
                    chunk = new ChatChunk(Sources: sources, RawJson: ev.Data);
                if (responseMapper.TryMap(root, out var responseText, out var responseThinking))
                {
                    if (!string.IsNullOrEmpty(responseText))
                    {
                        var split = thinkSplitter.Feed(responseText);
                        responseText = string.IsNullOrEmpty(split.Visible) ? null : split.Visible;
                        if (!string.IsNullOrEmpty(split.Thinking))
                            responseThinking = string.IsNullOrEmpty(responseThinking) ? split.Thinking : responseThinking + split.Thinking;
                    }
                    chunk = string.IsNullOrEmpty(responseText) && string.IsNullOrEmpty(responseThinking)
                        ? null
                        : new ChatChunk(DeltaText: responseText, DeltaThinking: responseThinking, RawJson: ev.Data);
                }
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        var toolText = toolSynthesizer.HandleToolCalls(delta);
                        if (toolText is not null)
                        {
                            chunk = string.IsNullOrEmpty(toolText) ? null : new ChatChunk(DeltaText: toolText, RawJson: ev.Data);
                        }
                        else
                        {
                            string? text = ExtractContentText(delta);
                            string? thinking = ReasoningExtractor.Extract(delta);
                            string? finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                            var finalizeToolUi = finish == "tool_calls" || (toolSynthesizer.HasOpenBlocks && (!string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(thinking)));
                            if (finalizeToolUi)
                            {
                                var toolTailInner = toolSynthesizer.FinalizeOpenBlocks();
                                if (!string.IsNullOrEmpty(toolTailInner))
                                    text = string.IsNullOrEmpty(text) ? toolTailInner : toolTailInner + text;
                            }
                            if (!string.IsNullOrEmpty(text))
                            {
                                var split = thinkSplitter.Feed(text!);
                                text = string.IsNullOrEmpty(split.Visible) ? null : split.Visible;
                                if (!string.IsNullOrEmpty(split.Thinking))
                                    thinking = string.IsNullOrEmpty(thinking) ? split.Thinking : thinking + split.Thinking;
                            }
                            chunk = new ChatChunk(DeltaText: text, DeltaThinking: thinking, FinishReason: finish, Usage: TryParseUsage(root), RawJson: ev.Data);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                chunk = new ChatChunk(RawJson: ev.Data);
            }
            if (chunk is not null) yield return chunk;
        }
    }

    public async Task<CompletedStreamData?> FetchCompletedStreamAsync(string sessionId, CancellationToken ct)
    {
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrEmpty(jwt)) return null;

        var baseUri = new Uri(NetworkSecurity.RequireHttpsBaseUrl(BaseUrl, "MolaGPT 云服务"));
        var url = ResolveEndpoint(baseUri, "api/auth/fetch_completed_stream.php", "MolaGPT 完成流拉取");

        var body = new { session_id = sessionId, cleanup = true };
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
        if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
            return null;

        var text = root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? string.Empty
            : string.Empty;

        List<SourceReference>? sources = null;
        if (root.TryGetProperty("sources", out var srcNode) && srcNode.ValueKind == JsonValueKind.Array)
        {
            sources = new List<SourceReference>();
            var fallbackId = 1;
            foreach (var item in srcNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = TryGetInt(item, "id") ?? fallbackId;
                var title = item.TryGetProperty("title", out var tn) && tn.ValueKind == JsonValueKind.String
                    ? tn.GetString() ?? string.Empty : string.Empty;
                var srcUrl = item.TryGetProperty("url", out var un) && un.ValueKind == JsonValueKind.String
                    ? un.GetString() ?? string.Empty : string.Empty;
                sources.Add(new SourceReference(id, title, srcUrl));
                fallbackId++;
            }
        }

        return new CompletedStreamData(text, sources);
    }
}

public sealed record StreamSessionStatus(string Status, int ChunksCount, long TotalBytes);
public sealed record CompletedStreamData(string Text, IReadOnlyList<SourceReference>? Sources);
public sealed record MolaGptPreparedAttachments(IReadOnlyList<Attachment> Attachments, string? SystemHint);
internal sealed record MolaGptUploadResult(string FileName, string? Url, string FilePathOnHost);
internal sealed record MolaGptSandboxEntry(string TypeLabel, string FileName, string? Url);

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
    IReadOnlyDictionary<string, MolaGptModelStatus> ModelStatus);

public sealed record MolaGptModelLimit(
    string DisplayName,
    int? DailyRequests,
    int? DailyTokens,
    bool Enabled);

public sealed record MolaGptModelStatus(
    bool Available,
    int? Remaining,
    int? RemainingTokens,
    string? Reason,
    string? Message);
