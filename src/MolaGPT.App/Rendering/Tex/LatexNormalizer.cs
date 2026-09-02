using System.Text;
using System.Text.RegularExpressions;

namespace MolaGPT.App.Rendering.Tex;

internal readonly record struct PreparedLatex(string Formula, bool DrawBox);

/// <summary>
/// Normalizes common amsmath output to the two embedded renderers' supported
/// dialects. Mathematical content is preserved when a layout-only command is
/// unavailable.
/// </summary>
internal static class LatexNormalizer
{
    public static PreparedLatex Prepare(string source)
    {
        var formula = StripOuterDelimiters(source.Trim());
        var drawBox = TryUnwrapOuterCommand(formula, "boxed", out var boxedBody);
        if (drawBox) formula = boxedBody;

        formula = RewriteEnvironment(formula, "equation", static body => body);
        formula = RewriteEnvironment(formula, "equation*", static body => body);
        formula = RewriteCompatibilityCommands(formula);
        return new PreparedLatex(formula.Trim(), drawBox);
    }

    public static string ForCSharpMath(string source)
    {
        var formula = Prepare(source).Formula;
        formula = RewriteEnvironment(
            formula, "align", static body => $@"\begin{{aligned}}{body}\end{{aligned}}");
        formula = RewriteEnvironment(
            formula, "align*", static body => $@"\begin{{aligned}}{body}\end{{aligned}}");
        formula = RewriteEnvironment(
            formula, "alignat", static body => $@"\begin{{aligned}}{StripLeadingGroup(body)}\end{{aligned}}");
        formula = RewriteEnvironment(
            formula, "alignat*", static body => $@"\begin{{aligned}}{StripLeadingGroup(body)}\end{{aligned}}");
        formula = RewriteEnvironment(
            formula, "alignedat", static body => $@"\begin{{aligned}}{StripLeadingGroup(body)}\end{{aligned}}");
        formula = RewriteEnvironment(
            formula, "gather*", static body => $@"\begin{{gather}}{body}\end{{gather}}");
        formula = RewriteEnvironment(
            formula, "multline", static body => $@"\begin{{gather}}{body}\end{{gather}}");
        formula = RewriteEnvironment(
            formula, "multline*", static body => $@"\begin{{gather}}{body}\end{{gather}}");
        return formula;
    }

