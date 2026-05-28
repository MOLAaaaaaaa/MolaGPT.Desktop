using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// String/object null-or-empty → Collapsed; otherwise Visible.
/// Used to hide the "清除角色" button in the PersonaPicker popup when no
/// persona is currently bound. (NullToVisibilityConverter exists but has no
/// Instance shortcut for x:Static usage.)
/// </summary>
public sealed class NullToInverseVisibility : IValueConverter
{
    public static NullToInverseVisibility Instance { get; } = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return Visibility.Collapsed;
        if (value is string s) return string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
