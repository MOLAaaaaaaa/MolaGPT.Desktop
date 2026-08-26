using Avalonia.Controls;
using Avalonia.Input;

namespace MolaGPT.App.Views;

public partial class ToolApprovalWindow : MolaWindow
{
    public ToolApprovalWindow(string title)
    {
        InitializeComponent();
        Title = title;
        PART_Title.Text = title;

        PART_Header.PointerPressed += OnHeaderPointerPressed;
        PART_Close.Click += (_, _) => Close();
        KeyDown += OnKeyDown;
    }

    public void SetBody(Control body) => PART_Body.Content = body;

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }
}
