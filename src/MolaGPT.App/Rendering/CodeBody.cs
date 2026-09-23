using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace MolaGPT.App.Rendering;

/// <summary>
/// A code fence's body in the transcript: a <see cref="CodeView"/> while the
/// fence is being written or when it is long, a <see cref="CodeTextBlock"/>
/// otherwise.
///
/// A text block lays out every line whether or not it is on screen, and a
/// coloured line costs about 0.5–0.9 ms of layout, so a 300-line fence was a
/// 200 ms stall every time its row scrolled into view, and a fence being
/// streamed paid it on every delta. The code view lays out only the lines
/// inside the transcript's viewport, and a delta costs its new lines. What it
/// gives up is taking part in selection that spans several blocks, which works
/// on text blocks only; it still selects and copies within itself, and the
/// fence has its own copy button. So a short fence, once finished, goes back to
/// a text block — and only when its colours are ready, so it does not flash
/// plain in between.
/// </summary>
public sealed class CodeBody : Decorator
{
    /// <summary>Past this many lines a finished fence stays a <see cref="CodeView"/>.</summary>
    public const int ViewThreshold = 60;

    private static readonly Thickness Inset = new(12, 6, 12, 10);

    public static readonly StyledProperty<string?> CodeProperty =
        AvaloniaProperty.Register<CodeBody, string?>(nameof(Code));

    public static readonly StyledProperty<string?> LanguageProperty =
        AvaloniaProperty.Register<CodeBody, string?>(nameof(Language));

    /// <summary>The fence is still being written: its closing marker has not arrived.</summary>
    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<CodeBody, bool>(nameof(IsLive));

    private int _swap;

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

    public bool IsLive
    {
        get => GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CodeProperty || change.Property == LanguageProperty || change.Property == IsLiveProperty)
            Update();
    }

    private void Update()
    {
        var code = Code ?? string.Empty;
        _swap++;

        if (IsLive || code.Length > CodeHighlighter.MaxTranscriptLength || CountLines(code) > ViewThreshold)
        {
            if (Child is not CodeView view) Child = view = new CodeView { Padding = Inset };
            view.SetCode(code, Language);
            return;
        }

        if (Child is CodeView)
        {
            SwapWhenColoured(code, Language);
            return;
        }

        ShowTextBlock(code, Language);
    }

    /// <summary>The fence just finished and is short: become a text block once
    /// its colours are cached, so the swap lands coloured.</summary>
    private async void SwapWhenColoured(string code, string? language)
    {
        var swap = _swap;
        if (CodeHighlighter.Supports(language))
            await CodeHighlighter.HighlightAsync(code, language, ActualThemeVariant == ThemeVariant.Dark);
        if (swap == _swap) ShowTextBlock(code, language);
    }

    private void ShowTextBlock(string code, string? language)
    {
        if (Child is not CodeTextBlock block) Child = block = new CodeTextBlock { Classes = { "code" }, Margin = Inset };
        block.Language = language;
        block.Code = code;
    }

    private static int CountLines(string code)
    {
        var lines = 1;
        foreach (var ch in code)
        {
            if (ch == '\n' && ++lines > ViewThreshold) break;
        }

        return lines;
    }
}
