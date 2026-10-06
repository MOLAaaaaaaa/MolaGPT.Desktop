using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat.Agents.Pi;

/// <summary>
/// Loopback HTTP host that receives tool callbacks from the Pi sidecar (seam ③)
/// and dispatches them to the real MolaGPT tool stack — so Pi runs the loop but
/// MolaGPT keeps ownership of the sandboxed Python tool (risk analyzer + session
/// allow-list), vision, image-gen, MCP, and, crucially, approval: the wrapped
/// <see cref="Tools.IChatToolHost.ExecuteAsync"/> gates risky tools through the
/// existing desktop approval flow when it runs, so no separate RPC UI hop is
/// needed.
///
/// Bound to 127.0.0.1 with a per-instance random token — nothing off-box can
/// reach the tool-execution surface.
/// </summary>
public sealed class PiWorkToolBridge : IDisposable
{
    /// <summary>Executes a tool call for the current turn. Set per turn by the
    /// provider so the bridge always has the live <c>ChatToolContext</c>/options.
    /// Returns the tool result string (same contract as IChatToolHost.ExecuteAsync).</summary>
    public delegate Task<string> ToolDispatcher(string toolName, string argumentsJson, CancellationToken ct);

    /// <summary>Returns the JSON array of OpenAI-format tool definitions the sidecar
    /// should register — MolaGPT's real, live tool set (respecting the composer
    /// toggles and configured MCP servers). Supplied per turn by the provider so the
    /// extension never hardcodes names or schemas.</summary>
    public delegate string ToolCatalog();

    /// <summary>Returns the system prompt MolaGPT wants for the turn about to run
    /// (persona, per-model prompt), or null to leave Pi's own prompt in place.
    /// Without this the agent silently runs on Pi's coding-assistant prompt and
    /// every persona the user picked is ignored.</summary>
    public delegate string? SystemPrompt();

    /// <summary>What one sidecar is allowed to do for the turn it is running.</summary>
    public sealed record TurnBinding(
        ToolDispatcher Dispatcher,
        ToolCatalog Catalog,
        SystemPrompt SystemPrompt,
        RolePromptPlan? RolePlan = null,
        Action<string>? PromptError = null,
        Func<string, string, Action<string>, CancellationToken, Task<string>>? ProgressDispatcher = null);

    /// <summary>A binding and the signal that its turn is over. Tool calls run
    /// against <see cref="Ended"/>, so whatever a turn still has in flight when it
    /// ends — an approval dialog, a running script — is cancelled with it.</summary>
    private sealed record Bound(TurnBinding Binding, CancellationTokenSource Ended);

    /// <summary>
    /// How often a running tool call writes a byte to its response.
    ///
    /// Pi's fetch gives up on a response whose headers or next body chunk take
    /// longer than five minutes, and a tool call can: approval waits on a person,
    /// and Python alone may run for 300 s. The heartbeat is also how a hung-up
    /// sidecar is noticed — a write to a closed connection fails.
    /// </summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Per-sidecar bindings, keyed by that sidecar's throwaway token — the same key
    /// <see cref="PiWorkLlmShim"/> routes on.
    ///
    /// These used to be three fields swapped per turn, which was safe only because
    /// a gate allowed one turn at a time. With a pool that gate is gone, and a
    /// shared "current dispatcher" would let one conversation's tool call execute
    /// against another conversation's workspace and approvals.
    /// </summary>
    private readonly ConcurrentDictionary<string, Bound> _bindings = new(StringComparer.Ordinal);
    private readonly object _bindingGate = new();

    public string Url { get; }

    private readonly Action<string>? _log;

