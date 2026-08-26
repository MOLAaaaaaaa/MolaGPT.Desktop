using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MolaGPT.Presentation;
using System.Text.RegularExpressions;

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
    private static readonly Regex DisplayMath = new(
        @"\\\[(?<formula>.*?)\\\]",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly StackPanel _host = new();

    static MarkdownListView()
    {
        BlockProperty.Changed.AddClassHandler<MarkdownListView>((x, _) => x.Rebuild());
        AccentBrushProperty.Changed.AddClassHandler<MarkdownListView>((x, _) => x.Rebuild());
        CodeBackgroundProperty.Changed.AddClassHandler<MarkdownListView>((x, _) => x.Rebuild());
        FontSizeProperty.Changed.AddClassHandler<MarkdownListView>((x, _) => x.Rebuild());
        LineHeightProperty.Changed.AddClassHandler<MarkdownListView>((x, _) => x.Rebuild());
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
        _host.Children.Clear();

        var block = Block;
        if (block is null || block.Items.Count == 0) return;

        foreach (var item in block.Items)
        {
            var row = new Grid
            {
                Margin = new Thickness(item.Depth * IndentPerLevel, 0, 0, 3),
                ColumnDefinitions = new ColumnDefinitions($"{MarkerColumn},*")
            };

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

            var content = BuildItemContent(item.Markdown);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);

            _host.Children.Add(row);
        }
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

        var matches = DisplayMath.Matches(markdown);
        if (matches.Count == 0) return Prose(markdown);

        var host = new StackPanel { Spacing = 3 };
        var cursor = 0;

        foreach (Match match in matches)
        {
            var before = markdown[cursor..match.Index].Trim();
            if (before.Length > 0) host.Children.Add(Prose(before));

            var formula = match.Groups["formula"].Value.Trim();
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

            cursor = match.Index + match.Length;
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
