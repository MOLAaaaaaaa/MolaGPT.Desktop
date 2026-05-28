using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MolaGPT.Desktop.Views;

/// <summary>Inverse of BoolToVisibility (true → Collapsed, false → Visible).</summary>
public sealed class BoolToVisibilityInverse : IValueConverter
{
    public static BoolToVisibilityInverse Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Collapsed;
}
