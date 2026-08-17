using System.Text.RegularExpressions;
using WpfMath.Parsers;

namespace MolaGPT.Desktop.Controls;

/// <summary>
/// Adapts common model-generated LaTeX to the smaller dialect understood by
/// WpfMath. Every formula is parsed here before a FormulaControl is created so
/// unsupported input can fall back to readable source instead of WpfMath's
/// collapsed red error template.
/// </summary>
internal static partial class WpfMathFormulaAdapter
{
    private static readonly object s_parserGate = new();

    internal static PreparedWpfMathFormula Prepare(string source)
    {
        var original = source?.Trim() ?? string.Empty;
        var formula = original;
        var drawBox = TryUnwrapOuterCommand(formula, "boxed", out var boxedBody);
        if (drawBox)
            formula = boxedBody;

        formula = BasicAliasRegex().Replace(formula, static match =>
            match.Groups["command"].Value switch
            {
                "dfrac" or "tfrac" => @"\frac",
                "quad" => @"\;\;",
                "qquad" => @"\;\;\;\;",
                // FormulaControl already chooses display/inline scale. These
                // style selectors are unsupported by WpfMath and can be safely
                // removed without changing the mathematical expression.
                "displaystyle" or "textstyle" or "scriptstyle" or "scriptscriptstyle" => string.Empty,
                _ => match.Value
            });

        // WpfMath supports \mathrm but not the amsmath operatorname helper.
        formula = OperatorNameRegex().Replace(formula, @"\mathrm");

        // These commands only select a font face. Use the closest WpfMath
        // equivalent so their contents remain typeset instead of disappearing.
        formula = RomanMathFontRegex().Replace(formula, @"\mathrm");
        formula = ScriptMathFontRegex().Replace(formula, @"\mathcal");
        formula = TextFontRegex().Replace(formula, @"\text");

        return new PreparedWpfMathFormula(original, formula.Trim(), drawBox);
    }

    internal static bool TryPrepare(
        string source,
        out PreparedWpfMathFormula prepared,
        out string? parseError)
    {
        prepared = Prepare(source);
        if (prepared.Formula.Length == 0)
        {
            parseError = "公式内容为空。";
            return false;
        }

        try
        {
            // The parser singleton owns mutable parsing state. Markdown is
            // normally rendered on the UI thread, but theme refreshes and tests
            // may invoke this concurrently, so keep preflight parsing serialized.
            lock (s_parserGate)
                _ = WpfTeXFormulaParser.Instance.Parse(prepared.Formula);

            parseError = null;
            return true;
        }
        catch (Exception ex)
        {
            parseError = ex.Message;
            return false;
        }
    }

    private static bool TryUnwrapOuterCommand(string source, string command, out string body)
    {
        body = string.Empty;
        var prefix = "\\" + command;
        if (!source.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var cursor = prefix.Length;
        if (cursor < source.Length && char.IsLetter(source[cursor]))
            return false;

        while (cursor < source.Length && char.IsWhiteSpace(source[cursor]))
            cursor++;
        if (cursor >= source.Length || source[cursor] != '{')
            return false;

        var openBrace = cursor;
        var depth = 0;
        for (; cursor < source.Length; cursor++)
        {
            if (IsEscaped(source, cursor))
                continue;

            switch (source[cursor])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        if (!string.IsNullOrWhiteSpace(source[(cursor + 1)..]))
                            return false;

                        body = source[(openBrace + 1)..cursor];
                        return true;
                    }
                    break;
            }
        }

        return false;
    }

    private static bool IsEscaped(string source, int index)
    {
        var slashCount = 0;
        for (var i = index - 1; i >= 0 && source[i] == '\\'; i--)
            slashCount++;
        return (slashCount & 1) != 0;
    }

    [GeneratedRegex(@"(?<!\\)\\(?<command>qquad|quad|dfrac|tfrac|displaystyle|textstyle|scriptstyle|scriptscriptstyle)(?![A-Za-z])")]
    private static partial Regex BasicAliasRegex();

    [GeneratedRegex(@"(?<!\\)\\operatorname\*?(?![A-Za-z])")]
    private static partial Regex OperatorNameRegex();

    [GeneratedRegex(@"(?<!\\)\\(?:mathbf|mathbb|mathsf|mathtt|mathfrak|boldsymbol)(?![A-Za-z])")]
    private static partial Regex RomanMathFontRegex();

    [GeneratedRegex(@"(?<!\\)\\mathscr(?![A-Za-z])")]
    private static partial Regex ScriptMathFontRegex();

    [GeneratedRegex(@"(?<!\\)\\(?:textrm|textbf|textit|texttt|mbox)(?![A-Za-z])")]
    private static partial Regex TextFontRegex();
}

internal readonly record struct PreparedWpfMathFormula(
    string OriginalFormula,
    string Formula,
    bool DrawBox);
