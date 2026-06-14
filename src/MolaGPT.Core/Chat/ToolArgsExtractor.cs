using System.Text.Json;
using System.Text.RegularExpressions;

namespace MolaGPT.Core.Chat;

/// <summary>
/// Projects a tool call's raw arguments JSON into a <see cref="ToolArgsView"/>
/// the chat surface can render directly. Pure function, no streaming state.
///
/// Decision tree:
///   • <c>search_web</c> → parse args.queries → list of (text, topic?).
///   • Else find a "primary" key on the top-level object:
///       url / href                 → <see cref="ToolPrimaryArgKind.Url"/>
///       path / file / filename …   → <see cref="ToolPrimaryArgKind.Path"/>
///       query / text / prompt …    → <see cref="ToolPrimaryArgKind.Text"/>
///   • Else pull up to 3 top-level scalar (string / number / bool) entries
///     into key-value chips; remaining count goes to <c>KeyValueOverflow</c>.
///   • Else empty.
///
/// Non-scalar values are kept compact: short JSON snippet, no nested rendering.
/// </summary>
public static partial class ToolArgsExtractor
{
    private const int KeyValueDisplayCount = 3;
    private const int KeyValueMaxLength = 200;
    private const int PrimaryArgMaxLength = 600;

    private static readonly string[] UrlKeys = { "url", "href", "link" };
    private static readonly string[] PathKeys = { "path", "file", "filename", "file_path", "filepath" };
    private static readonly string[] TextKeys = { "code", "query", "text", "prompt", "keyword", "keywords", "search", "pattern" };

