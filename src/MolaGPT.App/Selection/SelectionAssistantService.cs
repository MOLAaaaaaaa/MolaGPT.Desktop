using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using MolaGPT.App.Infrastructure;
using MolaGPT.App.Views;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Models;
using MolaGPT.Core.Net;
using MolaGPT.ViewModels;
using MolaGPT.Desktop.Services;
using static MolaGPT.App.Selection.SelectionNative;
using Point = MolaGPT.App.Selection.SelectionNative.Point;
using Rect = MolaGPT.App.Selection.SelectionNative.Rect;

namespace MolaGPT.App.Selection;

internal enum SelectionPopupCorner { BottomRight, BottomLeft, TopRight, TopLeft }

internal sealed class SelectionAssistantService : IDisposable
{
    private sealed record Options(bool HotkeyOnly, bool Fullscreen, bool Clipboard, bool GameMode,
        SelectionPopupCorner PopupCorner, HashSet<string> Excluded, HashSet<string> Games);
    private sealed record Capture(Point Point, nint Window, bool Explicit, long Version);
    private readonly SettingsViewModel _settings;
    private readonly ProviderRegistry _providers;
    private readonly IHttpClientFactory _http;
    private readonly Window _mainWindow;
    private GlobalSelectionHook? _hook;
    private AutoResetEvent? _signal;
    private Capture? _pending;
    private readonly object _signalGate = new();
    private long _version;
    private long _toolbarVersion;
    private volatile bool _toolbarVisible;
    private volatile bool _resultPinned;
    private volatile bool _gameForeground;
    private volatile Options _options = new(false, true, true, false, SelectionPopupCorner.BottomRight, [], []);
    private SelectionToolbarWindow? _toolbar;
    private SelectionResultWindow? _result;
    private SelectionResultWindow? _gameHiddenResult;
    private SelectionText? _selection;
    private bool _disposed;

