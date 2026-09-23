using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace MolaGPT.App.Rendering.Canvas;

/// <summary>Colours a canvas page inherits, as CSS custom properties.</summary>
internal sealed record CanvasTheme(bool Dark, IReadOnlyList<KeyValuePair<string, string>> Variables)
{
    public string Style()
    {
        var sb = new StringBuilder("<style id=\"mola-theme\">:root{color-scheme:");
        sb.Append(Dark ? "dark" : "light").Append(';');
        foreach (var (name, value) in Variables) sb.Append(name).Append(':').Append(value).Append(';');
        sb.Append("}html,body{background:var(--mola-bg);color:var(--mola-text);")
          .Append("font-family:\"Segoe UI\",\"Microsoft YaHei UI\",\"PingFang SC\",system-ui,sans-serif;}</style>");
        return sb.ToString();
    }

    public string Script() =>
        "window.__molaTheme&&window.__molaTheme(" + JsonSerializer.Serialize(new
        {
            scheme = Dark ? "dark" : "light",
            vars = Variables.ToDictionary(v => v.Key, v => v.Value),
        }) + ");";
}

/// <summary>What was loaded from outside, and what was refused — shown under
/// the canvas so the page's network behaviour is never invisible.</summary>
internal sealed class CanvasTraffic
{
    public HashSet<string> LoadedHosts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> BlockedHosts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Loaded { get; set; }
    public int Blocked { get; set; }
    public int Failed { get; set; }
}

/// <summary>
/// Serves canvas documents and polices everything a page tries to reach.
///
/// Each artifact gets its own origin (<c>https://c-{key}.mola-canvas.example</c>)
/// served from memory. A real origin is what makes localStorage work — a page
/// loaded with NavigateToString has an opaque origin and every storage call
/// throws — and a per-artifact one keeps two pages' storage apart. The CSP
/// travels as a response header, ahead of anything the page itself declares.
/// </summary>
internal sealed partial class CanvasSandbox
{
    public const string HostSuffix = ".mola-canvas.example";

    private static readonly string CdnSources = string.Join(' ', CdnProxy.AllowedHosts.Select(h => "https://" + h));

    // Pages may pull libraries from the allowed CDNs and talk to them (module
    // imports, data files); nothing else leaves the machine. unsafe-eval is for
    // libraries that compile templates at runtime (Vue, Alpine, math.js).
    private static readonly string Csp =
        "default-src 'none'; " +
        $"script-src 'self' 'unsafe-inline' 'unsafe-eval' blob: {CdnSources}; " +
        $"style-src 'self' 'unsafe-inline' {CdnSources}; " +
        $"font-src 'self' data: {CdnSources}; " +
        $"img-src 'self' data: blob: {CdnSources}; " +
        "media-src 'self' data: blob:; " +
        $"connect-src 'self' {CdnSources}; " +
        "worker-src 'self' blob:; child-src blob:; frame-src 'none'; object-src 'none'; " +
        "base-uri 'self'; form-action 'none'; manifest-src 'none'";

    private readonly Dictionary<string, string> _documents = new(StringComparer.OrdinalIgnoreCase);
    private CoreWebView2? _core;
    private string? _currentHost;
    private TaskCompletionSource? _loading;

    public CanvasTraffic Traffic { get; private set; } = new();

    /// <summary>The page threw (script error, failed library load).</summary>
    public event EventHandler<string>? PageError;

    /// <summary>A request was served or refused; the counters changed.</summary>
    public event EventHandler? TrafficChanged;

    public void Attach(CoreWebView2 core)
    {
        if (ReferenceEquals(core, _core)) return;
        _core = core;

        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = true;
        settings.AreDefaultScriptDialogsEnabled = true;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.DOMContentLoaded += (_, _) => OnPageLoaded();
        core.NavigationCompleted += (_, _) => OnPageLoaded();
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.WebMessageReceived += OnWebMessageReceived;
    }

    /// <param name="originKey">Stable per logical artifact, so its storage
    /// survives new versions and app restarts.</param>
    /// <returns>Completes when the page's DOM is ready — the point where
    /// showing it no longer means showing an empty rectangle.</returns>
    public Task Show(string originKey, string html)
    {
        if (_core is null) return Task.CompletedTask;
        var host = "c-" + originKey + HostSuffix;
        _documents[host] = html;
        _currentHost = host;
        Traffic = new CanvasTraffic();
        TrafficChanged?.Invoke(this, EventArgs.Empty);

        _loading?.TrySetResult();
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loading = loading;
        // The query only defeats the page cache; the document is served from memory.
        _core.Navigate($"https://{host}/?v={Environment.TickCount64}");
        return loading.Task;
    }

