using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MolaGPT.Presentation.Visuals;

// Props of the layout components — table, metric cards, item cards. Same rules
// as VisualSpecs: validated here, errors in the user's language naming the
// field, and lenient about the shapes models actually write (columns as plain
// strings, rows as arrays, a number where a string was asked for).

public enum TableColumnType
{
    Text,
    Number,
    Date,
    Boolean,
}

public enum TableAlign
{
    Start,
    Center,
    End,
}

public sealed record TableColumn(string Key, string Label, TableColumnType Type, TableAlign Align);

/// <summary>One cell: the text as shown, plus the key it sorts by under its
/// column's type. A cell that does not read as that type has no key and sorts
/// last in either direction.</summary>
public sealed record TableCell(string Text, bool IsEmpty, double? Number, DateTime? Date, bool? Flag);

public sealed partial record DataTableSpec(
    string? Title,
    IReadOnlyList<TableColumn> Columns,
    IReadOnlyList<IReadOnlyList<TableCell>> Rows,
    int PageSize)
{
    public const int MaxColumns = 16;
    public const int MaxRows = 1000;
    public const int DefaultPageSize = 10;

    public static bool TryParse(JsonElement props, out DataTableSpec? spec, out string? error)
    {
        spec = null;
        error = null;

        if (!props.TryGetProperty("rows", out var rowsNode) || rowsNode.ValueKind != JsonValueKind.Array)
        {
            error = "rows 必须是数组";
            return false;
        }

        var rowNodes = rowsNode.EnumerateArray().Take(MaxRows).ToList();
        var columns = ReadColumns(props, rowNodes, out error);
        if (columns is null) return false;

        // Positional rows index by column order, keyed rows by column key.
        var raw = new List<JsonElement?[]>();
        foreach (var node in rowNodes)
        {
            var cells = new JsonElement?[columns.Count];
            if (node.ValueKind == JsonValueKind.Object)
            {
                for (var c = 0; c < columns.Count; c++)
                    cells[c] = node.TryGetProperty(columns[c].Key, out var value) ? value : null;
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                var c = 0;
                foreach (var value in node.EnumerateArray())
                {
                    if (c >= columns.Count) break;
                    cells[c++] = value;
                }
            }
            else
            {
                error = "rows 的每一项应是对象（按列 key）或数组（按列顺序）";
                return false;
            }

            raw.Add(cells);
        }

        if (raw.Count == 0)
        {
            error = "rows 里没有数据";
            return false;
        }

        var typed = new List<TableColumn>(columns.Count);
        for (var c = 0; c < columns.Count; c++)
        {
            var column = columns[c];
            var type = column.Type;
            if (type == TableColumnType.Text && !column.TypeDeclared)
                type = InferType(raw.Select(cells => cells[c]));
            var align = column.Align ?? (type == TableColumnType.Number ? TableAlign.End : TableAlign.Start);
            typed.Add(new TableColumn(column.Key, column.Label, type, align));
        }

        var rows = raw
            .Select(cells => (IReadOnlyList<TableCell>)cells.Select((value, c) => ToCell(value, typed[c].Type)).ToArray())
            .ToList();

        var pageSize = VisualJson.Number(props, "pageSize") is { } size ? (int)Math.Round(size) : DefaultPageSize;
        spec = new DataTableSpec(VisualJson.String(props, "title"), typed, rows, Math.Clamp(pageSize, 5, 50));
        return true;
    }

    private sealed record ColumnDraft(string Key, string Label, TableColumnType Type, bool TypeDeclared, TableAlign? Align);

    private static List<ColumnDraft>? ReadColumns(JsonElement props, List<JsonElement> rows, out string? error)
    {
        error = null;
        var columns = new List<ColumnDraft>();

        if (props.TryGetProperty("columns", out var node) && node.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in node.EnumerateArray())
            {
                if (columns.Count >= MaxColumns) break;
                index++;
                switch (item.ValueKind)
                {
                    case JsonValueKind.String:
                        var name = item.GetString() ?? string.Empty;
                        columns.Add(new ColumnDraft(name, name, TableColumnType.Text, false, null));
                        break;
                    case JsonValueKind.Object:
                        var key = VisualJson.String(item, "key") ?? VisualJson.String(item, "field") ?? VisualJson.String(item, "label");
                        if (string.IsNullOrEmpty(key))
                        {
                            error = $"columns 第 {index} 项缺少 key";
                            return null;
                        }

                        var label = VisualJson.String(item, "label") ?? VisualJson.String(item, "title") ?? key;
                        var typeText = VisualJson.String(item, "type")?.Trim().ToLowerInvariant();
                        var type = typeText switch
                        {
                            "number" or "numeric" or "int" or "float" or "currency" or "percent" => TableColumnType.Number,
                            "date" or "datetime" or "time" => TableColumnType.Date,
                            "boolean" or "bool" => TableColumnType.Boolean,
                            _ => TableColumnType.Text,
                        };
                        var align = VisualJson.String(item, "align")?.Trim().ToLowerInvariant() switch
                        {
                            "end" or "right" => TableAlign.End,
                            "center" or "centre" => TableAlign.Center,
                            "start" or "left" => TableAlign.Start,
                            _ => (TableAlign?)null,
                        };
                        columns.Add(new ColumnDraft(key, label, type, typeText is "string" or "text" || type != TableColumnType.Text, align));
                        break;
                    default:
                        error = "columns 的每一项应是字符串或 {key, label}";
                        return null;
                }
            }
        }
        else if (rows.FirstOrDefault(r => r.ValueKind == JsonValueKind.Object) is { ValueKind: JsonValueKind.Object } first)
        {
            // No columns: the first keyed row's fields, in the order written.
            foreach (var property in first.EnumerateObject())
            {
                if (columns.Count >= MaxColumns) break;
                columns.Add(new ColumnDraft(property.Name, property.Name, TableColumnType.Text, false, null));
            }
        }

        if (columns.Count == 0)
        {
            error = "columns 至少需要一列";
            return null;
        }

        if (columns.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != columns.Count)
        {
            error = "columns 的 key 不能重复";
            return null;
        }

        return columns;
    }

    /// <summary>A column reads as numbers (or dates, or flags) when most of its
    /// filled cells do. "Most", not "all": one "—" or "未公布" in a column of
    /// figures should not turn its sort into string order.</summary>
    private static TableColumnType InferType(IEnumerable<JsonElement?> values)
    {
        int filled = 0, numbers = 0, dates = 0, flags = 0;
        foreach (var value in values)
        {
            if (value is not { } v || IsBlank(v)) continue;
            filled++;
            if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) flags++;
            else if (v.ValueKind == JsonValueKind.Number || (v.ValueKind == JsonValueKind.String && ParseNumber(v.GetString()) is not null)) numbers++;
            else if (v.ValueKind == JsonValueKind.String && ParseDate(v.GetString()) is not null) dates++;
        }

        if (filled == 0) return TableColumnType.Text;
        if (flags * 2 > filled) return TableColumnType.Boolean;
        if (numbers * 2 > filled) return TableColumnType.Number;
        if (dates * 2 > filled) return TableColumnType.Date;
        return TableColumnType.Text;
    }

    private static bool IsBlank(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));

    private static TableCell ToCell(JsonElement? value, TableColumnType type)
    {
        if (value is not { } v || IsBlank(v)) return new TableCell("—", true, null, null, null);

        // Numbers are shown as written: the model chose "0.10" or "2024" for a
        // reason, and regrouping a year into "2,024" is the classic way to get
        // that wrong.
        var text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()!,
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "✓",
            JsonValueKind.False => "✗",
            _ => v.GetRawText(),
        };

        return type switch
        {
            TableColumnType.Number => new TableCell(text, false,
                v.ValueKind == JsonValueKind.Number ? v.GetDouble() : ParseNumber(text), null, null),
            TableColumnType.Date => new TableCell(text, false, null, ParseDate(text), null),
            TableColumnType.Boolean => new TableCell(text, false, null, null,
                v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => ParseFlag(text) }),
            _ => new TableCell(text, false, null, null, null),
        };
    }

    [GeneratedRegex(@"^\s*([+\-−]?)\s*[¥$€£]?\s*((?:\d{1,3}(?:[,，]\d{3})+|\d+)(?:\.\d+)?|\.\d+)\s*(%|‰)?\s*$")]
    private static partial Regex NumberText();

    /// <summary>Plain figures with the decorations tables carry: sign, a currency
    /// symbol, thousands separators, a percent sign. Units like 万 or 亿 are not
    /// read — the prompt asks for the unit in the column label instead.</summary>
    public static double? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = NumberText().Match(text);
        if (!match.Success) return null;
        var digits = match.Groups[2].Value.Replace(",", string.Empty, StringComparison.Ordinal).Replace("，", string.Empty, StringComparison.Ordinal);
        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        return match.Groups[1].Value is "-" or "−" ? -value : value;
    }

    [GeneratedRegex(@"^\s*(\d{4})\s*(?:[-/.]|年)\s*(\d{1,2})\s*(?:(?:[-/.]|月)\s*(\d{1,2})\s*日?)?\s*月?(?:[ T](\d{1,2}):(\d{2}))?")]
    private static partial Regex DateText();

    public static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = DateText().Match(text);
        if (!match.Success) return null;
        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var day = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 1;
        var hour = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
        var minute = match.Groups[5].Success ? int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture) : 0;
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59) return null;
        return new DateTime(year, month, day, hour, minute, 0);
    }

    private static bool? ParseFlag(string text) => text.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "y" or "是" or "有" or "支持" or "✓" or "✔" or "√" => true,
        "false" or "no" or "n" or "否" or "无" or "不支持" or "✗" or "✘" or "×" => false,
        _ => null,
    };

    /// <summary>Sort order under a column's type; cells without a key go last
    /// whichever way the column is sorted, so flipping the order does not bring
    /// the blanks to the top.</summary>
    public static int Compare(TableCell a, TableCell b, TableColumnType type, bool descending)
    {
        IComparable? ka = Key(a, type);
        IComparable? kb = Key(b, type);
        if (ka is null) return kb is null ? 0 : 1;
        if (kb is null) return -1;
        var order = ka is string sa && kb is string sb
            ? string.Compare(sa, sb, CultureInfo.CurrentCulture, CompareOptions.StringSort)
            : ka.CompareTo(kb);
        return descending ? -order : order;
    }

    private static IComparable? Key(TableCell cell, TableColumnType type) =>
        cell.IsEmpty
            ? null
            : type switch
            {
                TableColumnType.Number => cell.Number,
                TableColumnType.Date => cell.Date,
                TableColumnType.Boolean => cell.Flag,
                _ => cell.Text,
            };
}

