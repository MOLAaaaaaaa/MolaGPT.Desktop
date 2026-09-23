using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;

namespace MolaGPT.App.Rendering;

/// <summary>
/// A fenced code block's body: selectable monospace text, syntax-highlighted
/// when <see cref="CodeHighlighter"/> recognises the language.
///
/// Falls back to plain text whenever highlighting is unavailable — unknown
/// language, oversized fence, tokenizer failure. That is a deliberate ordering:
/// the code must always be readable and copyable; colour is an enhancement.
///
/// Colour arrives asynchronously: a block shows its text at once and is
/// coloured when the highlighter thread is done, or immediately when the result
/// is already cached (a row scrolling back into view). A block whose code keeps
/// growing — a fence being streamed — stays plain until it settles: every run
/// costs layout (≈0.9 ms per coloured line, measured), and re-colouring a
/// 200-line fence on every delta was 150 ms of UI thread each time.
/// </summary>
public sealed class CodeTextBlock : SelectableTextBlock
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

    private (string? Code, string? Language, bool Dark)? _rendered;
    private string? _lastCode;
    private long _lastChange;
    private int _request;

    public static readonly StyledProperty<string?> CodeProperty =
        AvaloniaProperty.Register<CodeTextBlock, string?>(nameof(Code));

    public static readonly StyledProperty<string?> LanguageProperty =
        AvaloniaProperty.Register<CodeTextBlock, string?>(nameof(Language));

    public string? Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public string? Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public CodeTextBlock()
    {
        // The theme decides which TextMate palette applies, so a variant switch
        // has to re-tokenize rather than just re-colour.
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    static CodeTextBlock()
    {
        CodeProperty.Changed.AddClassHandler<CodeTextBlock>((x, _) => x.Rebuild());
        LanguageProperty.Changed.AddClassHandler<CodeTextBlock>((x, _) => x.Rebuild());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _request++;
    }

    private void Rebuild()
    {
        var code = Code;
        var language = Language;
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var state = (code, language, dark);
        if (_rendered == state) return;
        _rendered = state;
        _request++;

        var now = Environment.TickCount64;
        var growing = code is not null && _lastCode is not null && code.Length > _lastCode.Length
                      && code.StartsWith(_lastCode, StringComparison.Ordinal)
                      && now - _lastChange < SettleDelay.TotalMilliseconds;
        _lastCode = code;
        _lastChange = now;

        if (string.IsNullOrEmpty(code))
        {
            ShowPlain(string.Empty);
            return;
        }

        if (CodeHighlighter.TryGetCached(code, language, dark) is { } cached)
        {
            Apply(cached);
            return;
        }

        ShowPlain(code);
        if (!CodeHighlighter.Supports(language) || code.Length > CodeHighlighter.MaxTranscriptLength) return;

        if (growing) SettleThenHighlight();
        else RequestHighlight();
    }

    /// <summary>Colours the block once its code has stopped growing for a
    /// moment. Any change in the meantime supersedes the wait.</summary>
    private async void SettleThenHighlight()
    {
        var request = _request;
        await Task.Delay(SettleDelay);
        if (request == _request) RequestHighlight();
    }

    private async void RequestHighlight()
    {
        if (_rendered is not { } state || string.IsNullOrEmpty(state.Code)) return;
        var request = ++_request;
        // Resumes on the UI thread: this is only ever started from it.
        var result = await CodeHighlighter.HighlightAsync(state.Code, state.Language, state.Dark);
        // Superseded by newer code, a theme switch, or detaching.
        if (request != _request || result is null || _rendered != state) return;
        Apply(result);
    }

    private void ShowPlain(string code)
    {
        if (Inlines is { Count: > 0 }) Inlines.Clear();
        Text = code;
    }

    private void Apply(HighlightedCode code)
    {
        // Text and Inlines are alternative content sources; leaving Text set
        // would draw the plain copy underneath the highlighted one.
        Text = null;

        InlineCollection target;
        if (Inlines is null)
        {
            target = new InlineCollection();
            Inlines = target;
        }
        else
        {
            target = Inlines;
            target.Clear();
        }

        foreach (var (text, brush, style, weight) in CodeHighlighter.Runs(code))
        {
            var run = new Run(text) { FontStyle = style, FontWeight = weight };
            if (brush is not null) run.Foreground = brush;
            target.Add(run);
        }
    }
}