    public PiWorkToolBridge(Action<string>? log = null)
    {
        _log = log;
        var port = FreeTcpPort();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{Url}/");
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(log));
    }

    /// <summary>Bind one sidecar's callbacks for the turn it is about to run, or
    /// pass null to unbind. Calls arriving without a binding are refused rather
    /// than served from whatever ran last, and calls still running under the
    /// previous binding are cancelled.</summary>
    public void SetBinding(string sidecarToken, TurnBinding? binding)
    {
        Bound? previous;
        lock (_bindingGate)
        {
            _bindings.TryGetValue(sidecarToken, out previous);
            if (binding is null) _bindings.TryRemove(sidecarToken, out _);
            else _bindings[sidecarToken] = new Bound(binding, new CancellationTokenSource());
        }

        // Not disposed: a call that has just looked the binding up may still be
        // about to read its token.
        try { previous?.Ended.Cancel(); }
        catch (AggregateException ex) { _log?.Invoke("[tool-bridge] 取消工具调用时出错：" + ex.InnerException?.Message); }
    }

    private async Task AcceptLoopAsync(Action<string>? log)
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx, log));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, Action<string>? log)
    {
        var status = 200;
        string responseJson;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);

            var segments = ctx.Request.Url?.AbsolutePath.Trim('/').Split('/') ?? Array.Empty<string>();
            var token = ctx.Request.Headers["x-mola-token"];

            if (string.IsNullOrEmpty(token) || !_bindings.TryGetValue(token, out var bound))
            {
                // Either something else on the box found the port, or the sidecar
                // outlived its turn. Both are "not now", never "use the last one".
                status = 403;
                responseJson = "{\"error\":\"bad token\"}";
            }
            else if (segments is ["tools"])
            {
                // GET /tools — the sidecar asks what MolaGPT can do right now.
                responseJson = bound.Binding.Catalog();
            }
            else if (segments is ["system-prompt"])
            {
                responseJson = JsonSerializer.Serialize(new { prompt = bound.Binding.SystemPrompt(), rolePlan = bound.Binding.RolePlan }, RoleJson.Options);
            }
            else if (segments is ["prompt-error"])
            {
                using var error = JsonDocument.Parse(body);
                bound.Binding.PromptError?.Invoke(error.RootElement.GetProperty("message").GetString() ?? "角色上下文处理失败。");
                responseJson = "{}";
            }
            else
            {
                // POST /tools/<name>
                var name = segments.Length >= 2 ? segments[1] : "";
                await RunToolAsync(ctx, bound, name, ExtractArgs(body), log).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            status = 500;
            log?.Invoke("[tool-bridge] " + ex.Message);
            responseJson = JsonSerializer.Serialize(new { output = "工具执行失败：" + ex.Message, error = true });
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(responseJson);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch { /* client gone */ }
        finally { try { ctx.Response.Close(); } catch { /* ignore */ } }
    }

    /// <summary>
    /// POST /tools/&lt;name&gt;: run one tool call for the turn bound to this sidecar.
    ///
    /// The response is committed before the tool runs — status 200, chunked, a
    /// single space — and a space follows every <see cref="HeartbeatInterval"/>
    /// until the result is written. Clients opting into x-mola-progress receive
    /// newline-delimited activity records followed by the result; older clients
    /// still receive one JSON object with leading whitespace. A failure can
    /// no longer be a 500; it travels as <c>error: true</c> beside the output.
    /// A tool that ran and reported failure in its own result — a refused approval,
    /// an MCP error — is marked <c>failed: true</c>, so Pi records it as failed too.
    ///
    /// The call is cancelled when its turn ends or the sidecar hangs up (Stop, or
    /// Pi aborting the call), rather than finishing into a connection nobody
    /// reads: a script left running, or an approval dialog that runs the tool
    /// when clicked after the turn has already moved on.
    /// </summary>
    private async Task RunToolAsync(
        HttpListenerContext ctx,
        Bound bound,
        string name,
        string argsJson,
        Action<string>? log)
    {
        using var call = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, bound.Ended.Token);
        var response = ctx.Response;
        var progressEnabled = ctx.Request.Headers["x-mola-progress"] == "1";
        response.StatusCode = 200;
        response.ContentType = progressEnabled ? "application/x-ndjson" : "application/json";
        response.SendChunked = true;
        var stream = response.OutputStream;
        using var writes = new SemaphoreSlim(1, 1);
        using var stopHeartbeat = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(stream, writes, call, stopHeartbeat.Token);
        var updates = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var progress = WriteProgressAsync();

        async Task WriteProgressAsync()
        {
            try
            {
                await foreach (var activity in updates.Reader.ReadAllAsync(call.Token).ConfigureAwait(false))
                {
                    var line = JsonSerializer.Serialize(new { activity }) + "\n";
                    await WriteAsync(stream, writes, Encoding.UTF8.GetBytes(line)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (call.IsCancellationRequested) { }
            catch (Exception)
            {
                try { call.Cancel(); }
                catch (AggregateException) { }
            }
        }

        string json;
        try
        {
            var output = progressEnabled && bound.Binding.ProgressDispatcher is { } dispatch
                ? await dispatch(name, argsJson, activity => updates.Writer.TryWrite(activity), call.Token).ConfigureAwait(false)
                : await bound.Binding.Dispatcher(name, argsJson, call.Token).ConfigureAwait(false);
            json = JsonSerializer.Serialize(new { output, failed = ToolDeltaBuilder.IsToolError(output) });
        }
        // The dispatcher may be cancelled through its own turn's token before this
        // call's is, so either counts.
        catch (OperationCanceledException ex)
            when (call.IsCancellationRequested || ex.CancellationToken.IsCancellationRequested)
        {
            json = JsonSerializer.Serialize(new { output = "工具调用已取消。", error = true });
        }
        catch (Exception ex)
        {
            log?.Invoke("[tool-bridge] " + ex.Message);
            json = JsonSerializer.Serialize(new { output = "工具执行失败：" + ex.Message, error = true });
        }

        updates.Writer.TryComplete();
        await progress.ConfigureAwait(false);
        stopHeartbeat.Cancel();
        await heartbeat.ConfigureAwait(false);

        try { await WriteAsync(stream, writes, Encoding.UTF8.GetBytes(json + (progressEnabled ? "\n" : ""))).ConfigureAwait(false); }
        catch { /* client gone */ }
        finally { try { response.Close(); } catch { /* ignore */ } }
    }

    private static async Task HeartbeatAsync(
        Stream stream,
        SemaphoreSlim writes,
        CancellationTokenSource call,
        CancellationToken stop)
    {
        try
        {
            // The first byte is what sends the headers.
            await WriteAsync(stream, writes, Space).ConfigureAwait(false);
            while (true)
            {
                await Task.Delay(HeartbeatInterval, stop).ConfigureAwait(false);
                await WriteAsync(stream, writes, Space).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // The sidecar hung up on this call.
            try { call.Cancel(); }
            catch (AggregateException) { /* the call reports its own failure */ }
        }
    }

    private static readonly byte[] Space = " "u8.ToArray();

    private static async Task WriteAsync(Stream stream, SemaphoreSlim writes, byte[] bytes)
    {
        await writes.WaitAsync().ConfigureAwait(false);
        try { await stream.WriteAsync(bytes).ConfigureAwait(false); }
        finally { writes.Release(); }
    }

    /// <summary>The extension posts <c>{ ...toolArgs }</c>; MolaGPT tools take the
    /// raw arguments JSON. The body already IS that object, so pass it through.</summary>
    private static string ExtractArgs(string body) =>
        string.IsNullOrWhiteSpace(body) ? "{}" : body;

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
