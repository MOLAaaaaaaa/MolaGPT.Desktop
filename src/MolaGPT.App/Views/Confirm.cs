using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MolaGPT.App.Views;

/// <summary>
/// A yes/no dialog, built in code because Avalonia ships no MessageBox.
///
/// Deliberately a single helper rather than a control: every caller wants the
/// same three things (a title, a sentence of consequence, and a verb on the
/// confirming button), and giving each one its own window is how the wording
/// drifts apart.
/// </summary>
internal static class Confirm
{
    public static async Task<bool> AskAsync(Window owner, string title, string detail, string confirmVerb)
    {
        var confirmed = false;

        var confirm = new Button
        {
            Content = confirmVerb,
            Classes = { "primary" }
        };
        var cancel = new Button
        {
            Content = "取消",
            Classes = { "outline" }
        };

        var dialog = new MolaDialogWindow(title);
        dialog.SetBody(new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock
                {
                    Text = detail,
                    Classes = { "secondary" },
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, confirm }
                }
            }
        });

        confirm.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
        return confirmed;
    }
}
