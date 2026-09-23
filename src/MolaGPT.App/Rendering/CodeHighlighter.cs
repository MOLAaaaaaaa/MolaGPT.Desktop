using System.Collections.Concurrent;
using System.Threading.Channels;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
// FontStyle is declared in both Avalonia.Media and TextMateSharp.Themes;
// the Avalonia one is what the runs carry, so the TextMate namespace is
// imported under an alias rather than opened.
using TmTheme = TextMateSharp.Themes.Theme;

namespace MolaGPT.App.Rendering;

/// <summary>One coloured span within a line.</summary>
internal readonly record struct CodeToken(int Start, int Length, IBrush? Brush, FontStyle Style, FontWeight Weight);

/// <summary>A tokenized fence: its lines, and each line's coloured spans.</summary>
internal sealed class HighlightedCode(string code, string[] lines, CodeToken[][] tokens)
{
    public string Code { get; } = code;
    public string[] Lines { get; } = lines;
    public CodeToken[][] Tokens { get; } = tokens;
}

/// <summary>
/// Syntax highlighting for fenced code blocks, backed by TextMateSharp — the
/// same grammar and theme files VS Code uses.
///
/// Deliberately *not* AvaloniaEdit. The obvious route is to drop a read-only
/// TextEditor into each code block, but a transcript can hold hundreds of
/// fences, and a TextEditor is a full editing surface: caret, folding manager,
/// undo stack, its own virtualizing layer. Tokenizing here and emitting
/// coloured runs into the existing text block keeps a code row about as cheap
/// as a paragraph row.
///
/// All tokenizing happens on one worker thread, never the UI thread. Measured
/// on a real 297-line HTML page: loading the HTML grammar (which pulls in CSS
/// and JavaScript) took 397 ms the first time, and tokenizing the page 90–100
/// ms every time it changed — which, for a page streaming into the canvas, was
/// every 250 ms, and left the transcript unable to scroll. TextMate's registry
/// and grammars are not thread-safe, so the worker owns them outright.
///
/// Tokenizing is incremental. The worker remembers the last few documents per
/// language with the grammar state at the end of every line; a new version
/// that shares leading lines with one of them resumes from the first line that
/// differs. A fence streaming in costs its new lines, not the whole fence again.
/// </summary>
internal static class CodeHighlighter
{
    /// <summary>Longer fences stay plain in the transcript: colouring them costs
    /// more in layout than it is worth.</summary>
    public const int MaxTranscriptLength = 60_000;

    /// <summary>The canvas lays out only visible lines, so it can colour far more.</summary>
    public const int MaxViewLength = 400_000;

    private sealed record Palette(Registry Registry, TmTheme Theme);

    private sealed record Job(string Code, string Language, bool Dark, bool Remember, TaskCompletionSource<HighlightedCode?> Result);

    /// <summary>Tokenized lines and the grammar state after each, for resuming.</summary>
    private sealed class Memo(string[] lines, CodeToken[][] tokens, IStateStack?[] states)
    {
        public string[] Lines = lines;
        public CodeToken[][] Tokens = tokens;
        public IStateStack?[] States = states;
    }

    private static readonly Channel<Job?> Jobs = Channel.CreateUnbounded<Job?>(new UnboundedChannelOptions { SingleReader = true });
    private static readonly ConcurrentDictionary<(bool Dark, string Language, string Code), HighlightedCode> Cache = new();

    // Worker-owned: touched only by the worker thread.
    private static readonly Dictionary<bool, Palette> Palettes = new();
    private static readonly Dictionary<(bool Dark, string Language), IGrammar?> Grammars = new();
    private static readonly Dictionary<(bool Dark, string Language), List<Memo>> Memos = new();
    private static readonly Dictionary<string, IBrush> BrushCache = new(StringComparer.Ordinal);
    private const int MemosPerLanguage = 6;

