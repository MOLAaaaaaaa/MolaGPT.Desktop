using Avalonia;
using Avalonia.Controls;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Infrastructure;

/// <summary>
/// Applies 外观 → 圆角 by overriding the Radius.* ladder in Theme/Tokens.axaml.
///
/// Standard is Tokens.axaml itself. The other styles are a dictionary merged
/// after it, so their keys win; removing that dictionary restores the original
/// ladder without this file repeating its values. Every CornerRadius in the app
/// reaches the ladder through DynamicResource, so a swap restyles open windows
/// in place.
///
/// Radius.Pill is never overridden: it means "half the height", a shape rather
/// than a degree of rounding, and a square toggle or avatar reads as a
/// different control.
/// </summary>
internal static class CornerStyles
{
    private static ResourceDictionary? _applied;

    public static void Apply(Application app, CornerStyle style)
    {
        var merged = app.Resources.MergedDictionaries;
        if (_applied is not null) merged.Remove(_applied);

        _applied = style switch
        {
            CornerStyle.Small => Ladder(xs: 2, sm: 3, md: 4, lg: 6, xl: 8, xxl: 8),
            CornerStyle.Square => Ladder(xs: 0, sm: 0, md: 0, lg: 0, xl: 0, xxl: 0),
            _ => null
        };
        if (_applied is not null) merged.Add(_applied);
    }

    private static ResourceDictionary Ladder(double xs, double sm, double md, double lg, double xl, double xxl) => new()
    {
        ["Radius.2xs"] = new CornerRadius(xs),
        ["Radius.Xs"] = new CornerRadius(xs),
        ["Radius.Web.Sm"] = new CornerRadius(sm),
        ["Radius.Sm"] = new CornerRadius(sm),
        ["Radius.Md"] = new CornerRadius(md),
        ["Radius.Lg"] = new CornerRadius(lg),
        ["Radius.Xl"] = new CornerRadius(xl),
        ["Radius.Xxl"] = new CornerRadius(xxl),
        // The flat corner stays beside the avatar at every size.
        ["Radius.UserBubble"] = new CornerRadius(md, 0, md, md),
    };
}
