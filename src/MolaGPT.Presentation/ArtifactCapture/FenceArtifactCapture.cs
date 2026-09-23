using System.Text;
using System.Text.RegularExpressions;

namespace MolaGPT.Presentation.Artifacts;

public enum ArtifactRenderKind
{
    Image,
    Svg,
    Html,
    Mermaid,
    Table,
    Markdown,
    Code,
}

/// <summary>
/// Decides which fenced code blocks in an answer become canvas artifacts, and
/// what they are called.
///
/// Classification is by language first, so a fence that is still being written
/// already knows what it will become and the transcript can show "generating"
/// instead of flooding the page with markup. Size thresholds only apply once
/// the fence has closed: a three-line HTML snippet in a tutorial stays code.
///
/// Markdown and JSON are deliberately absent. Promoting them hid the text the
/// user was reading behind a chip that opened the very same text elsewhere.
/// </summary>
public static partial class FenceArtifactCapture
{
    /// <summary>Fence language of the inline component protocol. Never a canvas
    /// artifact: it renders in place.</summary>
    public const string UiFenceLanguage = "mola-ui";

    // Undeclared HTML has to be page-sized to leave the transcript: tutorials
    // are full of 10–20 line examples that belong next to their explanation.
    private const int HtmlMinBytes = 1500;
    private const int HtmlMinLines = 30;
    private const int SvgMinBytes = 300;
    private const int SvgMinLines = 6;
    private const int TableMinBytes = 400;
    private const int TableMinLines = 12;

