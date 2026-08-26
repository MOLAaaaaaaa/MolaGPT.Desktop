using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Small converters the transcript templates need for heading typography and
/// speaker alignment without duplicating near-identical data templates.
/// </summary>
public static class LevelConverters
{
    public static readonly IValueConverter HeadingFontSize =
        new FuncValueConverter<int, double>(level => level switch
        {
            1 => 24,
            2 => 18,
            _ => 15
        });

    public static readonly IValueConverter HeadingFontWeight =
        new FuncValueConverter<int, FontWeight>(level =>
            level == 1 ? FontWeight.Bold : FontWeight.SemiBold);

    /// <summary>Level 3 and deeper share one rhythm; deeper headings are rare in
    /// chat answers and smaller steps become indistinguishable from prose.</summary>
    public static readonly IValueConverter HeadingMargin =
        new FuncValueConverter<int, Thickness>(level => level switch
        {
            1 => new Thickness(0, 16, 0, 8),
            2 => new Thickness(0, 14, 0, 7),
            _ => new Thickness(0, 12, 0, 6)
        });

    /// <summary>Right-aligns a row for the user, left for the assistant.</summary>
    public static readonly IValueConverter EndIfTrue =
        new FuncValueConverter<bool, HorizontalAlignment>(
            isUser => isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left);

    /// <summary>First character of a label, for the avatar disc.</summary>
    public static readonly IValueConverter Initial =
        new FuncValueConverter<string?, string>(label =>
            string.IsNullOrWhiteSpace(label) ? "M" : label!.Trim()[..1].ToUpperInvariant());
}