    public static string ForAvaloniaMath(string source)
    {
        var formula = Prepare(source).Formula;

        formula = RewriteEnvironment(
            formula, "align*", static body => $@"\begin{{align}}{body}\end{{align}}");
        formula = RewriteEnvironment(
            formula, "alignat", static body => $@"\begin{{align}}{StripLeadingGroup(body)}\end{{align}}");
        formula = RewriteEnvironment(
            formula, "alignat*", static body => $@"\begin{{align}}{StripLeadingGroup(body)}\end{{align}}");
        formula = RewriteEnvironment(formula, "aligned", Matrix);
        formula = RewriteEnvironment(formula, "alignedat", static body => Matrix(StripLeadingGroup(body)));
        formula = RewriteEnvironment(formula, "gather", Matrix);
        formula = RewriteEnvironment(formula, "gather*", Matrix);
        formula = RewriteEnvironment(formula, "multline", Matrix);
        formula = RewriteEnvironment(formula, "multline*", Matrix);
        formula = RewriteEnvironment(formula, "split", Matrix);
        formula = RewriteEnvironment(formula, "matrix", Matrix);
        formula = RewriteEnvironment(formula, "smallmatrix", Matrix);
        formula = RewriteEnvironment(formula, "pmatrix", static body => $@"\pmatrix{{{body}}}");
        formula = RewriteEnvironment(formula, "bmatrix", static body => $@"\left[\matrix{{{body}}}\right]");
        formula = RewriteEnvironment(formula, "Bmatrix", static body => $@"\left\{{\matrix{{{body}}}\right\}}");
        formula = RewriteEnvironment(formula, "vmatrix", static body => $@"\left|\matrix{{{body}}}\right|");
        formula = RewriteEnvironment(formula, "Vmatrix", static body => $@"\left\|\matrix{{{body}}}\right\|");
        formula = RewriteEnvironment(formula, "cases", static body => $@"\cases{{{body}}}");
        formula = RewriteEnvironment(formula, "array", static body => Matrix(StripLeadingGroup(body)));

        formula = formula
            .Replace(@"\dfrac", @"\frac", StringComparison.Ordinal)
            .Replace(@"\tfrac", @"\frac", StringComparison.Ordinal)
            .Replace(@"\cfrac", @"\frac", StringComparison.Ordinal)
            .Replace(@"\dbinom", @"\binom", StringComparison.Ordinal)
            .Replace(@"\tbinom", @"\binom", StringComparison.Ordinal)
            .Replace(@"\qquad", @"\;\;\;\;", StringComparison.Ordinal)
            .Replace(@"\quad", @"\;\;", StringComparison.Ordinal)
            .Replace(@"\displaystyle", string.Empty, StringComparison.Ordinal)
            .Replace(@"\textstyle", string.Empty, StringComparison.Ordinal)
            .Replace(@"\scriptstyle", string.Empty, StringComparison.Ordinal)
            .Replace(@"\scriptscriptstyle", string.Empty, StringComparison.Ordinal)
            .Replace(@"\operatorname*", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\operatorname", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\mathbf", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\mathbb", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\mathsf", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\mathtt", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\mathfrak", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\boldsymbol", @"\mathrm", StringComparison.Ordinal)
            .Replace(@"\mathscr", @"\mathcal", StringComparison.Ordinal)
            .Replace(@"\textrm", @"\text", StringComparison.Ordinal)
            .Replace(@"\textbf", @"\text", StringComparison.Ordinal)
            .Replace(@"\textit", @"\text", StringComparison.Ordinal)
            .Replace(@"\texttt", @"\text", StringComparison.Ordinal)
            .Replace(@"\mbox", @"\text", StringComparison.Ordinal);

        return Regex.Replace(
            formula,
            @"\\dots(?![A-Za-z])",
            @"\ldots",
            RegexOptions.CultureInvariant);
    }

    private static string RewriteCompatibilityCommands(string formula)
    {
        var tags = new List<(string Body, bool Starred)>();
        formula = RewriteOneArgumentCommand(formula, "tag*", body =>
        {
            tags.Add((body, true));
            return string.Empty;
        });
        formula = RewriteOneArgumentCommand(formula, "tag", body =>
        {
            tags.Add((body, false));
            return string.Empty;
        });
        formula = RewriteOneArgumentCommand(formula, "label", static _ => string.Empty);

        for (var pass = 0; pass < 4; pass++)
        {
            var before = formula;
            formula = RewriteTwoArgumentCommand(
                formula, "overset", static (top, body) => $"{{{body}}}^{{{top}}}");
            formula = RewriteTwoArgumentCommand(
                formula, "stackrel", static (top, body) => $"{{{body}}}^{{{top}}}");
            formula = RewriteTwoArgumentCommand(
                formula, "underset", static (bottom, body) => $"{{{body}}}_{{{bottom}}}");
            formula = RewriteOneArgumentCommand(formula, "substack", static body => Matrix(body));
            formula = RewriteOneArgumentCommand(
                formula, "xrightarrow", static body => $@"\rightarrow^{{{body}}}");
            formula = RewriteOneArgumentCommand(
                formula, "xleftarrow", static body => $@"\leftarrow^{{{body}}}");
            formula = RewriteOneArgumentCommand(formula, "boxed", static body => $"{{{body}}}");
            if (string.Equals(before, formula, StringComparison.Ordinal)) break;
        }

        formula = formula
            .Replace(@"\overbrace", @"\overline", StringComparison.Ordinal)
            .Replace(@"\underbrace", @"\underline", StringComparison.Ordinal)
            .Replace(@"\textcolor", @"\color", StringComparison.Ordinal)
            .Replace(@"\notag", string.Empty, StringComparison.Ordinal)
            .Replace(@"\nonumber", string.Empty, StringComparison.Ordinal);

        foreach (var (body, starred) in tags)
        {
            formula += starred
                ? $@"\quad\mathrm{{{body}}}"
                : $@"\quad\mathrm{{({body})}}";
        }

        return formula;
    }

    private static string RewriteEnvironment(
        string source,
        string environment,
        Func<string, string> replacement)
    {
        var escaped = Regex.Escape(environment);
        return Regex.Replace(
            source,
            $@"\\begin\{{{escaped}\}}(?<body>[\s\S]*?)\\end\{{{escaped}\}}",
            match => replacement(match.Groups["body"].Value),
            RegexOptions.CultureInvariant);
    }

    private static string RewriteOneArgumentCommand(
        string source,
        string command,
        Func<string, string> replacement)
    {
        var marker = "\\" + command;
        StringBuilder? result = null;
        var search = 0;
        var emitted = 0;

        while (TryFindCommand(source, marker, search, out var commandStart))
        {
            var argumentStart = commandStart + marker.Length;
            while (argumentStart < source.Length && char.IsWhiteSpace(source[argumentStart]))
                argumentStart++;

            if (!TryReadGroup(source, argumentStart, out var body, out var argumentEnd))
            {
                search = commandStart + marker.Length;
                continue;
            }

            result ??= new StringBuilder(source.Length);
            result.Append(source, emitted, commandStart - emitted);
            result.Append(replacement(body));
            emitted = argumentEnd;
            search = argumentEnd;
        }

        if (result is null) return source;
        result.Append(source, emitted, source.Length - emitted);
        return result.ToString();
    }

    private static string RewriteTwoArgumentCommand(
        string source,
        string command,
        Func<string, string, string> replacement)
    {
        var marker = "\\" + command;
        StringBuilder? result = null;
        var search = 0;
        var emitted = 0;

        while (TryFindCommand(source, marker, search, out var commandStart))
        {
            var firstStart = commandStart + marker.Length;
            while (firstStart < source.Length && char.IsWhiteSpace(source[firstStart])) firstStart++;
            if (!TryReadGroup(source, firstStart, out var first, out var firstEnd))
            {
                search = commandStart + marker.Length;
                continue;
            }

            var secondStart = firstEnd;
            while (secondStart < source.Length && char.IsWhiteSpace(source[secondStart])) secondStart++;
            if (!TryReadGroup(source, secondStart, out var second, out var secondEnd))
            {
                search = firstEnd;
                continue;
            }

            result ??= new StringBuilder(source.Length);
            result.Append(source, emitted, commandStart - emitted);
            result.Append(replacement(first, second));
            emitted = secondEnd;
            search = secondEnd;
        }

        if (result is null) return source;
        result.Append(source, emitted, source.Length - emitted);
        return result.ToString();
    }

    private static bool TryFindCommand(string source, string marker, int start, out int commandStart)
    {
        for (commandStart = source.IndexOf(marker, start, StringComparison.Ordinal);
             commandStart >= 0;
             commandStart = source.IndexOf(marker, commandStart + marker.Length, StringComparison.Ordinal))
        {
            if (IsEscaped(source, commandStart)) continue;
            var after = commandStart + marker.Length;
            if (after >= source.Length || !char.IsLetter(source[after])) return true;
        }

        return false;
    }

    private static bool TryReadGroup(string source, int open, out string body, out int end)
    {
        body = string.Empty;
        end = open;
        if (open >= source.Length || source[open] != '{') return false;

        var depth = 0;
        for (var cursor = open; cursor < source.Length; cursor++)
        {
            if (IsEscaped(source, cursor)) continue;
            if (source[cursor] == '{')
            {
                depth++;
            }
            else if (source[cursor] == '}' && --depth == 0)
            {
                body = source[(open + 1)..cursor];
                end = cursor + 1;
                return true;
            }
        }

        return false;
    }

    private static bool TryUnwrapOuterCommand(string source, string command, out string body)
    {
        body = string.Empty;
        var marker = "\\" + command;
        if (!source.StartsWith(marker, StringComparison.Ordinal)) return false;

        var argumentStart = marker.Length;
        if (argumentStart < source.Length && char.IsLetter(source[argumentStart])) return false;
        while (argumentStart < source.Length && char.IsWhiteSpace(source[argumentStart])) argumentStart++;
        if (!TryReadGroup(source, argumentStart, out body, out var end)) return false;
        return string.IsNullOrWhiteSpace(source[end..]);
    }

    private static string StripOuterDelimiters(string source)
    {
        if (source.Length >= 4 && source.StartsWith("$$", StringComparison.Ordinal)
                               && source.EndsWith("$$", StringComparison.Ordinal))
            return source[2..^2].Trim();
        if (source.Length >= 4 && source.StartsWith(@"\[", StringComparison.Ordinal)
                               && source.EndsWith(@"\]", StringComparison.Ordinal))
            return source[2..^2].Trim();
        if (source.Length >= 4 && source.StartsWith(@"\(", StringComparison.Ordinal)
                               && source.EndsWith(@"\)", StringComparison.Ordinal))
            return source[2..^2].Trim();
        if (source.Length >= 2 && source[0] == '$' && source[^1] == '$')
            return source[1..^1].Trim();
        return source;
    }

    private static string StripLeadingGroup(string source)
    {
        var start = 0;
        while (start < source.Length && char.IsWhiteSpace(source[start])) start++;
        return TryReadGroup(source, start, out _, out var end) ? source[end..] : source;
    }

    private static string Matrix(string body) => $@"\matrix{{{body}}}";

    private static bool IsEscaped(string source, int index)
    {
        var slashCount = 0;
        for (var cursor = index - 1; cursor >= 0 && source[cursor] == '\\'; cursor--)
            slashCount++;
        return (slashCount & 1) != 0;
    }
}