    public SelectionAssistantService(SettingsViewModel settings, ProviderRegistry providers,
        IHttpClientFactory http, Window mainWindow)
    {
        _settings = settings; _providers = providers; _http = http; _mainWindow = mainWindow;
        settings.PropertyChanged += SettingsChanged;
        ApplySettings();
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.SelectionEnabled)
            or nameof(SettingsViewModel.SelectionHotkeyOnly) or nameof(SettingsViewModel.SelectionHotkeyKey)
            or nameof(SettingsViewModel.SelectionHotkeyModifiers)) ApplySettings();
        else if (e.PropertyName is nameof(SettingsViewModel.SelectionExcludedApps)
            or nameof(SettingsViewModel.SelectionGameMode) or nameof(SettingsViewModel.SelectionGameProcesses)
            or nameof(SettingsViewModel.SelectionPauseFullscreen) or nameof(SettingsViewModel.SelectionClipboardFallback)
            or nameof(SettingsViewModel.SelectionPopupPosition))
        { UpdateOptions(); Dismiss(); _hook?.RefreshForeground(); }
    }

    private void UpdateOptions() => _options = new(_settings.SelectionHotkeyOnly, _settings.SelectionPauseFullscreen,
        _settings.SelectionClipboardFallback, _settings.SelectionGameMode,
        ParsePopupCorner(_settings.SelectionPopupPosition),
        _settings.SelectionExcludedApps.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Path.GetFileNameWithoutExtension(x.Trim())).ToHashSet(StringComparer.OrdinalIgnoreCase),
        _settings.SelectionGameProcesses.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Path.GetFileNameWithoutExtension(x.Trim())).ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static SelectionPopupCorner ParsePopupCorner(string value) => value switch
    {
        SettingsViewModel.SelectionPopupPositionBottomLeft => SelectionPopupCorner.BottomLeft,
        SettingsViewModel.SelectionPopupPositionTopRight => SelectionPopupCorner.TopRight,
        SettingsViewModel.SelectionPopupPositionTopLeft => SelectionPopupCorner.TopLeft,
        _ => SelectionPopupCorner.BottomRight
    };

    private bool IsGameForeground(nint hwnd)
    {
        var options = _options;
        if (!options.GameMode || hwnd == 0 || IsOwn(hwnd)) return false;
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            using var process = Process.GetProcessById((int)pid);
            return options.Games.Contains(process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        { return false; }
    }

    private void ApplySettings()
    {
        Stop();
        UpdateOptions();
        _settings.SelectionHotkeyStatus = null;
        if (_disposed || !_settings.SelectionEnabled) return;
        var signal = new AutoResetEvent(false);
        _signal = signal;
        var clipboardOwner = _mainWindow.TryGetPlatformHandle()?.Handle ?? 0;
        var hook = new GlobalSelectionHook(_options.HotkeyOnly, (uint)_settings.SelectionHotkeyModifiers,
            (uint)_settings.SelectionHotkeyKey,
            (point, hwnd, explicitTrigger) => { if (_signal == signal) QueueCapture(point, hwnd, explicitTrigger); },
            () => { if (_signal == signal) InvalidateCapture(); },
            () => { if (_signal == signal) Dismiss(); },
            IsGameForeground,
            game => { if (_signal == signal) SetGameForeground(game); },
            status => Dispatcher.UIThread.Post(() => { if (_signal == signal) _settings.SelectionHotkeyStatus = status; }));
        _hook = hook;
        var worker = new Thread(() => RunWorker(signal, hook, clipboardOwner)) { IsBackground = true, Name = "Selection UIA" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    private void SetGameForeground(bool game)
    {
        _gameForeground = game;
        Dispatcher.UIThread.Post(() =>
        {
            if (_gameForeground != game) return;
            if (game)
            {
                HideToolbar();
                if (_result is { IsVisible: true, IsPinned: true } result)
                {
                    _gameHiddenResult = result;
                    result.Hide();
                }
            }
            else if (_gameHiddenResult is { } result)
            {
                _gameHiddenResult = null;
                if (_result != result) return;
                var activated = result.ShowActivated;
                result.ShowActivated = false;
                result.Show();
                result.ShowActivated = activated;
            }
        });
    }

    private void QueueCapture(Point point, nint hwnd, bool explicitTrigger)
    {
        if (hwnd == 0 || IsOwn(hwnd)) return;
        var version = Interlocked.Increment(ref _version);
        Interlocked.Exchange(ref _pending, new(point, hwnd, explicitTrigger, version));
        lock (_signalGate) _signal?.Set();
    }

    private void RunWorker(AutoResetEvent signal, GlobalSelectionHook hook, nint clipboardOwner)
    {
        UiaSelectionReader? reader = null;
        var initialized = false;
        try
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
            initialized = true;
            while (_signal == signal)
            {
                signal.WaitOne();
                if (_signal != signal) break;
                var request = Interlocked.Exchange(ref _pending, null);
                if (request is null) continue;
                var deadline = Environment.TickCount64 + (request.Explicit ? 1100 : 450);
                bool Relevant() => _signal == signal && Volatile.Read(ref _version) == request.Version
                    && GetForegroundWindow() == request.Window;
                bool Current() => Relevant() && Environment.TickCount64 <= deadline;
                if (!Current()) continue;
                var options = _options;
                if (!request.Explicit && options.Fullscreen && IsFullscreen(request.Window)) continue;
                try
                {
                    GetWindowThreadProcessId(request.Window, out var pid);
                    using var process = Process.GetProcessById((int)pid);
                    if (options.GameMode && options.Games.Contains(process.ProcessName)) continue;
                    // Explicit hotkeys may read excluded applications, but never automatically.
                    if (!request.Explicit && options.Excluded.Contains(process.ProcessName)) continue;
                    // Mouse-up precedes selection updates in some providers. Hotkeys must release modifiers before copy.
                    Thread.Sleep(45);
                    while (request.Explicit && Current() && (GetAsyncKeyState(0x11) < 0 || GetAsyncKeyState(0x12) < 0
                        || GetAsyncKeyState(0x10) < 0 || GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0)) Thread.Sleep(10);
                    if (!Current()) continue;
                    reader ??= new UiaSelectionReader();
                    var text = reader.Read(request.Window, request.Point, Current, out var allowClipboard);
                    if (text is null && allowClipboard && request.Explicit && options.Clipboard && Current())
                    {
                        var copied = ClipboardSelectionReader.Read(request.Window, clipboardOwner, Current, () => hook.CopyEpoch);
                        if (copied is not null) text = new(copied, request.Point);
                    }
                    if (!Relevant()) continue;
                    if (text is null && !Current())
                    {
                        if (request.Explicit && Relevant()) Dispatcher.UIThread.Post(() =>
                        {
                            if (!Relevant()) return;
                            if (_resultPinned && _result is { IsVisible: true }) _result.Activate();
                            else ShowCaptureFailure(request.Point, "取词超时，请重试。");
                        });
                        continue;
                    }
                    var result = text;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!Relevant()) return;
                        if (result is not null) ShowToolbar(result);
                        else if (request.Explicit)
                        {
                            if (_resultPinned && _result is { IsVisible: true }) _result.Activate();
                            else ShowCaptureFailure(request.Point, "未读取到选中文字。");
                        }
                    });
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                {
                    DiagnosticLog.Write("selection", $"取词失败：{ex.GetType().Name} 0x{ex.HResult:X8}");
                    if (request.Explicit && Relevant()) Dispatcher.UIThread.Post(() =>
                    {
                        if (Relevant()) ShowCaptureFailure(request.Point,
                            ex is InvalidOperationException ? ex.Message : "无法读取此应用的选区。");
                    });
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            DiagnosticLog.Write("selection", $"取词线程已停止：{ex.GetType().Name} 0x{ex.HResult:X8}\n{ex.StackTrace}");
            Dispatcher.UIThread.Post(() =>
            {
                if (_hook != hook) return;
                HideToolbar();
                _settings.SelectionHotkeyStatus = "取词服务已停止，请关闭后重新启用划词助手。";
            });
        }
        finally
        {
            reader?.Dispose();
            if (initialized) CoUninitialize();
            var stopHook = false;
            lock (_signalGate)
            {
                if (_signal == signal) { _signal = null; stopHook = true; }
                signal.Dispose();
            }
            if (stopHook) hook.Dispose();
        }
    }

    private void ShowToolbar(SelectionText selection)
    {
        _selection = selection;
        Interlocked.Increment(ref _toolbarVersion);
        if (_toolbar is null)
        {
            _toolbar = new SelectionToolbarWindow();
            _toolbar.ActionRequested += ExecuteAction;
        }
        Place(_toolbar, selection.Anchor, _options.PopupCorner, selection.Bounds);
        _toolbar.Show();
        _toolbarVisible = true;
        _hook?.SetToolbarVisible(true);
    }

    private IReadOnlyList<SelectionModelChoice> BuildModelChoices(out SelectionModelChoice? selected)
    {
        var list = new List<SelectionModelChoice>();
        foreach (var provider in _providers.Providers.OfType<PiWorkProvider>())
            foreach (var model in provider.Models)
                list.Add(new(provider.Id, model.Id, model.DisplayName, provider.DisplayName, model));
        var chosen = _settings.SelectedSelectionModel;
        selected = chosen is { ProviderId: { } p, ModelId: { } m }
            ? list.FirstOrDefault(x => x.ProviderId == p && x.ModelId == m)
            : null;
        return list;
    }

    private void OpenSelectionSettings()
    {
        if (_mainWindow is MainWindow mw) mw.OpenSelectionSettings();
    }

    private async void ExecuteAction(string action)
    {
        var selection = _selection;
        HideToolbar();
        if (action == "设置")
        {
            OpenSelectionSettings();
            return;
        }
        if (selection is null) return;
        if (action == "复制")
        {
            try { if (_toolbar?.Clipboard is { } clipboard) await clipboard.SetTextAsync(selection.Text); }
            catch (Exception ex) { ShowCaptureFailure(selection.Anchor, ex.Message); }
            return;
        }
        var language = string.IsNullOrWhiteSpace(_settings.SelectionLanguage) ? "简体中文" : _settings.SelectionLanguage.Trim();
        if (_result is { IsPinned: true, IsVisible: true } pinnedWindow)
        {
            pinnedWindow.LoadSelection(selection.Text, action, language);
            pinnedWindow.Activate();
            return;
        }
        _result?.Close();
        var models = BuildModelChoices(out var selectedModel);
        _result = new SelectionResultWindow(selection.Text, action, language, models, selectedModel, async (messages, reqOptions, onDelta, ct) =>
        {
            var providerId = reqOptions.ProviderId ?? _settings.SelectedSelectionModel?.ProviderId;
            var modelId = reqOptions.ModelId ?? _settings.SelectedSelectionModel?.ModelId;
            var found = providerId is not null && modelId is not null ? _providers.FindModel(providerId, modelId) : null;
            if (found is null || found.Value.Provider is not IOneShotTarget targetProvider
                || targetProvider.DescribeOneShot(found.Value.Model.Id) is not { } target)
                throw new InvalidOperationException("请在下方选择模型，或在设置 → 划词助手中配置可用模型。");
            if (messages.Sum(x => x.AsText().Length) > 48000)
                throw new InvalidOperationException("本次问答内容较长，请重新划词开始新的问答。");
            var clientName = MolaGptProviderIds.IsMolaGptAccount(found.Value.Provider.Id) ? HttpClientNames.MolaGpt : HttpClientNames.Byok;
            using var http = _http.CreateClient(clientName);
            var model = found.Value.Model;
            return await new OneShotCompletionClient(http).CompleteStreamAsync(
                target,
                model.Id,
                messages,
                onDelta: onDelta,
                maxTokens: 4096,
                useThinking: reqOptions.UseThinking,
                thinkingKind: model.ThinkingConfig?.Kind ?? ThinkingParamKindInference.InferFromModelId(model.Id),
                reasoningEffort: reqOptions.ReasoningEffort,
                thinkingConfig: model.ThinkingConfig,
                ct: ct);
        });
        var resultWindow = _result;
        resultWindow.PinChanged += pinned => _resultPinned = pinned;
        resultWindow.LanguageChanged += lang => _settings.SelectionLanguage = lang;
        resultWindow.ModelChanged += choice =>
        {
            _settings.RefreshSelectionModels();
            var match = _settings.SelectionModels.FirstOrDefault(x => x.ProviderId == choice.ProviderId && x.ModelId == choice.ModelId);
            if (match is not null) _settings.SelectedSelectionModel = match;
        };
        resultWindow.SettingsRequested += OpenSelectionSettings;
        resultWindow.Closed += (_, _) => { if (_result == resultWindow) _resultPinned = false; };
        Place(_result, selection.Anchor);
        _result.Show();
    }

    private void ShowCaptureFailure(Point point, string message)
    {
        if (_result is { IsVisible: true, IsPinned: true }) return;
        _result?.Close();
        _result = new SelectionResultWindow("", "问 AI", "简体中文", [], null,
            (_, _, _, _) => Task.FromException<string>(new InvalidOperationException(message)));
        _result.SettingsRequested += OpenSelectionSettings;
        _result.ShowMessage(message);
        Place(_result, point);
        _result.Show();
    }

    private static void Place(Window window, Point anchor, SelectionPopupCorner? popupCorner = null, Rect? selectionBounds = null)
    {
        var point = new PixelPoint(anchor.X, anchor.Y + 8);
        var screenPoint = popupCorner is not null ? new PixelPoint(anchor.X, anchor.Y) : point;
        if (window is SelectionResultWindow result) result.FitToScreen(screenPoint);
        var screen = window.Screens.ScreenFromPoint(screenPoint);
        if (screen is null) return;
        // 工具条 SizeToContent 未布局时 Width/Height 是 NaN；显式尺寸的窗口直接取。
        if (double.IsNaN(window.Width) || double.IsNaN(window.Height))
            window.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = (int)Math.Ceiling((double.IsNaN(window.Width) ? window.DesiredSize.Width : window.Width) * screen.Scaling);
        var height = (int)Math.Ceiling((double.IsNaN(window.Height) ? window.DesiredSize.Height : window.Height) * screen.Scaling);
        var area = screen.WorkingArea;
        if (popupCorner is { } corner)
        {
            var bounds = selectionBounds ?? new Rect { Left = anchor.X, Top = anchor.Y, Right = anchor.X, Bottom = anchor.Y };
            var margin = (int)Math.Round(8 * screen.Scaling);
            var x = corner is SelectionPopupCorner.BottomLeft or SelectionPopupCorner.TopLeft
                ? bounds.Left
                : bounds.Right - width;
            var top = bounds.Top - height - margin;
            var bottom = bounds.Bottom + margin;
            var y = corner is SelectionPopupCorner.TopLeft or SelectionPopupCorner.TopRight
                ? (top >= area.Y ? top : bottom)
                : (bottom + height <= area.Bottom ? bottom : top);
            x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width));
            y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height));
            window.Position = new(x, y);
            return;
        }

        var toolbarX = Math.Clamp(point.X, area.X, Math.Max(area.X, area.Right - width));
        var toolbarY = point.Y + height <= area.Bottom ? point.Y : anchor.Y - height - 8;
        window.Position = new(toolbarX, Math.Clamp(toolbarY, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }

    private void InvalidateCapture()
    {
        Interlocked.Exchange(ref _pending, null);
        Interlocked.Increment(ref _version);
    }

    private void Dismiss()
    {
        InvalidateCapture();
        if (!_toolbarVisible) return;
        var version = Volatile.Read(ref _toolbarVersion);
        Dispatcher.UIThread.Post(() =>
        {
            if (Volatile.Read(ref _toolbarVersion) == version) HideToolbar();
        });
    }

    private void Stop()
    {
        Interlocked.Increment(ref _version);
        lock (_signalGate)
        {
            var signal = Interlocked.Exchange(ref _signal, null);
            signal?.Set();
        }
        _hook?.Dispose(); _hook = null;
        Interlocked.Exchange(ref _pending, null);
        HideToolbar();
        _selection = null;
        _result?.Close(); _result = null;
        _gameHiddenResult = null;
        _gameForeground = false;
        _resultPinned = false;
    }

    public void Dispose()
    {
        _disposed = true;
        _settings.PropertyChanged -= SettingsChanged;
        Stop();
        _toolbar?.Close();
    }

    private void HideToolbar()
    {
        _toolbar?.Hide();
        _toolbarVisible = false;
        _hook?.SetToolbarVisible(false);
    }
}
