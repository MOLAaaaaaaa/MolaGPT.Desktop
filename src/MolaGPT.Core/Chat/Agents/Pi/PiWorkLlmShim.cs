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
/// It is a transparent streaming reverse proxy: request body in → add auth →
/// forward → copy the SSE response back byte-for-byte. It never parses or
/// rewrites the OpenAI payload, so whatever the relay does upstream (real
/// provider, chatv1.php, Responses translation, …) is completely unaffected.
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
    public sealed record ForwardTarget(
        string Endpoint,
        Func<CancellationToken, Task<string?>> TokenProvider,
        Action? OnUnauthorized = null,
        IReadOnlyList<KeyValuePair<string, string>>? Headers = null,
        IReadOnlyDictionary<string, JsonElement>? ExtraBody = null,
        AuthStyle Auth = AuthStyle.Bearer);

    public enum AuthStyle
    {
        /// <summary>OpenAI-compatible: <c>Authorization: Bearer …</c>.</summary>
        Bearer,

        /// <summary>Anthropic: <c>x-api-key</c> + <c>anthropic-version</c>.</summary>
        AnthropicApiKey,
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
    private volatile ForwardTarget? _target;

    /// <summary>Base URL to hand Pi (it appends <c>/chat/completions</c>).</summary>
    public string BaseUrl { get; }

    /// <summary>Per-instance secret handed to the sidecar as its "API key". Any
    /// other local process that finds the port cannot spend the user's quota:
    /// requests without this bearer are rejected before anything is forwarded.
    /// The sidecar never sees a real credential — only this throwaway.</summary>
    public string Token { get; } = Guid.NewGuid().ToString("N");

    public PiWorkLlmShim(HttpClient http, Action<string>? log = null)
    {
        _http = http;
        _log = log;
        var port = FreeTcpPort();
        BaseUrl = $"http://127.0.0.1:{port}/v1";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Point the shim at the endpoint + token source for the turn about to run.</summary>
    public void SetTarget(ForwardTarget? target) => _target = target;

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
            // Pi's client appends the path for whichever API the provider declares:
            // /chat/completions for OpenAI-compatible, /messages for Anthropic.
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (!path.EndsWith("/chat/completions", StringComparison.Ordinal)
                && !path.EndsWith("/messages", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            if (ctx.Request.Headers["Authorization"] != "Bearer " + Token)
            {
                await WriteError(ctx, 403, "bad token").ConfigureAwait(false);
                return;
            }

            var target = _target;
            if (target is null)
            {
                await WriteError(ctx, 409, "no active turn").ConfigureAwait(false);
                return;
            }

            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);

            using var upstream = new HttpRequestMessage(HttpMethod.Post, target.Endpoint)
            {
                Content = new StringContent(MergeExtraBody(body, target.ExtraBody), Encoding.UTF8, "application/json"),
            };
            var token = await target.TokenProvider(_cts.Token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token))
            {
                if (target.Auth == AuthStyle.AnthropicApiKey)
                {
                    upstream.Headers.TryAddWithoutValidation("x-api-key", token);
                    upstream.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
                }
                else
                {
                    upstream.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
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

            ctx.Response.StatusCode = (int)resp.StatusCode;
            ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "text/event-stream";
            ctx.Response.SendChunked = true;

            await using var upstreamStream = await resp.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);
            var buffer = new byte[8192];
            int read;
            while ((read = await upstreamStream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false)) > 0)
            {
                await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), _cts.Token).ConfigureAwait(false);
                await ctx.Response.OutputStream.FlushAsync(_cts.Token).ConfigureAwait(false); // push SSE promptly
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

    /// <summary>
    /// Merge the model's custom request parameters into the body Pi produced. The
    /// body is otherwise relayed untouched; this is the one exception, and it only
    /// engages when parameters are actually configured — without it, a model tuned
    /// through BYOK's custom-parameters feature would silently lose that tuning
    /// the moment it ran on Pi.
    /// </summary>
    private string MergeExtraBody(string body, IReadOnlyDictionary<string, JsonElement>? extra)
    {
        if (extra is null || extra.Count == 0) return body;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
            if (parsed is null) return body;

            var merged = parsed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);
            CustomRequestParams.ApplyBody(merged, extra);
            return JsonSerializer.Serialize(merged, RelaxedJson);
        }
        catch (JsonException ex)
        {
            // Relaying the original is strictly better than failing the turn.
            _log?.Invoke("[llm-shim] 自定义参数合并失败，按原样转发：" + ex.Message);
            return body;
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
