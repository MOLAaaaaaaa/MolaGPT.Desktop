using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MolaGPT.Presentation;

namespace MolaGPT.App.Rendering;

/// <summary>
/// A markdown list, drawn as markers plus content.
///
/// Before this, ListBlock only carried its raw source and the view printed it
/// verbatim — the user saw literal "- item" and "1. item" lines. The parser now
/// flattens the list into items with depth, so this is a straight loop: no
/// recursion, and a pathologically nested list cannot blow the layout stack.
/// </summary>
public sealed class MarkdownListView : TemplatedControl
{
    public static readonly StyledProperty<ListBlock?> BlockProperty =
        AvaloniaProperty.Register<MarkdownListView, ListBlock?>(nameof(Block));

    /// <summary>Inline-code and link colour, forwarded to each item's text.</summary>
    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<MarkdownListView, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<IBrush?> CodeBackgroundProperty =
        AvaloniaProperty.Register<MarkdownListView, IBrush?>(nameof(CodeBackground));

    public ListBlock? Block
    {
        get => GetValue(BlockProperty);
        set => SetValue(BlockProperty, value);
    }

    public IBrush? AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public IBrush? CodeBackground
    {
        get => GetValue(CodeBackgroundProperty);
        set => SetValue(CodeBackgroundProperty, value);
    }

    /// <summary>Prose line height, forwarded to the text this view builds.
    /// TemplatedControl has no LineHeight of its own, so it is declared here to
    /// keep the transcript styles able to set typography in one place.</summary>
    public static readonly StyledProperty<double> LineHeightProperty =
        AvaloniaProperty.Register<MarkdownListView, double>(nameof(LineHeight), double.NaN);

    public double LineHeight
    {
        get => GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    private const double IndentPerLevel = 22;
    private const double MarkerColumn = 22;

    private readonly StackPanel _host = new();

    /// <summary>The items the rows in <see cref="_host"/> were built from, index
    /// for index. Compared against the next block to find what actually changed.</summary>
    private IReadOnlyList<ListItem> _rows = Array.Empty<ListItem>();

    static MarkdownListView()
    {
        BlockProperty.Changed.AddClassHandler<MarkdownListView>((x, _) => x.Rebuild());
        AccentBrushProperty.Changed.AddClassHandler<MarkdownListView>((x, _) => x.RebuildAll());
        CodeBackgroundProperty.Changed.AddClassHandler<MarkdownListView>((x, _) => x.RebuildAll());
        FontSizeProperty.Changed.AddClassHandler<MarkdownListView>((x, _) => x.RebuildAll());
        LineHeightProperty.Changed.AddClassHandler<MarkdownListView>((x, _) => x.RebuildAll());
    }

    public MarkdownListView()
    {
        Template = new FuncControlTemplate<MarkdownListView>((view, _) => view._host);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        Rebuild();
    }

    private void Rebuild()
    {
        var items = Block?.Items ?? (IReadOnlyList<ListItem>)Array.Empty<ListItem>();

        // A streamed list grows at its end: the last entry gains characters and
        // new entries appear after it. Clearing every row for that re-created
        // the whole visual tree on each delta, which for a reasoning block
        // running to tens of thousands of characters was most of a frame.
        // ListItem is a record struct, so the shared prefix is a value compare.
        var shared = 0;
        var max = Math.Min(Math.Min(_rows.Count, items.Count), _host.Children.Count);
        while (shared < max && _rows[shared] == items[shared]) shared++;

        while (_host.Children.Count > shared)
            _host.Children.RemoveAt(_host.Children.Count - 1);

        for (var i = shared; i < items.Count; i++)
            _host.Children.Add(BuildRow(items[i]));

        _rows = items;
    }

    /// <summary>
    /// Typography and brushes are baked into every row when it is built, so a
    /// change to them cannot reuse anything.
    /// </summary>
    private void RebuildAll()
    {
        _rows = Array.Empty<ListItem>();
        _host.Children.Clear();
        Rebuild();
    }

    private Control BuildRow(ListItem item)
    {
        var row = new Grid
        {
            Margin = new Thickness(item.Depth * IndentPerLevel, 0, 0, 3),
            ColumnDefinitions = new ColumnDefinitions($"{MarkerColumn},*")
        };

        // A continuation keeps the entry's indent but takes no marker: it is the
        // same bullet's next paragraph, not a new entry.
        if (!item.IsContinuation)
        {
            var marker = new TextBlock
            {
                Text = Marker(item),
                FontSize = FontSize,
                LineHeight = LineHeight,
                Foreground = Foreground,
                Opacity = item.Ordered ? 0.85 : 0.65,
                TextAlignment = item.Ordered ? TextAlignment.Right : TextAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(marker, 0);
            row.Children.Add(marker);
        }

        var content = BuildItemContent(item.Markdown);
        Grid.SetColumn(content, 1);
        row.Children.Add(content);

        return row;
    }

    private Control BuildItemContent(string markdown)
    {
        MarkdownTextBlock Prose(string text) => new()
        {
            Markdown = text,
            FontSize = FontSize,
            LineHeight = LineHeight,
            Foreground = Foreground,
            AccentBrush = AccentBrush,
            CodeBackground = CodeBackground,
            TextWrapping = TextWrapping.Wrap
        };

        var spans = LatexDisplayParser.Find(markdown);
        if (spans.Count == 0) return Prose(markdown);

        var host = new StackPanel { Spacing = 3 };
        var cursor = 0;

        foreach (var span in spans)
        {
            var before = markdown[cursor..span.Start].Trim();
            if (before.Length > 0) host.Children.Add(Prose(before));

            var formula = markdown.Substring(span.FormulaStart, span.FormulaLength).Trim();
            if (formula.Length > 0)
            {
                host.Children.Add(new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = new MathView
                    {
                        Latex = formula,
                        FormulaSize = FontSize,
                        Foreground = Foreground,
                        Margin = new Thickness(1, 2, 1, 3)
                    }
                });
            }

            cursor = span.Start + span.Length;
        }

        var after = markdown[cursor..].Trim();
        if (after.Length > 0) host.Children.Add(Prose(after));
        return host;
    }

    /// <summary>
    /// Bullet shape cycles with depth the way every markdown renderer does it,
    /// so nesting is legible without indentation alone carrying the meaning.
    /// </summary>
    private static string Marker(ListItem item) =>
        item.Ordered
            ? item.Number + "."
            : (item.Depth % 3) switch { 0 => "•", 1 => "◦", _ => "▪" };
}
