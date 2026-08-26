using System.Collections.Concurrent;
using Avalonia.Media;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
// FontStyle is declared in both Avalonia.Media and TextMateSharp.Themes;
// the Avalonia one is what the runs carry, so the TextMate namespace is
// imported under an alias rather than opened.
using TmTheme = TextMateSharp.Themes.Theme;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Syntax highlighting for fenced code blocks, backed by TextMateSharp — the
/// same grammar and theme files VS Code uses.
///
/// Deliberately *not* AvaloniaEdit. The obvious route is to drop a read-only
/// TextEditor into each code block, but a transcript can hold hundreds of
/// fences, and a TextEditor is a full editing surface: caret, folding manager,
/// undo stack, its own virtualizing layer. Putting one in every row would make
/// rows heavy again, which is precisely the property this migration exists to
/// fix. Tokenizing here and emitting coloured <see cref="Run"/>s into the
/// existing text block keeps a code row about as cheap as a paragraph row.
///
/// Everything expensive is cached: the registry and theme are per-variant
/// singletons, grammars are cached per language, and highlighted output is
/// memoized per (language, code, variant) because a row is re-realized every
/// time it scrolls back into view.
/// </summary>
internal static class CodeHighlighter
{
    private sealed record Palette(Registry Registry, TmTheme Theme);

    private static readonly ConcurrentDictionary<bool, Palette> Palettes = new();
    private static readonly ConcurrentDictionary<(bool Dark, string Language), IGrammar?> Grammars = new();
    private static readonly ConcurrentDictionary<(bool Dark, string Language, string Code), IReadOnlyList<(string Text, IBrush? Brush, FontStyle Style, FontWeight Weight)>> Cache = new();

    /// <summary>
    /// A single fence's worth of tokens. Returns null when the language is
    /// unknown or tokenizing fails, which the caller renders as plain text —
    /// unhighlighted code is fine, missing code is not.
    /// </summary>
    public static IReadOnlyList<(string Text, IBrush? Brush, FontStyle Style, FontWeight Weight)>? Highlight(
        string? code, string? language, bool dark)
    {
        if (string.IsNullOrEmpty(code)) return null;

        var lang = NormalizeLanguage(language);
        if (lang is null) return null;

        // Very large fences are left plain: tokenizing them costs more than the
        // colour is worth, and it would happen on the UI thread during scroll.
        if (code.Length > 60_000) return null;

        var key = (dark, lang, code);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var result = Tokenize(code, lang, dark);
            if (result is null) return null;

            // Bounded so a long session cannot grow this without limit.
            if (Cache.Count > 400) Cache.Clear();
            Cache[key] = result;
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<(string, IBrush?, FontStyle, FontWeight)>? Tokenize(
        string code, string language, bool dark)
    {
        var palette = Palettes.GetOrAdd(dark, isDark =>
        {
            var options = new RegistryOptions(isDark ? ThemeName.DarkPlus : ThemeName.LightPlus);
            var registry = new Registry(options);
            return new Palette(registry, registry.GetTheme());
        });

        var grammar = Grammars.GetOrAdd((dark, language), k =>
        {
            var options = new RegistryOptions(k.Dark ? ThemeName.DarkPlus : ThemeName.LightPlus);
            var scope = options.GetScopeByLanguageId(k.Language);
            return string.IsNullOrEmpty(scope) ? null : palette.Registry.LoadGrammar(scope);
        });

        if (grammar is null) return null;

        var runs = new List<(string, IBrush?, FontStyle, FontWeight)>();
        var lines = code.Replace("\r\n", "\n").Split('\n');
        IStateStack? state = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var tokenized = grammar.TokenizeLine(line, state, TimeSpan.FromMilliseconds(200));
            state = tokenized.RuleStack;

            foreach (var token in tokenized.Tokens)
            {
                var start = Math.Min(token.StartIndex, line.Length);
                var end = Math.Min(token.EndIndex, line.Length);
                if (end <= start) continue;

                var text = line[start..end];
                var (brush, style, weight) = Style(palette.Theme, token.Scopes);
                runs.Add((text, brush, style, weight));
            }

            if (i < lines.Length - 1) runs.Add(("\n", null, FontStyle.Normal, FontWeight.Normal));
        }

        return runs;
    }

    // TextMate font-style bit flags. Spelled out rather than imported: the
    // constants are not exposed as a public type by TextMateSharp 2.0.4, and
    // these values are fixed by the TextMate grammar format itself.
    private const int StyleItalic = 1;
    private const int StyleBold = 2;

    private static (IBrush?, FontStyle, FontWeight) Style(TmTheme theme, List<string> scopes)
    {
        // Match takes the whole scope stack, innermost first, and returns the
        // rules that apply in precedence order.
        foreach (var rule in theme.Match(scopes))
        {
            if (rule.foreground <= 0) continue;

            var hex = theme.GetColor(rule.foreground);
            if (string.IsNullOrEmpty(hex)) continue;

            var flags = (int)rule.fontStyle;
            return (
                Parse(hex),
                (flags & StyleItalic) != 0 ? FontStyle.Italic : FontStyle.Normal,
                (flags & StyleBold) != 0 ? FontWeight.Bold : FontWeight.Normal);
        }

        return (null, FontStyle.Normal, FontWeight.Normal);
    }

    private static readonly ConcurrentDictionary<string, IBrush> BrushCache = new();

    private static IBrush? Parse(string hex) =>
        BrushCache.GetOrAdd(hex, h =>
        {
            try
            {
                var brush = new SolidColorBrush(Color.Parse(h));
                brush.ToImmutable();
                return brush;
            }
            catch
            {
                return Brushes.Transparent;
            }
        });

    /// <summary>
    /// Maps the fence's info string to a TextMate language id. Markdown fences
    /// carry whatever the model felt like writing, so the common aliases are
    /// spelled out rather than hoping the grammar set recognises them.
    /// </summary>
    private static string? NormalizeLanguage(string? language)
    {
        var lang = language?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(lang)) return null;

        return lang switch
        {
            "py" or "python3" => "python",
            "js" or "node" => "javascript",
            "ts" => "typescript",
            "sh" or "zsh" or "console" or "shell-session" => "shellscript",
            "bash" => "shellscript",
            "yml" => "yaml",
            "cs" or "c#" => "csharp",
            "c++" or "cpp" => "cpp",
            "rs" => "rust",
            "golang" => "go",
            "md" => "markdown",
            "text" or "plain" or "plaintext" or "txt" or "output" => null,
            _ => lang
        };
    }
}
