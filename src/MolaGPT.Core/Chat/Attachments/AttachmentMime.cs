using System.IO;
using System.Text;

namespace MolaGPT.Core.Chat.Attachments;

/// <summary>How an attachment's bytes should be turned into prompt content.</summary>
public enum AttachmentDocumentKind
{
    /// <summary>Bytes decode as UTF-8 text and can be inlined verbatim.</summary>
    Text,
    Pdf,
    Docx,
    Xlsx,
    Pptx,
    /// <summary>Pre-2007 Office binary formats (.doc/.xls/.ppt). We deliberately
    /// refuse to decode these rather than emit mojibake.</summary>
    LegacyOffice,
    /// <summary>Anything we cannot turn into text (archives, executables, media).</summary>
    Opaque
}

/// <summary>
/// Content-first attachment classification. File extensions are a hint, not the
/// answer: an image renamed to <c>.txt</c> must not be inlined as UTF-8, and a
/// <c>.log</c> / <c>.tex</c> / <c>.bib</c> file must not be pushed down the
/// binary path just because it is missing from a whitelist. Every predicate here
/// matches anchored (exact / prefix / suffix) — never <c>Contains</c>, which is
/// how "openxmlformats…document" gets mistaken for XML.
/// </summary>
public static class AttachmentMime
{
    /// <summary>Bytes inspected when sniffing a signature. Enough to cover the
    /// longest header we look at (ISO-BMFF brand list) with room to spare.</summary>
    private const int SniffLength = 64;

    /// <summary>Cap for the UTF-8 validity probe used to recognise text files
    /// whose extension we do not know. Scanning the head is sufficient — a file
    /// that is text for its first 64KB is text for our purposes.</summary>
    private const int TextProbeBytes = 64 * 1024;

