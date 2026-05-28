using System.Globalization;
using System.Windows.Data;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// "high" → "高" / "max" → "最大" / etc. Keeps effort chips localized without
/// duplicating view-model state.
/// </summary>
public sealed class EffortLabelConverter : IValueConverter
{
    public static EffortLabelConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        "none" => "无",
        "minimal" => "极低",
        "low" => "低",
        "medium" => "中",
        "high" => "高",
        "xhigh" => "极高",
        "max" => "最大",
        string s => s,
        _ => string.Empty
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
