namespace MolaGPT.Core.Models;

/// <summary>
/// Source metadata used by MolaGPT citation tags:
/// <c>&lt;ref source="1" /&gt;</c> maps to one of these records.
/// </summary>
public sealed record SourceReference(int Id, string Title, string Url, string? PublishedDate = null)
{
    /// <summary>
    /// Prefix that tells the renderer a markdown link is a citation marker and
    /// not prose, so it can draw the source pill instead of an underlined
    /// "[来源 3]".
    ///
    /// It lives in the link <i>title</i> — the one part of a markdown link this
    /// app never draws — because the alternative, a sentinel in the link text,
    /// ends up in whatever the user selects and copies. What follows the prefix
    /// is the rest of the source, packed by <see cref="Pack"/>.
    /// </summary>
    public const string CitationTitlePrefix = "molagpt-cite:";

    /// <summary>
    /// Separator inside the packed payload: U+001F UNIT SEPARATOR. A control
    /// character because the fields it divides are a page title and a date, and
    /// both of those are free text that has already surprised us once.
    /// </summary>
    private const char PackSeparator = '';

    /// <summary>Everything the renderer needs about this source, as one string
    /// it can carry through markdown. The URL is not in here — it is the link's
    /// own destination.</summary>
    public string Pack() => $"{CitationTitlePrefix}{PublishedDate}{PackSeparator}{Title}";

    /// <summary>Reads back what <see cref="Pack"/> wrote. Returns false for an
    /// ordinary link title.</summary>
    public static bool TryUnpack(string? packed, out string? date, out string title)
    {
        date = null;
        title = string.Empty;
        if (packed is null || !packed.StartsWith(CitationTitlePrefix, StringComparison.Ordinal))
            return false;

        var body = packed[CitationTitlePrefix.Length..];
        var cut = body.IndexOf(PackSeparator);
        if (cut < 0)
        {
            title = body;
            return true;
        }

        date = body[..cut] is { Length: > 0 } d ? d : null;
        title = body[(cut + 1)..];
        return true;
    }

    /// <summary>
    /// What to print on the pill for a URL — the site, as close to how a reader
    /// would name it as a hostname allows.
    ///
    /// Derived rather than carried: the backend sends only id/title/url, and a
    /// site's own name ("中央社 CNA") is nowhere in that. The registrable host
    /// with "www." dropped is the honest approximation, and it is never wrong
    /// the way a guessed display name can be.
    /// </summary>
    public static string SiteOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        var host = uri.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];

        // Left exactly as the host writes it. Capitalising the first letter
        // flatters "investing.com" and mangles every acronym domain there is —
        // "Cna.com.tw", "Bbc.co.uk" — and a name the reader half-recognises is
        // worse than one that is plainly a domain.
        return host.Length == 0 ? url : host;
    }
}
