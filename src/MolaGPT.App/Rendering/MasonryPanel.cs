using Avalonia;
using Avalonia.Controls;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Columns of equal width; each child goes to whichever column is currently
/// shortest. Cards keep their natural height, so pictures of different aspect
/// ratios pack without the ragged bottom edge a WrapPanel leaves — the layout a
/// picture feed wants and the reason this exists at all, since Avalonia ships no
/// masonry panel.
///
/// Deliberately not virtualizing. The gallery is capped at 200 entries by
/// <c>ImageGenerationWorkbenchView.GalleryLimit</c>, and a virtualizing masonry
/// panel has to guess the height of items it has never measured — guess wrong
/// and the columns re-balance under the scroll thumb while you are reading.
/// </summary>
public sealed class MasonryPanel : Panel
{
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(MinColumnWidth), 240d);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(ColumnSpacing), 12d);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(RowSpacing), 12d);

    /// <summary>What the arrange pass measured against, so it can tell whether
    /// it is being handed a width the measure pass never saw.</summary>
    private double _measuredColumnWidth = double.NaN;

    static MasonryPanel()
    {
        AffectsMeasure<MasonryPanel>(MinColumnWidthProperty, ColumnSpacingProperty, RowSpacingProperty);
    }

    /// <summary>Column count follows from this: as many columns as fit.</summary>
    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, columnWidth) = Resolve(availableSize.Width);
        _measuredColumnWidth = columnWidth;

        var bottoms = NewColumns(columns);
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            var column = Shortest(bottoms);
            bottoms[column] += RowSpacing + child.DesiredSize.Height;
        }

        var width = double.IsInfinity(availableSize.Width)
            ? columns * columnWidth + (columns - 1) * ColumnSpacing
            : availableSize.Width;
        return new Size(width, TallestColumn(bottoms));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, columnWidth) = Resolve(finalSize.Width);

        // A child measured for a 300px column and arranged into a 260px one
        // would wrap its caption against the wrong width and overflow the card.
        if (double.IsNaN(_measuredColumnWidth) || Math.Abs(columnWidth - _measuredColumnWidth) > 0.5)
        {
            foreach (var child in Children)
            {
                if (child.IsVisible) child.Measure(new Size(columnWidth, double.PositiveInfinity));
            }
            _measuredColumnWidth = columnWidth;
        }

        var bottoms = NewColumns(columns);
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;

            var column = Shortest(bottoms);
            var top = bottoms[column] + RowSpacing;
            var height = child.DesiredSize.Height;
            child.Arrange(new Rect(column * (columnWidth + ColumnSpacing), top, columnWidth, height));
            bottoms[column] = top + height;
        }

        return new Size(finalSize.Width, Math.Max(finalSize.Height, TallestColumn(bottoms)));
    }

    private (int Columns, double Width) Resolve(double available)
    {
        var minimum = Math.Max(1, MinColumnWidth);
        if (double.IsInfinity(available) || available <= 0) return (1, minimum);

        var gap = Math.Max(0, ColumnSpacing);
        var columns = Math.Max(1, (int)Math.Floor((available + gap) / (minimum + gap)));
        return (columns, (available - gap * (columns - 1)) / columns);
    }

    /// <summary>
    /// Starts every column one gap above zero so the first card in a column
    /// lands at y = 0 without a "is this the first one" branch — which would be
    /// wrong anyway for a zero-height child, and a pending card is exactly that
    /// for one layout pass.
    /// </summary>
    private double[] NewColumns(int columns)
    {
        var bottoms = new double[columns];
        Array.Fill(bottoms, -RowSpacing);
        return bottoms;
    }

    /// <summary>Not named Height: that is Layoutable's own property.</summary>
    private double TallestColumn(double[] bottoms)
    {
        var tallest = -RowSpacing;
        foreach (var bottom in bottoms) tallest = Math.Max(tallest, bottom);
        return Math.Max(0, tallest);
    }

    private static int Shortest(double[] bottoms)
    {
        var index = 0;
        for (var i = 1; i < bottoms.Length; i++)
        {
            if (bottoms[i] < bottoms[index]) index = i;
        }
        return index;
    }
}
