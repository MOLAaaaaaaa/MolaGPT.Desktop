using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MolaGPT.Desktop.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Chat.Tools.Mcp;
using MolaGPT.Core.Chat.Tools.Vision;
using MolaGPT.Core.Models;
using MolaGPT.Desktop.Services;
using MolaGPT.Desktop.Views;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.Desktop;

public partial class App : Application
{
    public IHost? Host { get; private set; }
    public static IServiceProvider Services => ((App)Current).Host!.Services;

    public const string MolaGptHttpClient = "molagpt";
    public const string ByokHttpClient = "byok";

    private ResourceDictionary? _activeTheme;
    private string _currentThemeKey = "Light";
    private ThemeMode _themePreference = ThemeMode.System;
    private static bool s_languageMetadataApplied;
    private static bool s_focusVisualHandlerRegistered;
    private CancellationTokenSource? _cloudStatusHideCts;
    private string? _pendingUpdateInstallerPath;
    private bool _installingUpdateWithRestart;

    protected override async void OnStartup(StartupEventArgs e)
    {
        DiagnosticLog.Write("App", $"OnStartup pid={Environment.ProcessId} args=[{string.Join(" | ", e.Args)}]");

        // Single-instance guard runs before anything else: a second launch
        // forwards its argv (typically the molagpt:// deep link from the
        // OAuth handoff) to the running process, then exits. Shutdown(0)
        // here keeps OnExit's Release() a no-op since the mutex was never
        // acquired.
        if (!SingleInstanceGuard.TryAcquire(e.Args))
        {
            DiagnosticLog.Write("App", "second instance — exiting after forward");
            Shutdown(0);
            return;
        }

        // Register the molagpt:// URL scheme on every startup so it stays
        // pointed at the current install path even after upgrades.
        UrlSchemeRegistrar.EnsureRegistered();

        ApplyDefaultLanguage();
        DisableDefaultFocusVisuals();
        base.OnStartup(e);

        Host = Microsoft.Extensions.Hosting.Host
            .CreateDefaultBuilder()
            .ConfigureServices((_, services) => ConfigureServices(services))
            .Build();

        await Host.StartAsync();

        Services.GetRequiredService<MolaGptDatabase>().EnsureSchema();

        // Seed built-in personas on first run (idempotent — no-op when any
        // persona already exists). Must run after EnsureSchema so the
        // personas table is created, and before any view reads PersonaList.
        Services.GetRequiredService<PersonaListViewModel>().EnsureBuiltinsSeeded();

        var registry = Services.GetRequiredService<ProviderRegistry>();

        RestoreSavedProviders();
        var logoutCoordinator = Services.GetRequiredService<MolaGptLogoutCoordinator>();

        // Validate the persisted JWT against the current UA hash. If they
        // disagree (e.g. we shipped a new app version with a different UA),
        // wipe the token so the user gets prompted to re-login on demand
        // instead of hitting a guaranteed-401 wall on every chat send.
        var auth = Services.GetRequiredService<MolaGptAuthService>();
        if (!string.IsNullOrEmpty(auth.CurrentJwt) && !auth.IsJwtValidForUa(UserAgentProvider.FixedUa))
        {
            auth.Logout();
        }

        if (!string.IsNullOrEmpty(auth.CurrentJwt))
        {
            var proxy = Services.GetRequiredService<MolaGptProxyProvider>();
            try { await proxy.RefreshModelsAsync(); }
            catch (MolaGptAuthExpiredException) { auth.Logout(); /* will not register proxy */ }
            catch { /* offline / WAF — defer registration to when the user retries */ }

            if (!string.IsNullOrEmpty(auth.CurrentJwt))
                registry.Register(proxy);
        }

        if (string.IsNullOrEmpty(auth.CurrentJwt))
            logoutCoordinator.CleanupLoggedOutAccountState("startup-no-jwt");

        var window = Services.GetRequiredService<MainWindow>();
        var mainVm = Services.GetRequiredService<MainViewModel>();
        var cloudSync = Services.GetRequiredService<CloudSyncService>();
        var composerVm = Services.GetRequiredService<ComposerViewModel>();
        var conversationListVm = Services.GetRequiredService<ConversationListViewModel>();
        var settingsVm = Services.GetRequiredService<SettingsViewModel>();

        // Wire theme preference: settings VM holds the user's choice, and we
        // own the actual ResourceDictionary swap. Apply once before showing
        // the window so the first frame paints in the correct theme.
        _themePreference = settingsVm.ThemeMode;
        ApplyTheme(ResolveThemeKey(_themePreference));
        settingsVm.ThemeModeChanged += (_, mode) =>
        {
            _themePreference = mode;
            Dispatcher.InvokeAsync(() => ApplyTheme(ResolveThemeKey(mode)));
        };
        SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
        mainVm.EnsureConversationDetailAsync = id => cloudSync.FetchConversationToLocalAsync(id);
        composerVm.ConversationCompletedAsync = cloudSync.CompleteConversationTurnAsync;
        cloudSync.LocalConversationsChanged += (_, _) =>
        {
            _ = Dispatcher.InvokeAsync(
                () => { _ = conversationListVm.ReloadAsync(); },
                DispatcherPriority.Background);
        };
        cloudSync.StatusChanged += (_, status) =>
        {
            Dispatcher.InvokeAsync(() => mainVm.UpdateCloudSyncStatus(
                status.State.ToString(),
                status.Message,
                status.Timestamp), DispatcherPriority.Background);
            if (status.State is CloudSyncState.Success or CloudSyncState.Error)
                ScheduleCloudStatusHide(mainVm);
            else if (status.State is CloudSyncState.Idle or CloudSyncState.Disabled)
                Dispatcher.InvokeAsync(mainVm.HideCloudSyncStatus, DispatcherPriority.Background);
        };
        mainVm.CloudSyncRequested = async () =>
        {
            var progress = new Progress<string>(message =>
                mainVm.UpdateCloudSyncStatus("Syncing", message, DateTimeOffset.Now));
            try
            {
                var result = await cloudSync.SyncAsync(progress);
                if (ShouldReloadConversationsAfterSync(result))
                    await Services.GetRequiredService<ConversationListViewModel>().ReloadAsync();
            }
            catch
            {
                // CloudSyncService already published the user-facing status.
            }
        };
        conversationListVm.ConversationsDeleted += async (_, ids) =>
        {
            try { await cloudSync.PushDeletedConversationsAsync(ids); }
            catch { /* status event carries failures; keep local delete intact */ }
        };
        mainVm.LoginRequested = () =>
        {
            // Logged-in users see the account panel; guests see the login modal.
            // Re-evaluated on every click so logout in AccountDialog
            // immediately surfaces the LoginDialog next time.
            var auth = Services.GetRequiredService<MolaGptAuthService>();
            if (!string.IsNullOrEmpty(auth.CurrentJwt))
            {
                var dlg = Services.GetRequiredService<AccountDialog>();
                dlg.Owner = window;
                dlg.ShowDialog();
            }
            else
            {
                var dlg = Services.GetRequiredService<LoginDialog>();
                dlg.Owner = window;
                if (dlg.ShowDialog() == true)
                    _ = RunCloudSyncAndReloadAsync(cloudSync, conversationListVm);
            }
        };
        mainVm.SettingsRequested = () =>
        {
            var dlg = Services.GetRequiredService<SettingsWindow>();
            dlg.Owner = window;
            var settingsRequest = mainVm.ConsumeSettingsOpenRequest();
            if (settingsRequest.OpenPersonas)
                dlg.OpenPersonasTab(settingsRequest.StartNewPersona);
            dlg.ShowDialog();
        };
        mainVm.AboutRequested = () =>
        {
            var dlg = Services.GetRequiredService<AboutWindow>();
            dlg.Owner = window;
            dlg.ShowDialog();
        };
        mainVm.ThemeToggleRequested = () =>
        {
            // The header toggle is a simple Light↔Dark flip; routing it through
            // the settings VM ensures the persisted preference and the radio
            // buttons in the settings window stay in sync.
            var settingsVm = Services.GetRequiredService<SettingsViewModel>();
            settingsVm.ThemeMode = _currentThemeKey == "Light" ? ThemeMode.Dark : ThemeMode.Light;
        };
        mainVm.SystemPromptRequested = () =>
        {
            if (!mainVm.ConversationSystemPromptVisible)
                return;

            var chatVm = Services.GetRequiredService<ChatViewModel>();
            var dlg = new SystemPromptDialog();
            // Persistence happens inside the dialog's Save click — the dialog
            // now owns the full conversation prompt + mode write-back rather
            // than just returning a string for the caller to handle.
            dlg.Show(chatVm, window);
        };
        mainVm.ImageWorkbenchRequested = conversationId =>
        {
            var workbench = new ImageGenerationWorkbenchWindow(
                Services.GetRequiredService<SettingsViewModel>(),
                Services.GetRequiredService<ImageGenerationTool>(),
                Services.GetRequiredService<AttachmentStore>(),
                Services.GetRequiredService<ConversationRepository>(),
                Services.GetRequiredService<MessageRepository>(),
                conversationId,
                (title, modelId) => conversationListVm.CreateImageWorkbenchConversation(title, modelId),
                Services.GetRequiredService<NotificationService>(),
                (id, generating) => conversationListVm.SetGenerating(id, generating),
                window.HideImageWorkbench);
            window.ShowImageWorkbench(workbench);
        };

        window.DataContext = mainVm;
        window.Show();
        SingleInstanceGuard.AttachActivator(window);
        SingleInstanceGuard.DeepLinkReceived += url =>
            Dispatcher.InvokeAsync(() => HandleOAuthDeepLinkAsync(url));
        DiagnosticLog.Write("App", "DeepLinkReceived subscribed");

        // The deep link may also arrive on the very first launch — Windows
        // hands the molagpt://... URL to argv when the scheme handler
        // bootstraps the process. Funnel it through the same path.
        var initialDeepLink = SingleInstanceGuard.ExtractDeepLink(e.Args);
        if (!string.IsNullOrEmpty(initialDeepLink))
        {
            DiagnosticLog.Write("App", "argv contains deep link — dispatching");
            _ = Dispatcher.InvokeAsync(() => HandleOAuthDeepLinkAsync(initialDeepLink));
        }

        // Initialize notification service (must be after window is shown so
        // toast click can activate the window).
        var notificationService = Services.GetRequiredService<NotificationService>();
        _ = notificationService; // keep alive via DI singleton

        cloudSync.StartPeriodicSync();
        _ = RunStartupCloudSyncAfterFirstPaintAsync(cloudSync, conversationListVm);

        mainVm.UpdateActionRequested = (version, notes, downloadUrl, actionText, installerSha256) =>
        {
            var autoUpdate = Services.GetRequiredService<AutoUpdateService>();
            var dlg = new UpdateDialog(
                version,
                notes,
                downloadUrl,
                actionText,
                installerSha256: installerSha256,
                autoUpdate: autoUpdate,
                backgroundDownloadRequested: package => BeginBackgroundUpdateDownloadAsync(mainVm, autoUpdate, package))
            { Owner = window };
            dlg.ShowDialog();
        };
        mainVm.UpdateBackgroundDownloadRequested = () =>
        {
            if (!TryCreateUpdatePackage(mainVm, out var package))
                return Task.CompletedTask;
            return BeginBackgroundUpdateDownloadAsync(
                mainVm,
                Services.GetRequiredService<AutoUpdateService>(),
                package);
        };
        mainVm.UpdateInstallReadyRequested = () =>
        {
            if (string.IsNullOrWhiteSpace(_pendingUpdateInstallerPath))
                return;
            _installingUpdateWithRestart = true;
            Services.GetRequiredService<AutoUpdateService>()
                .InstallAfterExitAndRestart(_pendingUpdateInstallerPath);
        };
        _ = RunUpdateCheckAsync(mainVm);
    }

