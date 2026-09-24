using System.Collections.Concurrent;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.Text;
using MolaGPT.Core.Chat;

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
    public sealed record ForwardTarget(
        string Endpoint,
        Func<CancellationToken, Task<string?>> TokenProvider,
        Action? OnUnauthorized = null,
        IReadOnlyList<KeyValuePair<string, string>>? Headers = null,
        IReadOnlyDictionary<string, JsonElement>? ExtraBody = null,
        AuthStyle Auth = AuthStyle.Bearer,
        IReadOnlyList<string>? DropBodyKeys = null,
        TargetPathMode PathMode = TargetPathMode.Fixed,
        GenerationOptions? Generation = null,
        Action<string>? PromptPrepared = null);

    public sealed record GenerationOptions(string Api, double? Temperature, double? TopP, int? MaxTokens);

    public enum TargetPathMode
    {
        /// <summary>Every request goes to <c>Endpoint</c> verbatim. Right for the
        /// shapes where the operation lives in the body — chat/completions,
        /// messages, responses — and the user may have configured a non-standard
        /// path we must not second-guess.</summary>
        Fixed,

        /// <summary><c>Endpoint</c> is a base; whatever Pi appended after the shim's
        /// own prefix is carried over, query string included. Required by Google's
        /// generative-language API, which puts the model id and the operation in the
        /// path (<c>/models/{id}:streamGenerateContent</c>) and selects the stream
        /// framing with <c>?alt=sse</c> — dropping the query yields a body Pi cannot
        /// parse ("Incomplete JSON segment at the end").</summary>
        AppendInboundSuffix,
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

    private readonly HttpClient _http;
    private readonly HttpListener _listener = new();
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
    /// client's own construction, which <see cref="TargetPathMode.AppendInboundSuffix"/>
    /// carries through to the real endpoint.</summary>
    private const string BasePath = "/v1";

    /// <summary>Base URL to hand Pi (it appends <c>/chat/completions</c> and friends).</summary>
    public string BaseUrl { get; }

    public PiWorkLlmShim(HttpClient http, Action<string>? log = null)
    {
        _http = http;
        _log = log;
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
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";

            // Which credential arrives depends on the API the sidecar was told to
            // speak: OpenAI-family clients send a bearer, Anthropic's sends
            // x-api-key, Google's sends x-goog-api-key. All three carry the same
            // throwaway value, so any of them identifies the sidecar.
            var presented = PresentedToken(ctx.Request);
            if (presented is null || !_targets.TryGetValue(presented, out var target))
            {
                await WriteError(ctx, 403, "bad token").ConfigureAwait(false);
                return;
            }

            var url = ResolveUpstreamUrl(target, ctx.Request.Url);
            if (url is null)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            var preparedBody = RewriteBody(body, target.ExtraBody, target.DropBodyKeys, target.Generation);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                target.PromptPrepared?.Invoke(preparedBody);
                using var upstream = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(preparedBody, Encoding.UTF8, "application/json"),
                };
                var token = await target.TokenProvider(_cts.Token).ConfigureAwait(false);
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

                using var resp = await _http
                    .SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, _cts.Token)
                    .ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    try { target.OnUnauthorized?.Invoke(); }
                    catch (Exception ex) { _log?.Invoke("[llm-shim] unauthorized handler: " + ex.Message); }
                }

                await using var upstreamStream = await resp.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);
                var prefix = attempt == 0
                    ? await ReadResponsePrefixAsync(upstreamStream, resp, _cts.Token).ConfigureAwait(false)
                    : (Bytes: Array.Empty<byte>(), UnsupportedImage: false);
                if (prefix.UnsupportedImage && PiImageFallback.ReplaceImages(preparedBody) is { } textOnly)
                {
                    preparedBody = textOnly.ToJsonString(RelaxedJson);
                    _log?.Invoke("[llm-shim] retrying request without image blocks");
                    continue;
                }

                ctx.Response.StatusCode = (int)resp.StatusCode;
                ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "text/event-stream";
                ctx.Response.SendChunked = true;
                var buffer = new byte[8192];
                ReadOnlyMemory<byte> chunk = prefix.Bytes;
                do
                {
                    if (!chunk.IsEmpty)
                    {
                        try
                        {
                            await ctx.Response.OutputStream.WriteAsync(chunk, _cts.Token).ConfigureAwait(false);
                            await ctx.Response.OutputStream.FlushAsync(_cts.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
                        {
                            return;
                        }
                    }
                    var read = await upstreamStream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                    chunk = buffer.AsMemory(0, read);
                }
                while (!chunk.IsEmpty);
                return;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke("[llm-shim] " + ex.Message);
            try { await WriteError(ctx, 502, "shim forward failed: " + ex.Message).ConfigureAwait(false); }
            catch { /* client gone */ }
        }
        finally { try { ctx.Response.Close(); } catch { /* ignore */ } }
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
    /// <see cref="TargetPathMode.AppendInboundSuffix"/> the path <em>is</em> the
    /// request, so it is carried over whole.
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
        if (suffix.Length == 0 || suffix == "/") return null;

        var query = requestUrl?.Query ?? "";
        return target.Endpoint.TrimEnd('/') + suffix + query;
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
    private string RewriteBody(
        string body,
        IReadOnlyDictionary<string, JsonElement>? extra,
        IReadOnlyList<string>? dropKeys,
        GenerationOptions? generation)
    {
        var hasExtra = extra is { Count: > 0 };
        var hasDrops = dropKeys is { Count: > 0 };
        var hasGeneration = generation is not null
            && (generation.Temperature is not null || generation.TopP is not null || generation.MaxTokens is not null);
        var uncap = generation is { MaxTokens: null } && generation.Api != "anthropic-messages";
        if (!hasExtra && !hasDrops && !hasGeneration && !uncap) return body;
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body)
            ?? throw new JsonException("Pi 请求体为空。");
        var merged = parsed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);
        if (uncap) RemoveOutputCap(merged, generation!.Api);
        if (hasExtra) CustomRequestParams.ApplyBody(merged, extra);
        if (hasGeneration) ApplyGenerationOptions(merged, generation!);
        if (hasDrops)
            foreach (var key in dropKeys!) merged.Remove(key);
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
