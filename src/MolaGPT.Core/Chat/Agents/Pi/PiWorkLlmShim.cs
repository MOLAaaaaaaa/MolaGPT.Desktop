using System.Collections.Concurrent;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.Text;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Models;
using MolaGPT.Core.Net;
using MolaGPT.Core.Sse;

namespace MolaGPT.Core.Chat.Agents.Pi;

/// <summary>
/// Loopback shim that lets Pi's stock OpenAI client reach MolaGPT's account-quota
/// billing endpoint. Solves two mismatches surfaced when wiring Work onto Pi:
///
///  1. <b>Path</b>: Pi uses the official OpenAI SDK, which always POSTs to
///     <c>{baseUrl}/chat/completions</c>. The MolaGPT relay is a single file
///     (<c>desktop_chat_completions.php</c>). The shim exposes a clean
///     <c>/v1/chat/completions</c> to Pi and forwards to the real relay URL.
///  2. <b>Rotating JWT</b>: the sidecar is long-lived but the account token
///     rotates. The shim injects the <em>current</em> token per request, so the
///     token is never baked into the Node process env (which also means no
///     credential ever lives inside the sidecar — a small security win).
///
/// Applies request settings, injects auth, and relays the response byte-for-byte.
/// An initial image-capability error permits one retry with image placeholders.
/// </summary>
public sealed class PiWorkLlmShim : IDisposable
{
    // Inspect the same SSE bytes that are relayed to Pi. Never alter the wire stream.
    private sealed class WorkStreamErrorObserver(Action<ChatRequestError?>? report)
    {
        private readonly SseEventParser _parser = new(64 * 1024, 64 * 1024, skipOversized: true);
        private bool _reported;
        public bool HasError => _reported;
        private StringBuilder? _legacyCandidate = new();

        public void Complete()
        {
            if (_reported) return;
            if (_parser.Complete() is { } last) Observe(last);
            if (!_reported && ChatApiErrorHelper.ReadLegacySharedQuota(_legacyCandidate?.ToString()) is { } error)
                Report(error);
        }

        private void Report(ChatRequestError error)
        {
            if (_reported) return;
            _reported = true;
            report?.Invoke(error);
        }