    private async Task BeginBackgroundUpdateDownloadAsync(
        MainViewModel mainVm,
        AutoUpdateService autoUpdate,
        AutoUpdateService.UpdatePackage package)
    {
        if (mainVm.UpdateState == "Downloading")
            return;

        mainVm.BeginUpdateDownload();
        try
        {
            var progress = new Progress<double>(mainVm.ReportUpdateDownloadProgress);
            _pendingUpdateInstallerPath = await autoUpdate.DownloadAndVerifyAsync(package, progress)
                .ConfigureAwait(false);
            await Dispatcher.InvokeAsync(mainVm.MarkUpdateReady);
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => mainVm.MarkUpdateFailed("更新下载失败：" + ex.Message));
        }
    }

    private static bool TryCreateUpdatePackage(
        MainViewModel mainVm,
        out AutoUpdateService.UpdatePackage package)
    {
        package = default!;
        if (string.IsNullOrWhiteSpace(mainVm.UpdateLatestVersion)
            || string.IsNullOrWhiteSpace(mainVm.UpdateDownloadUrl)
            || string.IsNullOrWhiteSpace(mainVm.UpdateInstallerSha256)
            || !Uri.TryCreate(mainVm.UpdateDownloadUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        package = new AutoUpdateService.UpdatePackage(
            mainVm.UpdateLatestVersion,
            mainVm.UpdateDownloadUrl,
            mainVm.UpdateInstallerSha256,
            Path.GetFileName(uri.LocalPath));
        return true;
    }

    private async Task RunUpdateCheckAsync(MolaGPT.ViewModels.MainViewModel mainVm)
    {
        // Stagger the check so it doesn't compete with cloud sync for
        // bandwidth on launch. A few seconds is plenty.
        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (Dispatcher.HasShutdownStarted) return;
        try
        {
            var info = await Services.GetRequiredService<UpdateCheckService>().CheckAsync().ConfigureAwait(false);
            if (info is null) return;
            await Dispatcher.InvokeAsync(() =>
                mainVm.AnnounceUpdate(
                    info.LatestVersion,
                    info.DownloadUrl,
                    info.Notes,
                    info.ActionText,
                    info.InstallerSha256));
        }
        catch (Exception ex)
        {
            // A transient network blip shouldn't surface as an error toast;
            // the chip just stays hidden. UpdateCheckService already logs
            // its own failures, so this is a last-resort guard.
            DiagnosticLog.Write("UpdateCheck", $"runner failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ApplyDefaultLanguage()
    {
        if (s_languageMetadataApplied) return;
        var zhCn = XmlLanguage.GetLanguage("zh-CN");
        try
        {
            try
            {
                FrameworkElement.LanguageProperty.OverrideMetadata(
                    typeof(FrameworkElement),
                    new FrameworkPropertyMetadata(zhCn));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // Metadata might already be sealed/registered by WPF/designer/bootstrap.
            }

            try
            {
                FrameworkContentElement.LanguageProperty.OverrideMetadata(
                    typeof(FrameworkContentElement),
                    new FrameworkPropertyMetadata(zhCn));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // Metadata might already be sealed/registered by WPF/designer/bootstrap.
            }
        }
        finally
        {
            // Avoid retrying on subsequent startups if the metadata has already been registered.
            s_languageMetadataApplied = true;
        }
    }

    private static void DisableDefaultFocusVisuals()
    {
        if (s_focusVisualHandlerRegistered) return;
        EventManager.RegisterClassHandler(
            typeof(Control),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Control control)
                    control.FocusVisualStyle = null;
            }));
        s_focusVisualHandlerRegistered = true;
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Storage
        services.AddSingleton(_ => new MolaGptDatabase(MolaGptDatabase.DefaultPath()));
        services.AddSingleton<ConversationRepository>();
        services.AddSingleton<MessageRepository>();
        services.AddSingleton<SettingsRepository>();
        services.AddSingleton<ProviderRepository>();
        services.AddSingleton<PersonaRepository>();

        services.AddSingleton(_ => new CredentialStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT", "creds.json")));

        // Single CookieContainer shared by the molagpt-bound HttpClient. Carrying
        // the same Cookie jar across warmup → login → chat lets Cloudflare's
        // __cf_bm and the backend's mola_did cookies persist, which is what the
        // web fetch() flow relies on.
        services.AddSingleton<CookieContainer>();

        // Named HttpClient — molagpt: uses CookieContainer so the backend's
        // mola_did cookie persists across warmup → login → chat calls. The
        // User-Agent here is hashed into JWT.ua, so it MUST stay constant
        // for the app's lifetime (see UserAgentProvider).
        services.AddHttpClient(MolaGptHttpClient, (_, client) =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.FixedUa);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/event-stream, */*; q=0.01");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
                // Lets a Cloudflare WAF custom rule key a Skip on either the UA
                // suffix or this header. See README "Cloudflare WAF rule".
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-MolaGPT-Client", UserAgentProvider.ClientMarker);
            })
            .ConfigurePrimaryHttpMessageHandler(sp => new HttpClientHandler
            {
                CookieContainer = sp.GetRequiredService<CookieContainer>(),
                UseCookies = true,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All
            })
            .SetHandlerLifetime(TimeSpan.FromHours(24));

        // BYOK clients can use a fresh handler each time — they don't need
        // sticky cookies.
        services.AddHttpClient(ByokHttpClient, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.FixedUa);
        });

        services.AddSingleton(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(MolaGptHttpClient);
            return new MolaGptAuthService(http, sp.GetRequiredService<CredentialStore>());
        });

        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(MolaGptHttpClient);
            return new MolaGptProxyProvider(http, sp.GetRequiredService<MolaGptAuthService>());
        });
        services.AddSingleton(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(MolaGptHttpClient);
            return new CloudSyncService(
                http,
                sp.GetRequiredService<MolaGptAuthService>(),
                sp.GetRequiredService<ConversationRepository>(),
                sp.GetRequiredService<MessageRepository>(),
                sp.GetRequiredService<SettingsRepository>());
        });
        services.AddSingleton<MolaGptLogoutCoordinator>();

        services.AddSingleton<BackgroundStreamService>();
        services.AddSingleton(sp => new McpHttpClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ByokHttpClient)));
        services.AddSingleton<McpClientManager>();
        services.AddSingleton(sp => new VisionProxyTool(
            sp.GetRequiredService<ProviderRegistry>(),
            () => sp.GetRequiredService<IHttpClientFactory>().CreateClient(ByokHttpClient)));
        services.AddSingleton(sp => new ImageGenerationTool(
            () => sp.GetRequiredService<IHttpClientFactory>().CreateClient(ByokHttpClient),
            sp.GetRequiredService<AttachmentStore>().Save));
        services.AddSingleton<IChatToolHost, ChatToolHost>();

        services.AddSingleton(sp => new ConversationListViewModel(
            sp.GetRequiredService<ConversationRepository>(),
            sp.GetRequiredService<PersonaListViewModel>(),
            sp.GetRequiredService<SettingsRepository>()));
        services.AddSingleton(sp => new PersonaListViewModel(sp.GetRequiredService<PersonaRepository>()));
        services.AddSingleton(sp => new ChatViewModel(
            sp.GetRequiredService<ProviderRegistry>(),
            sp.GetRequiredService<MessageRepository>(),
            sp.GetRequiredService<ConversationRepository>(),
            sp.GetRequiredService<PersonaListViewModel>()));
        services.AddSingleton(_ => new AttachmentStore());
        services.AddSingleton(sp => new ComposerViewModel(
            sp.GetRequiredService<ChatViewModel>(),
            sp.GetRequiredService<BackgroundStreamService>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<PersonaListViewModel>(),
            sp.GetRequiredService<AttachmentStore>(),
            sp.GetRequiredService<ImageGenerationTool>()));
        services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<ProviderRepository>(),
            sp.GetRequiredService<CredentialStore>(),
            sp.GetRequiredService<SettingsRepository>()));
        services.AddSingleton(sp => new MainViewModel(
            sp.GetRequiredService<ConversationListViewModel>(),
            sp.GetRequiredService<ChatViewModel>(),
            sp.GetRequiredService<ComposerViewModel>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<PersonaListViewModel>(),
            sp.GetRequiredService<BackgroundStreamService>()));

        services.AddSingleton<NotificationService>(sp => new NotificationService(
            sp.GetRequiredService<BackgroundStreamService>(),
            sp.GetRequiredService<SettingsViewModel>(),
            () => sp.GetRequiredService<ChatViewModel>().ConversationId,
            conversationId =>
            {
                var listVm = sp.GetRequiredService<ConversationListViewModel>();
                if (string.Equals(listVm.SelectedId, conversationId, StringComparison.Ordinal))
                    listVm.SelectedId = null;
                listVm.SelectById(conversationId);
            }));

        services.AddSingleton(sp =>
        {
            // BYOK client is fine for both update sources (GitHub API +
            // server manifest) — neither needs the CookieContainer-bound
            // molagpt client, and using ByokHttpClient keeps the molagpt
            // cookie jar pristine.
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(ByokHttpClient);
            return new UpdateCheckService(
                http,
                Environment.GetEnvironmentVariable("MOLAGPT_UPDATE_API_URL"),
                Environment.GetEnvironmentVariable("MOLAGPT_UPDATE_MANIFEST_URL"));
        });
        services.AddSingleton(sp =>
            new AutoUpdateService(sp.GetRequiredService<IHttpClientFactory>().CreateClient(ByokHttpClient)));

        services.AddSingleton<MainWindow>();
        services.AddTransient(sp => new LoginDialog(
            sp.GetRequiredService<MolaGptAuthService>(),
            sp.GetRequiredService<MolaGptProxyProvider>(),
            sp.GetRequiredService<ProviderRegistry>()));
        services.AddTransient(sp => new AccountDialog(
            sp.GetRequiredService<MolaGptAuthService>(),
            sp.GetRequiredService<MolaGptProxyProvider>()));
        services.AddTransient(sp =>
        {
            Func<HttpClient> factory = () =>
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(ByokHttpClient);
            return new SettingsWindow(
                sp.GetRequiredService<SettingsViewModel>(),
                sp.GetRequiredService<MolaGptAuthService>(),
                sp.GetRequiredService<ProviderRegistry>(),
                sp.GetRequiredService<CloudSyncService>(),
                sp.GetRequiredService<ConversationListViewModel>(),
                sp.GetRequiredService<PersonaListViewModel>(),
                factory,
                sp.GetRequiredService<IChatToolHost>());
        });
        services.AddTransient(sp =>
            new AboutWindow(
                sp.GetRequiredService<UpdateCheckService>(),
                sp.GetRequiredService<AutoUpdateService>()));
    }

    private void RestoreSavedProviders()
    {
        try
        {
            var repo = Services.GetRequiredService<ProviderRepository>();
            var registry = Services.GetRequiredService<ProviderRegistry>();
            var creds = Services.GetRequiredService<CredentialStore>();
            var http = Services.GetRequiredService<IHttpClientFactory>();

            foreach (var row in repo.List())
            {
                try
                {
                    if (!row.Enabled) continue;
                    if (SettingsViewModel.IsImagePurpose(row.Purpose)) continue;
                    string apiKey = string.Empty;
                    if (row.ApiKeyEnc is { Length: > 0 })
                        apiKey = creds.Decrypt(row.ApiKeyEnc) ?? "";

                    var models = TryDeserializeModels(row.Models);
                    var client = http.CreateClient(ByokHttpClient);

                    IChatProvider? prov = row.Type switch
                    {
                        "openai" => OpenAIProvider.Create(row.Id, row.Name, apiKey, models, client, row.BaseUrl, row.ApiPath),
                        "openai-compat" => new OpenAICompatibleProvider(row.Id, row.Name,
                            row.BaseUrl ?? OpenAIProvider.DefaultBaseUrl, apiKey, models, client,
                            Services.GetService<IChatToolHost>())
                            { ChatPath = OpenAICompatibleProvider.ResolveChatPath(row.ApiPath) },
                        "anthropic" => new AnthropicProvider(row.Id, row.Name, apiKey, models, client, row.BaseUrl)
                            { MessagesPath = string.IsNullOrWhiteSpace(row.ApiPath) ? "v1/messages" : row.ApiPath.Trim() },
                        "gemini" => GeminiProvider.Create(row.Id, row.Name, apiKey, models, client, row.BaseUrl, row.ApiPath),
                        _ => null
                    };
                    if (prov is not null) registry.Register(prov);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Restore provider '{row.Name}' failed: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RestoreSavedProviders failed: {ex}");
        }
    }

    private static List<ProviderModel> TryDeserializeModels(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var entries = JsonSerializer.Deserialize<List<MolaGPT.ViewModels.ProviderModelEntry>>(json) ?? new();
            return entries.Select(ToProviderModel).ToList();
        }
        catch (JsonException) { return new(); }
    }

    private static ProviderModel ToProviderModel(MolaGPT.ViewModels.ProviderModelEntry entry)
    {
        MolaGPT.Core.Models.ThinkingConfig? thinkingConfig = null;
        var kindStr = entry.ThinkingParamKind;
        if (entry.Thinking && string.IsNullOrWhiteSpace(kindStr))
        {
            var inferred = MolaGPT.Core.Models.ThinkingParamKindInference.InferFromModelId(entry.Id);
            if (inferred != MolaGPT.Core.Models.ThinkingParamKind.None)
                kindStr = inferred.ToString();
        }

        if (kindStr is { })
        {
            if (Enum.TryParse<MolaGPT.Core.Models.ThinkingParamKind>(kindStr, true, out var kind))
            {
                thinkingConfig = new MolaGPT.Core.Models.ThinkingConfig(
                    kind,
                    MinBudget: entry.ThinkingBudgetMin,
                    MaxBudget: entry.ThinkingBudgetMax,
                    DefaultBudget: entry.ThinkingBudgetDefault,
                    DefaultEffort: entry.DefaultEffort);
            }
        }

        return new(
            entry.Id,
            entry.DisplayName,
            SupportsVision: entry.Vision,
            SupportsThinking: entry.Thinking,
            SupportsReasoningEffort: entry.ReasoningEffort,
            SupportsToolCalling: entry.Tools,
            ContextWindow: entry.ContextWindow,
            ThinkingConfig: thinkingConfig);
    }

    private string ResolveThemeKey(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "Light",
        ThemeMode.Dark => "Dark",
        _ => ReadSystemAppTheme()
    };

    /// <summary>
    /// Reads the Windows "Apps use light/dark theme" preference from the
    /// registry. Defaults to Light when the value is missing or unreadable —
    /// matches the OS behavior on systems where the personalization API
    /// isn't initialized.
    /// </summary>
    private static string ReadSystemAppTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i) return i == 0 ? "Dark" : "Light";
        }
        catch
        {
            // Registry access can fail on locked-down profiles; fall through to Light.
        }
        return "Light";
    }

    private void OnSystemUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Only react when "follow system" is the active mode and the General
        // category fires (this is what Windows raises for app theme flips).
        if (e.Category != UserPreferenceCategory.General) return;
        if (_themePreference != ThemeMode.System) return;
        Dispatcher.InvokeAsync(() => ApplyTheme(ReadSystemAppTheme()));
    }

    private void ApplyTheme(string theme)
    {
        // Normalize and bail when the requested theme is already active —
        // re-applying would still work but RefreshMarkdownPresenters is
        // O(n) over the visual tree and runs on the UI thread.
        if (_activeTheme is not null && string.Equals(_currentThemeKey, theme, StringComparison.Ordinal))
            return;

        var dictUri = theme == "Dark" ? "Themes/Tokens.Dark.xaml" : "Themes/Tokens.Light.xaml";
        var newTokens = new ResourceDictionary { Source = new Uri(dictUri, UriKind.Relative) };
        if (_activeTheme is null)
        {
            for (int i = 0; i < Resources.MergedDictionaries.Count; i++)
            {
                var src = Resources.MergedDictionaries[i].Source?.OriginalString ?? "";
                if (src.EndsWith("Tokens.Light.xaml") || src.EndsWith("Tokens.Dark.xaml"))
                {
                    Resources.MergedDictionaries[i] = newTokens;
                    _activeTheme = newTokens;
                    _currentThemeKey = theme;
                    RefreshMarkdownPresenters();
                    return;
                }
            }
            Resources.MergedDictionaries.Insert(0, newTokens);
            _activeTheme = newTokens;
        }
        else
        {
            var idx = Resources.MergedDictionaries.IndexOf(_activeTheme);
            if (idx >= 0) Resources.MergedDictionaries[idx] = newTokens;
            else Resources.MergedDictionaries.Add(newTokens);
            _activeTheme = newTokens;
        }
        _currentThemeKey = theme;
        RefreshMarkdownPresenters();
    }

    private static void RefreshMarkdownPresenters()
    {
        foreach (Window window in Current.Windows)
        {
            foreach (var presenter in FindVisualChildren<MarkdownPresenter>(window))
            {
                presenter.RefreshTheme();
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    /// <summary>
    /// Handles a molagpt://oauth_callback?code=... deep link (or legacy
    /// ?token=...) delivered via argv or WM_COPYDATA. Exchanges the code
    /// over HTTPS for the session JWT, persists it, and refreshes the proxy.
    /// </summary>
    private async Task HandleOAuthDeepLinkAsync(string url)
    {
        DiagnosticLog.Write("OAuthDeepLink", $"received url.len={url?.Length ?? 0}");
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, UrlSchemeRegistrar.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticLog.Write("OAuthDeepLink", "rejected: bad scheme");
            return;
        }

        var query = uri.Query.TrimStart('?');
        string? code = null;
        string? legacyToken = null;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (string.Equals(key, "code", StringComparison.Ordinal))
                code = value;
            else if (string.Equals(key, "token", StringComparison.Ordinal))
                legacyToken = value;
        }

        var auth = Services.GetRequiredService<MolaGptAuthService>();
        var registry = Services.GetRequiredService<ProviderRegistry>();
        var loginOk = false;
        if (!string.IsNullOrEmpty(code))
        {
            DiagnosticLog.Write("OAuthDeepLink", $"code.len={code.Length}");
            var exchange = await auth.ExchangeOAuthCodeAsync(code).ConfigureAwait(true);
            loginOk = exchange.Success;
            DiagnosticLog.Write("OAuthDeepLink", $"ExchangeOAuthCode={loginOk}");
            if (!loginOk)
            {
                MessageBox.Show(exchange.ErrorMessage ?? "授权码兑换失败，请重新发起第三方登录。",
                    "MolaGPT 登录", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else if (!string.IsNullOrEmpty(legacyToken))
        {
            DiagnosticLog.Write("OAuthDeepLink", $"legacy token.len={legacyToken.Length}");
            loginOk = auth.ApplyExternalToken(legacyToken);
            DiagnosticLog.Write("OAuthDeepLink", $"ApplyExternalToken={loginOk}");
            if (!loginOk)
            {
                MessageBox.Show("第三方登录返回的 Token 无法解析，请重试。",
                    "MolaGPT 登录", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            return;
        }

        // Close the waiting login dialog as soon as the token is persisted.
        // Model refresh and cloud sync can be slower network work, and should
        // not leave the OAuth UI looking stuck after the browser returned.
        LoginDialog.NotifyExternalLoginCompleted();
        DiagnosticLog.Write("OAuthDeepLink", "LoginDialog notified");

        try
        {
            var proxy = Services.GetRequiredService<MolaGptProxyProvider>();
            await proxy.RefreshModelsAsync();
            registry.Register(proxy);
            DiagnosticLog.Write("OAuthDeepLink", "proxy registered");
        }
        catch (MolaGptAuthExpiredException ex)
        {
            DiagnosticLog.Write("OAuthDeepLink", $"AuthExpired: {ex.Message}");
            auth.Logout();
            MessageBox.Show($"第三方登录回写后无法验证账号: {ex.Message}",
                "MolaGPT 登录", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("OAuthDeepLink", $"refresh failed: {ex.GetType().Name}: {ex.Message}");
            // Token persisted; the user can still chat once network recovers.
        }

        var cloudSync = Services.GetRequiredService<CloudSyncService>();
        var conversationListVm = Services.GetRequiredService<ConversationListViewModel>();
        await RunCloudSyncAndReloadAsync(cloudSync, conversationListVm);
    }

    private async Task RunCloudSyncAndReloadAsync(
        CloudSyncService cloudSync,
        ConversationListViewModel conversationListVm)
    {
        try
        {
            var result = await Task.Run(() => cloudSync.RequestForegroundSyncAsync());
            if (ShouldReloadConversationsAfterSync(result))
                await conversationListVm.ReloadAsync();
        }
        catch
        {
            // CloudSyncService publishes the user-facing status.
        }
    }

    private async Task RunStartupCloudSyncAfterFirstPaintAsync(
        CloudSyncService cloudSync,
        ConversationListViewModel conversationListVm)
    {
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        if (Dispatcher.HasShutdownStarted) return;

        await Dispatcher.InvokeAsync(
            () => _ = RunCloudSyncAndReloadAsync(cloudSync, conversationListVm),
            DispatcherPriority.ContextIdle);
    }

    private static bool ShouldReloadConversationsAfterSync(CloudSyncResult? result) =>
        result is not null && (result.Uploaded > 0 || result.Downloaded > 0 || result.Deleted > 0);

    protected override async void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
        SingleInstanceGuard.Release();
        _cloudStatusHideCts?.Cancel();
        _cloudStatusHideCts?.Dispose();
        if (Host is not null)
        {
            Host.Services.GetService<CloudSyncService>()?.StopPeriodicSync();
            await Host.StopAsync(TimeSpan.FromSeconds(2));
            Host.Dispose();
        }
        if (!_installingUpdateWithRestart
            && !string.IsNullOrWhiteSpace(_pendingUpdateInstallerPath)
            && File.Exists(_pendingUpdateInstallerPath))
        {
            try
            {
                new AutoUpdateService(new HttpClient())
                    .InstallAfterExitWithoutRestart(_pendingUpdateInstallerPath);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("UpdateInstall", $"install-on-exit failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        base.OnExit(e);
    }

    private void ScheduleCloudStatusHide(MainViewModel mainVm)
    {
        var previous = _cloudStatusHideCts;
        _cloudStatusHideCts = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();

        var token = _cloudStatusHideCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                await Dispatcher.InvokeAsync(mainVm.HideCloudSyncStatus, DispatcherPriority.Background);
            }
            catch (OperationCanceledException) { }
        }, token);
    }
}
