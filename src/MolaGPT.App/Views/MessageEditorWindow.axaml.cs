using Avalonia.Controls;
using Avalonia.Threading;

namespace MolaGPT.App.Views;

public partial class MessageEditorWindow : MolaContentWindow
{
    public MessageEditorWindow()
    {
        InitializeComponent();
        PART_Cancel.Click += (_, _) => Close(null);
        PART_Save.Click += (_, _) => Close(PART_Text.Text ?? "");
    }

    public Task<string?> ShowForAsync(string text, Window owner)
    {
        PART_Text.Text = text;
        // Caret at the end, not at the start: the common edit is to continue or
        // amend, and selecting everything invites accidentally replacing it.
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            PART_Text.CaretIndex = PART_Text.Text?.Length ?? 0;
            PART_Text.Focus();
        });
        return ShowDialog<string?>(owner);
    }
}
