using Avalonia.Controls;
using Avalonia.Input;

namespace MolaGPT.App.Views;

public partial class ToolApprovalWindow : MolaContentWindow
{
    public ToolApprovalWindow(string title)
    {
        InitializeComponent();
        Title = title;
        PART_Title.Text = title;

        PART_Close.Click += (_, _) => Close();
        KeyDown += OnKeyDown;
    }

    public void SetBody(Control body) => PART_Body.Content = body;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }
}
