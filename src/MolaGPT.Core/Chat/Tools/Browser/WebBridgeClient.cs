using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MolaGPT.Core.Chat.Tools.Browser;

/// <summary>
/// Thin client for the Kimi WebBridge local daemon.
///
/// Two endpoints, and they are not interchangeable:
/// <c>GET /status</c> answers "is the daemon up and is the extension attached",
/// while <c>POST /command</c> runs a browser tool inside a session. Asking
/// <c>/command</c> for "status" does not return daemon health — it is dispatched
/// as a page tool and fails with "session has no tab".
///
/// Loopback only. The daemon exposes the user's real browser, so a non-local
/// target is refused rather than dialled.
/// </summary>
public sealed class WebBridgeClient
{
    public const string DefaultDaemonUrl = WebBridgeAddress.DefaultDaemonUrl;

    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _http;
    private readonly string? _fixedDaemonUrl;

    public WebBridgeClient(HttpClient http, string? daemonUrl = null)
    {
        _http = http;
        _fixedDaemonUrl = WebBridgeAddress.NormalizeLoopbackUrl(daemonUrl);
    }

    /// <summary>The address this call should use: the per-turn option when it is a
    /// usable loopback URL, otherwise whatever the daemon's own config says.</summary>
    public string ResolveDaemonUrl(string? perCallUrl) =>
        WebBridgeAddress.NormalizeLoopbackUrl(perCallUrl)
        ?? _fixedDaemonUrl
        ?? WebBridgeAddress.Resolve();

    public static string? NormalizeLoopbackUrl(string? url) =>
        WebBridgeAddress.NormalizeLoopbackUrl(url);

    /// <summary>
    /// Build the daemon request body. Kept pure so tests can assert the shape
    /// without a live daemon.
    /// </summary>
    public static string BuildCommandJson(string action, JsonObject? args, string session)
    {
        var root = new JsonObject
        {
            ["action"] = action,
            // JsonNode can only belong to one parent. Keep this pure so callers
            // can safely reuse the same payload for a transport-level retry.
            ["args"] = args?.DeepClone() ?? new JsonObject(),
            ["session"] = session
        };
        return root.ToJsonString(RequestJsonOptions);
    }

    /// <summary>
    /// Daemon health. This is the only call that works before a session owns a
    /// tab, so it is what "is the browser tool working" must be built on.
    /// </summary>
    public async Task<WebBridgeStatus> GetStatusAsync(
        string? daemonUrl,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var target = ResolveDaemonUrl(daemonUrl);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            using var response = await _http.GetAsync($"{target}/status", cts.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return WebBridgeStatus.Parse(text, target);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return WebBridgeStatus.Unreachable(target);
        }
        catch (HttpRequestException)
        {
            return WebBridgeStatus.Unreachable(target);
        }
    }

    public async Task<WebBridgeResponse> SendAsync(
        string action,
        JsonObject? args,
        string session,
        TimeSpan timeout,
        CancellationToken ct,
        string? daemonUrl = null)
    {
        var target = ResolveDaemonUrl(daemonUrl);
        var body = BuildCommandJson(action, args, session);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var response = await _http
                .PostAsync($"{target}/command", content, cts.Token)
                .ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return WebBridgeResponse.Parse(text, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return WebBridgeResponse.Fail(
                $"WebBridge 请求超时（{timeout.TotalSeconds:0}s）。状态未知的操作不要盲目重试；可先用 list_tabs 确认。",
                timedOut: true);
        }
        catch (HttpRequestException ex)
        {
            return WebBridgeResponse.Fail(DescribeConnectionFailure(ex, target), connectionFailed: true);
        }
    }

    public static string? ResolveInstalledDaemonPath() => WebBridgeAddress.InstalledDaemonPath();

