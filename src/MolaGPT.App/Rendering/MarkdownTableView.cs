using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MolaGPT.Presentation;

namespace MolaGPT.App.Rendering;

/// <summary>
/// A markdown table, drawn as a real grid.
///
/// Previously TableBlock only carried its source and the view showed the raw
/// pipe syntax in a monospace box, which is not a table. The parser now reports
/// cells, header flag and per-column alignment, so this lays out a grid with
/// hairline separators.
///
/// Ragged rows are padded here rather than in the parser: the parser reports
/// what was written, and a model that emits a short row should still get a
/// table rather than an exception.
/// </summary>
public sealed class MarkdownTableView : TemplatedControl
{
    public static readonly StyledProperty<TableBlock?> BlockProperty =
        AvaloniaProperty.Register<MarkdownTableView, TableBlock?>(nameof(Block));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<MarkdownTableView, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<IBrush?> CodeBackgroundProperty =
        AvaloniaProperty.Register<MarkdownTableView, IBrush?>(nameof(CodeBackground));

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<MarkdownTableView, IBrush?>(nameof(LineBrush));

    public static readonly StyledProperty<IBrush?> HeaderBackgroundProperty =
        AvaloniaProperty.Register<MarkdownTableView, IBrush?>(nameof(HeaderBackground));

    public TableBlock? Block
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

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? HeaderBackground
    {
        get => GetValue(HeaderBackgroundProperty);
        set => SetValue(HeaderBackgroundProperty, value);
    }

    /// <summary>Prose line height, forwarded to the text this view builds.
    /// TemplatedControl has no LineHeight of its own, so it is declared here to
    /// keep the transcript styles able to set typography in one place.</summary>
    public static readonly StyledProperty<double> LineHeightProperty =
        AvaloniaProperty.Register<MarkdownTableView, double>(nameof(LineHeight), double.NaN);

    public double LineHeight
    {
        get => GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    private readonly Grid _host = new();

    static MarkdownTableView()
    {
        BlockProperty.Changed.AddClassHandler<MarkdownTableView>((x, _) => x.Rebuild());
        AccentBrushProperty.Changed.AddClassHandler<MarkdownTableView>((x, _) => x.Rebuild());
        CodeBackgroundProperty.Changed.AddClassHandler<MarkdownTableView>((x, _) => x.Rebuild());
        LineBrushProperty.Changed.AddClassHandler<MarkdownTableView>((x, _) => x.Rebuild());
        HeaderBackgroundProperty.Changed.AddClassHandler<MarkdownTableView>((x, _) => x.Rebuild());
        FontSizeProperty.Changed.AddClassHandler<MarkdownTableView>((x, _) => x.Rebuild());
        LineHeightProperty.Changed.AddClassHandler<MarkdownTableView>((x, _) => x.Rebuild());
    }

    public MarkdownTableView()
    {
        Template = new FuncControlTemplate<MarkdownTableView>((view, _) => view._host);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        Rebuild();
    }

    private void Rebuild()
    {
        _host.Children.Clear();
        _host.RowDefinitions.Clear();
        _host.ColumnDefinitions.Clear();

        var block = Block;
        if (block is null || block.Rows.Count == 0) return;

        var columns = Math.Max(
            block.Alignments.Count,
            block.Rows.Max(r => r.Count));
        if (columns == 0) return;

        for (var c = 0; c < columns; c++)
            _host.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < block.Rows.Count; r++)
            _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // Let the last column absorb slack so the table fills its measure
        // instead of hugging the text and leaving a ragged right edge.
        _host.ColumnDefinitions[columns - 1].Width = new GridLength(1, GridUnitType.Star);

        for (var r = 0; r < block.Rows.Count; r++)
        {
            var isHeader = block.HasHeader && r == 0;
            var row = block.Rows[r];

            for (var c = 0; c < columns; c++)
            {
                var text = c < row.Count ? row[c] : string.Empty;

                var content = new MarkdownTextBlock
                {
                    Markdown = text,
                    FontSize = FontSize,
                    LineHeight = LineHeight,
                    Foreground = Foreground,
                    AccentBrush = AccentBrush,
                    CodeBackground = CodeBackground,
                    FontWeight = isHeader ? FontWeight.SemiBold : FontWeight.Normal,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = Align(block, c)
                };

                var cell = new Border
                {
                    Padding = new Thickness(11, 7),
                    Background = isHeader ? HeaderBackground : null,
                    BorderBrush = LineBrush,
                    // Interior hairlines only: the outer frame is drawn by the
                    // panel this sits in, so doubling it would read as 2px.
                    BorderThickness = new Thickness(
                        left: c == 0 ? 0 : 1,
                        top: r == 0 ? 0 : 1,
                        right: 0,
                        bottom: 0),
                    Child = content
                };

                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                _host.Children.Add(cell);
            }
        }
    }

    private static TextAlignment Align(TableBlock block, int column) =>
        column < block.Alignments.Count
            ? block.Alignments[column] switch
            {
                0 => TextAlignment.Center,
                1 => TextAlignment.Right,
                _ => TextAlignment.Left
            }
            : TextAlignment.Left;
}
