using System.Text;

namespace MolaGPT.App.Rendering.Canvas;

/// <summary>Locates document shell tokens without changing the page's raw text or attributes.</summary>
internal static class HtmlDocumentShell
{
    private readonly record struct Tag(int Start, int End, string Name, string Attributes, bool Closing);
    private static readonly HashSet<string> VoidElements = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"
    };
    private static readonly HashSet<string> RawElements = new(StringComparer.Ordinal)
    {
        "script", "style", "title", "textarea", "xmp", "iframe", "noembed", "noframes", "plaintext"
    };

    public static (string HtmlAttributes, string Head, string BodyAttributes, string Body) Extract(string source)
    {
        var htmlAttributes = "";
        var bodyAttributes = "";
        var head = new StringBuilder();
        var body = new StringBuilder();
        var stack = new List<string>();
        var emitted = 0;
        var headStart = -1;
        var inBody = false;
        var sawHtml = false;
        var sawBody = false;
        foreach (var tag in Tags(source))
        {
            if (tag.Name == "head" && stack.Count == 0 && !inBody)
            {
                if (headStart < 0) body.Append(source, emitted, tag.Start - emitted);
                if (tag.Closing)
                {
                    if (headStart >= 0) head.Append(source, headStart, tag.Start - headStart);
                    headStart = -1;
                }
                else headStart = tag.End;
                emitted = tag.End;
                continue;
            }
            if (stack.Count == 0 && tag.Name == "body")
            {
                if (headStart >= 0)
                {
                    head.Append(source, headStart, tag.Start - headStart);
                    headStart = -1;
                }
                else body.Append(source, emitted, tag.Start - emitted);
                if (!tag.Closing && !sawBody)
                {
                    bodyAttributes = tag.Attributes;
                    sawBody = true;
                }
                emitted = tag.End;
                inBody = true;
                continue;
            }
            if (stack.Count == 0 && tag.Name is "html" or "!doctype")
            {
                if (headStart < 0) body.Append(source, emitted, tag.Start - emitted);
                if (!tag.Closing && tag.Name == "html" && !sawHtml)
                {
                    htmlAttributes = tag.Attributes;
                    sawHtml = true;
                }
                emitted = tag.End;
                continue;
            }

            if (tag.Closing)
            {
                var opening = stack.LastIndexOf(tag.Name);
                if (opening >= 0) stack.RemoveRange(opening, stack.Count - opening);
            }
            else if (!VoidElements.Contains(tag.Name)
                && !(tag.Name is "svg" or "math" && tag.Attributes.TrimEnd().EndsWith('/')))
                stack.Add(tag.Name);
        }
        if (headStart >= 0) head.Append(source, headStart, source.Length - headStart);
        else body.Append(source, emitted, source.Length - emitted);
        return (htmlAttributes, head.ToString(), bodyAttributes, body.ToString());
    }

    private static IEnumerable<Tag> Tags(string source)
    {
        string? raw = null;
        for (var cursor = 0; cursor < source.Length;)
        {
            var start = source.IndexOf('<', cursor);
            if (start < 0) yield break;
            if (raw is not null)
            {
                if (!source.AsSpan(start).StartsWith("</" + raw, StringComparison.OrdinalIgnoreCase))
                {
                    cursor = start + 1;
                    continue;
                }
            }
            else if (source.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
            {
                var endComment = source.IndexOf("-->", start + 4, StringComparison.Ordinal);
                if (endComment < 0) yield break;
                cursor = endComment + 3;
                continue;
            }
            var nameStart = start + 1;
            var closing = nameStart < source.Length && source[nameStart] == '/';
            if (closing) nameStart++;
            var nameEnd = nameStart;
            if (source.AsSpan(nameStart).StartsWith("!doctype", StringComparison.OrdinalIgnoreCase))
                nameEnd += 8;
            else
                while (nameEnd < source.Length && (char.IsAsciiLetterOrDigit(source[nameEnd]) || source[nameEnd] is ':' or '-'))
                    nameEnd++;
            if (nameEnd == nameStart || nameEnd == source.Length
                || source[nameEnd] is not ('>' or '/' or ' ' or '\t' or '\r' or '\n' or '\f'))
            {
                cursor = start + 1;
                continue;
            }
            var name = source[nameStart..nameEnd].ToLowerInvariant();
            var end = nameEnd;
            var quote = '\0';
            for (; end < source.Length; end++)
            {
                var c = source[end];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c is '\'' or '"') quote = c;
                else if (c == '>') break;
            }
            if (end == source.Length) yield break;
            cursor = end + 1;
            if (raw is not null && (!closing || name != raw)) continue;
            raw = !closing && RawElements.Contains(name) ? name : null;
            yield return new Tag(start, cursor, name, source[nameEnd..end], closing);
            if (name == "plaintext") yield break;
        }
    }
}
