using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.ComponentModel;
using MolaGPT.App.Views;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Infrastructure;

/// <summary>
/// System tray icon, ported from MolaGPT.Desktop/Services/TrayIconService.cs.
///
/// The WPF version drove WinForms' NotifyIcon, which is why that file was one of
/// the eight in Desktop/Services that could not move as-is. Avalonia has a
/// first-class <see cref="TrayIcon"/>, so the port is a straight swap and the
/// WinForms framework reference goes away with it.
///
/// Behaviour kept from the original: left-click restores the window, closing the
/// window hides to tray rather than exiting, and only the menu's 退出 actually
/// shuts the app down.
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly SettingsViewModel _settings;
    private readonly TrayIcon _icon = new();
    private Window? _window;
    private bool _allowExit;
    private bool _closePromptOpen;
    private bool _disposed;

    public event EventHandler? SettingsRequested;
    public event EventHandler? AgentStatusRequested;

    public TrayIconHost(SettingsViewModel settings)
    {
        _settings = settings;
        _icon.ToolTipText = "MolaGPT";
        _icon.Icon = LoadIcon();
        _icon.Clicked += (_, _) => ShowWindow();

        var menu = new NativeMenu();
        menu.Add(Item("打开 MolaGPT", ShowWindow));
        menu.Add(Item("设置", () =>
        {
            ShowWindow();
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        }));
        menu.Add(Item("Agent 状态…", () => AgentStatusRequested?.Invoke(this, EventArgs.Empty)));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("退出", Exit));
        _icon.Menu = menu;
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    public void Attach(Window window)
    {
        _window = window;
        window.Closing += OnClosing;
        _settings.PropertyChanged += OnSettingsChanged;
        UpdateVisibility();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.EnableTrayIcon)) return;
        UpdateVisibility();
        if (!_settings.EnableTrayIcon && _window?.IsVisible == false) ShowWindow();
    }

    private void UpdateVisibility() => _icon.IsVisible = _settings.EnableTrayIcon && !_allowExit;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowExit || _disposed) return;

        if (!_settings.EnableTrayIcon || _settings.TrayCloseBehavior == TrayCloseBehavior.Exit)
        {
            e.Cancel = true;
            Exit();
            return;
        }

        if (_settings.TrayCloseBehavior == TrayCloseBehavior.MinimizeToTray)
        {
            e.Cancel = true;
            _window?.Hide();
            return;
        }

        e.Cancel = true;
        if (_closePromptOpen || _window is null) return;

        _closePromptOpen = true;
        try
        {
            var choice = await new TrayClosePromptWindow().ShowDialog<TrayCloseBehavior?>(_window);
            if (choice is null) return;

            _settings.TrayCloseBehavior = choice.Value;
            if (choice == TrayCloseBehavior.MinimizeToTray) _window.Hide();
            else Exit();
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void Exit()
    {
        _allowExit = true;
        _icon.IsVisible = false;
        if (Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private static WindowIcon? LoadIcon()
    {
        try
        {
            // The .ico ships as an Avalonia resource; AppContext.BaseDirectory
            // is not used here because the asset is embedded, not copied.
            using var stream = AssetLoader.Open(new Uri("avares://MolaGPT.Desktop/Assets/app.ico"));
            return new WindowIcon(stream);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_window is not null) _window.Closing -= OnClosing;
        _settings.PropertyChanged -= OnSettingsChanged;
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