public enum StatTrend
{
    None,
    Up,
    Down,
    Flat,
}

/// <summary>Whether a change is good news. Kept apart from direction on
/// purpose: rising cost is up and bad, and in a Chinese market red means up —
/// colouring by direction would be wrong for one reader or the other.</summary>
public enum StatTone
{
    Neutral,
    Good,
    Bad,
}

public sealed record StatItem(
    string Label,
    string Value,
    string? Unit,
    string? Delta,
    StatTrend Trend,
    StatTone Tone,
    string? Note,
    IReadOnlyList<double> History);

public sealed record StatGridSpec(string? Title, IReadOnlyList<StatItem> Items, IReadOnlyList<string> Periods)
{
    public const int MaxItems = 12;
    public const int MaxHistory = 120;

    public static bool TryParse(JsonElement props, out StatGridSpec? spec, out string? error)
    {
        spec = null;
        error = null;

        if (!props.TryGetProperty("items", out var itemsNode) || itemsNode.ValueKind != JsonValueKind.Array)
        {
            error = "items 必须是数组";
            return false;
        }

        var items = new List<StatItem>();
        var index = 0;
        foreach (var item in itemsNode.EnumerateArray())
        {
            if (items.Count >= MaxItems) break;
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = $"items 第 {index} 项应是对象";
                return false;
            }

            var label = VisualJson.String(item, "label") ?? VisualJson.String(item, "name") ?? VisualJson.String(item, "title");
            if (string.IsNullOrWhiteSpace(label))
            {
                error = $"items 第 {index} 项缺少 label";
                return false;
            }

            if (!item.TryGetProperty("value", out var valueNode) || valueNode.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                error = $"指标 {label} 缺少 value";
                return false;
            }

            var value = valueNode.ValueKind switch
            {
                JsonValueKind.String => valueNode.GetString()!,
                JsonValueKind.Number => valueNode.GetRawText(),
                JsonValueKind.True => "是",
                JsonValueKind.False => "否",
                _ => valueNode.GetRawText(),
            };

            var delta = Text(item, "delta") ?? Text(item, "change");
            var trend = VisualJson.String(item, "trend")?.Trim().ToLowerInvariant() switch
            {
                "up" or "rise" or "increase" or "上升" or "上涨" => StatTrend.Up,
                "down" or "fall" or "decrease" or "下降" or "下跌" => StatTrend.Down,
                "flat" or "stable" or "持平" => StatTrend.Flat,
                _ => TrendOf(delta),
            };
            var tone = VisualJson.String(item, "tone")?.Trim().ToLowerInvariant() switch
            {
                "good" or "positive" or "好" => StatTone.Good,
                "bad" or "negative" or "坏" or "差" => StatTone.Bad,
                _ => StatTone.Neutral,
            };

            var history = new List<double>();
            if (item.TryGetProperty("history", out var historyNode) && historyNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var point in historyNode.EnumerateArray().TakeLast(MaxHistory))
                {
                    if (point.ValueKind == JsonValueKind.Number && double.IsFinite(point.GetDouble())) history.Add(point.GetDouble());
                    else if (point.ValueKind == JsonValueKind.String && DataTableSpec.ParseNumber(point.GetString()) is { } parsed) history.Add(parsed);
                }
            }