    [GeneratedRegex(@"^\s*(?:<!doctype\s+html|<html\b)", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlDocumentStart();

    [GeneratedRegex(@"^\s*(?:<\?xml[^>]*\?>\s*)?(?:<!--[\s\S]*?-->\s*)*(?:<!doctype\s+svg[^>]*>\s*)?<(?:[a-z_][\w.-]*:)?svg[\s>/]", RegexOptions.IgnoreCase)]
    private static partial Regex SvgDocumentStart();

    [GeneratedRegex(@"<title[^>]*>([^<]{1,80})</title>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTitle();

    public static string NormalizeLanguage(string? info)
    {
        var value = (info ?? string.Empty).Trim();
        var cut = value.IndexOfAny([' ', '\t', ',', ';', '{']);
        if (cut >= 0) value = value[..cut];
        return value.Trim('.', '{', '}').ToLowerInvariant();
    }

    public static bool IsUiFence(CodeBlock block) =>
        string.Equals(NormalizeLanguage(block.Language), UiFenceLanguage, StringComparison.Ordinal);

    /// <summary>What this fence renders as on the canvas, judged by its language
    /// (and, for unlabeled fences, by the document root). Null keeps it code.</summary>
    public static ArtifactRenderKind? ClassifyLanguage(string? language, string code)
    {
        switch (NormalizeLanguage(language))
        {
            case "html" or "htm" or "xhtml":
                return ArtifactRenderKind.Html;
            case "svg" or "image/svg+xml":
                return ArtifactRenderKind.Svg;
            case "mermaid" or "mmd":
                return ArtifactRenderKind.Mermaid;
            case "csv":
                return ArtifactRenderKind.Table;
            case "" or "text" or "txt" or "plain" or "plaintext" or "xml":
                if (SvgDocumentStart().IsMatch(code)) return ArtifactRenderKind.Svg;
                // Only a whole document: an unlabeled fence holding a <div> is far
                // more often an example than something to run.
                if (HtmlDocumentStart().IsMatch(code)) return ArtifactRenderKind.Html;
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Whether this block shows as an artifact chip in the transcript.
    /// An open fence qualifies on its language alone (except CSV, whose small
    /// samples are common enough to stay code until they prove otherwise); a
    /// closed one must also clear the size threshold.
    /// </summary>
    public static bool IsCanvasArtifact(CodeBlock block, out ArtifactRenderKind kind)
    {
        kind = default;
        if (IsUiFence(block)) return false;
        var classified = ClassifyLanguage(block.Language, block.Code);
        if (classified is null) return false;
        kind = classified.Value;

        if (!block.IsClosed)
        {
            return kind switch
            {
                // A page announces itself on its first line (file name or
                // doctype); a demo snippet does not, and stays code unless it
                // grows into something page-sized.
                ArtifactRenderKind.Html => IsDeclaredPage(block.Code) || block.LineCount >= HtmlMinLines,
                ArtifactRenderKind.Svg or ArtifactRenderKind.Mermaid => true,
                _ => false,
            };
        }

        return PassesThreshold(kind, block.Code, block.LineCount);
    }

    private static bool IsDeclaredPage(string code) =>
        HtmlDocumentStart().IsMatch(code) || (ParseFileName(code) is { } name && name.Contains(".htm", StringComparison.OrdinalIgnoreCase))
        || HtmlDocumentStart().IsMatch(SkipFirstLine(code));

    private static string SkipFirstLine(string code)
    {
        var newline = code.IndexOf('\n');
        return newline < 0 ? string.Empty : code[(newline + 1)..Math.Min(code.Length, newline + 200)];
    }

    public static bool PassesThreshold(ArtifactRenderKind kind, string code, int lines)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var bytes = Encoding.UTF8.GetByteCount(code);
        return kind switch
        {
            ArtifactRenderKind.Html => IsDeclaredPage(code) || bytes >= HtmlMinBytes || lines >= HtmlMinLines,
            ArtifactRenderKind.Svg => bytes >= SvgMinBytes || lines >= SvgMinLines,
            ArtifactRenderKind.Mermaid => true,
            ArtifactRenderKind.Table => bytes >= TableMinBytes || lines >= TableMinLines,
            _ => false,
        };
    }

    public static ArtifactRenderKind MapExtension(string? extension)
    {
        var value = (extension ?? string.Empty).Trim();
        if (value.StartsWith('.')) value = value[1..];
        return value.ToLowerInvariant() switch
        {
            "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp" => ArtifactRenderKind.Image,
            "html" or "htm" => ArtifactRenderKind.Html,
            "svg" => ArtifactRenderKind.Svg,
            "csv" => ArtifactRenderKind.Table,
            "mmd" or "mermaid" => ArtifactRenderKind.Mermaid,
            "md" or "markdown" => ArtifactRenderKind.Markdown,
            _ => ArtifactRenderKind.Code,
        };
    }

    /// <summary>Short badge text: what the user would call the format.</summary>
    public static string KindBadge(ArtifactRenderKind kind) => kind switch
    {
        ArtifactRenderKind.Html => "HTML",
        ArtifactRenderKind.Svg => "SVG",
        ArtifactRenderKind.Mermaid => "图表",
        ArtifactRenderKind.Table => "CSV",
        ArtifactRenderKind.Markdown => "MD",
        ArtifactRenderKind.Image => "图片",
        _ => "代码",
    };

    public static string KindLabel(ArtifactRenderKind kind) => kind switch
    {
        ArtifactRenderKind.Html => "网页",
        ArtifactRenderKind.Svg => "SVG 图形",
        ArtifactRenderKind.Mermaid => "Mermaid 图",
        ArtifactRenderKind.Table => "CSV 表格",
        ArtifactRenderKind.Markdown => "Markdown 文档",
        ArtifactRenderKind.Image => "图片",
        _ => "代码",
    };

    /// <summary>
    /// The file name the model declared on the first line (<c>&lt;!-- solar.html --&gt;</c>,
    /// <c>%% flow.mmd</c>, <c># data.csv</c>). This is the artifact's identity across
    /// revisions, so it is only accepted when it looks like a file name.
    /// </summary>
    public static string? ParseFileName(string code)
    {
        // Called on every regroup while a fence streams, so no Split: that
        // would copy the whole body just to look at its first line.
        var start = 0;
        while (start < code.Length)
        {
            var end = code.IndexOf('\n', start);
            if (end < 0) end = code.Length;
            var line = code.AsSpan(start, end - start).Trim();
            if (line.Length > 0) return line.Length > 160 ? null : TryParseFileComment(line.ToString());
            start = end + 1;
        }

        return null;
    }

    /// <summary>Title shown to the user: declared file name, else the page's
    /// &lt;title&gt;, else what kind of thing it is.</summary>
    public static string DisplayTitle(string code, ArtifactRenderKind kind)
    {
        if (ParseFileName(code) is { } name) return name;
        if (kind == ArtifactRenderKind.Html)
        {
            var head = code.Length > 4096 ? code[..4096] : code;
            var match = HtmlTitle().Match(head);
            if (match.Success)
            {
                var title = match.Groups[1].Value.Trim();
                if (title.Length > 0) return title;
            }
        }

        return KindLabel(kind);
    }

    private static string? TryParseFileComment(string line)
    {
        ReadOnlySpan<char> span = line;
        if (span.StartsWith("<!--", StringComparison.Ordinal))
        {
            var end = span.IndexOf("-->", StringComparison.Ordinal);
            span = end > 4 ? span[4..end] : span[4..];
        }
        else if (span.StartsWith("/*", StringComparison.Ordinal))
        {
            var end = span.IndexOf("*/", StringComparison.Ordinal);
            span = end > 2 ? span[2..end] : span[2..];
        }
        else if (span.StartsWith("%%", StringComparison.Ordinal) || span.StartsWith("//", StringComparison.Ordinal))
        {
            span = span[2..];
        }
        else if (span.StartsWith('#') || span.StartsWith(';'))
        {
            span = span[1..];
        }
        else
        {
            return null;
        }

        var text = span.Trim().ToString();
        foreach (var prefix in (string[])["filename:", "file:", "name:", "文件名：", "文件名:", "文件："])
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].Trim();
                break;
            }
        }

        if (text.Length == 0 || text.Length > 64 || text.IndexOfAny(['/', '\\', '\0', ' ', '<', '>', '"']) >= 0)
            return null;
        var dot = text.LastIndexOf('.');
        if (dot <= 0 || dot >= text.Length - 1) return null;
        var extension = text[(dot + 1)..];
        return extension.Length <= 8 && extension.All(char.IsLetterOrDigit) ? text : null;
    }
}
