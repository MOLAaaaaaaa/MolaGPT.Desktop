using Avalonia.Input;

namespace MolaGPT.App.Views;

public partial class SettingsWindow
{
    private void OnResetSelectionGames(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _settings.SelectionGameProcesses = MolaGPT.ViewModels.SettingsViewModel.DefaultSelectionGameProcesses;

    private void OnSelectionHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab) return;
        e.Handled = true;
        var modifiers = e.KeyModifiers;
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0) return;
        var key = e.Key;
        var vk = key >= Key.A && key <= Key.Z ? 0x41 + (int)key - (int)Key.A
            : key >= Key.D0 && key <= Key.D9 ? 0x30 + (int)key - (int)Key.D0
            : key >= Key.F1 && key <= Key.F12 ? 0x70 + (int)key - (int)Key.F1 : 0;
        if (vk == 0) return;
        var nativeModifiers = (modifiers.HasFlag(KeyModifiers.Alt) ? 1 : 0)
            | (modifiers.HasFlag(KeyModifiers.Control) ? 2 : 0)
            | (modifiers.HasFlag(KeyModifiers.Shift) ? 4 : 0)
            | (modifiers.HasFlag(KeyModifiers.Meta) ? 8 : 0);
        var parts = new List<string>();
        if ((nativeModifiers & 2) != 0) parts.Add("Ctrl");
        if ((nativeModifiers & 1) != 0) parts.Add("Alt");
        if ((nativeModifiers & 4) != 0) parts.Add("Shift");
        if ((nativeModifiers & 8) != 0) parts.Add("Win");
        parts.Add(key >= Key.D0 && key <= Key.D9 ? ((int)key - (int)Key.D0).ToString() : key.ToString());
        _settings.SelectionHotkeyLabel = string.Join(" + ", parts);
        _settings.SelectionHotkeyModifiers = nativeModifiers;
        _settings.SelectionHotkeyKey = vk;
    }
}
