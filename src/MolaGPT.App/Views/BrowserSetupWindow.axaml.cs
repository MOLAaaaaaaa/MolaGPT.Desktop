using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;      // ClipboardExtensions.SetTextAsync
using MolaGPT.Core.Chat.Tools.Browser;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

/// <summary>
/// Setting up the Kimi browser extension and its local service.
///
/// Reachable at any time, not only when detection fails: someone who has not
/// installed it yet has no failing state to click through, and someone whose
/// install broke wants the steps again.
/// </summary>
public partial class BrowserSetupWindow : MolaContentWindow
{
    /// <summary>True when the user closed with 「完成并检测」.</summary>
    public bool CheckRequested { get; private set; }

    public BrowserSetupWindow() : this(null) { }

    public BrowserSetupWindow(BrowserBridgeStatusViewModel? status)
    {
        InitializeComponent();

        // Shares the settings page's view model when there is one, so an install
        // run here updates the connection card behind it.
        DataContext = status ?? new BrowserBridgeStatusViewModel(new WebBridgeClient(new HttpClient()));

        PART_OpenChromeStore.Click += (_, _) => Open(BrowserBridgeStatusViewModel.ChromeStoreUrl);
        PART_OpenEdgeStore.Click += (_, _) => Open(BrowserBridgeStatusViewModel.EdgeStoreUrl);
        PART_OpenOfficial.Click += (_, _) => Open(BrowserBridgeStatusViewModel.InstallUrl);
        PART_OpenHelp.Click += (_, _) => Open(BrowserBridgeStatusViewModel.HelpUrl);
        PART_CopyCommand.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync(WebBridgeInstaller.DisplayCommand);
            PART_CopyCommand.Content = "已复制";
        };
        PART_CheckNow.Click += (_, _) =>
        {
            CheckRequested = true;
            Close();
        };
    }

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // No default browser, or the shell refused. The addresses are on the
            // page either way; nothing here is worth an error dialog.
        }
    }
}
