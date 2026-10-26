using Avalonia.Controls;
using Avalonia.Input;

namespace MolaGPT.App.Views;

/// <summary>
/// Asked every time full access is switched on, never when it is switched off.
/// Cancel holds the initial focus, so Enter cannot turn it on by accident.
/// </summary>
public partial class FullAccessConfirmWindow : MolaContentWindow
{
    private bool _confirmed;

    public FullAccessConfirmWindow()
    {
        InitializeComponent();
        PART_Close.Click += (_, _) => Close();
        PART_Cancel.Click += (_, _) => Close();
        PART_Confirm.Click += (_, _) =>
        {
            _confirmed = true;
            Close();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
        Opened += (_, _) => PART_Cancel.Focus();
    }

    public static async Task<bool> AskAsync(Window owner)
    {
        var dialog = new FullAccessConfirmWindow();
        await dialog.ShowDialog(owner);
        return dialog._confirmed;
    }
}