    private void OnPageLoaded()
    {
        // Only the page that was asked for: the about:blank a hidden canvas
        // navigated to can finish after the next page has been requested.
        if (_core is null || _currentHost is null) return;
        if (!Uri.TryCreate(_core.Source, UriKind.Absolute, out var source)
            || !string.Equals(source.Host, _currentHost, StringComparison.OrdinalIgnoreCase)) return;
        _loading?.TrySetResult();
    }

    /// <summary>Stops the running page (timers, audio, animation frames) while
    /// the canvas shows something native instead.</summary>
    public void Blank()
    {
        _currentHost = null;
        try
        {
            _core?.Navigate("about:blank");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
    }

    public void ApplyTheme(CanvasTheme theme)
    {
        if (_core is null || _currentHost is null) return;
        _ = _core.ExecuteScriptAsync(theme.Script());
    }

    // ---- documents ------------------------------------------------------------------

    [GeneratedRegex(@"<html\b([^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlOpen();

    [GeneratedRegex(@"<head\b[^>]*>([\s\S]*?)</head\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadBlock();

    [GeneratedRegex(@"<body\b([^>]*)>([\s\S]*?)(?:</body\s*>|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BodyBlock();

    [GeneratedRegex(@"<!doctype[^>]*>|</?html\b[^>]*>|<head\b[^>]*>[\s\S]*?</head\s*>|</?body\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentShell();

    /// <summary>
    /// Re-assembles the model's page with our head first: charset, viewport,
    /// theme variables and the error bridge, then the page's own head. The
    /// page's html/body attributes are kept; everything else it wrote is used
    /// as written.
    /// </summary>
    public static string BuildHtml(string source, CanvasTheme theme)
    {
        var htmlAttributes = HtmlOpen().Match(source) is { Success: true } html ? html.Groups[1].Value : string.Empty;
        var userHead = HeadBlock().Match(source) is { Success: true } head ? head.Groups[1].Value : string.Empty;
        string bodyAttributes;
        string body;
        if (BodyBlock().Match(source) is { Success: true } bodyMatch)
        {
            bodyAttributes = bodyMatch.Groups[1].Value;
            body = bodyMatch.Groups[2].Value;
        }
        else
        {
            bodyAttributes = string.Empty;
            body = DocumentShell().Replace(source, string.Empty);
        }

        return "<!doctype html><html" + htmlAttributes + "><head><meta charset=\"utf-8\">"
               + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
               + theme.Style() + RuntimeScript + userHead
               + "</head><body" + bodyAttributes + ">" + body + "</body></html>";
    }

    public static string BuildMermaid(string source, CanvasTheme theme)
    {
        var diagram = JsonSerializer.Serialize(source);
        var mermaidTheme = theme.Dark ? "dark" : "neutral";
        return "<!doctype html><html><head><meta charset=\"utf-8\">"
               + theme.Style() + RuntimeScript
               + "<style>html,body{height:100%;margin:0}#canvas{min-height:100%;display:flex;align-items:center;justify-content:center;padding:20px;box-sizing:border-box}"
               + "#canvas svg{max-width:100%;height:auto}</style>"
               + "</head><body><div id=\"canvas\"><pre class=\"mermaid\" id=\"diagram\"></pre></div>"
               + "<script>document.getElementById('diagram').textContent=" + diagram + ";</script>"
               + "<script src=\"/_mola/mermaid.min.js\"></script>"
               + "<script>mermaid.initialize({startOnLoad:false,securityLevel:'strict',theme:'" + mermaidTheme + "'});"
               + "mermaid.run({nodes:[document.getElementById('diagram')]}).catch(function(e){throw e;});</script>"
               + "</body></html>";
    }

    /// <summary>Reports errors to the host (and shows them in the page, so a
    /// broken page says so instead of sitting blank), and lets the host restyle
    /// the page when the app's theme changes without reloading it.</summary>
    private const string RuntimeScript = """
<script>(function(){
var post=function(m){try{window.chrome.webview.postMessage(JSON.stringify(m));}catch(e){}};
var box=null;
var show=function(text){try{
if(!document.body){return;}
if(!box){box=document.createElement('div');box.setAttribute('data-mola-error','');
box.style.cssText='position:fixed;left:12px;right:12px;bottom:12px;z-index:2147483647;max-height:40vh;overflow:auto;padding:10px 12px;border-radius:8px;font:12px/1.5 Consolas,monospace;white-space:pre-wrap;background:#fdecec;color:#8a1c1c;border:1px solid #e8b0b0';
box.onclick=function(){box.remove();box=null;};document.body.appendChild(box);}
box.textContent=(box.textContent?box.textContent+'\n':'')+text;}catch(e){}};
var report=function(text){text=String(text||'未知错误');post({type:'error',message:text});show(text);};
window.addEventListener('error',function(e){
var t=e.target;
if(t&&t!==window&&(t.tagName==='SCRIPT'||t.tagName==='LINK')){report('资源加载失败：'+(t.src||t.href));return;}
if(t&&t!==window){return;}
report((e.error&&e.error.stack)||e.message);},true);
window.addEventListener('unhandledrejection',function(e){var r=e.reason;report((r&&r.stack)||r);});
document.addEventListener('securitypolicyviolation',function(e){post({type:'blocked',uri:String(e.blockedURI||'')});});
window.__molaTheme=function(t){var r=document.documentElement;r.style.colorScheme=t.scheme;for(var k in t.vars){r.style.setProperty(k,t.vars[k]);}};
})();</script>
""";

    // ---- request policy -----------------------------------------------------------------

    private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_core is not { } core) return;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;

        if (uri.Host.EndsWith(HostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            e.Response = ServeLocal(core, uri);
            return;
        }

        if (uri.Scheme is not ("http" or "https")) return;

        if (!CdnProxy.IsAllowed(uri.Host))
        {
            Traffic.Blocked++;
            Traffic.BlockedHosts.Add(uri.Host);
            TrafficChanged?.Invoke(this, EventArgs.Empty);
            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
            return;
        }

        var traffic = Traffic;
        var deferral = e.GetDeferral();
        try
        {
            var response = await CdnProxy.FetchAsync(uri, CancellationToken.None);
            if (response is null)
            {
                traffic.Failed++;
                e.Response = core.Environment.CreateWebResourceResponse(null, 502, "Unreachable", "Content-Type: text/plain");
            }
            else
            {
                traffic.Loaded++;
                traffic.LoadedHosts.Add(uri.Host);
                e.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(response.Body), 200, "OK",
                    $"Content-Type: {response.ContentType}\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: max-age=86400");
            }

            if (ReferenceEquals(traffic, Traffic)) TrafficChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The page went away while we were fetching.
        }
        finally
        {
            deferral.Complete();
        }
    }

    private CoreWebView2WebResourceResponse ServeLocal(CoreWebView2 core, Uri uri)
    {
        var path = uri.AbsolutePath;
        if (path is "/" or "/index.html" && _documents.TryGetValue(uri.Host, out var html))
        {
            return core.Environment.CreateWebResourceResponse(
                new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK",
                "Content-Type: text/html; charset=utf-8\r\n"
                + "Cache-Control: no-store\r\n"
                + "Content-Security-Policy: " + Csp);
        }

        if (path.StartsWith("/_mola/", StringComparison.Ordinal))
        {
            var name = Path.GetFileName(path);
            var file = Path.Combine(AppContext.BaseDirectory, "Assets", "vendor", name);
            if (name.Length > 0 && File.Exists(file))
            {
                return core.Environment.CreateWebResourceResponse(
                    File.OpenRead(file), 200, "OK",
                    "Content-Type: application/javascript; charset=utf-8\r\nCache-Control: max-age=86400");
            }
        }

        return core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
    }

    /// <summary>A page cannot navigate itself anywhere. A link the user
    /// actually clicked opens in their browser instead.</summary>
    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) { e.Cancel = true; return; }
        if (uri.Host.EndsWith(HostSuffix, StringComparison.OrdinalIgnoreCase)) return;
        if (e.Uri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase)) return;

        e.Cancel = true;
        if (e.IsUserInitiated) OpenExternally(uri);
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.IsUserInitiated && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) OpenExternally(uri);
    }

    private static void OpenExternally(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
            if (type == "error" && root.TryGetProperty("message", out var message))
            {
                PageError?.Invoke(this, message.GetString() ?? string.Empty);
            }
            else if (type == "blocked" && root.TryGetProperty("uri", out var blocked))
            {
                // The CSP stops these inside the page, before any request exists
                // for the resource filter to see; the page reports them itself.
                Traffic.Blocked++;
                if (Uri.TryCreate(blocked.GetString(), UriKind.Absolute, out var uri) && uri.Host.Length > 0)
                    Traffic.BlockedHosts.Add(uri.Host);
                TrafficChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
        }
    }
}
