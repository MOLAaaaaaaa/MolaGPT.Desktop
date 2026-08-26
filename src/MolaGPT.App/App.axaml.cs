using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MolaGPT.App.Infrastructure;
using MolaGPT.App.Views;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Agents.Relay;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Chat.Tools;
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
    private string? _pendingUpdateInstallerPath;

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

            ProviderRestorer.Restore(_services, line => Debug.WriteLine(line));

            main.EnsureConversationDetailAsync = id => cloudSync.FetchConversationToLocalAsync(id);
            main.Composer.ConversationCompletedAsync = cloudSync.CompleteConversationTurnAsync;
            main.Composer.LocalConversationTitleAsync = (conversationId, providerId, modelId, ct) =>
                _services.GetRequiredService<ConversationTitleService>()
                    .GenerateAsync(conversationId, providerId, modelId, ct);

            cloudSync.LocalConversationsChanged += (_, _) =>
                Dispatcher.UIThread.Post(() => _ = main.ConversationList.ReloadAsync(), DispatcherPriority.Background);
            cloudSync.StatusChanged += (_, status) =>
                Dispatcher.UIThread.Post(
                    () => main.UpdateCloudSyncStatus(status.State.ToString(), status.Message, status.Timestamp),
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
                _services.GetRequiredService<AppStatusService>(),
                _services.GetRequiredService<SkillsViewModel>(),
                 _services.GetRequiredService<IHttpClientFactory>(),
                 _services.GetRequiredService<IChatToolHost>(),
                 _services.GetRequiredService<PiByokProviderFactory>(),
                 _services.GetRequiredService<AppNotificationService>());
            desktop.MainWindow = window;

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

            SingleInstanceHost.Attach(deepLink => Dispatcher.UIThread.Post(() =>
            {
                BringToFront(window);
                if (!string.IsNullOrEmpty(deepLink)) _ = HandleOAuthDeepLinkAsync(deepLink, window);
            }));

            cloudSync.StartPeriodicSync();
            _ = RunStartupCloudSyncAsync(cloudSync, main.ConversationList);
            _ = Task.Run(SweepOrphanedPiSessions);
            _ = Task.Run(SweepOrphanedAttachments);
            Dispatcher.UIThread.Post(
                () => _ = RunStartupAccountRefreshAsync(
                    auth, providers, proxy, localTools, accountSession, window),
                DispatcherPriority.Background);
            _ = RunUpdateCheckAsync(main, updateCheck);

            desktop.ShutdownRequested += (_, _) =>
            {
                cloudSync.StopPeriodicSync();
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
        try { await localTools.RefreshAsync(); }
        catch { }

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
        try
        {
            var progress = new Progress<double>(main.ReportUpdateDownloadProgress);
            _pendingUpdateInstallerPath = await autoUpdate.DownloadAndVerifyAsync(package, progress);
            await Dispatcher.UIThread.InvokeAsync(main.MarkUpdateReady);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => main.MarkUpdateFailed("更新下载失败：" + ex.Message));
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

    private static async Task RunUpdateCheckAsync(MainViewModel main, UpdateCheckService updateCheck)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        var info = await updateCheck.CheckAsync();
        if (info is null) return;

        Dispatcher.UIThread.Post(() => main.AnnounceUpdate(
            info.LatestVersion,
            info.DownloadUrl,
            info.Notes,
            info.ActionText,
            info.InstallerSha256));
    }

    private static async Task RunStartupCloudSyncAsync(
        CloudSyncService cloudSync,
        ConversationListViewModel conversations)
    {
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await cloudSync.RequestForegroundSyncAsync().ConfigureAwait(false);
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
