using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MolaGPT.App.Infrastructure;
using MolaGPT.App.Rendering;
using MolaGPT.App.Views;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Agents.Relay;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Chat.Tools.Browser;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Chat.Tools.Mcp;
using MolaGPT.Desktop.Services;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;
using MolaGPT.ViewModels.Agents;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private TrayIconHost? _tray;
    private CancellationTokenSource? _agentRelayCts;
    private Task? _agentRelayTask;
    private const string UpdateNotificationKey = "app-update";
    private const string CloudSyncNotificationKey = "cloud-sync";

    private string? _pendingUpdateInstallerPath;
    private NotificationRouter? _notificationRouter;
    private NotificationCenter? _notifications;
    private long _lastBackgroundTrimTicks;
    private bool _backgroundMode;
    private IDisposable? _backgroundCheck;

    /// <summary>How long a window state has to hold before it counts. Sized
    /// against the ~1.5s Minimized→Normal→Minimized blip Windows emits on its
    /// own; a real restore just waits this long for its prewarm.</summary>
    private static readonly TimeSpan BackgroundSettleDelay = TimeSpan.FromSeconds(3);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            UrlSchemeRegistrar.EnsureRegistered();
            _services = AppServices.Build();
            _services.GetRequiredService<MolaGptDatabase>().EnsureSchema();
            _services.GetRequiredService<PersonaListViewModel>().EnsureBuiltinsSeeded();

            var main = _services.GetRequiredService<MainViewModel>();
            var settings = _services.GetRequiredService<SettingsViewModel>();
            var auth = _services.GetRequiredService<MolaGptAuthService>();
            var providers = _services.GetRequiredService<ProviderRegistry>();
            var proxy = _services.GetRequiredService<MolaGptProxyProvider>();
            var localTools = _services.GetRequiredService<MolaGptLocalToolsRegistrar>();
            var cloudSync = _services.GetRequiredService<CloudSyncService>();
            var agentBridge = _services.GetRequiredService<AgentBridgeService>();
            var agentConfig = _services.GetRequiredService<DesktopAgentConfigProvider>();
            var agentStatus = _services.GetRequiredService<AgentBridgeStatusViewModel>();
            var updateCheck = _services.GetRequiredService<UpdateCheckService>();
            var autoUpdate = _services.GetRequiredService<AppAutoUpdateService>();
            var notifications = _services.GetRequiredService<NotificationCenter>();
            _notifications = notifications;

            if (!string.IsNullOrEmpty(auth.CurrentJwt)
                && !auth.IsJwtValidForUa(UserAgentProvider.FixedUa))
            {
                auth.Logout();
            }

            settings.IsLoggedIn = !string.IsNullOrEmpty(auth.CurrentJwt);
            settings.MolaGptUsername = settings.IsLoggedIn ? auth.CurrentUsername : null;

            var accountSession = _services.GetRequiredService<AccountSessionCoordinator>();
            if (!settings.IsLoggedIn) accountSession.CleanupLoggedOutAccountState();

            ApplyTheme(settings.ThemeMode);
            settings.ThemeModeChanged += (_, mode) => ApplyTheme(mode);

            // DiagnosticLog, not Debug.WriteLine: the previous sink was compiled out
            // of release builds, so on the machines where rows silently vanish from
            // the picker the reason was the one thing not written down anywhere.
            var restored = ProviderRestorer.Restore(
                _services, line => DiagnosticLog.Write("provider", line));
            if (restored.LostEverythingToRuntime)
                DiagnosticLog.Write("provider",
                    $"{restored.RuntimeUnavailable} 个已保存的服务全部未注册：Agent 运行环境不可用。"
                    + "模型选择器的本地侧会是空的。");

            main.EnsureConversationDetailAsync = id => cloudSync.FetchConversationToLocalAsync(id);
            main.Composer.ConversationCompletedAsync = cloudSync.CompleteConversationTurnAsync;
            main.Composer.ResponsePostProcessingFailed += message =>
                notifications.Error("回答后处理失败", message, key: "response-postprocessing");

            WireBrowserBridgeNotifications(notifications);
            main.Composer.LocalConversationTitleAsync = (conversationId, providerId, modelId, ct) =>
                _services.GetRequiredService<ConversationTitleService>()
                    .GenerateAsync(conversationId, providerId, modelId, ct);

            cloudSync.LocalConversationsChanged += (_, _) =>
                Dispatcher.UIThread.Post(() => _ = main.ConversationList.ReloadAsync(), DispatcherPriority.Background);
            cloudSync.StatusChanged += (_, status) =>
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        main.UpdateCloudSyncStatus(status.State.ToString(), status.Message, status.Timestamp);
                        PublishCloudSyncNotification(notifications, main, status);
                    },
                    DispatcherPriority.Background);
            main.CloudSyncRequested = async () =>
            {
                await cloudSync.RequestForegroundSyncAsync();
                await main.ConversationList.ReloadAsync();
            };
            main.ConversationList.ConversationsDeleted += async (_, ids) =>
            {
                try { await cloudSync.PushDeletedConversationsAsync(ids); }
                catch { }

                // 会话的标签组归这个对话所有。对话删了标签还开着，用户就得自己去浏览器
                // 里认领一堆没人管的标签页——而它们本来就是我们开的。
                var browser = _services.GetService<BrowserControlTool>();
                if (browser is null) return;
                foreach (var id in ids)
                {
                    try { await browser.CloseSessionAsync(id, settings.BrowserToolEnabled); }
                    catch { }
                }
            };

            var window = new MainWindow(
                main,
                main.Chat, main.ConversationList, main.Composer,
                providers,
                settings,
                _services.GetRequiredService<UpdateCheckService>(),
                 auth, proxy, localTools, cloudSync, agentStatus,
                 _services.GetRequiredService<McpHttpClient>(),
                 _services.GetRequiredService<ImageGenerationTool>(),
                 _services.GetRequiredService<AttachmentStore>(),
                 _services.GetRequiredService<ConversationRepository>(),
                 _services.GetRequiredService<MessageRepository>(),
                 _services.GetRequiredService<PythonRuntimeManager>(),
                 _services.GetRequiredService<PiSidecarRuntimeManager>(),
                 _services.GetRequiredService<PiWorkSidecarLocator>(),
                 notifications,
                _services.GetRequiredService<SkillsViewModel>(),
                 _services.GetRequiredService<BrowserActivityLog>(),
                 _services.GetRequiredService<IHttpClientFactory>(),
                 _services.GetRequiredService<IChatToolHost>(),
                 _services.GetRequiredService<PiByokProviderFactory>(),
                 _services.GetRequiredService<PersonalizationViewModel>(),
                 _services.GetRequiredService<MemoryPageViewModel>());
            desktop.MainWindow = window;

            // One router owns "banner, Windows toast, or wait" for every source.
            // The workbench counts as the current conversation while it is open,
            // so finishing an image you are watching stays silent.
            _notificationRouter = new NotificationRouter(
                notifications,
                _services.GetRequiredService<BackgroundStreamService>(),
                settings,
                window.Notifications,
                _services.GetRequiredService<AppNotificationService>(),
                window,
                () => main.IsImageWorkbenchVisible
                    ? main.ConversationList.SelectedId
                    : main.Chat.ConversationId);

            main.UpdateActionRequested = async (version, notes, downloadUrl, actionText, installerSha256) =>
            {
                var dialog = new UpdateWindow(
                    version, notes, downloadUrl, actionText, installerSha256,
                    package => BeginBackgroundUpdateDownloadAsync(main, autoUpdate, package));
                await dialog.ShowDialog<bool>(window);
            };
            main.UpdateBackgroundDownloadRequested = () =>
            {
                if (!TryCreateUpdatePackage(main, out var package)) return Task.CompletedTask;
                return BeginBackgroundUpdateDownloadAsync(main, autoUpdate, package);
            };
            main.UpdateInstallReadyRequested = () =>
            {
                if (string.IsNullOrWhiteSpace(_pendingUpdateInstallerPath)) return;
                try
                {
                    AppAutoUpdateService.StartInstallerAfterExit(_pendingUpdateInstallerPath);
                    desktop.Shutdown();
                }
                catch (Exception ex)
                {
                    main.MarkUpdateFailed("无法启动更新安装：" + ex.Message);
                }
            };

            agentBridge.Start();
            agentStatus.InitializeBridgeEnabled(agentConfig.BridgeEnabled);
            agentStatus.ConfirmEnableAsync = async () =>
                await new BridgePrivacyWindow().ShowDialog<bool>(window);
            agentStatus.ApplyBridgeEnabled = enabled =>
            {
                agentConfig.BridgeEnabled = enabled;
                if (enabled) StartAgentRelay();
                else _ = StopAgentRelayAsync();
            };
            if (agentConfig.BridgeEnabled) StartAgentRelay();

            // Tray close behavior may hide the main window, so shutdown stays
            // explicit and the tray host owns the final decision.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            _tray = new TrayIconHost(settings);
            _tray.Attach(window);
            _tray.SettingsRequested += (_, _) => window.OpenSettings();
            window.PropertyChanged += OnMainWindowPropertyChanged;

            SingleInstanceHost.Attach(deepLink => Dispatcher.UIThread.Post(() =>
            {
                BringToFront(window);
                if (!string.IsNullOrEmpty(deepLink)) _ = HandleOAuthDeepLinkAsync(deepLink, window);
            }));

            cloudSync.StartPeriodicSync();
            _ = RunStartupCloudSyncAsync(cloudSync, main.ConversationList);
            _ = Task.Run(SweepOrphanedPiSessions);
            _ = Task.Run(SweepOrphanedAttachments);
            _ = Task.Run(CatchUpMemoryIndex);
            WireMemoryNotifications(notifications);
            Dispatcher.UIThread.Post(
                () => _ = RunStartupAccountRefreshAsync(
                    auth, providers, proxy, localTools, accountSession, window),
                DispatcherPriority.Background);
            _ = RunUpdateCheckAsync(main, updateCheck, notifications);

            desktop.ShutdownRequested += (_, _) =>
            {
                window.PropertyChanged -= OnMainWindowPropertyChanged;
                _backgroundCheck?.Dispose();
                cloudSync.StopPeriodicSync();
                _notificationRouter?.Dispose();
                StopAgentRelayAsync().GetAwaiter().GetResult();
                _tray?.Dispose();
                var services = _services;
                _services = null;
                services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };

            // Off the dispatcher: the sidebar read is the first thing that
            // touches SQLite, and doing it inline delays the first frame.
            _ = main.ConversationList.ReloadAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunStartupAccountRefreshAsync(
        MolaGptAuthService auth,
        ProviderRegistry providers,
        MolaGptProxyProvider proxy,
        MolaGptLocalToolsRegistrar localTools,
        AccountSessionCoordinator accountSession,
        MainWindow window)
    {
        if (string.IsNullOrEmpty(auth.CurrentJwt)) return;

        try
        {
            await proxy.RefreshModelsAsync();
        }
        catch (MolaGptAuthExpiredException)
        {
            auth.Logout();
        }
        catch
        {
            // Keep the stored account available while offline. The next login or
            // account action can refresh the model list when the network returns.
        }

        if (!string.IsNullOrEmpty(auth.CurrentJwt)) providers.Register(proxy);
        // Work disappearing from the picker used to leave nothing behind at all.
        // Still non-fatal — Chat must come up either way — but it is written down.
        try { await localTools.RefreshAsync(); }
        catch (Exception ex) { DiagnosticLog.Write("pi-work", "启动时刷新 Work 失败：" + ex.Message); }

        if (string.IsNullOrEmpty(auth.CurrentJwt))
            accountSession.CleanupLoggedOutAccountState();
        else
            window.CompleteAccountLogin();
    }

    private async Task BeginBackgroundUpdateDownloadAsync(
        MainViewModel main,
        AppAutoUpdateService autoUpdate,
        AppAutoUpdateService.UpdatePackage package)
    {
        if (main.UpdateState == "Downloading") return;

        main.BeginUpdateDownload();
        _notifications?.Progress(UpdateNotificationKey, $"正在下载 {package.Version}", "0%", 0);

        try
        {
            // Reported per chunk; the banner only redraws when the whole
            // percent moves, which is the most a 344px card can show anyway.
            var lastPercent = -1;
            var progress = new Progress<double>(value =>
            {
                main.ReportUpdateDownloadProgress(value);

                var percent = Math.Clamp((int)(value * 100), 0, 100);
                if (percent == lastPercent) return;
                lastPercent = percent;
                _notifications?.Progress(
                    UpdateNotificationKey, $"正在下载 {package.Version}", $"{percent}%", value);
            });

            _pendingUpdateInstallerPath = await autoUpdate.DownloadAndVerifyAsync(package, progress);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                main.MarkUpdateReady();
                _notifications?.Notify(new AppNotification
                {
                    Key = UpdateNotificationKey,
                    Kind = NotifyKind.Success,
                    Title = "更新已就绪",
                    Body = $"{package.Version} 校验通过，重启后完成安装",
                    ActionText = "重启安装",
                    Action = () => main.UpdateInstallReadyRequested?.Invoke(),
                    Sticky = true
                });
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                main.MarkUpdateFailed("更新下载失败：" + ex.Message);
                _notifications?.Notify(new AppNotification
                {
                    Key = UpdateNotificationKey,
                    Kind = NotifyKind.Error,
                    Title = "更新下载失败",
                    Body = ex.Message,
                    ActionText = "重试",
                    Action = () => _ = main.UpdateBackgroundDownloadRequested?.Invoke()
                });
            });
        }
    }

    private static bool TryCreateUpdatePackage(
        MainViewModel main,
        out AppAutoUpdateService.UpdatePackage package)
    {
        package = default!;
        if (string.IsNullOrWhiteSpace(main.UpdateLatestVersion)
            || string.IsNullOrWhiteSpace(main.UpdateDownloadUrl)
            || string.IsNullOrWhiteSpace(main.UpdateInstallerSha256)
            || !Uri.TryCreate(main.UpdateDownloadUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        package = new AppAutoUpdateService.UpdatePackage(
            main.UpdateLatestVersion,
            main.UpdateDownloadUrl,
            main.UpdateInstallerSha256,
            Path.GetFileName(uri.LocalPath));
        return true;
    }

    /// <summary>
    /// 桥断了要让人知道。
    ///
    /// 模型收到的是一句可执行的错误，但那句话只出现在工具卡里——用户可能正在别的
    /// 地方等结果，而「浏览器没开」这件事他一秒钟就能修好。这是事件不是状态（刚发生、
    /// 修好就没了），所以走横幅；同一个 key 反复替换，一次任务里连撞五次也只有一条。
    /// </summary>
    private void WireBrowserBridgeNotifications(NotificationCenter notifications)
    {
        if (_services?.GetService<BrowserActivityLog>() is not { } log) return;

        log.Recorded += (_, entry) =>
        {
            if (!entry.IsBridgeFailure) return;
            Dispatcher.UIThread.Post(() => notifications.Notify(new AppNotification
            {
                Key = "browser-bridge",
                Kind = NotifyKind.Warning,
                Title = "浏览器未连接",
                Body = "模型使用浏览器时遇到问题，本机服务或 Kimi 扩展异常。",
                ActionText = "问题排除",
                Action = () => OpenBrowserSettings()
            }));
        };
    }

    private void OpenBrowserSettings()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        if (desktop.MainWindow is MainWindow window) window.OpenBrowserSettings();
    }

    private static async Task RunUpdateCheckAsync(
        MainViewModel main,
        UpdateCheckService updateCheck,
        NotificationCenter notifications)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        var info = await updateCheck.CheckAsync();
        if (info is null) return;

        Dispatcher.UIThread.Post(() =>
        {
            main.AnnounceUpdate(
                info.LatestVersion,
                info.DownloadUrl,
                info.Notes,
                info.ActionText,
                info.InstallerSha256);

            // "An update exists" is a state and stays on the header chip. The
            // banner only marks the moment it was found, and sticks because a
            // release the user never saw is the same as no release at all.
            notifications.Notify(new AppNotification
            {
                Key = UpdateNotificationKey,
                Kind = NotifyKind.Info,
                Title = $"发现新版本 {info.LatestVersion}",
                Body = FirstLine(info.Notes),
                ActionText = string.IsNullOrWhiteSpace(info.ActionText) ? "查看" : info.ActionText,
                Action = () => main.OpenUpdateDownloadCommand.Execute(null),
                Sticky = true
            });
        });
    }

    private static string? FirstLine(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var line = notes.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim(' ', '-', '*', '#'))
            .FirstOrDefault(l => l.Length > 0);
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    /// <summary>
    /// Sync is mostly unattended, so only a sync the user asked for narrates
    /// itself. Failures always surface — silently not syncing is the one
    /// outcome worth interrupting for.
    /// </summary>
    private static void PublishCloudSyncNotification(
        NotificationCenter notifications,
        MainViewModel main,
        CloudSyncStatusChangedEventArgs status)
    {
        switch (status.State)
        {
            case CloudSyncState.Error:
                notifications.Notify(new AppNotification
                {
                    Key = CloudSyncNotificationKey,
                    Kind = NotifyKind.Error,
                    Title = "云同步失败",
                    Body = status.Message,
                    ActionText = "重试",
                    Action = () => main.SyncCloudCommand.Execute(null)
                });
                return;

            case CloudSyncState.Syncing when status.IsUserInitiated:
                notifications.Progress(CloudSyncNotificationKey, status.Message);
                return;

            case CloudSyncState.Success when status.IsUserInitiated:
                notifications.Success(status.Message, key: CloudSyncNotificationKey);
                return;

            case CloudSyncState.Idle:
            case CloudSyncState.Disabled:
                notifications.Dismiss(CloudSyncNotificationKey);
                return;
        }
    }

    private static async Task RunStartupCloudSyncAsync(
        CloudSyncService cloudSync,
        ConversationListViewModel conversations)
    {
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        // Nobody asked for this one, so it stays silent unless it fails.
        await cloudSync.RequestForegroundSyncAsync(userInitiated: false).ConfigureAwait(false);
        await conversations.ReloadAsync().ConfigureAwait(false);
    }

    private void SweepOrphanedPiSessions()
    {
        if (_services is null) return;
        try
        {
            var live = _services.GetRequiredService<ConversationRepository>()
                .ListActive()
                .Select(conversation => conversation.Id)
                .ToArray();
            var removed = new PiWorkSessionSweeper(PiWorkSidecarLocator.SessionRoot).Sweep(live);
            if (removed > 0) DiagnosticLog.Write("pi-work", $"清理了 {removed} 个无主的 Pi 会话文件");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("pi-work", "清理 Pi 会话文件失败：" + ex.Message);
        }
    }

    /// <summary>
    /// Fold whatever accumulated while memory was off (or while the app was
    /// closed) into the search index, once, off the UI thread. Only messages
    /// newer than the last watermark are touched, so the steady-state cost is a
    /// single empty query.
    /// </summary>
    private void CatchUpMemoryIndex()
    {
        if (_services is null) return;
        try
        {
            var settings = _services.GetRequiredService<SettingsViewModel>();
            if (!settings.MemoryEnabled || !settings.MemoryRecallEnabled) return;
            _services.GetRequiredService<MemoryService>().EnsureIndexCurrent();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("memory", "建立记忆检索索引失败：" + ex.Message);
        }
    }

    /// <summary>
    /// Automatic consolidation reports through the one notification system.
    /// A short banner, and only when something was actually written — a pass
    /// that found nothing is not an event.
    /// </summary>
    private void WireMemoryNotifications(NotificationCenter notifications)
    {
        if (_services is null) return;
        _services.GetRequiredService<MemoryConsolidator>().Completed += (_, report) =>
            notifications.Success("记忆整理完成", report.Describe(), key: "memory-consolidation");
    }

    private void SweepOrphanedAttachments()
    {
        if (_services is null) return;
        try
        {
            var removed = AttachmentStoreSweeper.Sweep(
                _services.GetRequiredService<AttachmentStore>(),
                _services.GetRequiredService<MessageRepository>());
            if (removed > 0) DiagnosticLog.Write("attachments", $"清理了 {removed} 个无引用的附件文件");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("attachments", "清理附件文件失败：" + ex.Message);
        }
    }

    private async Task HandleOAuthDeepLinkAsync(string url, MainWindow window)
    {
        if (_services is null
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, UrlSchemeRegistrar.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? code = null;
        string? token = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0) continue;
            var key = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            if (key == "code") code = value;
            else if (key == "token") token = value;
        }

        var auth = _services.GetRequiredService<MolaGptAuthService>();
        if (!string.IsNullOrEmpty(code))
        {
            var result = await auth.ExchangeOAuthCodeAsync(code);
            if (!result.Success)
            {
                LoginWindow.NotifyExternalLoginCompleted(false, result.ErrorMessage ?? "授权码兑换失败，请重新登录。");
                return;
            }
        }
        else if (!string.IsNullOrEmpty(token))
        {
            if (!auth.ApplyExternalToken(token))
            {
                LoginWindow.NotifyExternalLoginCompleted(false, "第三方登录返回的 Token 无法解析，请重试。");
                return;
            }
        }
        else
        {
            return;
        }

        try
        {
            var proxy = _services.GetRequiredService<MolaGptProxyProvider>();
            await proxy.RefreshModelsAsync();
            _services.GetRequiredService<ProviderRegistry>().Register(proxy);
            try { await _services.GetRequiredService<MolaGptLocalToolsRegistrar>().RefreshAsync(); }
            catch { }
        }
        catch (MolaGptAuthExpiredException ex)
        {
            auth.Logout();
            LoginWindow.NotifyExternalLoginCompleted(false, $"账号验证失败：{ex.Message}");
            return;
        }
        catch
        {
            // The token is already stored; model discovery will retry on the next
            // account action or application start.
        }

        if (!LoginWindow.NotifyExternalLoginCompleted(true))
            window.CompleteAccountLogin();
    }

    /// <summary>
    /// 进入后台时释放空闲资源，正在运行的任务继续执行。
    /// </summary>
    private void OnMainWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.IsVisibleProperty && e.Property != Window.WindowStateProperty) return;
        if (sender is not Window window) return;

        // Windows reports a minimized window as Normal for a moment and flips it
        // back ~1.5s later — the change arrives from DefWindowProc, with no app
        // code on the stack, so there is nothing to suppress at the source. Read
        // at face value it looks like the user came back, which resumes cloud
        // sync and re-prewarms a sidecar for a window still sitting in the
        // taskbar. So settle first and then read the state that lasted.
        _backgroundCheck?.Dispose();
        _backgroundCheck = DispatcherTimer.RunOnce(() =>
        {
            _backgroundCheck = null;
            var background = !window.IsVisible || window.WindowState == WindowState.Minimized;
            if (_backgroundMode == background) return;
            _backgroundMode = background;
            if (background) EnterBackground();
            else ExitBackground();
        }, BackgroundSettleDelay);
    }

    private void EnterBackground()
    {
        _services?.GetRequiredService<AgentBridgeService>().SetBackgroundMode(true);
        _services?.GetRequiredService<PiRuntime>().SetBackgroundMode(true);
        _services?.GetRequiredService<CloudSyncService>().SetBackgroundMode(true);

        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref _lastBackgroundTrimTicks) < TimeSpan.FromSeconds(30).Ticks) return;
        Interlocked.Exchange(ref _lastBackgroundTrimTicks, now);

        CodeHighlighter.TrimForBackground();
        ImageSourceLoader.TrimForBackground();

        // 回收一次并归还空闲 GC 堆页；30s 内不重复请求。
        _ = Task.Run(() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive,
            blocking: true, compacting: true));
    }

    private void ExitBackground()
    {
        _services?.GetRequiredService<AgentBridgeService>().SetBackgroundMode(false);
        _services?.GetRequiredService<PiRuntime>().SetBackgroundMode(false);
        // 补同步排在预热之前：它是网络等待，不占 UI 线程。
        _services?.GetRequiredService<CloudSyncService>().SetBackgroundMode(false);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is MainWindow window)
            window.StartActiveProviderPrewarm();
    }

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!window.IsVisible) window.Show();
        window.Activate();
    }

    private void StartAgentRelay()
    {
        if (_agentRelayTask is not null || _services is null) return;

        _agentRelayCts = new CancellationTokenSource();
        var token = _agentRelayCts.Token;
        var relay = _services.GetRequiredService<AgentRelayClient>();
        _agentRelayTask = Task.Run(async () =>
        {
            var attempt = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await relay.StartAsync(token).ConfigureAwait(false);
                    attempt = 0;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write("AgentRelay", $"disconnected: {ex.GetType().Name}: {ex.Message}");
                }

                try
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(30, 2 + attempt * 2));
                    attempt++;
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        });
    }

    private async Task StopAgentRelayAsync()
    {
        var cts = _agentRelayCts;
        var task = _agentRelayTask;
        _agentRelayCts = null;
        _agentRelayTask = null;

        if (cts is null && task is null) return;

        cts?.Cancel();
        if (_services?.GetService<AgentRelayClient>() is { } relay)
        {
            using var offlineCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await relay.StopAsync(offlineCts.Token).ConfigureAwait(false);
        }

        if (task is not null)
            await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        cts?.Dispose();
    }

    private void ApplyTheme(ThemeMode mode) =>
        RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
}
