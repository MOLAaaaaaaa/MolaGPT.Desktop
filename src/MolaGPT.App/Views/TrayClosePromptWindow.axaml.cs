using Avalonia.Controls;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class TrayClosePromptWindow : MolaContentWindow
{
    public TrayClosePromptWindow()
    {
        InitializeComponent();
        PART_Cancel.Click += (_, _) => Close(null);
        PART_Minimize.Click += (_, _) => Close(TrayCloseBehavior.MinimizeToTray);
        PART_Exit.Click += (_, _) => Close(TrayCloseBehavior.Exit);
    }
}
