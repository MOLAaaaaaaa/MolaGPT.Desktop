using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MolaGPT.App.Rendering;

public sealed class SearchHighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string> SourceTextProperty =
        AvaloniaProperty.Register<SearchHighlightTextBlock, string>(nameof(SourceText), string.Empty);
    public static readonly StyledProperty<string> QueryProperty =
        AvaloniaProperty.Register<SearchHighlightTextBlock, string>(nameof(Query), string.Empty);

    public string SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public string Query
    {
        get => GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SourceTextProperty && change.Property != QueryProperty) return;

        var text = SourceText;
        var needle = Query;
        if (needle.Length == 0 || !text.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            Inlines = null;
            Text = text;
            return;
        }

        var inlines = new InlineCollection();
        var cursor = 0;
        while (cursor < text.Length)
        {
            var at = text.IndexOf(needle, cursor, StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;
            if (at > cursor) inlines.Add(new Run(text[cursor..at]));
            var match = new Run(text.Substring(at, needle.Length)) { FontWeight = FontWeight.SemiBold };
            match.Bind(TextElement.BackgroundProperty, this.GetResourceObservable("Brush.Primary.Border"));
            inlines.Add(match);
            cursor = at + needle.Length;
        }
        if (cursor < text.Length) inlines.Add(new Run(text[cursor..]));
        Text = null;
        Inlines = inlines;
    }
}