        private void ObserveLegacyText(JsonElement root)
        {
            if (_legacyCandidate is null || root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0 || choices[0].ValueKind != JsonValueKind.Object
                || !choices[0].TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                return;
            if (delta.TryGetProperty("tool_calls", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
            {
                _legacyCandidate = null;
                return;
            }
            if (!delta.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return;
            var part = content.GetString() ?? "";
            var notice = ChatApiErrorHelper.LegacySharedQuotaNotice;
            if (_legacyCandidate.Length + part.Length > notice.Length + 64)
            {
                _legacyCandidate = null;
                return;
            }
            _legacyCandidate.Append(part);
            var probe = _legacyCandidate.ToString().TrimStart();
            if (!notice.StartsWith(probe, StringComparison.Ordinal) && !string.Equals(probe.TrimEnd(), notice, StringComparison.Ordinal))
                _legacyCandidate = null;
        }

        public void Feed(ReadOnlyMemory<byte> bytes)
        {
            if (_reported || report is null) return;
            foreach (var item in _parser.Feed(bytes))
            {
                Observe(item);
                if (_reported) return;
            }
        }

        private void Observe(SsePayload item)
        {
            var data = item.Data.Trim();
            if (data.Length == 0) return;
            if (data == "[DONE]")
            {
                if (ChatApiErrorHelper.ReadLegacySharedQuota(_legacyCandidate?.ToString()) is { } legacy)
                    Report(legacy);
                return;
            }
            ChatRequestError? error;
            try
            {
                using var document = JsonDocument.Parse(data);
                error = ChatApiErrorHelper.ReadBusinessError(document.RootElement, item.EventName);
                if (error is null) ObserveLegacyText(document.RootElement);
            }
            catch (JsonException)
            {
                error = item.EventName is "error" or "mola_error" or "response.failed"
                    ? ChatApiErrorHelper.ReadProviderError(data) : null;
            }
            if (error is not null) Report(error);
        }
    }

    /// <summary>Where a turn's LLM traffic should actually go, resolved per turn.</summary>
    /// <param name="Endpoint">Absolute URL of the real chat-completions endpoint
    /// (account relay <c>.php</c>, or a BYOK provider's chat endpoint).</param>
    /// <param name="TokenProvider">Returns the bearer token to stamp on each
    /// request — the live account JWT, or a BYOK key. Null token → no auth header.</param>
    /// <param name="OnUnauthorized">Invoked when the endpoint rejects the token
    /// (401), so an expired account session still triggers the normal logout —
    /// parity with the direct provider's UnauthorizedHandler.</param>
    /// <param name="Headers">Provider-configured extra headers. Some endpoints do
    /// not work without them, so they have to survive the trip through Pi.</param>
    /// <param name="ExtraBody">Model-level custom request parameters, merged into
    /// the outgoing body exactly as the direct provider merges them.</param>
    /// <param name="Auth">How the endpoint expects the credential. Anthropic takes
    /// <c>x-api-key</c> plus a version header rather than a bearer.</param>
    /// <param name="DropBodyKeys">Fields this endpoint rejects. Pi builds every
    /// request to the plain OpenAI dialect because the shim hides the real provider
    /// from its compatibility detection; see <see cref="PiEndpointQuirks"/>.</param>
    /// <param name="PathMode">How <paramref name="Endpoint"/> relates to the path Pi
    /// asked for. See <see cref="TargetPathMode"/>.</param>
    /// <param name="ResponseReceived">Sees each upstream response before its body
    /// is relayed, for figures an endpoint reports in headers.</param>
    /// <param name="RelayTap">Sees the response body as it is relayed, chunk by
    /// chunk. Used for figures that only exist inside the stream — DeepSeek's
    /// server-side search reports its sources that way, and there is no second
    /// channel to wait for.</param>
    public sealed record ForwardTarget(
        HttpClient Http,
        string Endpoint,
        Func<CancellationToken, Task<string?>> TokenProvider,
        Action<string?>? OnUnauthorized = null,
        IReadOnlyList<KeyValuePair<string, string>>? Headers = null,
        IReadOnlyDictionary<string, JsonElement>? ExtraBody = null,
        AuthStyle Auth = AuthStyle.Bearer,
        IReadOnlyList<string>? DropBodyKeys = null,
        TargetPathMode PathMode = TargetPathMode.Fixed,
        GenerationOptions? Generation = null,
        Action<string>? PromptPrepared = null,
        Action<System.Text.Json.Nodes.JsonObject>? AdaptBody = null,
        Action<HttpResponseMessage>? ResponseReceived = null,
        string? ModelId = null,
        Action<ReadOnlyMemory<byte>>? RelayTap = null,
        CancellationToken TurnCancellation = default,
        IProgress<PendingStatusDelta>? WorkStatus = null,
        Action<ChatRequestError?>? WorkError = null,
        Func<ChatRequestError?>? ReadWorkError = null,
        ThinkingConfig? GeminiThinking = null,
        string? ProviderId = null);

    public sealed record GenerationOptions(
        string Api, double? Temperature, double? TopP, int? MaxTokens,
        string? ReasoningEffort = null, bool? UseThinking = null);

    /// <summary>Thrown by a <see cref="ForwardTarget.TokenProvider"/> that has no
    /// credential to give — signed out, or a session that has lapsed. The message
    /// is what the user reads, so it goes back as is rather than as a relay
    /// failure.</summary>
    public sealed class CredentialUnavailableException(string message) : Exception(message);

    public enum TargetPathMode
    {
        /// <summary>Every request goes to <c>Endpoint</c> verbatim. Right for the
        /// shapes where the operation lives in the body — chat/completions,
        /// messages, responses — and the user may have configured a non-standard
        /// path we must not second-guess.</summary>
        Fixed,

        /// <summary><c>Endpoint</c> is a Gemini URL template. The model from Pi's
        /// request replaces <c>{model}</c>; its query string carries <c>alt=sse</c>.</summary>
        GeminiModelTemplate,
    }

    public enum AuthStyle
    {
        /// <summary>OpenAI-compatible: <c>Authorization: Bearer …</c>.</summary>
        Bearer,

        /// <summary>Anthropic: <c>x-api-key</c> + <c>anthropic-version</c>.</summary>
        AnthropicApiKey,

        /// <summary>Google generative-language: <c>x-goog-api-key</c>. Sending a
        /// bearer instead is not a soft failure — the endpoint answers 401
        /// <c>ACCESS_TOKEN_TYPE_UNSUPPORTED</c>, because it reads a bearer as an
        /// OAuth token rather than an API key.</summary>
        GoogleApiKey,
    }

    private const string AnthropicVersion = "2023-06-01";

    /// <summary>System.Text.Json escapes every non-ASCII character by default, which
    /// would rewrite a Chinese prompt into <c>\uXXXX</c> escapes and roughly double
    /// the bytes on every request. The relaxed encoder keeps the text as UTF-8, the
    /// way it arrived.</summary>
    private static readonly JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpListener _listener = new();
    private const int MaxRequestBytes = 128 * 1024 * 1024;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string>? _log;

    /// <summary>
    /// Per-sidecar routing table, keyed by the throwaway credential that sidecar
    /// was launched with.
    ///
    /// A single mutable target would have been enough while one turn ran at a time,
    /// but the sidecar pool runs several — and a global "current target" is exactly
    /// how one conversation's traffic would end up billed to another's endpoint.
    /// The sidecar's own token is the natural key: it is already unique per process
    /// and already on every request.
    /// </summary>
    private readonly ConcurrentDictionary<string, ForwardTarget> _targets = new(StringComparer.Ordinal);

    /// <summary>Path prefix the sidecar's client sees. Anything after it is the
    /// client's own construction, which <see cref="TargetPathMode.GeminiModelTemplate"/>
    /// uses to resolve the real model endpoint.</summary>
    private const string BasePath = "/v1";

    /// <summary>Base URL to hand Pi (it appends <c>/chat/completions</c> and friends).</summary>
    public string BaseUrl { get; }

    public PiWorkLlmShim(Action<string>? log = null, int maxConcurrent = PiRuntime.DefaultMaxSidecars)
    {
        _log = log;
        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        var port = FreeTcpPort();
        BaseUrl = $"http://127.0.0.1:{port}{BasePath}";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Mint a credential for a new sidecar. Any other local process that
    /// finds the port cannot spend the user's quota: a request whose token is not
    /// in the routing table is rejected before anything is forwarded. The sidecar
    /// never sees a real credential — only this throwaway.</summary>
    public static string NewSidecarToken() => Guid.NewGuid().ToString("N");

    /// <summary>Point one sidecar's traffic at an endpoint for the turn it is about
    /// to run. Pass null to unregister when the sidecar goes away.</summary>
    public void SetTarget(string sidecarToken, ForwardTarget? target)
    {
        if (target is null) _targets.TryRemove(sidecarToken, out _);
        else _targets[sidecarToken] = target;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }
            var token = PresentedToken(ctx.Request);
            var target = token is null ? null : _targets.GetValueOrDefault(token);
            if (target is not null && _slots.Wait(0))
            {
                _ = Task.Run(() => HandleAsync(ctx, target));
                continue;
            }
            try
            {
                if (target is null) await WriteError(ctx, 403, "bad token").ConfigureAwait(false);
                else
                {
                    ctx.Response.Headers["Retry-After"] = "1";
                    await WriteError(ctx, 503, "shim at capacity").ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException) { }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, ForwardTarget target)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var operationKey = Guid.NewGuid().ToString("N");
        var requestId = operationKey[..8];
        var outcome = "failed";
        _log?.Invoke($"[llm-shim] request={requestId} started");
        try
        {
            var url = ResolveUpstreamUrl(target, ctx.Request.Url);
            if (url is null)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            var official = string.Equals(target.ProviderId, MolaGptProviderIds.LocalTools, StringComparison.OrdinalIgnoreCase);
            if (official)
                MolaGptProxyProvider.RequireLocalToolsChatEndpoint(new Uri(url, UriKind.Absolute));

            using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, target.TurnCancellation);
            var ct = requestLifetime.Token;
            string body;
            try
            {
                if (ctx.Request.ContentLength64 > MaxRequestBytes) throw new InvalidDataException();
                var bytes = await BoundedRead.ReadAsync(ctx.Request.InputStream, MaxRequestBytes, ct).ConfigureAwait(false);
                using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8);
                body = reader.ReadToEnd();
            }
            catch (InvalidDataException)
            {
                await WriteError(ctx, 413, "request exceeds 128 MiB").ConfigureAwait(false);
                return;
            }
            var preparedBody = RewriteBody(body, target);
            if (target.AdaptBody is { } adapt
                && System.Text.Json.Nodes.JsonNode.Parse(preparedBody) is System.Text.Json.Nodes.JsonObject adapted)
            {
                adapt(adapted);
                preparedBody = adapted.ToJsonString(RelaxedJson);
            }
            var retriedConcurrency = false;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                // Pi may retry an SSE failure as another HTTP request. The first
                // terminal error belongs to this turn and must not spend more quota.
                if (official && target.ReadWorkError?.Invoke() is { } priorError)
                {
                    outcome = "work_error_latched";
                    await WriteError(ctx, 409, priorError.Message).ConfigureAwait(false);
                    return;
                }
                var attemptStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                target.PromptPrepared?.Invoke(preparedBody);
                using var upstream = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(preparedBody, Encoding.UTF8, "application/json"),
                };
                if (official) upstream.Headers.TryAddWithoutValidation("Idempotency-Key", operationKey);
                var token = await target.TokenProvider(ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(token))
                {
                    switch (target.Auth)
                    {
                        case AuthStyle.AnthropicApiKey:
                            upstream.Headers.TryAddWithoutValidation("x-api-key", token);
                            upstream.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
                            break;
                        case AuthStyle.GoogleApiKey:
                            upstream.Headers.TryAddWithoutValidation("x-goog-api-key", token);
                            break;
                        default:
                            upstream.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                            break;
                    }
                }
                upstream.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                OpenRouterAttribution.Apply(upstream, target.Endpoint, target.Headers);
                CustomRequestParams.ApplyHeaders(upstream, target.Headers);
                if (string.Equals(url, ChatGptAccount.CodexEndpoint, StringComparison.OrdinalIgnoreCase)
                    && !upstream.Headers.Contains("session-id")
                    && ctx.Request.Headers["session_id"] is { Length: > 0 } sessionId)
                    upstream.Headers.TryAddWithoutValidation("session-id", sessionId);

                using var resp = await target.Http
                    .SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                _log?.Invoke($"[llm-shim] request={requestId} attempt={attempt + 1} HTTP={(int)resp.StatusCode} headers_ms={System.Diagnostics.Stopwatch.GetElapsedTime(attemptStarted).TotalMilliseconds:F0}");

                ChatRequestError? workError = null;
                if (official && !resp.IsSuccessStatusCode && resp.Content.Headers.ContentType?.MediaType == "application/json")
                {
                    var error = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    try
                    {
                        using var json = JsonDocument.Parse(error);
                        workError = ChatApiErrorHelper.ReadBusinessError(json.RootElement, httpStatus: (int)resp.StatusCode, accountRequest: true);
                    }
                    catch (JsonException) { }

                    if (!retriedConcurrency && resp.StatusCode == HttpStatusCode.TooManyRequests
                        && workError is { Type: "concurrency_limit", Retryable: true, RetryAfter: > 0 and <= 30 })
                    {
                        retriedConcurrency = true;
                        target.WorkStatus?.Report(new PendingStatusDelta(
                            "等待其他回答完成", $"{workError.RetryAfter.Value} 秒后重试。"));
                        await Task.Delay(TimeSpan.FromSeconds(workError.RetryAfter.Value), ct).ConfigureAwait(false);
                        attempt--;
                        continue;
                    }
                }
                if (resp.StatusCode == HttpStatusCode.Unauthorized && (workError is null || workError.InvalidatesAccountSession))
                {
                    try { target.OnUnauthorized?.Invoke(token); }
                    catch (Exception ex) { _log?.Invoke("[llm-shim] unauthorized handler: " + ex.Message); }
                }
                if (target.ResponseReceived is { } observe)
                {
                    try { observe(resp); }
                    catch (Exception ex) { _log?.Invoke("[llm-shim] response observer: " + ex.Message); }
                }

                await using var upstreamStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var prefix = attempt == 0
                    ? await ReadResponsePrefixAsync(upstreamStream, resp, ct).ConfigureAwait(false)
                    : (Bytes: Array.Empty<byte>(), UnsupportedImage: false);
                if (prefix.UnsupportedImage && PiImageFallback.ReplaceImages(preparedBody) is { } textOnly)
                {
                    preparedBody = textOnly.ToJsonString(RelaxedJson);
                    operationKey = Guid.NewGuid().ToString("N");
                    _log?.Invoke($"[llm-shim] request={requestId} retrying without image blocks");
                    continue;
                }

                // The existing image-capability fallback is resolved before
                // recording a terminal error. Never clear a previous error.
                if (official && workError is not null) target.WorkError?.Invoke(workError);
                ctx.Response.StatusCode = (int)resp.StatusCode;
                ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "text/event-stream";
                ctx.Response.SendChunked = true;
                var buffer = new byte[8192];
                var errorObserver = official && resp.IsSuccessStatusCode
                    && resp.Content.Headers.ContentType?.MediaType == "text/event-stream"
                    ? new WorkStreamErrorObserver(target.WorkError) : null;
                ReadOnlyMemory<byte> chunk = prefix.Bytes;
                do
                {
                    if (!chunk.IsEmpty)
                    {
                        errorObserver?.Feed(chunk);
                        if (target.RelayTap is { } tap)
                        {
                            // Watchers must never break the relay: a parse error in
                            // a source collector is not the client's problem.
                            try { tap(chunk); }
                            catch (Exception ex) { _log?.Invoke("[llm-shim] relay tap: " + ex.Message); }
                        }
                        try
                        {
                            await ctx.Response.OutputStream.WriteAsync(chunk, ct).ConfigureAwait(false);
                            await ctx.Response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
                        {
                            outcome = "client_disconnected";
                            return;
                        }
                    }
                    var read = await upstreamStream.ReadAsync(buffer, ct).ConfigureAwait(false);
                    chunk = buffer.AsMemory(0, read);
                }
                while (!chunk.IsEmpty);
                errorObserver?.Complete();
                outcome = errorObserver?.HasError == true ? "stream_error"
                    : resp.IsSuccessStatusCode ? "completed" : "http_error";
                return;
            }
        }
        catch (CredentialUnavailableException ex)
        {
            outcome = "credentials_unavailable";
            try { await WriteError(ctx, 401, ex.Message).ConfigureAwait(false); }
            catch { /* client gone */ }
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            ctx.Response.Abort();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[llm-shim] request={requestId} failed type={ex.GetType().Name}");
            try { await WriteError(ctx, 502, "shim forward failed: " + ex.Message).ConfigureAwait(false); }
            catch { /* client gone */ }
        }
        finally
        {
            _slots.Release();
            _log?.Invoke($"[llm-shim] request={requestId} result={outcome} elapsed_ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            try { ctx.Response.Close(); } catch { /* ignore */ }
        }
    }

    private static async Task<(byte[] Bytes, bool UnsupportedImage)> ReadResponsePrefixAsync(
        Stream stream, HttpResponseMessage response, CancellationToken ct)
    {
        const int maxPrefixBytes = 64 * 1024;
        var sse = string.Equals(response.Content.Headers.ContentType?.MediaType ?? "text/event-stream",
            "text/event-stream", StringComparison.OrdinalIgnoreCase);
        using var prefix = new MemoryStream();
        var buffer = new byte[8192];
        while (prefix.Length < maxPrefixBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0,
                Math.Min(buffer.Length, maxPrefixBytes - (int)prefix.Length)), ct).ConfigureAwait(false);
            if (read == 0) break;
            prefix.Write(buffer, 0, read);
            if (sse && TryReadFirstDataEvent(
                    Encoding.UTF8.GetString(prefix.GetBuffer(), 0, (int)prefix.Length),
                    !response.IsSuccessStatusCode, out var unsupported))
                return (prefix.ToArray(), unsupported);
        }
        var bytes = prefix.ToArray();
        return (bytes, (!sse || response.Content.Headers.ContentType is null) && PiImageFallback.ClassifyResponse(
            Encoding.UTF8.GetString(bytes), !response.IsSuccessStatusCode) == PiImageFallback.ResponseKind.UnsupportedImage);
    }

