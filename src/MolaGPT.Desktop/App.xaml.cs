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
using MolaGPT.Desktop.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;
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
    private static bool s_languageMetadataApplied;
    private static bool s_focusVisualHandlerRegistered;
    private CancellationTokenSource? _cloudStatusHideCts;

    protected override async void OnStartup(StartupEventArgs e)
    {
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

        var window = Services.GetRequiredService<MainWindow>();
        var mainVm = Services.GetRequiredService<MainViewModel>();
        var cloudSync = Services.GetRequiredService<CloudSyncService>();
        var composerVm = Services.GetRequiredService<ComposerViewModel>();
        var conversationListVm = Services.GetRequiredService<ConversationListViewModel>();
        mainVm.EnsureConversationDetailAsync = id => cloudSync.FetchConversationToLocalAsync(id);
        composerVm.ConversationCompletedAsync = cloudSync.CompleteConversationTurnAsync;
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
        mainVm.ThemeToggleRequested = () =>
        {
            ApplyTheme(_currentThemeKey == "Light" ? "Dark" : "Light");
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

        window.DataContext = mainVm;
        window.Show();

        // Initialize notification service (must be after window is shown so
        // toast click can activate the window).
        var notificationService = Services.GetRequiredService<NotificationService>();
        _ = notificationService; // keep alive via DI singleton

        cloudSync.StartPeriodicSync();
        _ = RunStartupCloudSyncAfterFirstPaintAsync(cloudSync, conversationListVm);
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

        services.AddSingleton<BackgroundStreamService>();

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
        services.AddSingleton(sp => new ComposerViewModel(
            sp.GetRequiredService<ChatViewModel>(),
            sp.GetRequiredService<BackgroundStreamService>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<PersonaListViewModel>()));
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
            conversationId =>
            {
                var chatVm = sp.GetRequiredService<ChatViewModel>();
                var listVm = sp.GetRequiredService<ConversationListViewModel>();
                listVm.SelectById(conversationId);
            }));

        services.AddSingleton<MainWindow>();
        services.AddTransient(sp => new LoginDialog(
            sp.GetRequiredService<MolaGptAuthService>(),
            sp.GetRequiredService<MolaGptProxyProvider>(),
            sp.GetRequiredService<ProviderRegistry>()));
        services.AddTransient(sp => new AccountDialog(
            sp.GetRequiredService<MolaGptAuthService>(),
            sp.GetRequiredService<MolaGptProxyProvider>(),
            sp.GetRequiredService<ProviderRegistry>()));
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
                factory);
        });
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
                    string apiKey = string.Empty;
                    if (row.ApiKeyEnc is { Length: > 0 })
                        apiKey = creds.Decrypt(row.ApiKeyEnc) ?? "";

                    var models = TryDeserializeModels(row.Models);
                    var client = http.CreateClient(ByokHttpClient);

                    IChatProvider? prov = row.Type switch
                    {
                        "openai" => OpenAIProvider.Create(row.Id, row.Name, apiKey, models, client, row.BaseUrl),
                        "openai-compat" => new OpenAICompatibleProvider(row.Id, row.Name,
                            row.BaseUrl ?? OpenAIProvider.DefaultBaseUrl, apiKey, models, client),
                        "anthropic" => new AnthropicProvider(row.Id, row.Name, apiKey, models, client, row.BaseUrl),
                        "gemini" => GeminiProvider.Create(row.Id, row.Name, apiKey, models, client, row.BaseUrl),
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

    private void ApplyTheme(string theme)
    {
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
        _cloudStatusHideCts?.Cancel();
        _cloudStatusHideCts?.Dispose();
        if (Host is not null)
        {
            Host.Services.GetService<CloudSyncService>()?.StopPeriodicSync();
            await Host.StopAsync(TimeSpan.FromSeconds(2));
            Host.Dispose();
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
