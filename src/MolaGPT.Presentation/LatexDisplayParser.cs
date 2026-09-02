namespace MolaGPT.Presentation;

public readonly record struct LatexDisplaySpan(
    int Start,
    int Length,
    int FormulaStart,
    int FormulaLength);

/// <summary>
/// Finds display-math regions without treating Markdown code spans as LaTeX.
/// The returned offsets always index the original source.
/// </summary>
public static class LatexDisplayParser
{
    private static readonly HashSet<string> s_displayEnvironments = new(StringComparer.Ordinal)
    {
        "equation", "equation*",
        "align", "align*", "alignat", "alignat*", "aligned", "alignedat",
        "gather", "gather*", "multline", "multline*", "split",
        "matrix", "smallmatrix", "pmatrix", "bmatrix", "Bmatrix", "vmatrix", "Vmatrix",
        "cases", "array"
    };

    public static IReadOnlyList<LatexDisplaySpan> Find(string source)
    {
        if (string.IsNullOrEmpty(source)) return Array.Empty<LatexDisplaySpan>();

        var spans = new List<LatexDisplaySpan>();
        for (var index = 0; index < source.Length;)
        {
            if (source[index] == '`')
            {
                index = SkipCodeSpan(source, index);
                continue;
            }

            if (StartsWith(source, index, "$$") && !IsEscaped(source, index))
            {
                var close = FindUnescaped(source, "$$", index + 2);
                if (close >= 0)
                {
                    AddDelimited(spans, source, index, 2, close, 2);
                    index = close + 2;
                    continue;
                }
            }

            if (StartsWith(source, index, @"\[") && !IsEscaped(source, index))
            {
                var close = FindUnescaped(source, @"\]", index + 2);
                if (close >= 0)
                {
                    AddDelimited(spans, source, index, 2, close, 2);
                    index = close + 2;
                    continue;
                }
            }

            if (TryReadEnvironment(source, index, out var environment, out var openingEnd)
                && s_displayEnvironments.Contains(environment))
            {
                var closeEnd = FindEnvironmentEnd(source, environment, openingEnd);
                if (closeEnd >= 0)
                {
                    spans.Add(new LatexDisplaySpan(index, closeEnd - index, index, closeEnd - index));
                    index = closeEnd;
                    continue;
                }
            }

            index++;
        }

        return spans;
    }

    private static void AddDelimited(
        List<LatexDisplaySpan> spans,
        string source,
        int openingStart,
        int openingLength,
        int closingStart,
        int closingLength)
    {
        var formulaStart = openingStart + openingLength;
        var formulaLength = closingStart - formulaStart;
        if (formulaLength <= 0 || source.AsSpan(formulaStart, formulaLength).Trim().IsEmpty)
            return;

        spans.Add(new LatexDisplaySpan(
            openingStart,
            closingStart + closingLength - openingStart,
            formulaStart,
            formulaLength));
    }

    private static int SkipCodeSpan(string source, int start)
    {
        var runLength = 1;
        while (start + runLength < source.Length && source[start + runLength] == '`')
            runLength++;

        for (var cursor = start + runLength; cursor < source.Length;)
        {
            var tick = source.IndexOf('`', cursor);
            if (tick < 0) return start + runLength;

            var closingLength = 1;
            while (tick + closingLength < source.Length && source[tick + closingLength] == '`')
                closingLength++;

            if (closingLength == runLength) return tick + closingLength;
            cursor = tick + closingLength;
        }

        return start + runLength;
    }

    private static bool TryReadEnvironment(
        string source,
        int start,
        out string environment,
        out int openingEnd)
    {
        environment = string.Empty;
        openingEnd = start;
        const string prefix = @"\begin{";
        if (!StartsWith(source, start, prefix) || IsEscaped(source, start)) return false;

        var nameStart = start + prefix.Length;
        var nameEnd = source.IndexOf('}', nameStart);
        if (nameEnd < 0) return false;

        environment = source[nameStart..nameEnd];
        openingEnd = nameEnd + 1;
        return environment.Length > 0;
    }

    private static int FindEnvironmentEnd(string source, string environment, int start)
    {
        var opening = $@"\begin{{{environment}}}";
        var closing = $@"\end{{{environment}}}";
        var depth = 1;
        var cursor = start;

        while (cursor < source.Length)
        {
            var nextOpening = FindUnescaped(source, opening, cursor);
            var nextClosing = FindUnescaped(source, closing, cursor);
            if (nextClosing < 0) return -1;

            if (nextOpening >= 0 && nextOpening < nextClosing)
            {
                depth++;
                cursor = nextOpening + opening.Length;
                continue;
            }

            depth--;
            cursor = nextClosing + closing.Length;
            if (depth == 0) return cursor;
        }

        return -1;
    }

    private static int FindUnescaped(string source, string token, int start)
    {
        for (var index = source.IndexOf(token, start, StringComparison.Ordinal);
             index >= 0;
             index = source.IndexOf(token, index + token.Length, StringComparison.Ordinal))
        {
            if (!IsEscaped(source, index)) return index;
        }

        return -1;
    }

    private static bool StartsWith(string source, int start, string value) =>
        start + value.Length <= source.Length
        && source.AsSpan(start, value.Length).SequenceEqual(value);

    private static bool IsEscaped(string source, int index)
    {
        var slashCount = 0;
        for (var cursor = index - 1; cursor >= 0 && source[cursor] == '\\'; cursor--)
            slashCount++;
        return (slashCount & 1) != 0;
    }
}
