using Avalonia.Controls;
using Avalonia.Input;

namespace MolaGPT.App.Views;

public partial class MolaDialogWindow : MolaWindow
{
    public MolaDialogWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
    }

    public MolaDialogWindow(string title) : this()
    {
        Title = title;
        PART_TitleBar.TitleText = title;
    }

    public void SetBody(Control body) => PART_Body.Content = body;
}