    static CodeHighlighter()
    {
        var worker = new Thread(Work) { IsBackground = true, Name = "Code highlighter", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    /// <summary>
    /// 退到后台时丢掉记忆的高亮结果。行上的 run 不受影响，切回来滚动到代码块时
    /// 按需重新 tokenize。Palettes/Grammars 保留：重建它们要读语法文件，反而贵。
    /// </summary>
    internal static void TrimForBackground()
    {
        Cache.Clear();
        Jobs.Writer.TryWrite(null);
    }

    /// <summary>Whether this language is one we colour at all.</summary>
    public static bool Supports(string? language) => NormalizeLanguage(language) is not null;

    /// <summary>A finished result for exactly this code, if there is one.</summary>
    public static HighlightedCode? TryGetCached(string? code, string? language, bool dark)
    {
        if (string.IsNullOrEmpty(code) || NormalizeLanguage(language) is not { } lang) return null;
        return Cache.TryGetValue((dark, lang, code), out var cached) ? cached : null;
    }

    /// <summary>
    /// Tokenizes on the worker. Completes with null when the language is
    /// unknown, the code is over <paramref name="maxLength"/>, or tokenizing
    /// fails — the caller shows plain text: unhighlighted code is fine, missing
    /// code is not. Continuations run wherever the caller awaits.
    ///
    /// <paramref name="remember"/> false keeps the result out of the cache: a
    /// page streaming into the canvas asks for every snapshot, and caching each
    /// one would fill the cache with versions nobody will ask for again.
    /// </summary>
    public static Task<HighlightedCode?> HighlightAsync(string? code, string? language, bool dark, int maxLength = MaxTranscriptLength, bool remember = true)
    {
        if (string.IsNullOrEmpty(code) || code.Length > maxLength || NormalizeLanguage(language) is not { } lang)
            return Task.FromResult<HighlightedCode?>(null);
        if (Cache.TryGetValue((dark, lang, code), out var cached))
            return Task.FromResult<HighlightedCode?>(cached);

        var job = new Job(code, lang, dark, remember, new TaskCompletionSource<HighlightedCode?>(TaskCreationOptions.RunContinuationsAsynchronously));
        Jobs.Writer.TryWrite(job);
        return job.Result.Task;
    }

    private static void Work()
    {
        var reader = Jobs.Reader;
        while (true)
        {
            Job? job;
            try
            {
                if (!reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()) return;
                if (!reader.TryRead(out job)) continue;
            }
            catch
            {
                return;
            }

            if (job is null)
            {
                Memos.Clear();
                continue;
            }

            HighlightedCode? result = null;
            try
            {
                var key = (job.Dark, job.Language, job.Code);
                if (!Cache.TryGetValue(key, out result))
                {
                    result = Tokenize(job.Code, job.Language, job.Dark);
                    if (result is not null && job.Remember)
                    {
                        // Bounded so a long session cannot grow this without limit.
                        if (Cache.Count > 400) Cache.Clear();
                        Cache[key] = result;
                    }
                }
            }
            catch
            {
                result = null;
            }

            job.Result.TrySetResult(result);
        }
    }

    private static HighlightedCode? Tokenize(string code, string language, bool dark)
    {
        if (!Palettes.TryGetValue(dark, out var palette))
        {
            var options = new RegistryOptions(dark ? ThemeName.DarkPlus : ThemeName.LightPlus);
            var registry = new Registry(options);
            palette = new Palette(registry, registry.GetTheme());
            Palettes[dark] = palette;
        }

        if (!Grammars.TryGetValue((dark, language), out var grammar))
        {
            var options = new RegistryOptions(dark ? ThemeName.DarkPlus : ThemeName.LightPlus);
            var scope = options.GetScopeByLanguageId(language);
            grammar = string.IsNullOrEmpty(scope) ? null : palette.Registry.LoadGrammar(scope);
            Grammars[(dark, language)] = grammar;
        }

        if (grammar is null) return null;

        var lines = code.Replace("\r\n", "\n").Split('\n');
        var tokens = new CodeToken[lines.Length][];
        var states = new IStateStack?[lines.Length];

        // Resume from the remembered document sharing the most leading lines.
        if (!Memos.TryGetValue((dark, language), out var memos))
            Memos[(dark, language)] = memos = new List<Memo>();
        Memo? best = null;
        var reused = 0;
        foreach (var memo in memos)
        {
            var shared = 0;
            var limit = Math.Min(memo.Lines.Length, lines.Length);
            while (shared < limit && string.Equals(memo.Lines[shared], lines[shared], StringComparison.Ordinal)) shared++;
            if (shared > reused)
            {
                reused = shared;
                best = memo;
            }
        }

        // The last shared line may have been cut mid-token when that version was
        // taken (a line still being written); redo it rather than trust it.
        if (best is not null && reused == best.Lines.Length) reused--;
        reused = Math.Max(0, reused);
        if (best is not null)
        {
            Array.Copy(best.Tokens, tokens, reused);
            Array.Copy(best.States, states, reused);
        }

        IStateStack? state = reused > 0 ? states[reused - 1] : null;
        var spans = new List<CodeToken>();
        for (var i = reused; i < lines.Length; i++)
        {
            var line = lines[i];
            var tokenized = grammar.TokenizeLine(line, state, TimeSpan.FromMilliseconds(200));
            state = tokenized.RuleStack;
            states[i] = state;

            spans.Clear();
            foreach (var token in tokenized.Tokens)
            {
                var start = Math.Min(token.StartIndex, line.Length);
                var end = Math.Min(token.EndIndex, line.Length);
                if (end <= start) continue;
                var (brush, style, weight) = Style(palette.Theme, token.Scopes);
                spans.Add(new CodeToken(start, end - start, brush, style, weight));
            }

            tokens[i] = spans.ToArray();
        }

        var remembered = new Memo(lines, tokens, states);
        if (best is not null) memos.Remove(best);
        memos.Insert(0, remembered);
        if (memos.Count > MemosPerLanguage) memos.RemoveAt(memos.Count - 1);

        return new HighlightedCode(code, lines, tokens);
    }

    /// <summary>
    /// The flat run list a text block takes, with as few runs as the colours
    /// allow. Layout cost is per run (≈0.05 ms each, measured), so neighbours of
    /// the same style are merged, and whitespace — line breaks included, which
    /// show no colour — joins whatever run it follows.
    /// </summary>
    public static List<(string Text, IBrush? Brush, FontStyle Style, FontWeight Weight)> Runs(HighlightedCode code)
    {
        var runs = new List<(string, IBrush?, FontStyle, FontWeight)>();
        var text = new System.Text.StringBuilder();
        IBrush? brush = null;
        var style = FontStyle.Normal;
        var weight = FontWeight.Normal;
        var open = false;

        void Add(string segment, IBrush? segmentBrush, FontStyle segmentStyle, FontWeight segmentWeight)
        {
            if (segment.Length == 0) return;
            if (open && (string.IsNullOrWhiteSpace(segment)
                         || (ReferenceEquals(segmentBrush, brush) && segmentStyle == style && segmentWeight == weight)))
            {
                text.Append(segment);
                return;
            }

            if (open) runs.Add((text.ToString(), brush, style, weight));
            text.Clear().Append(segment);
            (brush, style, weight, open) = (segmentBrush, segmentStyle, segmentWeight, true);
        }

        for (var i = 0; i < code.Lines.Length; i++)
        {
            var line = code.Lines[i];
            var cursor = 0;
            foreach (var token in code.Tokens[i])
            {
                if (token.Start > cursor) Add(line[cursor..token.Start], null, FontStyle.Normal, FontWeight.Normal);
                Add(line.Substring(token.Start, token.Length), token.Brush, token.Style, token.Weight);
                cursor = token.Start + token.Length;
            }

            if (cursor < line.Length) Add(line[cursor..], null, FontStyle.Normal, FontWeight.Normal);
            if (i < code.Lines.Length - 1) Add("\n", null, FontStyle.Normal, FontWeight.Normal);
        }

        if (open) runs.Add((text.ToString(), brush, style, weight));
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

    // Immutable: built on the worker, drawn on the UI thread. A mutable
    // SolidColorBrush is an AvaloniaObject and would refuse the cross-thread use.
    private static IBrush Parse(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var brush)) return brush;
        try
        {
            brush = new ImmutableSolidColorBrush(Color.Parse(hex));
        }
        catch
        {
            brush = Brushes.Transparent;
        }

        BrushCache[hex] = brush;
        return brush;
    }

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
            "htm" => "html",
            "mmd" => "mermaid",
            "text" or "plain" or "plaintext" or "txt" or "output" => null,
            _ => lang
        };
    }
}
