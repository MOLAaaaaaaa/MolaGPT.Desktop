using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MolaGPT.Presentation.Visuals;

// Props of the inline components, validated here so the views only ever see
// well-formed specs. Errors are written for the reader of a folded fallback,
// in the user's language, and name the field at fault.

public sealed record PlotParam(string Name, double Min, double Max, double Default, double Step);

public enum CurveKind
{
    /// <summary>y = f(x)</summary>
    ExplicitY,
    /// <summary>x = g(y)</summary>
    ExplicitX,
    /// <summary>F(x, y) = 0, traced as a zero set.</summary>
    Implicit,
    /// <summary>r = f(θ)</summary>
    Polar,
    /// <summary>(x(t), y(t))</summary>
    Parametric,
}

public sealed class PlotCurve
{
    public required CurveKind Kind { get; init; }
    public required string Source { get; init; }
    public string? Label { get; init; }
    public string? Latex { get; init; }
    public string? Error { get; init; }
    /// <summary>Evaluator over <see cref="FunctionPlotSpec.VariableOrder"/>.</summary>
    public Func<double[], double>? Fn { get; init; }
    /// <summary>Second component of a parametric curve.</summary>
    public Func<double[], double>? FnY { get; init; }
    public double TMin { get; init; }
    public double TMax { get; init; }
}

