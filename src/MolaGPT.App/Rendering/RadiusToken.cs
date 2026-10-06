using Avalonia.Controls;

namespace MolaGPT.App.Rendering;

internal static class RadiusToken
{
    /// <summary>
    /// The code-built equivalent of CornerRadius="{DynamicResource Radius.X}",
    /// so the border follows 外观 → 圆角 instead of keeping the value it was built with.
    /// </summary>
    public static T WithRadius<T>(this T border, string key) where T : Border
    {
        border.Bind(Border.CornerRadiusProperty, border.GetResourceObservable(key));
        return border;
    }
}