    /// <summary>
    /// Detect an image MIME type from the leading bytes. Returns null when the
    /// bytes are not a recognised image, regardless of what the file name or the
    /// caller-supplied MIME claims.
    /// </summary>
    public static string? SniffImageMime(byte[]? bytes)
    {
        if (bytes is not { Length: >= 12 }) return null;
        var head = bytes.AsSpan(0, Math.Min(SniffLength, bytes.Length));

        if (StartsWith(head, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
            return "image/png";
        if (StartsWith(head, [0xFF, 0xD8, 0xFF]))
            return "image/jpeg";
        if (StartsWithAscii(head, 0, "GIF8"))
            return "image/gif";
        if (StartsWithAscii(head, 0, "RIFF") && StartsWithAscii(head, 8, "WEBP"))
            return "image/webp";
        if (StartsWithAscii(head, 0, "BM"))
            return "image/bmp";
        if (StartsWith(head, [0x49, 0x49, 0x2A, 0x00]) || StartsWith(head, [0x4D, 0x4D, 0x00, 0x2A]))
            return "image/tiff";

        // ISO base media container: [size][ftyp][brand]. HEIC/HEIF/AVIF all live
        // here and are told apart by the major brand, which modern phone cameras
        // emit in several spellings.
        if (StartsWithAscii(head, 4, "ftyp"))
        {
            var brand = ReadAscii(head, 8, 4);
            return brand switch
            {
                "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" => "image/heic",
                "mif1" or "msf1" => "image/heif",
                "avif" or "avis" => "image/avif",
                _ => null
            };
        }

        return null;
    }

    /// <summary>MIME types every mainstream provider accepts inline. Anything
    /// else has to be transcoded before it can be sent.</summary>
    public static bool IsInlineSafeImageMime(string? mimeType) =>
        Normalize(mimeType) is "image/png" or "image/jpeg" or "image/gif" or "image/webp";

    /// <summary>
    /// Decide how a non-image attachment should be converted to prompt text.
    /// <paramref name="bytes"/> is optional; when supplied it overrides the
    /// extension for container formats and enables the UTF-8 probe that rescues
    /// text files with unfamiliar extensions.
    /// </summary>
    public static AttachmentDocumentKind ClassifyDocument(string? mimeType, string? fileName, byte[]? bytes = null)
    {
        var ext = GetExtension(fileName);

        // Legacy Office is checked first: the bytes are an OLE compound file that
        // no probe below would classify usefully, and decoding it as text is the
        // classic mojibake bug.
        if (ext is "doc" or "xls" or "ppt" && !IsZipContainer(bytes))
            return AttachmentDocumentKind.LegacyOffice;

        if (ext == "pdf" || IsPdf(bytes))
            return AttachmentDocumentKind.Pdf;

        // OOXML is a zip; trust the extension only when the bytes agree (or are
        // unavailable), so a renamed archive cannot be fed to the docx parser.
        if (bytes is null || IsZipContainer(bytes))
        {
            switch (ext)
            {
                case "docx" or "docm": return AttachmentDocumentKind.Docx;
                case "xlsx" or "xlsm": return AttachmentDocumentKind.Xlsx;
                case "pptx" or "pptm": return AttachmentDocumentKind.Pptx;
            }
        }

        if (IsKnownTextExtension(ext)) return AttachmentDocumentKind.Text;
        if (IsTextMime(mimeType) && !IsZipContainer(bytes)) return AttachmentDocumentKind.Text;

        // Unknown extension: let the bytes decide. This is what makes .log/.tex/
        // .bib/.ini/.conf work without maintaining an ever-growing whitelist.
        if (bytes is { Length: > 0 } && LooksLikeUtf8Text(bytes)) return AttachmentDocumentKind.Text;

        return AttachmentDocumentKind.Opaque;
    }

    /// <summary>True when the attachment can be turned into prompt text without
    /// the Python tool — i.e. everything except opaque binaries and the legacy
    /// Office formats we refuse to guess at.</summary>
    public static bool CanExtractText(AttachmentDocumentKind kind) => kind
        is AttachmentDocumentKind.Text
        or AttachmentDocumentKind.Pdf
        or AttachmentDocumentKind.Docx
        or AttachmentDocumentKind.Xlsx
        or AttachmentDocumentKind.Pptx;

    /// <summary>Short uppercase label for the composer chip ("PDF", "DOCX", …).</summary>
    public static string ChipLabel(string? fileName)
    {
        var ext = GetExtension(fileName);
        return ext.Length == 0 ? "文件" : ext.ToUpperInvariant();
    }

    private static bool IsTextMime(string? mimeType)
    {
        var mime = Normalize(mimeType);
        if (mime.StartsWith("text/", StringComparison.Ordinal)) return true;
        return mime is "application/json" or "application/xml" or "application/javascript"
            or "application/x-yaml" or "application/yaml" or "application/toml";
    }

    private static bool IsKnownTextExtension(string ext) => ext is
        "md" or "markdown" or "txt" or "text" or "log" or "csv" or "tsv" or "json" or "jsonl"
        or "xml" or "yaml" or "yml" or "toml" or "ini" or "conf" or "cfg" or "env" or "properties"
        or "html" or "htm" or "css" or "scss" or "less" or "svg"
        or "py" or "js" or "mjs" or "cjs" or "ts" or "tsx" or "jsx" or "cs" or "java" or "kt"
        or "go" or "rs" or "c" or "cc" or "cpp" or "cxx" or "h" or "hpp" or "m" or "mm"
        or "swift" or "rb" or "php" or "pl" or "lua" or "r" or "jl" or "sql" or "sh" or "bash"
        or "ps1" or "bat" or "cmd" or "dockerfile" or "makefile" or "gradle" or "tex" or "bib"
        or "vue" or "svelte" or "ipynb" or "patch" or "diff";

    private static bool IsPdf(byte[]? bytes) =>
        bytes is { Length: >= 5 } && StartsWithAscii(bytes.AsSpan(), 0, "%PDF-");

    private static bool IsZipContainer(byte[]? bytes) =>
        bytes is { Length: >= 4 }
        && bytes[0] == 'P' && bytes[1] == 'K'
        && (bytes[2] == 0x03 || bytes[2] == 0x05 || bytes[2] == 0x07);

    /// <summary>
    /// Strict UTF-8 validation over the head of the file, plus a NUL check.
    /// Strict decoding is the point: it rejects UTF-16, legacy code pages and
    /// arbitrary binaries instead of silently producing replacement characters.
    /// </summary>
    private static bool LooksLikeUtf8Text(byte[] bytes)
    {
        var length = Math.Min(bytes.Length, TextProbeBytes);
        for (var i = 0; i < length; i++)
        {
            if (bytes[i] == 0) return false;
        }

        // A truncated probe can split a multi-byte sequence; back off to the last
        // byte that can legally start one so a clean file is not rejected.
        if (length < bytes.Length)
        {
            var end = length;
            while (end > 0 && (bytes[end - 1] & 0b1100_0000) == 0b1000_0000) end--;
            if (end > 0) end--;              // drop the lead byte itself
            length = end;
        }
        if (length == 0) return false;

        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes, 0, length);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Normalize(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return string.Empty;
        var value = mimeType!.Trim();
        var semicolon = value.IndexOf(';');
        if (semicolon >= 0) value = value[..semicolon];
        return value.Trim().ToLowerInvariant();
    }

    private static string GetExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
        var name = Path.GetFileName(fileName!.Trim());
        var ext = Path.GetExtension(name);
        if (ext.Length > 1) return ext[1..].ToLowerInvariant();

        // Extension-less conventions that are still plain text.
        return name.ToLowerInvariant() is "dockerfile" or "makefile" ? name.ToLowerInvariant() : string.Empty;
    }

    private static bool StartsWith(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> signature) =>
        buffer.Length >= signature.Length && buffer[..signature.Length].SequenceEqual(signature);

    private static bool StartsWithAscii(ReadOnlySpan<byte> buffer, int offset, string ascii)
    {
        if (offset < 0 || buffer.Length < offset + ascii.Length) return false;
        for (var i = 0; i < ascii.Length; i++)
        {
            if (buffer[offset + i] != (byte)ascii[i]) return false;
        }
        return true;
    }

    private static string ReadAscii(ReadOnlySpan<byte> buffer, int offset, int length)
    {
        if (offset < 0 || buffer.Length < offset + length) return string.Empty;
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++) chars[i] = (char)buffer[offset + i];
        return new string(chars).ToLowerInvariant();
    }
}