            items.Add(new StatItem(label.Trim(), value, Text(item, "unit"), delta, trend, tone, Text(item, "note"), history));
        }

        if (items.Count == 0)
        {
            error = "items 至少需要一项";
            return false;
        }

        var periods = new List<string>();
        if (props.TryGetProperty("periods", out var periodsNode) && periodsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var period in periodsNode.EnumerateArray().TakeLast(MaxHistory))
                periods.Add(period.ValueKind == JsonValueKind.String ? period.GetString()! : period.GetRawText());
        }

        spec = new StatGridSpec(VisualJson.String(props, "title"), items, periods);
        return true;
    }

    /// <summary>Direction only, read off the sign the model already wrote.</summary>
    private static StatTrend TrendOf(string? delta)
    {
        var text = delta?.TrimStart();
        if (string.IsNullOrEmpty(text)) return StatTrend.None;
        return text[0] switch
        {
            '+' or '↑' or '▲' => StatTrend.Up,
            '-' or '−' or '↓' or '▼' => StatTrend.Down,
            _ => StatTrend.None,
        };
    }

    private static string? Text(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var node)) return null;
        var text = node.ValueKind switch
        {
            JsonValueKind.String => node.GetString(),
            JsonValueKind.Number => node.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}

public sealed record CardItem(string Title, string? Summary, string? Tag, string? Source, string? Url);

