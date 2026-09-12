using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace MolaGPT.App.Views;

/// <summary>
/// Decoded persona portraits, shared by every <see cref="PersonaAvatar"/> on
/// screen.
///
/// A portrait is stored as a data URI holding a PNG up to 384px on its long side
/// (<see cref="PersonaAvatar.EncodeImage"/>). Decoding one of those whole costs
/// about 590 KB of pixels, and the same portrait shows up in four places at
/// once — the composer chip at 20px, the picker row at 28, the welcome card at
/// 36, and every message it signs. Decoding per control, at full size, is
/// therefore the worst of both: several copies of an image nobody draws at
/// anything near that size.
///
/// So each portrait is decoded once per box size it is actually drawn at — a
/// 28px row on a 1.25 scale screen is a 48×48 bitmap, roughly 9 KB — and the
/// result is handed to every control that asks for the same pair.
///
/// UI thread only, like the controls it serves; there is no lock.
/// </summary>
internal static class PersonaAvatarImages
{
    /// <summary>Past this many distinct (portrait, size) pairs the table is
    /// dropped whole rather than evicted entry by entry. Nothing here is
    /// disposed: a bitmap handed out earlier may still be on screen, and the
    /// entries are small enough that letting the GC take the dropped ones is
    /// cheaper than tracking who still holds which.</summary>
    private const int Capacity = 96;

    private static readonly Dictionary<(string Value, int Pixels), Bitmap?> Cache = new(ByIdentity.Instance);

    /// <summary>The decoded portrait, or null when <paramref name="value"/> is
    /// not readable as one. Failures are cached too — a truncated data URI from
    /// a half-written import should cost one failed decode, not one per row
    /// that scrolls past.</summary>
    internal static Bitmap? Get(string value, int pixels)
    {
        var key = (value, pixels);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        if (Cache.Count >= Capacity) Cache.Clear();
        var bitmap = Decode(value, pixels);
        Cache[key] = bitmap;
        return bitmap;
    }

    private static Bitmap? Decode(string value, int pixels)
    {
        try
        {
            var comma = value.IndexOf(',');
            if (comma < 0) return null;
            var bytes = Convert.FromBase64String(value[(comma + 1)..]);

            // The avatar box is square and fills by cropping, so it is the
            // portrait's *short* side that has to reach the target: scaling the
            // long one down to it leaves Skia an image smaller than the box,
            // which then gets upscaled and looks soft.
            using var probe = new MemoryStream(bytes);
            using var codec = SKCodec.Create(probe);
            var tallerThanWide = codec is null || codec.Info.Height >= codec.Info.Width;

            using var stream = new MemoryStream(bytes);
            return tallerThanWide
                ? Bitmap.DecodeToWidth(stream, pixels, BitmapInterpolationMode.HighQuality)
                : Bitmap.DecodeToHeight(stream, pixels, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keys on the string's identity, not its contents: the base64 payload runs
    /// into the hundreds of KB, and hashing all of it on every row that scrolls
    /// into view would cost more than the decode this table exists to avoid. A
    /// persona view model hands out the same string instance for as long as its
    /// avatar is unchanged, so identity holds. Two personas carrying byte-wise
    /// identical portraits decode twice — that is the whole price of being
    /// wrong here.
    /// </summary>
    private sealed class ByIdentity : IEqualityComparer<(string Value, int Pixels)>
    {
        internal static readonly ByIdentity Instance = new();

        public bool Equals((string Value, int Pixels) a, (string Value, int Pixels) b) =>
            a.Pixels == b.Pixels && ReferenceEquals(a.Value, b.Value);

        public int GetHashCode((string Value, int Pixels) key) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(key.Value), key.Pixels);
    }
}
