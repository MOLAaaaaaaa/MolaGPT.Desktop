using Avalonia.Controls;

namespace MolaGPT.App.Views;

public partial class BridgePrivacyWindow : MolaContentWindow
{
    public BridgePrivacyWindow()
    {
        InitializeComponent();
        PART_Cancel.Click += (_, _) => Close(false);
        PART_Confirm.Click += (_, _) => Close(true);
    }
}
