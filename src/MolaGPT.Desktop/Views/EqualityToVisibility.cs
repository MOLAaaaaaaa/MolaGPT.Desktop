using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// MultiBinding: returns <see cref="Visibility.Visible"/> when both bound
/// values are non-null and string-equal (case-sensitive), otherwise
/// <see cref="Visibility.Collapsed"/>.
///
/// Used in the PersonaPicker popup to show a ✓ next to the currently
/// active persona row: bind [Id, Chat.ActivePersonaId] and compare.
/// </summary>
public sealed class EqualityToVisibility : IMultiValueConverter
{
    public static EqualityToVisibility Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return Visibility.Collapsed;
        var a = values[0]?.ToString();
        var b = values[1]?.ToString();
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return Visibility.Collapsed;
        return string.Equals(a, b, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
