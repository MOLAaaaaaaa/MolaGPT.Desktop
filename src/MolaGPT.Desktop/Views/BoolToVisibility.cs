using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MolaGPT.Desktop.Views;

/// <summary>Tiny converter for HasThinking and similar bool→Vis flags. Singleton instance for x:Static usage.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public static BoolToVisibility Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Visible;
}
