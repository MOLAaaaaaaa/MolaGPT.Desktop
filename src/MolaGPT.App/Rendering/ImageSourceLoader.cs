using Avalonia.Media.Imaging;
using MolaGPT.Core.Net;
using SkiaSharp;
using System.Runtime.CompilerServices;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Turns a markdown image URL into a bitmap.
///
/// The URL shapes that reach here are the ones MarkdownPresenter already had to
/// cope with, and they are not all http: a tool that wrote a chart to the
/// session working directory emits a <c>file://</c> URI or a bare Windows path,
/// and an inlined thumbnail arrives as a <c>data:</c> URI. Handling only http
/// looks like "images are broken" for exactly the cases the app generates
/// itself, so all three are resolved here.
///
/// Decoding is capped by width rather than kept at native resolution: a 4000px
/// screenshot displayed in a 640px card costs ~40MB of bitmap for no visible
/// gain, and a transcript holds many of them.
/// </summary>
public static class ImageSourceLoader
{
    private const long CacheBudget = 32L * 1024 * 1024;
    internal const int MaxEncodedBytes = 32 * 1024 * 1024;
    private const long RemoteSourceBudget = 32L * 1024 * 1024;
    private const int RemoteSourceCapacity = 32;
    private static readonly Dictionary<(string Url, int Width), Bitmap> s_cache = new();
    private static readonly List<(string Url, int Width)> s_order = [];
    private static readonly ConditionalWeakTable<Bitmap, BitmapMemoryPressure> s_pressure = new();
    private static readonly LinkedList<(string Url, byte[] Bytes)> s_remoteSources = new();
    private static long s_cacheBytes;
    private static long s_remoteSourceBytes;
    private static readonly SemaphoreSlim s_gate = new(1, 1);

    private static readonly HttpClient s_http = new(new HttpClientHandler
        { AutomaticDecompression = System.Net.DecompressionMethods.All });