    /// <summary>
    /// Best-effort local daemon start when the binary is already installed.
    /// Never downloads or installs. Never kills a process that is still
    /// running — a listener that outlives the wait window is the intended
    /// outcome of <c>start</c>, not a failure.
    /// </summary>
    public static async Task<bool> TryStartInstalledDaemonAsync(CancellationToken ct)
    {
        var exe = ResolveInstalledDaemonPath();
        if (exe is null) return false;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "start",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            if (!process.Start()) return false;

            // `start` is expected to fork into the background and exit quickly.
            // If it stays alive it is almost certainly the daemon itself — leave
            // it alone and let the caller retry the HTTP command.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Still running: treat as started; do not kill.
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeConnectionFailure(HttpRequestException ex, string target)
    {
        var installed = WebBridgeAddress.IsInstalled;
        var hint = installed
            ? $"无法连接本机 Kimi 浏览器扩展服务（{target}）。服务可能未启动，可在设置 → 浏览器使用中检测并启动。"
            : "本机未安装 Kimi 浏览器扩展服务。请在设置 → 浏览器使用 → 配置指引中完成安装与配置。";
        return string.IsNullOrWhiteSpace(ex.Message) ? hint : $"{hint}\n底层错误：{ex.Message}";
    }
}

/// <summary>
/// The daemon's own health report (<c>GET /status</c>). <see cref="Running"/>
/// means the daemon answered; <see cref="ExtensionConnected"/> means a browser
/// is actually attached — a daemon with no extension accepts commands and then
/// fails every one of them, so the two are reported separately.
/// </summary>
public sealed record WebBridgeStatus(
    bool Running,
    bool ExtensionConnected,
    string DaemonUrl,
    string? DaemonVersion = null,
    string? ExtensionVersion = null,
    int Port = 0)
{
    public static WebBridgeStatus Unreachable(string daemonUrl) => new(false, false, daemonUrl);

    public static WebBridgeStatus Parse(string text, string daemonUrl)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            return new WebBridgeStatus(
                Running: !root.TryGetProperty("running", out var running)
                         || running.ValueKind != JsonValueKind.False,
                ExtensionConnected: root.TryGetProperty("extension_connected", out var connected)
                                    && connected.ValueKind == JsonValueKind.True,
                DaemonUrl: daemonUrl,
                DaemonVersion: ReadString(root, "version"),
                ExtensionVersion: ReadString(root, "extension_version"),
                Port: root.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number
                    ? port.GetInt32()
                    : 0);
        }
        catch (JsonException)
        {
            return Unreachable(daemonUrl);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}

public sealed record WebBridgeResponse(
    bool Success,
    string BodyText,
    int StatusCode = 0,
    bool ConnectionFailed = false,
    bool TimedOut = false,
    string? ErrorMessage = null)
{
    /// <summary>
    /// Read the daemon's envelope.
    ///
    /// The shape is <c>{"ok":true,"data":{…}}</c> or
    /// <c>{"ok":false,"error":{"code":…,"message":…}}</c> — note that
    /// <c>error</c> is an OBJECT, and that argument-level failures come back
    /// with HTTP 200. Checking only for a string <c>error</c> or a non-2xx code
    /// classifies those as success and hands the model a failure it reads as a
    /// result.
    /// </summary>
    public static WebBridgeResponse Parse(string text, int statusCode)
    {
        var httpFailed = statusCode != 0 && statusCode is < 200 or >= 300;

        if (string.IsNullOrWhiteSpace(text))
            return new WebBridgeResponse(false, "{}", statusCode, ErrorMessage: "WebBridge 返回了空响应。");

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var error = ReadError(root);

            var ok = !httpFailed
                     && error is null
                     && !IsFalse(root, "ok")
                     && !IsFalse(root, "success");

            return new WebBridgeResponse(ok, text, statusCode, ErrorMessage: error);
        }
        catch (JsonException)
        {
            return new WebBridgeResponse(false, text, statusCode, ErrorMessage: Trim(text));
        }
    }

    /// <summary>The daemon's message, whether it arrived as an object or a string.</summary>
    private static string? ReadError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error)) return null;

        return error.ValueKind switch
        {
            JsonValueKind.String when !string.IsNullOrWhiteSpace(error.GetString()) => error.GetString(),
            JsonValueKind.Object => ReadErrorObject(error),
            _ => null
        };
    }

    private static string ReadErrorObject(JsonElement error)
    {
        var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
        var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

        if (!string.IsNullOrWhiteSpace(message)) return Trim(message!);
        return string.IsNullOrWhiteSpace(code) ? "WebBridge 操作失败。" : $"WebBridge 错误：{code}";
    }

    private static bool IsFalse(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;

    /// <summary>The daemon writes multi-line agent instructions into some error
    /// messages; the first paragraph is the part worth forwarding.</summary>
    private static string Trim(string message)
    {
        var cut = message.IndexOf('\n');
        var head = cut > 0 ? message[..cut] : message;
        return head.Length > 400 ? head[..400] + "…" : head.Trim();
    }

    public static WebBridgeResponse Fail(string message, bool connectionFailed = false, bool timedOut = false) =>
        new(false,
            JsonSerializer.Serialize(new { success = false, error = message }),
            0,
            connectionFailed,
            timedOut,
            message);
}