public sealed record CardGridSpec(string? Title, IReadOnlyList<CardItem> Items, IReadOnlyList<string> Tags)
{
    public const int MaxItems = 24;

    public static bool TryParse(JsonElement props, out CardGridSpec? spec, out string? error)
    {
        spec = null;
        error = null;

        if (!props.TryGetProperty("items", out var itemsNode) || itemsNode.ValueKind != JsonValueKind.Array)
        {
            error = "items 必须是数组";
            return false;
        }

        var items = new List<CardItem>();
        var index = 0;
        foreach (var item in itemsNode.EnumerateArray())
        {
            if (items.Count >= MaxItems) break;
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = $"items 第 {index} 项应是对象";
                return false;
            }

            var title = Clean(VisualJson.String(item, "title") ?? VisualJson.String(item, "name"));
            var summary = Clean(VisualJson.String(item, "summary") ?? VisualJson.String(item, "description"));
            if (title is null)
            {
                error = $"items 第 {index} 项缺少 title";
                return false;
            }

            items.Add(new CardItem(title, summary, Clean(VisualJson.String(item, "tag")), Clean(VisualJson.String(item, "source")),
                Clean(VisualJson.String(item, "url") ?? VisualJson.String(item, "link"))));
        }

        if (items.Count == 0)
        {
            error = "items 至少需要一项";
            return false;
        }

        // Filters come from the tags in first-seen order; the model does not
        // declare them separately, so they cannot disagree with the cards.
        var tags = items.Select(i => i.Tag).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        spec = new CardGridSpec(VisualJson.String(props, "title"), items, tags);
        return true;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
