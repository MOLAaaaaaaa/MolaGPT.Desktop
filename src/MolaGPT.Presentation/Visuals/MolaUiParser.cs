using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MolaGPT.Presentation.Visuals;

public enum MolaUiStatus
{
    /// <summary>Still streaming and not yet parseable — keep the placeholder up.</summary>
    Incomplete,
    /// <summary>Complete but unusable. Shown as folded source plus the reason.</summary>
    Invalid,
    Ok,
}

public sealed record MolaUiEnvelope(string Component, string Id, JsonElement Props);

public sealed record MolaUiParseResult(MolaUiStatus Status, MolaUiEnvelope? Envelope, string? Error)
{
    public static MolaUiParseResult Incomplete { get; } = new(MolaUiStatus.Incomplete, null, null);
    public static MolaUiParseResult Fail(string error) => new(MolaUiStatus.Invalid, null, error);
}

/// <summary>
/// Reads a <c>mola-ui</c> fence: one JSON object with component, id and props.
///
/// Models deviate from JSON in four predictable ways — trailing commas,
/// comments, ASCII double quotes left unescaped inside Chinese prose, and LaTeX
/// backslashes left unescaped. The first two the reader tolerates natively;
/// backslashes are first read as LaTeX; quotes are repaired by treating a
/// quote as closing a string only when what follows is structural.
/// </summary>
public static partial class MolaUiParser
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 32,
    };

    [GeneratedRegex("\"component\"\\s*:\\s*\"([A-Za-z0-9-]+)\"")]
    private static partial Regex ComponentPeek();

    /// <param name="closed">The fence has its closing marker, or the message is
    /// finished. Until then a parse failure with open braces means "not yet".</param>
    public static MolaUiParseResult Parse(string raw, bool closed)
    {
        var text = raw.Trim();
        if (text.Length == 0) return closed ? MolaUiParseResult.Fail("组件内容为空") : MolaUiParseResult.Incomplete;

        var prepared = KeepLatexBackslashes(text);
        JsonDocument? document = null;
        string? error = null;
        try
        {
            document = JsonDocument.Parse(prepared, Options);
        }
        catch (JsonException first)
        {
            try
            {
                document = JsonDocument.Parse(RepairQuotes(prepared), Options);
            }
            catch (JsonException)
            {
                error = first.Message;
            }
        }

        if (document is null)
        {
            if (!closed && !BracesBalanced(text)) return MolaUiParseResult.Incomplete;
            return MolaUiParseResult.Fail("JSON 无法解析：" + Shorten(error));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return MolaUiParseResult.Fail("顶层必须是 JSON 对象");

            if (!root.TryGetProperty("component", out var componentNode) || componentNode.ValueKind != JsonValueKind.String)
                return MolaUiParseResult.Fail("缺少 component 字段");
            var component = componentNode.GetString()!.Trim().ToLowerInvariant();

            var id = root.TryGetProperty("id", out var idNode)
                ? idNode.ValueKind switch
                {
                    JsonValueKind.String => idNode.GetString() ?? string.Empty,
                    JsonValueKind.Number => idNode.GetRawText(),
                    _ => string.Empty,
                }
                : string.Empty;

            if (!root.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object)
                return MolaUiParseResult.Fail("props 必须是对象");

            return new MolaUiParseResult(MolaUiStatus.Ok, new MolaUiEnvelope(component, id, props.Clone()), null);
        }
    }

    /// <summary>The component name usually arrives first, which lets the host
    /// reserve that component's height while the rest is still streaming.</summary>
    public static string? PeekComponent(string raw)
    {
        var match = ComponentPeek().Match(raw);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Models writing LaTeX in a component (\sin, \alpha, \frac) don't double
    /// their backslashes. An invalid escape like \s fails the whole component;
    /// \f, \b, \r and \t are valid but read back as a control character plus
    /// half a command.
    ///
    /// So an invalid escape keeps its backslash, and so do \b \f \r \t when a
    /// letter follows directly — models never mean those control characters.
    /// \n stays a newline: it is too common in prose to second-guess.
    /// </summary>
    private static string KeepLatexBackslashes(string text)
    {
        if (!text.Contains('\\')) return text;
        var output = new StringBuilder(text.Length + 8);
        var inString = false;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (!inString || c != '\\' || i + 1 >= text.Length)
            {
                if (c == '"') inString = !inString;
                output.Append(c);
                i++;
                continue;
            }

            var next = text[i + 1];
            var literal = next switch
            {
                '"' or '\\' or '/' or 'n' => false,
                'u' => !(i + 5 < text.Length && text.AsSpan(i + 2, 4).IndexOfAnyExcept(HexDigits) < 0),
                'b' or 'f' or 'r' or 't' => i + 2 < text.Length && char.IsAsciiLetter(text[i + 2]),
                _ => true,
            };
            if (literal)
            {
                output.Append(@"\\");
                i++;
            }
            else
            {
                output.Append(c).Append(next);
                i += 2;
            }
        }

        return output.ToString();
    }

    private static readonly System.Buffers.SearchValues<char> HexDigits =
        System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");

    private static string RepairQuotes(string text)
    {
        var output = new StringBuilder(text.Length + 16);
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!inString)
            {
                if (c == '"') inString = true;
                output.Append(c);
                continue;
            }

            if (c == '\\' && i + 1 < text.Length)
            {
                output.Append(c).Append(text[++i]);
                continue;
            }

            if (c == '"')
            {
                var look = i + 1;
                while (look < text.Length && char.IsWhiteSpace(text[look])) look++;
                if (look >= text.Length || text[look] is ',' or '}' or ']' or ':')
                {
                    inString = false;
                    output.Append(c);
                }
                else
                {
                    output.Append("\\\"");
                }

                continue;
            }

            // Raw newlines inside a string are another thing models emit.
            if (c == '\n') { output.Append("\\n"); continue; }
            if (c == '\r') continue;
            output.Append(c);
        }

        return output.ToString();
    }

    private static bool BracesBalanced(string text)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
        }

        return depth <= 0 && !inString;
    }

    private static string Shorten(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "格式错误";
        var cut = message.IndexOf(". Path:", StringComparison.Ordinal);
        if (cut > 0) message = message[..cut];
        return message.Length > 120 ? message[..120] + "…" : message;
    }
}