public sealed record FunctionPlotSpec(
    string? Title,
    IReadOnlyList<PlotCurve> Curves,
    IReadOnlyList<PlotParam> Params,
    (double Min, double Max)? X,
    (double Min, double Max)? Y)
{
    public const int MaxCurves = 6;
    public const int MaxParams = 4;

    /// <summary>Value slots every compiled curve reads: x, y, then the curve
    /// parameter under each name models use for it, then the sliders.</summary>
    public static readonly string[] FixedVariables = ["x", "y", "t", "theta", "θ"];

    public IReadOnlyList<string> VariableOrder => FixedVariables.Concat(Params.Select(p => p.Name)).ToArray();

    public static bool TryParse(JsonElement props, out FunctionPlotSpec? spec, out string? error)
    {
        spec = null;
        error = null;

        var title = VisualJson.String(props, "title");
        var parameters = new List<PlotParam>();
        if (props.TryGetProperty("params", out var paramsNode) && paramsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in paramsNode.EnumerateArray())
            {
                if (parameters.Count >= MaxParams) break;
                if (!TryParseParam(item, out var param, out error)) return false;
                if (parameters.Any(p => p.Name == param!.Name))
                {
                    error = $"参数 {param!.Name} 重复";
                    return false;
                }

                parameters.Add(param!);
            }
        }

        var x = VisualJson.Range(props, "x", out var xError);
        if (xError is not null) { error = xError; return false; }
        var y = VisualJson.Range(props, "y", out var yError);
        if (yError is not null) { error = yError; return false; }

        if (!props.TryGetProperty("functions", out var functions))
            props.TryGetProperty("curves", out functions);
        if (functions.ValueKind == JsonValueKind.String || functions.ValueKind == JsonValueKind.Object)
            functions = JsonDocument.Parse($"[{functions.GetRawText()}]").RootElement.Clone();
        if (functions.ValueKind != JsonValueKind.Array || functions.GetArrayLength() == 0)
        {
            error = "functions 至少需要一条曲线";
            return false;
        }

        var order = FixedVariables.Concat(parameters.Select(p => p.Name)).ToArray();
        var known = order;
        var curves = new List<PlotCurve>();
        foreach (var item in functions.EnumerateArray())
        {
            if (curves.Count >= MaxCurves) break;
            curves.Add(ParseCurve(item, order, known));
        }

        spec = new FunctionPlotSpec(title, curves, parameters, x, y);
        return true;
    }

    private static bool TryParseParam(JsonElement item, out PlotParam? param, out string? error)
    {
        param = null;
        error = null;
        var name = VisualJson.String(item, "name")?.Trim();
        if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, @"^[\p{L}_][\p{L}\p{Nd}_]{0,11}$"))
        {
            error = "params[].name 必须是简短的变量名";
            return false;
        }

        if (FixedVariables.Contains(name) || name is "e" or "pi")
        {
            error = $"参数名 {name} 与保留名冲突";
            return false;
        }

        var min = VisualJson.Number(item, "min");
        var max = VisualJson.Number(item, "max");
        if (min is null || max is null || !(min < max))
        {
            error = $"参数 {name} 需要 min < max";
            return false;
        }

        var value = Math.Clamp(VisualJson.Number(item, "default") ?? VisualJson.Number(item, "value") ?? (min.Value + max.Value) / 2, min.Value, max.Value);
        var step = VisualJson.Number(item, "step") is { } s && s > 0 ? s : NiceStep((max.Value - min.Value) / 100);
        param = new PlotParam(name, min.Value, max.Value, value, step);
        return true;
    }

    private static PlotCurve ParseCurve(JsonElement item, string[] order, string[] known)
    {
        string? label = null;
        string? expr = null;
        string? px = null;
        string? py = null;
        (double Min, double Max)? t = null;

        switch (item.ValueKind)
        {
            case JsonValueKind.String:
                expr = item.GetString();
                break;
            case JsonValueKind.Object:
                label = VisualJson.String(item, "label");
                expr = VisualJson.String(item, "expr") ?? VisualJson.String(item, "fn");
                px = VisualJson.String(item, "x");
                py = VisualJson.String(item, "y");
                t = VisualJson.Range(item, "t", out _) ?? VisualJson.Range(item, "theta", out _);
                break;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(px) && !string.IsNullOrWhiteSpace(py))
            {
                var ex = MathExpression.Parse(px, known);
                var ey = MathExpression.Parse(py, known);
                return new PlotCurve
                {
                    Kind = CurveKind.Parametric,
                    Source = $"({px}, {py})",
                    Label = label,
                    Latex = $@"\left({ex.ToLatex()},\ {ey.ToLatex()}\right)",
                    Fn = ex.Compile(order),
                    FnY = ey.Compile(order),
                    TMin = t?.Min ?? 0,
                    TMax = t?.Max ?? Math.Tau,
                };
            }

            if (string.IsNullOrWhiteSpace(expr))
                return Broken(expr ?? string.Empty, label, "缺少 expr");

            return ParseEquation(expr.Trim(), label, t, order, known);
        }
        catch (MathSyntaxException ex)
        {
            return Broken(expr ?? $"({px}, {py})", label, ex.Message);
        }
    }

    private static PlotCurve ParseEquation(string text, string? label, (double Min, double Max)? t, string[] order, string[] known)
    {
        var normalized = text.Replace("==", "=", StringComparison.Ordinal);
        var parts = normalized.Split('=');
        if (parts.Length > 2) return Broken(text, label, "只能有一个等号");

        if (parts.Length == 1)
        {
            var e = MathExpression.Parse(normalized, known);
            var vars = e.Variables;
            var usesY = vars.Contains("y");
            var usesX = vars.Contains("x");
            if (usesY && usesX)
                return Implicit(text, label, e, MathExpression.Parse("0", known), order);
            if (usesY)
                return Broken(text, label, "只含 y 时请写成 x = …");
            return Explicit(CurveKind.ExplicitY, text, label, "y", e, order);
        }

        var lhs = parts[0].Trim();
        var rhs = parts[1].Trim();
        if (lhs.Length == 0 || rhs.Length == 0) return Broken(text, label, "等式不完整");

        if (lhs == "y" || Regex.IsMatch(lhs, @"^[A-Za-z]\s*\(\s*x\s*\)$"))
            return Explicit(CurveKind.ExplicitY, text, label, lhs == "y" ? "y" : lhs, MathExpression.Parse(rhs, known), order);
        if (lhs == "x")
            return Explicit(CurveKind.ExplicitX, text, label, "x", MathExpression.Parse(rhs, known), order);
        if (lhs is "r" or "ρ")
        {
            var polar = MathExpression.Parse(rhs, known);
            return new PlotCurve
            {
                Kind = CurveKind.Polar,
                Source = text,
                Label = label,
                Latex = "r = " + polar.ToLatex(),
                Fn = polar.Compile(order),
                TMin = t?.Min ?? 0,
                TMax = t?.Max ?? Math.Tau,
            };
        }

        return Implicit(text, label, MathExpression.Parse(lhs, known), MathExpression.Parse(rhs, known), order);
    }

    private static PlotCurve Explicit(CurveKind kind, string text, string? label, string lhs, MathExpression e, string[] order)
    {
        var allowed = kind == CurveKind.ExplicitY ? "x" : "y";
        var stray = e.Variables.FirstOrDefault(v => FixedVariables.Contains(v) && v != allowed);
        if (stray is not null)
            return Broken(text, label, kind == CurveKind.ExplicitY ? $"y = … 右边只能用 x（出现了 {stray}）" : $"x = … 右边只能用 y（出现了 {stray}）");

        var head = lhs.Length > 1 ? lhs.Replace(" ", string.Empty, StringComparison.Ordinal) : lhs;
        return new PlotCurve
        {
            Kind = kind,
            Source = text,
            Label = label,
            Latex = head + " = " + e.ToLatex(),
            Fn = e.Compile(order),
        };
    }

    private static PlotCurve Implicit(string text, string? label, MathExpression lhs, MathExpression rhs, string[] order)
    {
        var fl = lhs.Compile(order);
        var fr = rhs.Compile(order);
        return new PlotCurve
        {
            Kind = CurveKind.Implicit,
            Source = text,
            Label = label,
            Latex = lhs.ToLatex() + " = " + rhs.ToLatex(),
            Fn = v => fl(v) - fr(v),
        };
    }

    private static PlotCurve Broken(string text, string? label, string error) => new()
    {
        Kind = CurveKind.ExplicitY,
        Source = text,
        Label = label,
        Error = error,
    };

    public static double NiceStep(double raw)
    {
        if (!(raw > 0) || double.IsInfinity(raw)) return 0.01;
        var exponent = Math.Floor(Math.Log10(raw));
        var fraction = raw / Math.Pow(10, exponent);
        var nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
        return nice * Math.Pow(10, exponent);
    }
}

