using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
using MolaGPT.ViewModels;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.App.Infrastructure;

public sealed class AppNotificationService : IDisposable
{
    private readonly BackgroundStreamService _backgroundStreams;
    private readonly SettingsViewModel _settings;
    private readonly Func<string?> _getCurrentConversationId;
    private readonly Action<string> _navigateToConversation;
    private readonly OnActivated _toastActivated;

    public AppNotificationService(
        BackgroundStreamService backgroundStreams,
        SettingsViewModel settings,
        Func<string?> getCurrentConversationId,
        Action<string> navigateToConversation)
    {
        _backgroundStreams = backgroundStreams;
        _settings = settings;
        _getCurrentConversationId = getCurrentConversationId;
        _navigateToConversation = navigateToConversation;

        _toastActivated = e => OnToastActivated(e.Argument);
        _backgroundStreams.TaskCompleted += OnTaskCompleted;
        ToastNotificationManagerCompat.OnActivated += _toastActivated;
    }

    private void OnTaskCompleted(object? sender, BackgroundStreamCompletedEventArgs e)
    {
        if (!_settings.EnableCompletionNotification) return;

        var mainWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow is { IsActive: true }
            && mainWindow.WindowState != WindowState.Minimized
            && string.Equals(_getCurrentConversationId(), e.ConversationId, StringComparison.Ordinal))
        {
            return;
        }

        new ToastContentBuilder()
            .AddArgument("conversationId", e.ConversationId)
            .AddText(e.ModelLabel ?? "MolaGPT")
            .AddText(string.IsNullOrWhiteSpace(e.ConversationTitle)
                ? "回复已完成"
                : $"「{e.ConversationTitle}」回复已完成")
            .Show();
    }

    public void ShowImageGenerationStarted(string conversationId, string? taskTitle)
    {
        if (!_settings.EnableCompletionNotification) return;

        new ToastContentBuilder()
            .AddArgument("conversationId", conversationId)
            .AddText("图像正在后台生成")
            .AddText(string.IsNullOrWhiteSpace(taskTitle) ? "完成后将通知你" : $"「{taskTitle}」完成后将通知你")
            .Show();
    }

    public void ShowImageGenerationCompleted(
        string conversationId,
        string? taskTitle,
        int imageCount,
        bool force = false)
    {
        if (!force && !_settings.EnableCompletionNotification) return;

        new ToastContentBuilder()
            .AddArgument("conversationId", conversationId)
            .AddText(string.IsNullOrWhiteSpace(taskTitle) ? "图像生成完成" : $"「{taskTitle}」生成完成")
            .AddText(imageCount > 0 ? $"已生成 {imageCount} 张图片，点击查看" : "点击查看结果")
            .Show();
    }

    public void ShowImageGenerationFailed(
        string conversationId,
        string? taskTitle,
        string message,
        bool force = false)
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
        if (!args.TryGetValue("conversationId", out var conversationId)
            || string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

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

    public void Dispose()
    {
        _backgroundStreams.TaskCompleted -= OnTaskCompleted;
        ToastNotificationManagerCompat.OnActivated -= _toastActivated;
    }
}
