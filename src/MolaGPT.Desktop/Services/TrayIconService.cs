using System.ComponentModel;
using System.IO;
using System.Windows;
using MolaGPT.Desktop.Views;
using MolaGPT.ViewModels;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace MolaGPT.Desktop.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly SettingsViewModel _settings;
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _menu;
    private Drawing.Icon? _icon;
    private Window? _window;
    private Action? _openSettingsRequested;
    private Action? _openAgentStatusRequested;
    private bool _allowExit;
    private bool _hiddenToTray;
    private bool _inBackgroundMode;
    private bool _disposed;

    public event Action<bool>? BackgroundModeChanged;

    public TrayIconService(SettingsViewModel settings)
    {
        _settings = settings;
    }

    public void Attach(Window window, Action? openSettingsRequested, Action? openAgentStatusRequested = null)
    {
        if (_window is not null)
            DetachWindow();

        _window = window;
        _openSettingsRequested = openSettingsRequested;
        _openAgentStatusRequested = openAgentStatusRequested;
        _settings.PropertyChanged += OnSettingsChanged;
        window.Closing += OnWindowClosing;
        window.Closed += OnWindowClosed;
        window.IsVisibleChanged += OnWindowIsVisibleChanged;
        window.StateChanged += OnWindowStateChanged;

        EnsureNotifyIcon();
        UpdateVisibility();
    }

    private void EnsureNotifyIcon()
    {
        if (_notifyIcon is not null)
            return;

        _icon = LoadIcon();
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("打开 MolaGPT", null, (_, _) => ShowMainWindow());
        _menu.Items.Add("设置", null, (_, _) =>
        {
            ShowMainWindow();
            _openSettingsRequested?.Invoke();
        });
        _menu.Items.Add("Agent 状态…", null, (_, _) => _openAgentStatusRequested?.Invoke());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "MolaGPT",
            Icon = _icon,
            ContextMenuStrip = _menu,
            Visible = false
        };
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
    }

    private static Drawing.Icon LoadIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
            return new Drawing.Icon(iconPath);

        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath)
            && Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath) is { } extracted)
        {
            return extracted;
        }

        return Drawing.SystemIcons.Application;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.EnableTrayIcon))
            UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        EnsureNotifyIcon();
        if (_notifyIcon is not null)
            _notifyIcon.Visible = _settings.EnableTrayIcon;
    }

    private void OnNotifyIconMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
            ShowMainWindow();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowExit
            || Application.Current?.Dispatcher.HasShutdownStarted == true
            || !_settings.EnableTrayIcon)
        {
            return;
        }

        switch (_settings.TrayCloseBehavior)
        {
            case TrayCloseBehavior.MinimizeToTray:
                e.Cancel = true;
                HideMainWindow();
                return;
            case TrayCloseBehavior.Exit:
                _allowExit = true;
                return;
            case TrayCloseBehavior.Ask:
            default:
                var choice = TrayClosePromptWindow.ShowFor(_window);
                if (choice is null)
                {
                    e.Cancel = true;
                    return;
                }

                _settings.TrayCloseBehavior = choice.Value;
                if (choice.Value == TrayCloseBehavior.MinimizeToTray)
                {
                    e.Cancel = true;
                    HideMainWindow();
                }
                else
                {
                    _allowExit = true;
                }
                return;
        }
    }

    private void HideMainWindow()
    {
        var window = _window ?? Application.Current?.MainWindow;
        if (window is null) return;
        window.Hide();
        _hiddenToTray = true;
        UpdateBackgroundMode();
    }

    private void ShowMainWindow()
    {
        var window = _window ?? Application.Current?.MainWindow;
        if (window is null) return;

        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        _hiddenToTray = false;
        UpdateBackgroundMode();
        window.Activate();
    }

    private void ExitApplication()
    {
        _allowExit = true;
        _hiddenToTray = false;
        UpdateBackgroundMode();
        if (_notifyIcon is not null)
            _notifyIcon.Visible = false;
        Application.Current?.Shutdown();
    }

    private void OnWindowClosed(object? sender, EventArgs e) => DetachWindow();

    private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _hiddenToTray = false;
            UpdateBackgroundMode();
        }
        else if (_settings.EnableTrayIcon
                 && !_allowExit
                 && Application.Current?.Dispatcher.HasShutdownStarted != true)
        {
            _hiddenToTray = true;
            UpdateBackgroundMode();
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateBackgroundMode();

    private void UpdateBackgroundMode()
    {
        var window = _window ?? Application.Current?.MainWindow;
        var inBackgroundMode = !_allowExit
                               && Application.Current?.Dispatcher.HasShutdownStarted != true
                               && (_hiddenToTray || window?.WindowState == WindowState.Minimized);

        if (_inBackgroundMode == inBackgroundMode) return;
        _inBackgroundMode = inBackgroundMode;
        BackgroundModeChanged?.Invoke(inBackgroundMode);
    }

    private void DetachWindow()
    {
        if (_window is not null)
        {
            _window.Closing -= OnWindowClosing;
            _window.Closed -= OnWindowClosed;
            _window.IsVisibleChanged -= OnWindowIsVisibleChanged;
            _window.StateChanged -= OnWindowStateChanged;
            _window = null;
        }

        _settings.PropertyChanged -= OnSettingsChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachWindow();

        if (_notifyIcon is not null)
        {
            _notifyIcon.MouseClick -= OnNotifyIconMouseClick;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _menu?.Dispose();
        _menu = null;

        if (!ReferenceEquals(_icon, Drawing.SystemIcons.Application))
            _icon?.Dispose();
        _icon = null;
    }
}