public enum ChartType
{
    Line,
    Bar,
    Area,
    Scatter,
    Pie,
}

public sealed record ChartSeries(string Name, IReadOnlyList<double?> Values, IReadOnlyList<(double X, double Y)> Points);

public sealed record ChartSpec(
    string? Title,
    ChartType Type,
    IReadOnlyList<string> Categories,
    IReadOnlyList<ChartSeries> Series,
    string? Unit,
    bool Stacked)
{
    public const int MaxSeries = 8;
    public const int MaxPoints = 400;

    public static bool TryParse(JsonElement props, out ChartSpec? spec, out string? error)
    {
        spec = null;
        error = null;

        var typeText = (VisualJson.String(props, "type") ?? "line").Trim().ToLowerInvariant();
        ChartType type;
        switch (typeText)
        {
            case "line" or "折线" or "spline": type = ChartType.Line; break;
            case "bar" or "column" or "柱状" or "horizontal-bar": type = ChartType.Bar; break;
            case "area" or "面积": type = ChartType.Area; break;
            case "scatter" or "point" or "散点": type = ChartType.Scatter; break;
            case "pie" or "donut" or "doughnut" or "饼图": type = ChartType.Pie; break;
            default:
                error = $"type 不支持 {typeText}，可用 line / bar / area / scatter / pie";
                return false;
        }

        var categories = new List<string>();
        foreach (var key in (string[])["x", "labels", "categories"])
        {
            if (props.TryGetProperty(key, out var xs) && xs.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in xs.EnumerateArray())
                {
                    if (categories.Count >= MaxPoints) break;
                    categories.Add(item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString() ?? string.Empty,
                        JsonValueKind.Number => MathExpression.FormatNumber(item.GetDouble()),
                        _ => item.GetRawText(),
                    });
                }

                break;
            }
        }

        var seriesNodes = new List<JsonElement>();
        if (props.TryGetProperty("series", out var seriesNode))
        {
            if (seriesNode.ValueKind == JsonValueKind.Array) seriesNodes.AddRange(seriesNode.EnumerateArray());
            else if (seriesNode.ValueKind == JsonValueKind.Object) seriesNodes.Add(seriesNode);
        }
        else if (props.TryGetProperty("data", out _))
        {
            seriesNodes.Add(props);
        }

        var series = new List<ChartSeries>();
        foreach (var node in seriesNodes)
        {
            if (series.Count >= MaxSeries) break;
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                error = "series[].data 必须是数组";
                return false;
            }

            var name = VisualJson.String(node, "name") ?? $"系列 {series.Count + 1}";
            var values = new List<double?>();
            var points = new List<(double, double)>();
            if (type == ChartType.Scatter)
            {
                ReadPoints(data, points);
            }
            else
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (values.Count >= MaxPoints) break;
                    values.Add(item.ValueKind == JsonValueKind.Number ? item.GetDouble()
                        : item.ValueKind == JsonValueKind.String && double.TryParse(item.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed
                        : item.ValueKind == JsonValueKind.Object && VisualJson.Number(item, "value") is { } v ? v
                        : null);
                }
            }

            series.Add(new ChartSeries(name, values, points));
        }

        if (series.Count == 0 || series.All(s => s.Values.All(v => v is null) && s.Points.Count == 0))
        {
            error = "series 里没有可用的数据";
            return false;
        }

        if (type != ChartType.Scatter)
        {
            var length = series.Max(s => s.Values.Count);
            for (var i = categories.Count; i < length; i++)
                categories.Add((i + 1).ToString(CultureInfo.InvariantCulture));
        }

        var stacked = props.TryGetProperty("stacked", out var stackedNode) && stackedNode.ValueKind == JsonValueKind.True;
        spec = new ChartSpec(VisualJson.String(props, "title"), type, categories, series, VisualJson.String(props, "unit"), stacked);
        return true;
    }

    private static void ReadPoints(JsonElement data, List<(double, double)> points)
    {
        var items = data.EnumerateArray().ToList();
        if (items.Count > 0 && items.All(i => i.ValueKind == JsonValueKind.Number))
        {
            for (var i = 0; i + 1 < items.Count && points.Count < MaxPoints; i += 2)
                points.Add((items[i].GetDouble(), items[i + 1].GetDouble()));
            return;
        }

        foreach (var item in items)
        {
            if (points.Count >= MaxPoints) break;
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2
                && item[0].ValueKind == JsonValueKind.Number && item[1].ValueKind == JsonValueKind.Number)
            {
                points.Add((item[0].GetDouble(), item[1].GetDouble()));
            }
            else if (item.ValueKind == JsonValueKind.Object
                     && VisualJson.Number(item, "x") is { } x && VisualJson.Number(item, "y") is { } y)
            {
                points.Add((x, y));
            }
        }
    }
}

