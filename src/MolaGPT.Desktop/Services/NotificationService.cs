using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;
using MolaGPT.ViewModels;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.Desktop.Services;

public sealed class NotificationService : IDisposable
{
    private readonly BackgroundStreamService _backgroundStreams;
    private readonly SettingsViewModel _settings;
    private readonly Func<string?>? _getCurrentConversationId;
    private readonly Action<string>? _navigateToConversation;

    public NotificationService(
        BackgroundStreamService backgroundStreams,
        SettingsViewModel settings,
        Func<string?>? getCurrentConversationId = null,
        Action<string>? navigateToConversation = null)
    {
        _backgroundStreams = backgroundStreams;
        _settings = settings;
        _getCurrentConversationId = getCurrentConversationId;
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
            && mainWindow.WindowState != WindowState.Minimized
            && string.Equals(_getCurrentConversationId?.Invoke(), e.ConversationId, StringComparison.Ordinal))
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

    public void ShowImageGenerationStarted(string conversationId, string? taskTitle)
    {
        if (!_settings.EnableCompletionNotification) return;

        new ToastContentBuilder()
            .AddArgument("conversationId", conversationId)
            .AddText("图像正在后台生成")
            .AddText(string.IsNullOrWhiteSpace(taskTitle)
                ? "完成后将通知你"
                : $"「{taskTitle}」完成后将通知你")
            .Show();
    }

    public void ShowImageGenerationCompleted(string conversationId, string? taskTitle, int imageCount, bool force = false)
    {
        if (!force && !_settings.EnableCompletionNotification) return;

        new ToastContentBuilder()
            .AddArgument("conversationId", conversationId)
            .AddText(string.IsNullOrWhiteSpace(taskTitle) ? "图像生成完成" : $"「{taskTitle}」生成完成")
            .AddText(imageCount > 0 ? $"已生成 {imageCount} 张图片，点击查看" : "点击查看结果")
            .Show();
    }

    public void ShowImageGenerationFailed(string conversationId, string? taskTitle, string message, bool force = false)
    {
        if (!force && !_settings.EnableCompletionNotification) return;

        new ToastContentBuilder()
            .AddArgument("conversationId", conversationId)
            .AddText(string.IsNullOrWhiteSpace(taskTitle) ? "图像生成失败" : $"「{taskTitle}」生成失败")
            .AddText(message)
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
                    if (!window.IsVisible)
                        window.Show();
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
