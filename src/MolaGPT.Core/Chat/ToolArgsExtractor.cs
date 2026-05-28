using System.Text.Json;

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
public static class ToolArgsExtractor
{
    private const int KeyValueDisplayCount = 3;
    private const int KeyValueMaxLength = 200;
    private const int PrimaryArgMaxLength = 600;

    private static readonly string[] UrlKeys = { "url", "href", "link" };
    private static readonly string[] PathKeys = { "path", "file", "filename", "file_path", "filepath" };
    private static readonly string[] TextKeys = { "query", "text", "prompt", "keyword", "keywords", "search" };

    public static ToolArgsView Extract(string? toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return ToolArgsView.Empty;

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            return ToolArgsView.Empty;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ToolArgsView.Empty;

            if (string.Equals(toolName, "search_web", System.StringComparison.OrdinalIgnoreCase))
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

                var q = ReadString(item, "query");
                if (string.IsNullOrWhiteSpace(q)) continue;
                var topic = ReadString(item, "topic");
                list.Add(new ToolSearchQueryView(
                    q!.Trim(),
                    string.IsNullOrWhiteSpace(topic) ? null : topic!.Trim()));
            }
        }

        if (list.Count == 0)
        {
            var legacy = ReadString(root, "query");
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
}
