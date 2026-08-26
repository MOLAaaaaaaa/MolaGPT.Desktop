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
/// </summary>
public sealed class CodeTextBlock : SelectableTextBlock
{
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
        // The theme decides which TextMate palette applies, so a variant switch
        // has to re-tokenize rather than just re-colour.
        Rebuild();
    }


    private void Rebuild()
    {
        var code = Code;
        if (string.IsNullOrEmpty(code))
        {
            Inlines?.Clear();
            Text = string.Empty;
            return;
        }

        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var runs = CodeHighlighter.Highlight(code, Language, dark);

        if (runs is null)
        {
            Inlines?.Clear();
            Text = code;
            return;
        }

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

        foreach (var (text, brush, style, weight) in runs)
        {
            var run = new Run(text) { FontStyle = style, FontWeight = weight };
            if (brush is not null) run.Foreground = brush;
            target.Add(run);
        }
    }
}
