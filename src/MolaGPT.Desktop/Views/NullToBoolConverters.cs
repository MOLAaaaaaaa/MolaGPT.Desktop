using System.Globalization;
using System.Windows.Data;

namespace MolaGPT.Desktop.Views;

/// <summary>Returns true when value is non-null. Used to gate XAML triggers off
/// nullable properties without inverting boolean logic in the template.</summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public static NullToBoolConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return false;
        if (value is byte[] bytes) return bytes.Length > 0;
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns true when value is a non-empty string.</summary>
public sealed class NullOrEmptyToBoolConverter : IValueConverter
{
    public static NullOrEmptyToBoolConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrEmpty(s);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