    internal static void TrimForBackground()
    {
        lock (s_cache)
        {
            // Keep remote images available even if their URL expires or the
            // connection goes offline. Controls still own displayed bitmaps.
            foreach (var key in s_order.ToArray())
            {
                var url = key.Url.Trim().Trim('"', '\'');
                if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    && !TryResolveLocalPath(url, out _)) continue;
                RemoveCached(key);
            }
        }
    }

    /// <summary>
    /// Loads <paramref name="url"/>, decoded to at most <paramref name="decodeWidth"/>
    /// device pixels wide. Returns null when the URL cannot be resolved — an
    /// unreachable image is an ordinary outcome in a transcript, not an error.
    /// </summary>
    public static async Task<Bitmap?> LoadAsync(string url, int decodeWidth, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        ct.ThrowIfCancellationRequested();
        var key = (url, decodeWidth);
        lock (s_cache)
        {
            if (s_cache.TryGetValue(key, out var hit))
            {
                // Keep recently requested images cached for reuse.
                s_order.Remove(key);
                s_order.Add(key);
                return hit;
            }
        }

        Bitmap? bitmap = null;
        try
        {
            bitmap = await DecodeAsync(url.Trim().Trim('"', '\''), decodeWidth, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Malformed URL, unreadable file, HTTP failure, corrupt payload:
            // all of them mean the same thing to the caller.
        }

        if (bitmap is null) return null;

        lock (s_cache)
        {
            if (s_cache.TryGetValue(key, out var raced))
            {
                bitmap.Dispose();
                return raced;
            }

            var bytes = BitmapBytes(bitmap);
            s_pressure.Add(bitmap, new BitmapMemoryPressure(bytes));
            if (bytes <= CacheBudget)
            {
                while (s_cacheBytes + bytes > CacheBudget) RemoveCached(s_order[0]);
                s_cache[key] = bitmap;
                s_order.Add(key);
                s_cacheBytes += bytes;
            }
        }

        return bitmap;
    }

    private static long BitmapBytes(Bitmap bitmap) =>
        (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;

    private static void RemoveCached((string Url, int Width) key)
    {
        // The view can still be displaying this shared bitmap.
        s_cache.Remove(key, out var bitmap);
        s_cacheBytes -= BitmapBytes(bitmap!);
        s_order.Remove(key);
    }

    private sealed class BitmapMemoryPressure
    {
        private readonly long _bytes;

        public BitmapMemoryPressure(long bytes)
        {
            _bytes = bytes;
            GC.AddMemoryPressure(bytes);
        }

        ~BitmapMemoryPressure() => GC.RemoveMemoryPressure(_bytes);
    }

    private static async Task<Bitmap?> DecodeAsync(string url, int decodeWidth, CancellationToken ct)
    {
        if (url.Length == 0) return null;

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            if (comma < 0) return null;
            if (!url.AsSpan(0, comma).Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;
            var payload = url.AsSpan(comma + 1);
            var chars = 0;
            var padding = 0;
            foreach (var c in payload)
            {
                if (c is ' ' or '\r' or '\n' or '\t') continue;
                chars++;
                if (c == '=' && ++padding > 2) return null;
            }
            var size = (long)chars / 4 * 3 - padding;
            if (size is <= 0 or > MaxEncodedBytes || chars % 4 != 0) return null;
            var bytes = new byte[(int)size];
            return Convert.TryFromBase64Chars(payload, bytes, out _) ? Decode(bytes, decodeWidth) : null;
        }

        if (TryResolveLocalPath(url, out var path))
            return Decode(await ReadFileAsync(path, ct).ConfigureAwait(false), decodeWidth);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;

        var source = FindRemoteSource(url);
        if (source is null)
        {
            // Keep downloads serial so they don't compete with the streaming reply.
            await s_gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                source = FindRemoteSource(url);
                if (source is null)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    using var response = await s_http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > MaxEncodedBytes) return null;
                    using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    var bytes = await BoundedRead.ReadAsync(body, MaxEncodedBytes, timeout.Token).ConfigureAwait(false);
                    var bitmap = Decode(bytes, decodeWidth);
                    if (bitmap is not null) CacheRemoteSource(url, bytes);
                    return bitmap;
                }
            }
            finally
            {
                s_gate.Release();
            }
        }

        // A cache hit bypasses the asynchronous download, so decode off the UI thread.
        return await Task.Run(() => Decode(source, decodeWidth), ct)
            .ConfigureAwait(false);
    }

    private static byte[]? FindRemoteSource(string url)
    {
        lock (s_remoteSources)
        {
            for (var node = s_remoteSources.First; node is not null; node = node.Next)
            {
                if (node.Value.Url != url) continue;
                s_remoteSources.Remove(node);
                s_remoteSources.AddFirst(node);
                return node.Value.Bytes;
            }
        }
        return null;
    }

    private static void CacheRemoteSource(string url, byte[] bytes)
    {
        if (bytes.Length > RemoteSourceBudget) return;
        // Keep the encoded image available after the bitmap is evicted, even
        // if the URL has since expired. Callers insert under the download gate.
        lock (s_remoteSources)
        {
            while (s_remoteSources.Count >= RemoteSourceCapacity || s_remoteSourceBytes + bytes.Length > RemoteSourceBudget)
            {
                s_remoteSourceBytes -= s_remoteSources.Last!.Value.Bytes.Length;
                s_remoteSources.RemoveLast();
            }
            s_remoteSources.AddFirst((url, bytes));
            s_remoteSourceBytes += bytes.Length;
        }
    }

    internal static async Task<byte[]> ReadFileAsync(string path, CancellationToken ct = default)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        return await BoundedRead.ReadAsync(file, MaxEncodedBytes, ct).ConfigureAwait(false);
    }

    internal static Bitmap? Decode(byte[] bytes, int decodeWidth)
    {
        if (bytes.Length > MaxEncodedBytes) return null;
        using (var probe = new MemoryStream(bytes, writable: false))
        using (var codec = SKCodec.Create(probe))
        {
            if (codec is null || !ValidSize(codec.Info.Width, codec.Info.Height)) return null;
            if (decodeWidth > 0 && !ValidSize(decodeWidth,
                Math.Max(1, ((long)decodeWidth * codec.Info.Height + codec.Info.Width - 1) / codec.Info.Width))) return null;
        }
        using var stream = new MemoryStream(bytes, writable: false);
        return decodeWidth > 0 ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality) : new Bitmap(stream);
    }

    internal static bool ValidSize(int width, long height) =>
        width is > 0 and <= 16_384 && height is > 0 and <= 16_384 && width * height <= 40_000_000;

    /// <summary>
    /// Resolves the local-file shapes a markdown image URL can take, in order of
    /// how literally they should be read.
    ///
    /// Every candidate is tried rather than committing to the first
    /// interpretation that parses. A percent-escaped Windows path parses
    /// perfectly well as a file URI, so returning on that branch alone rejects
    /// paths that do exist — and a filename containing a literal "%20" would be
    /// rejected by the opposite shortcut. Trying both and taking whichever
    /// actually names a file on disk is the only reading that handles both.
    /// </summary>
    public static bool TryResolveLocalPath(string url, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return false;

        var raw = url.Trim().Trim('"', '\'');
        if (raw.Length == 0) return false;

        var unescaped = SafeUnescape(raw).Trim().Trim('"', '\'');

        foreach (var candidate in Candidates(raw, unescaped))
        {
            if (candidate is not { Length: > 0 } || !File.Exists(candidate)) continue;
            path = Path.GetFullPath(candidate);
            return true;
        }

        return false;
    }

    private static IEnumerable<string?> Candidates(string raw, string unescaped)
    {
        // As written, before any unescaping: the only reading that survives a
        // filename with a literal % in it.
        if (LooksLikeWindowsPath(raw)) yield return raw.Replace('/', Path.DirectorySeparatorChar);
        yield return raw;

        // file:// URIs, which Uri already unescapes for us.
        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute) && absolute.IsFile)
            yield return absolute.LocalPath;

        // A path that came back through a URL, with %20 for every space.
        if (!string.Equals(raw, unescaped, StringComparison.Ordinal))
        {
            if (LooksLikeWindowsPath(unescaped)) yield return unescaped.Replace('/', Path.DirectorySeparatorChar);
            yield return unescaped;
        }
    }

    private static string SafeUnescape(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return value; }
    }

    private static bool LooksLikeWindowsPath(string value) =>
        (value.Length >= 3
         && char.IsLetter(value[0])
         && value[1] == ':'
         && value[2] is '\\' or '/')
        || value.StartsWith(@"\\", StringComparison.Ordinal)
        || value.StartsWith("//", StringComparison.Ordinal);
}