    private static bool TryReadFirstDataEvent(string prefix, bool httpError, out bool unsupported)
    {
        unsupported = false;
        using var reader = new StringReader(prefix.TrimStart('\uFEFF'));
        var data = new StringBuilder();
        string? eventName = null;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(5).TrimStart(' '));
                }
                else if (line.StartsWith("event:", StringComparison.Ordinal))
                    eventName = line[6..].Trim();
                continue;
            }
            if (data.Length == 0)
            {
                eventName = null;
                continue;
            }
            var kind = PiImageFallback.ClassifyResponse(data.ToString(), httpError, eventName);
            if (kind == PiImageFallback.ResponseKind.EmptyStart)
            {
                data.Clear();
                eventName = null;
                continue;
            }
            unsupported = kind == PiImageFallback.ResponseKind.UnsupportedImage;
            return true;
        }
        return false;
    }

    /// <summary>The throwaway credential the sidecar presented, in whichever header
    /// its client uses, or null when there is none to read.</summary>
    private static string? PresentedToken(HttpListenerRequest request)
    {
        if (request.Headers["Authorization"] is { Length: > 0 } authorization
            && authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return authorization["Bearer ".Length..];
        }

        return request.Headers["x-api-key"] is { Length: > 0 } anthropic
            ? anthropic
            : request.Headers["x-goog-api-key"] is { Length: > 0 } google
                ? google
                : null;
    }

    /// <summary>
    /// Where this request actually goes, or null when the path is not one the
    /// sidecar's client should be producing.
    ///
    /// In <see cref="TargetPathMode.Fixed"/> the path is only a sanity check — the
    /// destination is the configured endpoint. In
    /// <see cref="TargetPathMode.GeminiModelTemplate"/> the incoming model is
    /// substituted into the configured URL template.
    /// </summary>
    private static string? ResolveUpstreamUrl(ForwardTarget target, Uri? requestUrl)
    {
        var path = requestUrl?.AbsolutePath ?? "";

        if (target.PathMode == TargetPathMode.Fixed)
        {
            return path.EndsWith("/chat/completions", StringComparison.Ordinal)
                   || path.EndsWith("/messages", StringComparison.Ordinal)
                   || path.EndsWith("/responses", StringComparison.Ordinal)
                ? target.Endpoint
                : null;
        }

        var suffix = path.StartsWith(BasePath + "/", StringComparison.Ordinal)
            ? path[BasePath.Length..]
            : path;
        const string modelPrefix = "/models/";
        const string operation = ":streamGenerateContent";
        if (!suffix.StartsWith(modelPrefix, StringComparison.Ordinal)
            || !suffix.EndsWith(operation, StringComparison.Ordinal)) return null;

        var model = suffix[modelPrefix.Length..^operation.Length];
        var endpoint = target.Endpoint.Replace("{model}", model, StringComparison.Ordinal);
        var query = requestUrl!.Query;
        return query.Length == 0
            ? endpoint
            : endpoint + (endpoint.Contains('?') ? "&" : "?") + query.TrimStart('?');
    }

    /// <summary>
    /// The edits the shim is allowed to make to Pi's request body. Everything
    /// else is relayed untouched.
    ///
    /// <b>Uncap</b>: the output cap Pi wrote from its placeholder. See
    /// <see cref="RemoveOutputCap"/>. Runs first, so a cap the user did ask for —
    /// through the role's reply limit or the model's custom parameters — is
    /// written back by the steps after it.
    ///
    /// <b>Merge</b>: the model's custom request parameters. Without this, a model
    /// tuned through BYOK's custom-parameters feature would silently lose that
    /// tuning the moment it ran on Pi.
    ///
    /// <b>Drop</b>: fields the target endpoint rejects. Pi cannot know which those
    /// are — the shim is what hides the real provider from it — and the failure is
    /// a 400 on the whole request rather than a degraded answer.
    /// </summary>
    private string RewriteBody(string body, ForwardTarget target)
    {
        var extra = target.ExtraBody;
        var dropKeys = target.DropBodyKeys;
        var generation = target.Generation;
        var nativeGoogle = target.PathMode == TargetPathMode.GeminiModelTemplate;
        var google = target.GeminiThinking is not null;
        var officialPath = OpenAiRequestParams.OfficialPath(target.Endpoint);
        var hasExtra = extra is { Count: > 0 };
        var hasDrops = dropKeys is { Count: > 0 };
        var hasGeneration = generation is not null
            && (generation.Temperature is not null || generation.TopP is not null || generation.MaxTokens is not null);
        var uncap = generation is { MaxTokens: null } && generation.Api != "anthropic-messages";
        if (!hasExtra && !hasDrops && !hasGeneration && !uncap && !google && officialPath is null) return body;
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body)
            ?? throw new JsonException("Pi 请求体为空。");
        var merged = parsed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);
        if (uncap) RemoveOutputCap(merged, generation!.Api);
        if (!google && extra is not null
            && (extra.ContainsKey("thinking_budget")
                || (extra.TryGetValue("enable_thinking", out var enabled)
                    && enabled.ValueKind == JsonValueKind.False)))
            merged.Remove("reasoning_effort");
        if (hasExtra) CustomRequestParams.ApplyBody(merged, extra);
        if (hasGeneration) ApplyGenerationOptions(merged, generation!);
        if (hasDrops)
            foreach (var key in dropKeys!) merged.Remove(key);
        OpenAiRequestParams.Apply(merged, officialPath);
        if (target.GeminiThinking is { } config)
            GeminiRequestParams.Apply(merged, config, nativeGoogle, generation?.ReasoningEffort, generation?.UseThinking, extra);
        return JsonSerializer.Serialize(merged, RelaxedJson);
    }

    /// <summary>
    /// Pi's model schema requires an output cap and every api path sends it, but
    /// MolaGPT's model rows have none to give, so what arrives here is
    /// <see cref="PiModelCatalog"/>'s placeholder. Sent upstream it is a real limit:
    /// a reasoning model at max effort spent all 8,192 tokens thinking and came back
    /// with no answer. Without the field the endpoint applies its own ceiling, which
    /// is what the direct provider has always done.
    ///
    /// Anthropic is left alone because its Messages API rejects a request with no
    /// <c>max_tokens</c>.
    /// </summary>
    internal static void RemoveOutputCap(IDictionary<string, object?> body, string api)
    {
        if (api == "google-generative-ai")
        {
            if (body.TryGetValue("generationConfig", out var existing)
                && existing is JsonElement { ValueKind: JsonValueKind.Object } element)
            {
                var config = element.Deserialize<Dictionary<string, JsonElement>>()!;
                if (config.Remove("maxOutputTokens"))
                    body["generationConfig"] = JsonSerializer.SerializeToElement(config);
            }
            return;
        }

        body.Remove("max_tokens");
        body.Remove("max_completion_tokens");
        body.Remove("max_output_tokens");
    }

    internal static void ApplyGenerationOptions(IDictionary<string, object?> body, GenerationOptions options)
    {
        if (options.Api == "google-generative-ai")
        {
            var config = body.TryGetValue("generationConfig", out var existing) && existing is JsonElement element
                ? element.Deserialize<Dictionary<string, object?>>()! : new Dictionary<string, object?>();
            if (options.Temperature is { } temperature) config["temperature"] = temperature;
            if (options.TopP is { } topP) config["topP"] = topP;
            if (options.MaxTokens is { } maxTokens) config["maxOutputTokens"] = maxTokens;
            body["generationConfig"] = config;
        }
        else
        {
            if (options.Temperature is { } temperature) body["temperature"] = temperature;
            if (options.TopP is { } topP) body["top_p"] = topP;
            if (options.MaxTokens is { } maxTokens)
            {
                var key = options.Api == "openai-responses" ? "max_output_tokens"
                    : body.ContainsKey("max_completion_tokens") ? "max_completion_tokens" : "max_tokens";
                body[key] = maxTokens;
            }
        }
    }

    private static async Task WriteError(HttpListenerContext ctx, int code, string message)
    {
        var bytes = Encoding.UTF8.GetBytes($"{{\"error\":{{\"message\":{System.Text.Json.JsonSerializer.Serialize(message)}}}}}");
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