    public static ToolArgsView Extract(string? toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return ToolArgsView.Empty;

        var isPython = string.Equals(toolName, "execute_python_code", System.StringComparison.OrdinalIgnoreCase);

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            if (!isPython || !TryExtractPartialPythonCode(argumentsJson, out var partialCode))
                return ToolArgsView.Empty;

            return new ToolArgsView(null, null, null, 0, new ToolCodeArgView(partialCode!, "python"));
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ToolArgsView.Empty;

            if (isPython)
            {
                var code = ReadString(root, "code") ?? ReadString(root, "python") ?? ReadString(root, "script");
                if (!string.IsNullOrWhiteSpace(code))
                    return new ToolArgsView(null, null, null, 0, new ToolCodeArgView(code!, "python"));
            }

            if (string.Equals(toolName, "search_web", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "web_search", System.StringComparison.OrdinalIgnoreCase))
            {
                var queries = ExtractSearchQueries(root);
                if (queries is not null)
                    return new ToolArgsView(queries, null, null, 0);
            }

            if (TryExtractPrimary(root, UrlKeys,  ToolPrimaryArgKind.Url,  out var url))
                return new ToolArgsView(null, url, null, 0);
            if (TryExtractPrimary(root, PathKeys, ToolPrimaryArgKind.Path, out var path))
                return new ToolArgsView(null, path, null, 0);
            if (TryExtractPrimary(root, TextKeys, ToolPrimaryArgKind.Text, out var text))
                return new ToolArgsView(null, text, null, 0);

            var (kvs, overflow) = ExtractKeyValues(root);
            return kvs.Count == 0
                ? ToolArgsView.Empty
                : new ToolArgsView(null, null, kvs, overflow);
        }
    }

    private static IReadOnlyList<ToolSearchQueryView>? ExtractSearchQueries(JsonElement root)
    {
        var list = new List<ToolSearchQueryView>();

        if (root.TryGetProperty("queries", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        list.Add(new ToolSearchQueryView(s!.Trim(), null));
                    continue;
                }
                if (item.ValueKind != JsonValueKind.Object) continue;

                var q = ReadString(item, "query") ?? ReadString(item, "text") ?? ReadString(item, "q");
                if (string.IsNullOrWhiteSpace(q)) continue;
                var topic = ReadString(item, "topic") ?? ReadString(item, "time_range") ?? ReadString(item, "country");
                list.Add(new ToolSearchQueryView(
                    q!.Trim(),
                    string.IsNullOrWhiteSpace(topic) ? null : topic!.Trim()));
            }
        }

        if (list.Count == 0)
        {
            var legacy = ReadString(root, "query") ?? ReadString(root, "search_query") ?? ReadString(root, "text") ?? ReadString(root, "q");
            if (!string.IsNullOrWhiteSpace(legacy))
                list.Add(new ToolSearchQueryView(legacy!.Trim(), null));
        }

        return list.Count == 0 ? null : list;
    }

    private static bool TryExtractPrimary(
        JsonElement root,
        IReadOnlyList<string> candidates,
        ToolPrimaryArgKind kind,
        out ToolPrimaryArgView? view)
    {
        view = null;
        foreach (var candidate in candidates)
        {
            if (!TryFindProperty(root, candidate, out var prop)) continue;
            if (prop.ValueKind != JsonValueKind.String) continue;
            var raw = prop.GetString();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var trimmed = raw!.Trim();
            if (trimmed.Length > PrimaryArgMaxLength)
                trimmed = trimmed[..PrimaryArgMaxLength] + "…";
            view = new ToolPrimaryArgView(kind, trimmed, BuildPrimaryBadge(root, kind));
            return true;
        }
        return false;
    }

    private static string? BuildPrimaryBadge(JsonElement root, ToolPrimaryArgKind kind)
    {
        return kind switch
        {
            ToolPrimaryArgKind.Url  => ReadString(root, "action"),
            ToolPrimaryArgKind.Path => ReadString(root, "mode"),
            _ => null
        };
    }

    private static (IReadOnlyList<ToolKeyValueView> Items, int Overflow) ExtractKeyValues(JsonElement root)
    {
        var items = new List<ToolKeyValueView>();
        var scalarCount = 0;

        foreach (var prop in root.EnumerateObject())
        {
            if (!IsDisplayableScalar(prop.Value)) continue;
            scalarCount++;
            if (items.Count >= KeyValueDisplayCount) continue;

            var value = FormatScalar(prop.Value);
            if (string.IsNullOrEmpty(value)) continue;

            if (value.Length > KeyValueMaxLength)
                value = value[..KeyValueMaxLength] + "…";

            var isMono = prop.Value.ValueKind != JsonValueKind.String
                         || LooksLikeIdentifier(prop.Value.GetString());
            items.Add(new ToolKeyValueView(prop.Name, value, isMono));
        }

        var overflow = System.Math.Max(0, scalarCount - items.Count);
        return (items, overflow);
    }

    private static bool IsDisplayableScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => true,
        _ => false
    };

    private static string FormatScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True   => "true",
        JsonValueKind.False  => "false",
        _ => string.Empty
    };

    private static bool LooksLikeIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) continue;
            if (ch is '_' or '-' or '.' or '/' or '\\' or ':' or '=' or '<' or '>' or '\'' or '"') continue;
            return false;
        }
        return true;
    }

    private static bool TryFindProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, System.StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        return TryFindProperty(obj, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryExtractPartialPythonCode(string argumentsJson, out string? code)
    {
        code = null;
        var match = PartialCodeRegex().Match(argumentsJson);
        if (!match.Success)
            return false;

        code = DecodeJsonStringFragment(match.Groups["value"].Value);
        return !string.IsNullOrWhiteSpace(code);
    }

    private static string DecodeJsonStringFragment(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch != '\\')
            {
                builder.Append(ch);
                continue;
            }

            if (i + 1 >= value.Length)
                break;

            var escaped = value[++i];
            switch (escaped)
            {
                case '"':
                case '\\':
                case '/':
                    builder.Append(escaped);
                    break;
                case 'b':
                    builder.Append('\b');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'u':
                    if (i + 4 < value.Length
                        && int.TryParse(value.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var scalar))
                    {
                        builder.Append((char)scalar);
                        i += 4;
                    }
                    else
                    {
                        i = value.Length;
                    }
                    break;
                default:
                    builder.Append(escaped);
                    break;
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex("\"code\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)", RegexOptions.CultureInvariant)]
    private static partial Regex PartialCodeRegex();
}
