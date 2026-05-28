using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;
using MolaGPT.ViewModels;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.Desktop.Services;

public sealed class NotificationService : IDisposable
{
    private readonly BackgroundStreamService _backgroundStreams;
    private readonly SettingsViewModel _settings;
    private readonly Action<string>? _navigateToConversation;

    public NotificationService(
        BackgroundStreamService backgroundStreams,
        SettingsViewModel settings,
        Action<string>? navigateToConversation = null)
    {
        _backgroundStreams = backgroundStreams;
        _settings = settings;
        _navigateToConversation = navigateToConversation;

        _backgroundStreams.TaskCompleted += OnTaskCompleted;
        ToastNotificationManagerCompat.OnActivated += e => OnToastActivated(e.Argument);
    }

    private void OnTaskCompleted(object? sender, BackgroundStreamCompletedEventArgs e)
    {
        if (!_settings.EnableCompletionNotification) return;

        var mainWindow = Application.Current?.MainWindow;
        if (mainWindow is not null
            && mainWindow.IsActive
            && mainWindow.WindowState != WindowState.Minimized)
        {
            return;
        }

        var title = e.ModelLabel ?? "MolaGPT";
        var body = string.IsNullOrWhiteSpace(e.ConversationTitle)
            ? "回复已完成"
            : $"「{e.ConversationTitle}」回复已完成";

        new ToastContentBuilder()
            .AddArgument("conversationId", e.ConversationId)
            .AddText(title)
            .AddText(body)
            .Show();
    }

    private void OnToastActivated(string argument)
    {
        var args = ToastArguments.Parse(argument);
        if (args.TryGetValue("conversationId", out var conversationId) &&
            !string.IsNullOrEmpty(conversationId))
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var window = Application.Current.MainWindow;
                if (window is not null)
                {
                    window.Activate();
                    if (window.WindowState == WindowState.Minimized)
                        window.WindowState = WindowState.Normal;
                }
                _navigateToConversation?.Invoke(conversationId);
            });
        }
    }

    public void Dispose()
    {
        _backgroundStreams.TaskCompleted -= OnTaskCompleted;
    }
}
