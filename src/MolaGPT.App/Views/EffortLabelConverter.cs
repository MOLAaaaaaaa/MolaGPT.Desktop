using System.Globalization;
using Avalonia.Data.Converters;

namespace MolaGPT.App.Views;

public sealed class EffortLabelConverter : IValueConverter
{
    public static EffortLabelConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() switch
        {
            "none" => "无",
            "minimal" => "极低",
            "low" => "低",
            "medium" => "中",
            "high" => "高",
            "xhigh" => "极高",
            "max" => "最大",
            "ultra" => "Ultra",
            { Length: > 0 } other => other,
            _ => string.Empty
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
