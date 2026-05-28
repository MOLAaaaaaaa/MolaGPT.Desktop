using System.Globalization;
using System.Windows.Data;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// Generic enum-equality converter — true when value.ToString() == parameter.
/// Used by SettingsWindow theme radio buttons.
/// </summary>
public sealed class EnumEqualityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is not null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}
