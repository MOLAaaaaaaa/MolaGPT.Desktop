using Avalonia.Media.Imaging;
using System.Text;

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
    /// <summary>Matches MarkdownPresenter.MarkdownImageCacheCapacity. Small on
    /// purpose — these are decoded surfaces, not compressed bytes.</summary>
    private const int CacheCapacity = 32;

    private static readonly Dictionary<string, Bitmap> s_cache = new(StringComparer.Ordinal);
    private static readonly List<string> s_order = [];
    private static readonly SemaphoreSlim s_gate = new(1, 1);

    private static readonly HttpClient s_http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    /// <summary>
    /// Loads <paramref name="url"/>, decoded to at most <paramref name="decodeWidth"/>
    /// device pixels wide. Returns null when the URL cannot be resolved — an
    /// unreachable image is an ordinary outcome in a transcript, not an error.
    /// </summary>
    public static async Task<Bitmap?> LoadAsync(string url, int decodeWidth, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var key = $"{url}|{decodeWidth}";
        lock (s_cache)
        {
            if (s_cache.TryGetValue(key, out var hit))
            {
                // Refresh recency: an image the viewport is still showing must
                // not be evicted by one scrolled past long ago.
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
            while (s_cache.Count >= CacheCapacity && s_order.Count > 0)
            {
                var oldest = s_order[0];
                s_order.RemoveAt(0);
                if (s_cache.Remove(oldest, out var evicted)) evicted.Dispose();
            }

            if (s_cache.TryGetValue(key, out var raced))
            {
                bitmap.Dispose();
                return raced;
            }

            s_cache[key] = bitmap;
            s_order.Add(key);
        }

        return bitmap;
    }

    private static async Task<Bitmap?> DecodeAsync(string url, int decodeWidth, CancellationToken ct)
    {
        if (url.Length == 0) return null;

        if (TryResolveLocalPath(url, out var path))
        {
            await using var file = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            return Decode(await BufferAsync(file, ct).ConfigureAwait(false), decodeWidth);
        }

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            if (comma < 0) return null;
            var payload = url[(comma + 1)..];
            if (!url[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase)) return null;
            return Decode(new MemoryStream(Convert.FromBase64String(payload)), decodeWidth);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;

        // Throttled to one download at a time: a transcript scrolled quickly can
        // ask for dozens at once, and the images are decorative — none of them
        // is worth saturating the connection the streaming response is using.
        await s_gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var bytes = await s_http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
            return Decode(new MemoryStream(bytes), decodeWidth);
        }
        finally
        {
            s_gate.Release();
        }
    }

    private static async Task<MemoryStream> BufferAsync(Stream source, CancellationToken ct)
    {
        var memory = new MemoryStream();
        await source.CopyToAsync(memory, ct).ConfigureAwait(false);
        memory.Position = 0;
        return memory;
    }

    internal static Bitmap? Decode(MemoryStream stream, int decodeWidth)
    {
        using (stream)
        {
            stream.Position = 0;
            if (IsSvg(stream)) return null;

            stream.Position = 0;
            return decodeWidth > 0
                ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality)
                : new Bitmap(stream);
        }
    }

    private static bool IsSvg(Stream stream)
    {
        Span<byte> prefix = stackalloc byte[1024];
        var read = stream.Read(prefix);
        stream.Position = 0;
        if (read == 0) return false;

        return Encoding.UTF8.GetString(prefix[..read])
            .Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

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
