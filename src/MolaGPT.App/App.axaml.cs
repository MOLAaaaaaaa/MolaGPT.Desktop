using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MolaGPT.App.Infrastructure;
using MolaGPT.App.Rendering;
using MolaGPT.App.Rendering.Canvas;
using MolaGPT.App.Remote;
using MolaGPT.App.Views;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Agents.Relay;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Chat.Tools.Browser;
using MolaGPT.Core.Chat.Tools.Mcp;
using MolaGPT.Core.Net;
using MolaGPT.Core.Runtime;
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
    private const string CommunityNotificationKey = "community-prompt";

    private (string Path, string Sha256)? _pendingUpdateInstaller;
    private NotificationRouter? _notificationRouter;
    private Selection.SelectionAssistantService? _selectionAssistant;
    private NotificationCenter? _notifications;
    private AppPowerModeCoordinator? _powerMode;
    private readonly List<IDisposable> _windowPowerSubscriptions = [];
    private long _lastBackgroundTrimTicks;
    private bool _backgroundMode;
    private bool _pendingBackgroundTrim;
    private volatile bool _shuttingDown;
    private IDisposable? _backgroundCheck;

    /// <summary>How long a window state has to hold before it counts. Sized
    /// against the ~1.5s Minimized→Normal→Minimized blip Windows emits on its
    /// own; a real restore just waits this long before leaving background mode.</summary>
    private static readonly TimeSpan BackgroundSettleDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BackgroundTrimCooldown = TimeSpan.FromMinutes(5);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            UrlSchemeRegistrar.EnsureRegistered();
            _services = AppServices.Build();
            _services.GetRequiredService<MolaGptDatabase>().EnsureSchema();
            Resources["ServerToolActions"] = _services.GetRequiredService<ServerToolActions>();
            CdnProxy.ConfigureProxy(_services.GetRequiredService<AppProxy>().General);
            _services.GetRequiredService<CredentialStore>().UpgradeEncryption();
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
            var piSidecar = _services.GetRequiredService<PiSidecarRuntimeManager>();
            var autoUpdate = _services.GetRequiredService<AppAutoUpdateService>();
            var notifications = _services.GetRequiredService<NotificationCenter>();
            var storage = _services.GetRequiredService<StorageService>();
            _notifications = notifications;

            if (!string.IsNullOrEmpty(auth.CurrentJwt)
                && !auth.IsJwtValidForUa(UserAgentProvider.FixedUa))
            {
                auth.Logout();
            }

            var accountSession = _services.GetRequiredService<AccountSessionCoordinator>();
            accountSession.Initialize();

            ApplyTheme(settings.ThemeMode);
            settings.ThemeModeChanged += (_, mode) => ApplyTheme(mode);
            CornerStyles.Apply(this, settings.CornerStyle);
            settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.CornerStyle))
                    CornerStyles.Apply(this, settings.CornerStyle);
            };

            // DiagnosticLog, not Debug.WriteLine: the previous sink was compiled out
            // of release builds, so on the machines where rows silently vanish from
            // the picker the reason was the one thing not written down anywhere.
            var providerRestorer = _services.GetRequiredService<ProviderRestorer>();
            var restored = providerRestorer.LoadSaved();
            if (restored.LostEverythingToRuntime)
                DiagnosticLog.Write("provider",
                    $"{restored.RuntimeUnavailable} 个已保存的服务全部未注册：Agent 运行环境不可用。"
                    + "模型选择器的本地侧会是空的。");

            main.EnsureConversationDetailAsync = cloudSync.FetchConversationToLocalAsync;
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
            {
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (status.SessionGeneration != auth.SessionGeneration) return;
                        main.UpdateCloudSyncStatus(status.State.ToString(), status.Message, status.Timestamp);
                        PublishCloudSyncNotification(notifications, main, status);
                    },
                    DispatcherPriority.Background);
            };
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

            var uiServices = _services;
            var httpClients = uiServices.GetRequiredService<IHttpClientFactory>();
            var agentRuntime = uiServices.GetRequiredService<AgentRuntimeSetup>();
            var remoteWorkSettings = uiServices.GetRequiredService<RemoteWorkSettingsViewModel>();
            var mcp = uiServices.GetRequiredService<McpClientManager>();
            void ReportHistorySaveFailure(string error)
            {
                DiagnosticLog.Write("storage", $"消息保存失败：{error}");
                notifications.Error("消息保存失败", error, "message-history");
            }
            void ReportMcpUnavailable(McpServerOptions server, string error) =>
                notifications.Warning($"MCP 服务器「{server.Name}」不可用", error, "mcp-" + server.Id);
            main.Chat.HistorySaveFailed += ReportHistorySaveFailure;
            mcp.ServerUnavailable += ReportMcpUnavailable;
            main.Composer.AutoStorySummaryAsync = async (conversationId, providerId, modelId, ct) =>
            {
                try
                {
                    if (providerId is null || modelId is null || providers.FindModel(providerId, modelId) is not { } target) return;
                    using var http = httpClients.CreateClient(HttpClientNames.Byok);
                    await main.Chat.AutoSummarizeAsync(conversationId, new OneShotCompletionClient(http),
                        target.Provider, target.Model, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex) { notifications.Error("剧情整理失败", ex.Message, "story-summary-" + conversationId); }
            };

            var window = new MainWindow(
                main,
                providers,
                updateCheck, auth, cloudSync,
                uiServices.GetRequiredService<AttachmentStore>(),
                uiServices.GetRequiredService<ImageWorkbenchTasks>(),
                uiServices.GetRequiredService<PythonRuntimeSetup>(),
                agentRuntime, notifications, httpClients,
                uiServices.GetRequiredService<SettingsWindow>,
                uiServices.GetRequiredService<LoginWindow>,
                remoteWorkSettings,
                uiServices.GetRequiredService<SponsorsService>(),
                uiServices.GetRequiredService<MembershipService>());
            var powerMode = _services.GetRequiredService<AppPowerModeCoordinator>();
            _powerMode = powerMode;
            desktop.MainWindow = window;
            _selectionAssistant = new Selection.SelectionAssistantService(settings, providers,
                httpClients, window, powerMode);

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
                    ? window.DisplayedImageTaskId
                    : main.Chat.ConversationId);
            restored.ReportFailures(notifications);

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
                if (_pendingUpdateInstaller is not { } installer) return;
                try
                {
                    AppAutoUpdateService.StartInstallerAfterExit(installer.Path, installer.Sha256);
                    desktop.TryShutdown();
                }
                catch (Exception ex)
                {
                    main.MarkUpdateFailed("无法启动更新安装：" + ex.Message);
                }
            };

            agentBridge.Start();
            agentStatus.InitializeBridgeEnabled(agentConfig.BridgeEnabled, settings.IsLoggedIn);
            agentStatus.ConfirmEnableAsync = async () =>
                await new BridgePrivacyWindow().ShowDialog<bool>(window);
            var agentRelaySync = new SemaphoreSlim(1, 1);
            var agentRelayRestartNeeded = false;
            agentStatus.ApplyBridgeEnabled = enabled =>
            {
                if (!enabled) Volatile.Write(ref agentRelayRestartNeeded, true);
                agentConfig.BridgeEnabled = enabled;
                SyncAgentRelay();
            };
            // The relay authenticates as the MolaGPT account. Run signed out, every
            // heartbeat and meta post it made threw and was retried on a timer.
            settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(SettingsViewModel.IsLoggedIn)) return;
                if (!settings.IsLoggedIn) Volatile.Write(ref agentRelayRestartNeeded, true);
                agentStatus.SetSignedIn(settings.IsLoggedIn);
                SyncAgentRelay();
            };
            SyncAgentRelay();

            void SyncAgentRelay() => _ = SyncAgentRelayAsync();

            async Task SyncAgentRelayAsync()
            {
                await agentRelaySync.WaitAsync();
                try
                {
                    if (_shuttingDown) return;
                    var forceRestart = Volatile.Read(ref agentRelayRestartNeeded);
                    Volatile.Write(ref agentRelayRestartNeeded, false);
                    var shouldRun = agentConfig.BridgeEnabled && settings.IsLoggedIn;
                    if (!shouldRun || forceRestart)
                        await StopAgentRelayAsync(shutdown: false);
                    if (shouldRun && !_shuttingDown)
                        StartAgentRelay();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write("AgentRelay", $"sync failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    agentRelaySync.Release();
                }
            }

            // Remote clients control MolaGPT conversations while switched on: over the account
            // relay while signed in, and over the account-less link always.
            var remoteWork = _services.GetRequiredService<RemoteControlHost>();
            remoteWork.ConfirmAccountPairingAsync = async device =>
            {
                powerMode.NotifyForegroundIntent("remote-pair-confirm");
                if (!window.IsVisible) window.Show();
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
                return await Confirm.AskAsync(window, "允许新设备配对？",
                    $"设备「{device}」正在申请查看并控制本机的 MolaGPT 对话。请确认这是你自己的设备。",
                    "允许配对");
            };
            var remoteSync = new SemaphoreSlim(1, 1);
            var remoteLifetime = new CancellationTokenSource();
            var remoteToken = remoteLifetime.Token;
            remoteWorkSettings.EnabledChanged += SyncRemoteWork;
            settings.PropertyChanged += OnRemoteAccountChanged;
            void OnRemoteAccountChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName != nameof(SettingsViewModel.IsLoggedIn)) return;
                remoteWork.Phones.AccountChanged();
                SyncRemoteWork();
                OfferRemoteWork();
            }
            // A code is typed at this desk, so its result shows on the settings page.
            // Same-account clients are approved locally; announce the new device here.
            remoteWork.Paired += OnRemotePaired;
            void OnRemotePaired(string device, bool viaAccount)
            {
                if (!viaAccount) return;
                Dispatcher.UIThread.Post(() => notifications.Notify(new AppNotification
                {
                    Key = "remote-pair",
                    Kind = NotifyKind.Info,
                    Title = "新设备已配对",
                    Body = $"「{device}」可查看和控制本机对话",
                    ActionText = "管理",
                    Action = () => window.OpenAgentSettings(),
                    Sticky = true
                }));
            }
            SyncRemoteWork();
            OfferRemoteWork();

            void SyncRemoteWork()
            {
                if (!remoteToken.IsCancellationRequested) _ = SyncRemoteWorkAsync();
            }

            async Task SyncRemoteWorkAsync()
            {
                try
                {
                    await remoteSync.WaitAsync(remoteToken);
                    try
                    {
                        remoteToken.ThrowIfCancellationRequested();
                        var enabled = remoteWorkSettings.IsEnabled;
                        if (enabled && settings.IsLoggedIn) remoteWork.Start();
                        else await remoteWork.StopAccountAsync();
                        if (enabled) await remoteWork.StartAnonymousAsync(remoteToken);
                        else await remoteWork.StopAnonymousAsync();
                    }
                    finally { remoteSync.Release(); }
                }
                catch (OperationCanceledException) when (remoteToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    DiagnosticLog.Write("RemoteWork", $"sync failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Signed in but never switched on: offer remote control once.
            void OfferRemoteWork()
            {
                if (!settings.IsLoggedIn || !settings.ShowAccountFeatures || remoteWorkSettings.IsEnabled) return;
                if (!remoteWorkSettings.TryClaimHint()) return;
                Dispatcher.UIThread.Post(() => notifications.Notify(new AppNotification
                {
                    Key = "remote-hint",
                    Kind = NotifyKind.Info,
                    Title = "在手机或网页端继续对话",
                    Body = "开启后，可在手机或网页端通过同一账号继续本机的 Work 和 BYOK 对话。",
                    ActionText = "去开启",
                    Action = () => window.OpenAgentSettings(),
                    Sticky = true
                }));
            }

            // Tray close behavior may hide the main window, so shutdown stays
            // explicit and the tray host owns the final decision.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            _tray = new TrayIconHost(settings, onWake: () => powerMode.NotifyForegroundIntent("tray-restore"));
            _tray.Attach(window);
            _tray.SettingsRequested += (_, _) =>
            {
                powerMode.NotifyForegroundIntent("tray-settings");
                window.OpenSettings();
            };
            _tray.AgentStatusRequested += (_, _) =>
            {
                powerMode.NotifyForegroundIntent("tray-agent");
                window.OpenAgentSettings();
            };
            window.PropertyChanged += OnMainWindowPropertyChanged;
            _windowPowerSubscriptions.Add(Visual.IsVisibleProperty.Changed.AddClassHandler<Window>(
                (_, _) => RefreshWindowPowerState(desktop, "window-visible")));
            _windowPowerSubscriptions.Add(Window.WindowStateProperty.Changed.AddClassHandler<Window>(
                (_, _) => RefreshWindowPowerState(desktop, "window-state")));
            _windowPowerSubscriptions.Add(WindowBase.IsActiveProperty.Changed.AddClassHandler<Window>(
                (activeWindow, e) =>
                {
                    if (e.GetNewValue<bool>() && IsVisibleUnminimized(activeWindow))
                        powerMode.NotifyForegroundIntent("window-activated");
                }));
            powerMode.EnteredBackgroundIdle += OnPowerModeEnteredBackgroundIdle;
            RefreshWindowPowerState(desktop, "startup");

            SingleInstanceHost.Attach(deepLink => Dispatcher.UIThread.Post(() =>
            {
                BringToFront(window);
                if (!string.IsNullOrEmpty(deepLink)) _ = HandleOAuthDeepLinkAsync(deepLink, window);
            }));

            var maintenanceLifetime = new CancellationTokenSource();
            var maintenanceToken = maintenanceLifetime.Token;
            cloudSync.StartPeriodicSync();
            _ = RunStartupCloudSyncAsync(cloudSync, main.ConversationList, maintenanceToken);
            var startupStorage = Task.Run(() =>
            {
                try { storage.StartupMaintenance(); }
                catch (Exception ex) { DiagnosticLog.Write("storage", "启动清理失败：" + ex.Message); }
            });
            _ = RunStorageMaintenanceAsync(storage, startupStorage, maintenanceToken);
            var piSweepCutoff = DateTime.UtcNow;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(45), maintenanceToken).ConfigureAwait(false);
                SweepOrphanedPiSessions(piSweepCutoff, maintenanceToken);
            }, maintenanceToken);
            _ = Task.Run(SweepOrphanedAttachments);
            _ = Task.Run(CatchUpMemoryIndex);
            WireMemoryNotifications(notifications);
            WireCommunityPrompts(
                _services.GetRequiredService<BackgroundStreamService>(),
                settings,
                notifications,
                window);
            Dispatcher.UIThread.Post(
                () => _ = RunStartupAccountRefreshAsync(
                    auth, providers, proxy, localTools, window),
                DispatcherPriority.Background);
            _ = RunUpdateCheckAsync(main, updateCheck, notifications);
            _ = RunAgentRuntimeUpdateAfterStorageAsync(piSidecar, startupStorage,
                () => agentRuntime.OfferInstall(window.OpenProviderSettings), maintenanceToken);

            desktop.ShutdownRequested += (_, _) =>
            {
                if (_shuttingDown) return;
                _shuttingDown = true;
                remoteWorkSettings.EnabledChanged -= SyncRemoteWork;
                settings.PropertyChanged -= OnRemoteAccountChanged;
                remoteWork.Paired -= OnRemotePaired;
                remoteWork.ConfirmAccountPairingAsync = null;
                remoteLifetime.Cancel();
                maintenanceLifetime.Cancel();
                window.PropertyChanged -= OnMainWindowPropertyChanged;
                if (_powerMode is { } pm) pm.EnteredBackgroundIdle -= OnPowerModeEnteredBackgroundIdle;
                foreach (var sub in _windowPowerSubscriptions) sub.Dispose();
                _windowPowerSubscriptions.Clear();
                main.Chat.HistorySaveFailed -= ReportHistorySaveFailure;
                mcp.ServerUnavailable -= ReportMcpUnavailable;
                _selectionAssistant?.Dispose();
                // ShutdownRequested is synchronous, including Windows sign-out.
                // Drain captured writes before disposing their services.
                try { main.Composer.SaveInProgressMessagesAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { DiagnosticLog.Write("storage", $"退出时保存消息失败：{ex.Message}"); }
                _backgroundCheck?.Dispose();
                cloudSync.StopPeriodicSync();
                _notificationRouter?.Dispose();
                try { StopAgentRelayAsync(shutdown: true).GetAwaiter().GetResult(); }
                catch (Exception ex) { DiagnosticLog.Write("AgentRelay", "退出时停止中继失败：" + ex.Message); }
                _tray?.Dispose();
                _powerMode = null;
                var services = _services;
                _services = null;
                try { services?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                finally
                {
                    remoteLifetime.Dispose();
                    maintenanceLifetime.Dispose();
                }
            };

            // Off the dispatcher: the sidebar read is the first thing that
            // touches SQLite, and doing it inline delays the first frame.
            _ = main.ConversationList.ReloadAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunAgentRuntimeUpdateAfterStorageAsync(
        PiSidecarRuntimeManager piSidecar, Task startupStorage, Action announce, CancellationToken ct)
    {
        try
        {
            await startupStorage.WaitAsync(ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (piSidecar.GetCompatibleInstalled() is null || !await piSidecar.IsUpdateAvailableAsync(ct).ConfigureAwait(false))
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!ct.IsCancellationRequested && piSidecar.GetCompatibleInstalled() is not null)
                    announce();
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static async Task RunStorageMaintenanceAsync(StorageService storage, Task startup, CancellationToken ct)
    {
        try
        {
            await startup.ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
            do
            {
                try
                {
                    var result = await Task.Run(() => storage.PurgeDeletedAsync(TimeSpan.FromDays(7), compact: true, ct), ct).ConfigureAwait(false);
                    if (result.CompactError is not null) DiagnosticLog.Write("storage", "数据库整理未完成：" + result.CompactError);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { DiagnosticLog.Write("storage", "自动清理已删除对话失败：" + ex.Message); }
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static async Task RunStartupAccountRefreshAsync(
        MolaGptAuthService auth,
        ProviderRegistry providers,
        MolaGptProxyProvider proxy,
        MolaGptLocalToolsRegistrar localTools,
        MainWindow window)
    {
        var session = auth.ReadSession();
        if (!session.IsLoggedIn) return;

        try
        {
            await proxy.RefreshModelsAsync();
        }
        catch
        {
            // Keep the stored account available while offline. The next login or
            // account action can refresh the model list when the network returns.
        }

        if (session.Generation != auth.SessionGeneration) return;
        providers.Register(proxy);
        // Work disappearing from the picker used to leave nothing behind at all.
        // Still non-fatal — Chat must come up either way — but it is written down.
        try { await localTools.RefreshAsync(); }
        catch (Exception ex) { DiagnosticLog.Write("pi-work", "启动时刷新 Work 失败：" + ex.Message); }

        if (session.Generation == auth.SessionGeneration)
            window.CompleteAccountLogin();
    }

    private async Task BeginBackgroundUpdateDownloadAsync(
        MainViewModel main,
        AppAutoUpdateService autoUpdate,
        AppAutoUpdateService.UpdatePackage package)
    {
        if (main.UpdateState == "Downloading") return;

        using var updateLease = _powerMode?.Acquire("app-update-download");
        main.BeginUpdateDownload();
        _notifications?.Progress(UpdateNotificationKey, $"正在下载 {package.Version}", "0%", 0);

        try
        {
            // Reported per chunk; the banner only redraws when the whole
            // percent moves, which is the most a 344px card can show anyway.
            var lastPercent = int.MinValue;
            var progress = new Progress<double?>(value =>
            {
                main.ReportUpdateDownloadProgress(value);

                var percent = value is { } fraction ? Math.Clamp((int)(fraction * 100), 0, 100) : -1;
                if (percent == lastPercent) return;
                lastPercent = percent;
                _notifications?.Progress(
                    UpdateNotificationKey, $"正在下载 {package.Version}", percent < 0 ? null : $"{percent}%", value);
            });

            _pendingUpdateInstaller = (await autoUpdate.DownloadAndVerifyAsync(package, progress), package.Sha256.Trim());
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
        ConversationListViewModel conversations,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            // Nobody asked for this one, so it stays silent unless it fails.
            await cloudSync.RequestForegroundSyncAsync(ct, userInitiated: false).ConfigureAwait(false);
            if (!ct.IsCancellationRequested) await conversations.ReloadAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void SweepOrphanedPiSessions(DateTime cutoff, CancellationToken ct)
    {
        if (_services is null || ct.IsCancellationRequested) return;
        try
        {
            var live = _services.GetRequiredService<ConversationRepository>()
                .ListAll()
                .Select(conversation => conversation.Id)
                .ToArray();
            var children = _services.GetRequiredService<MessageRepository>().ListSubagentSessionKeys();
            if (children is null)
            {
                DiagnosticLog.Write("pi-work", "子会话引用扫描不完整，跳过文件清理。");
                return;
            }
            if (ct.IsCancellationRequested) return;
            var removed = new PiWorkSessionSweeper(PiWorkSidecarLocator.SessionRoot).Sweep(live, children, cutoff, ct);
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

    private static void WireCommunityPrompts(
        BackgroundStreamService streams,
        SettingsViewModel settings,
        NotificationCenter notifications,
        MainWindow window)
    {
        streams.TaskCompleted += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var count = settings.CompletedTurnCount + 1;
            settings.CompletedTurnCount = count;

            if (!window.IsActive || !window.IsVisible || window.WindowState == WindowState.Minimized)
                return;

            if (count >= 5 && !settings.GitHubStarPromptShown)
            {
                settings.GitHubStarPromptShown = true;
                notifications.Notify(new AppNotification
                {
                    Key = CommunityNotificationKey,
                    Kind = NotifyKind.Info,
                    Title = "觉得 MolaGPT Desktop 好用？",
                    Body = "欢迎在 GitHub 点亮 Star，让更多人发现这个开源项目。",
                    ActionText = "前往 Star",
                    Action = () => CommunityLinks.TryOpenBrowser(CommunityLinks.GitHubUrl),
                    Sticky = true
                });
                return;
            }

            if (count >= 15 && !settings.AfdianSupportPromptShown)
            {
                settings.AfdianSupportPromptShown = true;
                if (settings.IsDonor) return;
                notifications.Notify(new AppNotification
                {
                    Key = CommunityNotificationKey,
                    Kind = NotifyKind.Info,
                    Title = "支持 MolaGPT 持续维护",
                    Body = "支持项目可解锁更多模型与额度。",
                    ActionText = "查看支持方案",
                    Action = () => window.OpenSupport(),
                    Sticky = true
                });
            }
        });
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
            || !string.Equals(uri.Scheme, UrlSchemeRegistrar.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "oauth_callback", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Port != -1 || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath.Length != 0 && uri.AbsolutePath != "/")
            || !LoginWindow.HasPendingExternalLogin)
        {
            return;
        }

        string? code = null;
        string? state = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0) continue;
            var key = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            if (key == "code")
            {
                if (code is not null) return;
                code = value;
            }
            else if (key == "state")
            {
                if (state is not null) return;
                state = value;
            }
        }

        var auth = _services.GetRequiredService<MolaGptAuthService>();
        if (!string.IsNullOrEmpty(code))
        {
            LoginResult result;
            try
            {
                result = await auth.ExchangeOAuthCodeAsync(code, state);
            }
            catch (Exception ex)
            {
                LoginWindow.NotifyExternalLoginCompleted(false, $"第三方登录失败：{ex.Message}");
                return;
            }
            if (!result.Success)
            {
                LoginWindow.NotifyExternalLoginCompleted(false, result.ErrorMessage ?? "授权码兑换失败，请重新登录。", result.AppealToken);
                return;
            }
        }
        else
        {
            return;
        }

        var session = auth.ReadSession();
        try
        {
            var proxy = _services.GetRequiredService<MolaGptProxyProvider>();
            await proxy.RefreshModelsAsync();
            if (session.Generation != auth.SessionGeneration) return;
            _services.GetRequiredService<ProviderRegistry>().Register(proxy);
            try { await _services.GetRequiredService<MolaGptLocalToolsRegistrar>().RefreshAsync(); }
            catch { }
        }
        catch
        {
            // The token is already stored; model discovery will retry on the next
            // account action or application start.
        }

        if (session.Generation != auth.SessionGeneration || !session.IsLoggedIn)
        {
            LoginWindow.NotifyExternalLoginCompleted(false, "登录状态已变化，请重新登录。");
            return;
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
            var background = !IsVisibleUnminimized(window);
            if (_backgroundMode == background) return;
            _backgroundMode = background;
            if (background) EnterBackground();
            else ExitBackground();
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                RefreshWindowPowerState(desktop, "main-window-settled");
        }, BackgroundSettleDelay);
    }

    private static bool IsVisibleUnminimized(Window window) =>
        window.IsVisible && window.WindowState != WindowState.Minimized;

    private void RefreshWindowPowerState(IClassicDesktopStyleApplicationLifetime desktop, string reason)
    {
        // The main window can report Normal before its restore has settled.
        // A real activation and a visible secondary window still promote QoS immediately.
        var hasVisible = false;
        foreach (var window in desktop.Windows)
        {
            if (!IsVisibleUnminimized(window)) continue;
            if (ReferenceEquals(window, desktop.MainWindow) && _backgroundMode && !window.IsActive) continue;
            hasVisible = true;
            break;
        }
        _powerMode?.SetHasVisibleWindow(hasVisible, reason);
    }

    private void EnterBackground()
    {
        _services?.GetRequiredService<AgentBridgeService>().SetBackgroundMode(true);
        _services?.GetRequiredService<PiRuntime>().SetBackgroundMode(true);
        _services?.GetRequiredService<CloudSyncService>().SetBackgroundMode(true);

        _pendingBackgroundTrim = true;
        if (_powerMode is null)
        {
            TryExecuteBackgroundTrim(expectedIdleGeneration: null);
            return;
        }
        if (_powerMode.IsIdleAtGeneration(out var idleGen))
        {
            TryExecuteBackgroundTrim(idleGen);
        }
    }

    private void OnPowerModeEnteredBackgroundIdle(long idleGeneration)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_backgroundMode || !_pendingBackgroundTrim) return;
            if (_powerMode is null || !_powerMode.MatchesIdleGeneration(idleGeneration)) return;
            TryExecuteBackgroundTrim(idleGeneration);
        });
    }

    private void TryExecuteBackgroundTrim(long? expectedIdleGeneration)
    {
        if (!_pendingBackgroundTrim) return;
        if (_powerMode is { HasActiveWork: true }) return;
        if (expectedIdleGeneration is { } gen && _powerMode is not null && !_powerMode.MatchesIdleGeneration(gen)) return;

        _pendingBackgroundTrim = false;

        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref _lastBackgroundTrimTicks) < BackgroundTrimCooldown.Ticks) return;
        Interlocked.Exchange(ref _lastBackgroundTrimTicks, now);

        CodeHighlighter.TrimForBackground();
        ImageSourceLoader.TrimForBackground();
        _ = TrimGraphicsForBackgroundAsync();

        // 每次切入后台最多执行一次，驻留后台期间不再重复，跨周期至少间隔 5 分钟。
        var powerMode = _powerMode;
        _ = Task.Run(() =>
        {
            if (powerMode is not null)
            {
                if (expectedIdleGeneration is { } idleGen)
                {
                    if (!powerMode.MatchesIdleGeneration(idleGen)) return;
                }
                else if (powerMode.HasActiveWork)
                {
                    return;
                }
            }
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive,
                blocking: true, compacting: true);
        });
    }

    private async Task TrimGraphicsForBackgroundAsync()
    {
        try
        {
            var compositor = Compositor.TryGetDefaultCompositor();
            if (compositor is null) return;
            var feature = await compositor.TryGetRenderInterfaceFeature(
                typeof(IOpenGlTextureSharingRenderInterfaceContextFeature));
            if (!_backgroundMode || feature is not IPlatformGraphicsContext graphics) return;

            using var current = graphics.EnsureCurrent();
            using var process = Process.GetCurrentProcess();
            var angle = process.Modules.Cast<ProcessModule>().FirstOrDefault(module =>
                string.Equals(module.ModuleName, "av_libglesv2.dll", StringComparison.OrdinalIgnoreCase));
            if (angle is null) return;

            var getProc = Marshal.GetDelegateForFunctionPointer<EglGetProcAddress>(
                NativeLibrary.GetExport(angle.BaseAddress, "EGL_GetProcAddress"));
            var getDisplayAddress = getProc("eglGetCurrentDisplay");
            var queryDisplayAddress = getProc("eglQueryDisplayAttribEXT");
            var queryDeviceAddress = getProc("eglQueryDeviceAttribEXT");
            if (getDisplayAddress == 0 || queryDisplayAddress == 0 || queryDeviceAddress == 0) return;
            var display = Marshal.GetDelegateForFunctionPointer<EglGetCurrentDisplay>(getDisplayAddress)();
            var queryDisplay = Marshal.GetDelegateForFunctionPointer<EglQueryAttribute>(queryDisplayAddress);
            var queryDevice = Marshal.GetDelegateForFunctionPointer<EglQueryAttribute>(queryDeviceAddress);
            if (queryDisplay(display, 0x322C, out var eglDevice) == 0
                || queryDevice(eglDevice, 0x33A1, out var d3dDevice) == 0 || d3dDevice == 0) return;

            var iid = new Guid("6007896c-3244-4afd-bf18-a6d3beda5023");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in iid, out var dxgiDevice));
            try
            {
                // IDXGIDevice3::Trim follows the 17 inherited COM methods.
                var trim = Marshal.GetDelegateForFunctionPointer<DxgiTrimDevice>(
                    Marshal.ReadIntPtr(Marshal.ReadIntPtr(dxgiDevice), 17 * IntPtr.Size));
                trim(dxgiDevice);
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("rendering", "后台图形缓存回收失败：" + ex.Message);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint EglGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint EglGetCurrentDisplay();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EglQueryAttribute(nint handle, int attribute, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DxgiTrimDevice(nint device);

    private void ExitBackground()
    {
        _pendingBackgroundTrim = false;
        _services?.GetRequiredService<AgentBridgeService>().SetBackgroundMode(false);
        _services?.GetRequiredService<PiRuntime>().SetBackgroundMode(false);
        _services?.GetRequiredService<CloudSyncService>().SetBackgroundMode(false);
    }

    private void BringToFront(Window window)
    {
        _powerMode?.NotifyForegroundIntent("bring-to-front");
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!window.IsVisible) window.Show();
        window.Activate();
    }

    private void StartAgentRelay()
    {
        if (_shuttingDown || _agentRelayTask is not null || _services is null) return;

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

    private async Task StopAgentRelayAsync(bool shutdown = false)
    {
        var cts = _agentRelayCts;
        var task = _agentRelayTask;
        if (shutdown)
        {
            _shuttingDown = true;
            _agentRelayCts = null;
            _agentRelayTask = null;
        }

        if (cts is null && task is null) return;

        cts?.Cancel();
        if (_services?.GetService<AgentRelayClient>() is { } relay)
        {
            using var offlineCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await relay.StopAsync(offlineCts.Token).ConfigureAwait(false);
        }

        if (task is not null)
        {
            if (shutdown)
            {
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            }
            else
            {
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { DiagnosticLog.Write("AgentRelay", $"stop error: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        if (!shutdown)
        {
            if (ReferenceEquals(_agentRelayCts, cts)) _agentRelayCts = null;
            if (ReferenceEquals(_agentRelayTask, task)) _agentRelayTask = null;
        }
        if (task is null || task.IsCompleted)
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
