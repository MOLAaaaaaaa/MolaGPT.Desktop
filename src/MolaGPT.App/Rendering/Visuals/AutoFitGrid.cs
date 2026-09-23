using Avalonia;
using Avalonia.Controls;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Equal-width columns, as many as fit — CSS's
/// <c>repeat(auto-fit, minmax(MinColumnWidth, 1fr))</c>, with the rows kept
/// balanced. Cells in a row share
/// the row's height, so cards side by side line up top and bottom whatever
/// their text length.
///
/// UniformGrid needs a column count up front and WrapPanel leaves a ragged
/// right edge; the answer column's width changes with the window and the canvas
/// drawer, so the count has to come from the width.
/// </summary>
internal sealed class AutoFitGrid : Panel
{
    public double MinColumnWidth { get; init; } = 160;
    public int MaxColumns { get; init; } = 4;
    public double Spacing { get; init; } = 10;

    private int _columns = 1;
    private double[] _rowHeights = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        if (visible.Count == 0) return default;

        var width = double.IsInfinity(availableSize.Width)
            ? MaxColumns * MinColumnWidth + (MaxColumns - 1) * Spacing
            : availableSize.Width;
        var fit = Math.Clamp((int)((width + Spacing) / (MinColumnWidth + Spacing)), 1, MaxColumns);
        // Fewest rows that fit, then the fewest columns that still give those
        // rows: six cards where four fit are two rows of three, not four and a
        // stranded two.
        var rowsNeeded = (visible.Count + fit - 1) / fit;
        _columns = (visible.Count + rowsNeeded - 1) / rowsNeeded;
        var cellWidth = Math.Max(0, (width - (_columns - 1) * Spacing) / _columns);

        var rows = (visible.Count + _columns - 1) / _columns;
        _rowHeights = new double[rows];
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Measure(new Size(cellWidth, double.PositiveInfinity));
            _rowHeights[i / _columns] = Math.Max(_rowHeights[i / _columns], visible[i].DesiredSize.Height);
        }

        return new Size(width, _rowHeights.Sum() + (rows - 1) * Spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        var cellWidth = Math.Max(0, (finalSize.Width - (_columns - 1) * Spacing) / _columns);
        var y = 0d;
        for (var row = 0; row < _rowHeights.Length; row++)
        {
            for (var column = 0; column < _columns; column++)
            {
                var index = row * _columns + column;
                if (index >= visible.Count) break;
                visible[index].Arrange(new Rect(column * (cellWidth + Spacing), y, cellWidth, _rowHeights[row]));
            }

            y += _rowHeights[row] + Spacing;
        }

        return finalSize;
    }
}
