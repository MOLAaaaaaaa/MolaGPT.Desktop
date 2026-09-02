using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Toolkit.Uwp.Notifications;

namespace MolaGPT.App.Infrastructure;

/// <summary>
/// The Windows toast sink, and nothing more. It no longer decides <em>whether</em>
/// to notify — <see cref="NotificationRouter"/> owns that, so the rule
/// "system toasts only for a finished answer while the app is in the background"
/// lives in exactly one place.
/// </summary>
public sealed class AppNotificationService : IDisposable
{
    private readonly Action<string> _navigateToConversation;
    private readonly OnActivated _toastActivated;

    public AppNotificationService(Action<string> navigateToConversation)
    {
        _navigateToConversation = navigateToConversation;
        _toastActivated = e => OnToastActivated(e.Argument);
        ToastNotificationManagerCompat.OnActivated += _toastActivated;
    }

    public void ShowSystemToast(string title, string? body, string? conversationId)
    {
        var builder = new ToastContentBuilder().AddText(title);

        if (!string.IsNullOrWhiteSpace(body)) builder.AddText(body);
        if (!string.IsNullOrWhiteSpace(conversationId)) builder.AddArgument("conversationId", conversationId);

        builder.Show();
    }

    /// <summary>Brings the window forward and selects a conversation.</summary>
    public void NavigateTo(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return;

        Dispatcher.UIThread.Post(() =>
        {
            var mainWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (mainWindow is not null)
            {
                if (!mainWindow.IsVisible) mainWindow.Show();
                if (mainWindow.WindowState == WindowState.Minimized) mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            }

            _navigateToConversation(conversationId);
        });
    }

    private void OnToastActivated(string argument)
    {
        var args = ToastArguments.Parse(argument);
        if (args.TryGetValue("conversationId", out var conversationId))
            NavigateTo(conversationId);
    }

    public void Dispose() => ToastNotificationManagerCompat.OnActivated -= _toastActivated;
}
