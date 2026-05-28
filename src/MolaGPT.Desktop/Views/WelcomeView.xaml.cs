using System.Windows;
using System.Windows.Controls;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Views;

public partial class WelcomeView : UserControl
{
    public WelcomeView() => InitializeComponent();

    private void HintChip_Click(object sender, RoutedEventArgs e)
    {
        // Walk up to the Window's DataContext (MainViewModel) so we can reach
        // the Composer regardless of how this UserControl was hosted.
        if (sender is not Button { Content: string text }) return;
        if (Window.GetWindow(this)?.DataContext is MainViewModel mvm)
            mvm.Composer.ApplyHintCommand.Execute(text);
    }

    /// <summary>Welcome screen persona quick-pick: clicking a persona card
    /// binds the conversation to it and focuses the composer. The user can
    /// then either start typing or switch to a different persona via the
    /// Composer toolbar.</summary>
    private void PersonaCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var id = fe.Tag?.ToString();
        if (string.IsNullOrEmpty(id)) return;
        if (Window.GetWindow(this)?.DataContext is MainViewModel mvm)
            mvm.Chat.SaveActivePersona(id);
    }
}
