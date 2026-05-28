using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// XAML-friendly converter shortcuts. Exposed as static fields so they can be
/// used inline as <c>Converter="{x:Static views:Converters.BoolToVis}"</c> without
/// declaring a resource block.
/// </summary>
public static class Converters
{
    public static readonly IValueConverter BoolToVis = new BooleanToVisibilityConverter();
    public static readonly IValueConverter BoolInvertVis = new BooleanInvertedToVisibilityConverter();
}

internal sealed class BooleanInvertedToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Collapsed;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ProviderTypeToLabelConverter : IValueConverter
{
    public static readonly ProviderTypeToLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() switch
        {
            "openai" or "openai-compat" => "OpenAI 格式",
            "anthropic" => "Claude 格式",
            "gemini" => "Gemini 格式",
            "openai-response" => "OpenAI Response",
            _ => value?.ToString() ?? ""
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BadgeBackgroundConverter : IValueConverter
{
    public static readonly BadgeBackgroundConverter Instance = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        return Application.Current.TryFindResource("Brush.Bg.Tertiary") ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BadgeForegroundConverter : IValueConverter
{
    public static readonly BadgeForegroundConverter Instance = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value?.ToString() ?? "Muted";
        var resourceKey = key switch
        {
            "Primary" => "Brush.Primary",
            "Info" => "Brush.Info",
            "Success" => "Brush.Success",
            "Warning" => "Brush.Warning",
            _ => "Brush.Text.Muted"
        };
        return Application.Current.TryFindResource(resourceKey) ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
