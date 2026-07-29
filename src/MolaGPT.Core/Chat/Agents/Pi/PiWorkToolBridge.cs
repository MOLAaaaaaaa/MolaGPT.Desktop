using System.Net;
using System.Text;
using System.Text.Json;

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

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile ToolDispatcher? _dispatcher;
    private volatile ToolCatalog? _catalog;
    private volatile SystemPrompt? _systemPrompt;

    public string Url { get; }
    public string Token { get; }

    public PiWorkToolBridge(Action<string>? log = null)
    {
        var port = FreeTcpPort();
        Url = $"http://127.0.0.1:{port}";
        Token = Guid.NewGuid().ToString("N");
        _listener.Prefixes.Add($"{Url}/");
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(log));
    }

    /// <summary>Point the bridge at the dispatcher for the turn about to run.</summary>
    public void SetDispatcher(ToolDispatcher? dispatcher) => _dispatcher = dispatcher;

    /// <summary>Point the bridge at the tool catalogue for the turn about to run.</summary>
    public void SetCatalog(ToolCatalog? catalog) => _catalog = catalog;

    /// <summary>Point the bridge at the system prompt for the turn about to run.</summary>
    public void SetSystemPrompt(SystemPrompt? provider) => _systemPrompt = provider;

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
            var dispatcher = _dispatcher;
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);

            var segments = ctx.Request.Url?.AbsolutePath.Trim('/').Split('/') ?? Array.Empty<string>();

            if (ctx.Request.Headers["x-mola-token"] != Token)
            {
                status = 403;
                responseJson = "{\"error\":\"bad token\"}";
            }
            else if (segments is ["tools"])
            {
                // GET /tools — the sidecar asks what MolaGPT can do right now.
                var catalog = _catalog;
                if (catalog is null)
                {
                    status = 409;
                    responseJson = "{\"error\":\"no active turn\"}";
                }
                else
                {
                    responseJson = catalog();
                }
            }
            else if (segments is ["system-prompt"])
            {
                var prompt = _systemPrompt?.Invoke();
                responseJson = JsonSerializer.Serialize(new { prompt });
            }
            else if (dispatcher is null)
            {
                status = 409;
                responseJson = "{\"error\":\"no active turn\"}";
            }
            else
            {
                // POST /tools/<name>
                var name = segments.Length >= 2 ? segments[1] : "";
                var argsJson = ExtractArgs(body);
                var output = await dispatcher(name, argsJson, _cts.Token).ConfigureAwait(false);
                responseJson = JsonSerializer.Serialize(new { output });
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
