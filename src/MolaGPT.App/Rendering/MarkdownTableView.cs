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
    private readonly List<Border[]> _cells = new();
    private int _columns;
    private bool _hasHeader;
    private IReadOnlyList<int> _alignments = [];

    static MarkdownTableView()
    {
        BlockProperty.Changed.AddClassHandler<MarkdownTableView>((x, _) => x.Invalidate(full: false));
        AccentBrushProperty.Changed.AddClassHandler<MarkdownTableView>((x, _) => x.Invalidate(full: true));
        CodeBackgroundProperty.Changed.AddClassHandler<MarkdownTableView>((x, _) => x.Invalidate(full: true));
        LineBrushProperty.Changed.AddClassHandler<MarkdownTableView>((x, _) => x.Invalidate(full: true));
        HeaderBackgroundProperty.Changed.AddClassHandler<MarkdownTableView>((x, _) => x.Invalidate(full: true));
        FontSizeProperty.Changed.AddClassHandler<MarkdownTableView>((x, _) => x.Invalidate(full: true));
        LineHeightProperty.Changed.AddClassHandler<MarkdownTableView>((x, _) => x.Invalidate(full: true));
    }

    public MarkdownTableView()
    {
        Template = new FuncControlTemplate<MarkdownTableView>((view, _) => view._host);
    }

    // Built when measured, not when each property lands. A fresh table is
    // styled one property at a time — font size, line height, four brushes —
    // and rebuilding on each one built every cell seven times over.
    private bool _rebuild = true;
    private bool _update;

    private void Invalidate(bool full)
    {
        if (full) _rebuild = true;
        else _update = true;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_rebuild)
        {
            _rebuild = _update = false;
            Rebuild();
        }
        else if (_update)
        {
            _update = false;
            Update();
        }

        return base.MeasureOverride(availableSize);
    }

    /// <summary>
    /// A new block for the same table — the next delta of a table still being
    /// written — only touches what changed. Rebuilding every cell on each delta
    /// re-parsed and re-laid-out the whole table, measured at 30–90 ms a delta
    /// for a 15-row table; now the rows already there keep their controls and
    /// only a cell whose text differs is re-parsed.
    /// </summary>
    private void Update()
    {
        var block = Block;
        if (block is null || block.Rows.Count == 0 || _cells.Count == 0
            || Columns(block) != _columns || block.HasHeader != _hasHeader
            || !block.Alignments.SequenceEqual(_alignments))
        {
            Rebuild();
            return;
        }

        for (var r = 0; r < block.Rows.Count; r++)
        {
            if (r >= _cells.Count)
            {
                AddRow(block, r);
                continue;
            }

            var row = block.Rows[r];
            for (var c = 0; c < _columns; c++)
            {
                var text = c < row.Count ? row[c] : string.Empty;
                if (_cells[r][c].Child is MarkdownTextBlock content && !string.Equals(content.Markdown, text, StringComparison.Ordinal))
                    content.Markdown = text;
            }
        }

        while (_cells.Count > block.Rows.Count)
        {
            foreach (var cell in _cells[^1]) _host.Children.Remove(cell);
            _cells.RemoveAt(_cells.Count - 1);
            _host.RowDefinitions.RemoveAt(_host.RowDefinitions.Count - 1);
        }

        StreamTailFade.KeepTailOnLast(_host);
    }

    private void Rebuild()
    {
        _host.Children.Clear();
        _host.RowDefinitions.Clear();
        _host.ColumnDefinitions.Clear();
        _cells.Clear();
        _columns = 0;

        var block = Block;
        if (block is null || block.Rows.Count == 0) return;

        var columns = Columns(block);
        if (columns == 0) return;
        _columns = columns;
        _hasHeader = block.HasHeader;
        _alignments = block.Alignments.ToArray();

        for (var c = 0; c < columns; c++)
            _host.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        // Let the last column absorb slack so the table fills its measure
        // instead of hugging the text and leaving a ragged right edge.
        _host.ColumnDefinitions[columns - 1].Width = new GridLength(1, GridUnitType.Star);

        for (var r = 0; r < block.Rows.Count; r++) AddRow(block, r);

        // One text control per cell, so without this every cell in the table
        // dims its own last words while the answer is still being written.
        StreamTailFade.KeepTailOnLast(_host);
    }

    private void AddRow(TableBlock block, int r)
    {
        _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var isHeader = block.HasHeader && r == 0;
        var row = block.Rows[r];
        var cells = new Border[_columns];

        for (var c = 0; c < _columns; c++)
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
            cells[c] = cell;
        }

        _cells.Add(cells);
    }

    private static int Columns(TableBlock block) => Math.Max(block.Alignments.Count, block.Rows.Max(r => r.Count));

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