internal static class VisualJson
{
    public static string? String(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    public static double? Number(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var node)) return null;
        if (node.ValueKind == JsonValueKind.Number) return node.GetDouble();
        if (node.ValueKind == JsonValueKind.String)
        {
            var text = node.GetString() ?? string.Empty;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return value;
            // "2*pi", "-pi" — ranges are often written the way the reader thinks of them.
            try
            {
                var e = MathExpression.Parse(text);
                if (e.Variables.Count == 0) return e.Compile(Array.Empty<string>())(Array.Empty<double>());
            }
            catch (MathSyntaxException)
            {
            }
        }

        return null;
    }

    public static (double Min, double Max)? Range(JsonElement owner, string name, out string? error)
    {
        error = null;
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var node) || node.ValueKind == JsonValueKind.Null)
            return null;
        if (node.ValueKind != JsonValueKind.Array || node.GetArrayLength() != 2)
        {
            error = $"{name} 应写成 [最小值, 最大值]";
            return null;
        }

        var wrapper = JsonDocument.Parse($"{{\"a\":{node[0].GetRawText()},\"b\":{node[1].GetRawText()}}}").RootElement;
        var min = Number(wrapper, "a");
        var max = Number(wrapper, "b");
        if (min is null || max is null || !(min < max) || double.IsInfinity(min.Value) || double.IsInfinity(max.Value))
        {
            error = $"{name} 需要满足 最小值 < 最大值";
            return null;
        }

        return (min.Value, max.Value);
    }
}
