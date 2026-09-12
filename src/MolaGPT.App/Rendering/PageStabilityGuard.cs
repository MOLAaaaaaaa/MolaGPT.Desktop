using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MolaGPT.App.Rendering;

/// <summary>
/// The page owns its scroll position; a control sitting on it does not.
///
/// Avalonia breaks that in both directions, and both were reported as bugs in
/// the settings dialogs:
///
/// - Scrolling the page edits the control. ComboBox moves SelectedIndex one item
///   per wheel notch while focused, NumericUpDown moves Value the same way.
///   Focus arrives on the first click, so a control behaves until the user picks
///   something from it — after that the pointer merely crossing it on the way
///   down the page silently rewrites the setting. (Slider was measured and does
///   not do this, so it is not wired up.)
///
/// - Opening the control scrolls the page. ComboBox focuses the selected item as
///   its popup opens, that item asks to be brought into view, and the request is
///   routed — a popup's route continues into the control that owns it, so it
///   reaches the page's ScrollContentPresenter, which cannot map a rect from a
///   popup root into its own content and scrolls to the top instead. Measured in
///   世界书: a pane at offset 329 jumped to 0 when 插入位置 opened, extent
///   unchanged, and slid back when the popup closed and focus returned.
///
/// Neither is swallowed outright. The wheel event is re-raised at the parent, so
/// the page scrolls exactly as it does over an untouched control, and a
/// bring-into-view the control asks for on its own behalf still passes — tabbing
/// onto a box below the fold has to bring it into view.
///
/// Attached by the theme (Controls.axaml) rather than per call site: the app has
/// dozens of these, and a rule that has to be remembered is one that will be
/// missed. Our own flyouts need no guard — they leave focus on the trigger, so
/// nothing inside them ever asks to be scrolled to.
/// </summary>
public static class PageStabilityGuard
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>(
            "IsEnabled", typeof(PageStabilityGuard), defaultValue: false);

    public static void SetIsEnabled(Control element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(Control element) => element.GetValue(IsEnabledProperty);

    static PageStabilityGuard()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            control.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
            control.RemoveHandler(Control.RequestBringIntoViewEvent, OnRequestBringIntoView);
            if (!args.GetNewValue<bool>()) return;

            // Tunnel for the wheel, because the stepping happens in the
            // control's own bubble-phase handler: a handler on the way down is
            // the only one that gets to run first. The bring-into-view request
            // has to die here too — the page's ScrollContentPresenter acts on it
            // long before the ScrollViewer itself would see it, so guarding the
            // scroller instead does nothing (tried, measured).
            control.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
            control.AddHandler(Control.RequestBringIntoViewEvent, OnRequestBringIntoView, RoutingStrategies.Bubble);
        });
    }

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // An open drop-down scrolls its own list; only the closed control is guarded.
        if (e.Handled || sender is not Control control || control is ComboBox { IsDropDownOpen: true }) return;

        e.Handled = true;

        if (control.GetVisualParent() is not Interactive parent || TopLevel.GetTopLevel(control) is not { } root)
            return;

        // Re-raised rather than scrolling an ancestor by hand: the page keeps
        // whatever wheel behaviour it already had, including the transcript's
        // own handler and any logical-scrolling list.
        parent.RaiseEvent(new PointerWheelEventArgs(
            parent,
            e.Pointer,
            root,
            e.GetPosition(root),
            e.Timestamp,
            e.GetCurrentPoint(root).Properties,
            e.KeyModifiers,
            e.Delta));
    }

    private static void OnRequestBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (e.Handled || sender is not Visual owner) return;

        // The control asking on its own behalf — keyboard focus landing on it,
        // say — is the case the page is supposed to follow.
        if (e.TargetObject is Visual target && (ReferenceEquals(target, owner) || owner.IsVisualAncestorOf(target)))
            return;

        e.Handled = true;
    }
}
