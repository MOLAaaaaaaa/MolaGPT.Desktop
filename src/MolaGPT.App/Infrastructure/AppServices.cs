using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MolaGPT.App.Remote;
using MolaGPT.App.Views;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Agents.Relay;
using MolaGPT.Core.Chat.Attachments;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Chat.Tasks;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Chat.Tools.Browser;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Chat.Tools.Mcp;
using MolaGPT.Core.Chat.Tools.PythonExecution;
using MolaGPT.Core.Chat.Tools.Vision;
using MolaGPT.Core.Memory;
using MolaGPT.Core.Net;
using MolaGPT.Core.Personalization;
using MolaGPT.Core.Remote;
using MolaGPT.Core.Runtime;
using MolaGPT.Desktop.Services;
using MolaGPT.Desktop.Services.Remote;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;
using MolaGPT.ViewModels.Agents;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.App.Infrastructure;

/// <summary>
/// Composition root for the desktop application. Shared services live in
/// MolaGPT.Core / .Storage / .ViewModels / .Services; UI-specific adapters live
/// in this project.
/// </summary>
internal static class AppServices
{
    private const string AuthHttpClientName = "molagpt-auth";
    private const string BrowserActivityStorageKey = "browser_tool_activity";

    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        // ---- power & idle activity tracking --------------------------------
        services.AddSingleton(_ => new AppPowerModeCoordinator(
            line => DiagnosticLog.Write("power", line)));
        services.AddSingleton<IActivityTracker>(sp => sp.GetRequiredService<AppPowerModeCoordinator>());

        // ---- storage -------------------------------------------------------
        services.AddSingleton(_ => new MolaGptDatabase(MolaGptDatabase.DefaultPath()));
        services.AddSingleton<ConversationRepository>();
        services.AddSingleton<MessageRepository>();
        services.AddSingleton<SettingsRepository>();
        services.AddSingleton<ProviderRepository>();
        services.AddSingleton<PersonaRepository>();
        services.AddSingleton<MemoryIndexRepository>();
        services.AddSingleton(_ => new AttachmentStore());

        services.AddSingleton(_ => new CredentialStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT", "creds.json")));
        services.AddSingleton<PersonalDataService>();

        // ---- http ----------------------------------------------------------
        // One CookieContainer shared by the molagpt client so Cloudflare's
        // __cf_bm and the backend's mola_did cookie persist across
        // warmup → login → chat. The User-Agent is hashed into JWT.ua and must
        // stay constant for the process lifetime.
        services.AddSingleton<CookieContainer>();
        services.AddTransient<MolaDeviceCookieHandler>();
        services.AddTransient(sp => new MolaAccountAuthHandler(
            sp.GetRequiredService<MolaGptAuthService>(), line => DiagnosticLog.Write("account", line)));

        // Loaded here rather than by the settings page: the first request can leave
        // before anyone opens it, and it must already know the user's proxy.
        services.AddSingleton(sp =>
        {
            var proxy = new AppProxy();
            proxy.Apply(ProxyConfigStore.Load(
                sp.GetRequiredService<SettingsRepository>(), sp.GetRequiredService<CredentialStore>()));
            return proxy;
        });

        foreach (var clientName in new[] { HttpClientNames.MolaGpt, AuthHttpClientName })
        {
            var clientBuilder = services.AddHttpClient(clientName, (_, client) =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.FixedUa);
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "Accept", "application/json, text/event-stream, */*; q=0.01");
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "X-MolaGPT-Client", UserAgentProvider.ClientMarker);
            })
            .AddHttpMessageHandler<MolaDeviceCookieHandler>()
            .ConfigurePrimaryHttpMessageHandler(sp => new HttpClientHandler
            {
                CookieContainer = sp.GetRequiredService<CookieContainer>(),
                UseCookies = true,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                Proxy = sp.GetRequiredService<AppProxy>().Official,
                UseProxy = true
            })
            .SetHandlerLifetime(TimeSpan.FromHours(24));
            if (clientName == HttpClientNames.MolaGpt)
                clientBuilder.AddHttpMessageHandler<MolaAccountAuthHandler>();
        }

        services.AddHttpClient(HttpClientNames.Byok, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.FixedUa);
            })
            // Keeps the factory's own defaults (no cookie jar, pooled lifetime); only the proxy is ours.
            .UseSocketsHttpHandler((handler, sp) => handler.Proxy = sp.GetRequiredService<AppProxy>().General);

        // The app owns the clients used by long-lived services and Pi providers.
        // Interactive probes, tools and downloads that create a client per call
        // keep using the named factory and dispose that client when done.
        foreach (var name in new[] { HttpClientNames.MolaGpt, AuthHttpClientName, HttpClientNames.Byok })
            services.AddKeyedSingleton<HttpClient>(name,
                (sp, _) => sp.GetRequiredService<IHttpClientFactory>().CreateClient(name));
        services.AddKeyedSingleton<Func<HttpClient>>(HttpClientNames.Byok, (sp, _) =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return () => factory.CreateClient(HttpClientNames.Byok);
        });

        // ---- auth / providers ---------------------------------------------
        services.AddSingleton(sp => new MolaGptAuthService(
            sp.GetRequiredKeyedService<HttpClient>(AuthHttpClientName),
            sp.GetRequiredService<CredentialStore>(),
            log: line => DiagnosticLog.Write("account", line)));

        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<ProviderConnection>();
        services.AddSingleton(sp => new MolaGptProxyProvider(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.MolaGpt),
            sp.GetRequiredService<MolaGptAuthService>()));

        services.AddSingleton(sp => new ServerToolActions(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.MolaGpt),
            sp.GetRequiredService<MolaGptAuthService>()));

        services.AddSingleton(sp => new CloudSyncService(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.MolaGpt),
            sp.GetRequiredService<MolaGptAuthService>(),
            sp.GetRequiredService<ConversationRepository>(),
            sp.GetRequiredService<MessageRepository>(),
            sp.GetRequiredService<SettingsRepository>(),
            sp.GetRequiredService<IActivityTracker>()));

        services.AddSingleton(sp => new MolaPersonalizationService(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.MolaGpt),
            sp.GetRequiredService<MolaGptAuthService>()));

        services.AddSingleton(sp => new ConversationTitleService(
            sp.GetRequiredService<ConversationRepository>(),
            sp.GetRequiredService<MessageRepository>(),
            sp.GetRequiredService<ProviderRegistry>(),
            sp.GetRequiredService<SettingsRepository>(),
            sp.GetRequiredKeyedService<Func<HttpClient>>(HttpClientNames.Byok),
            (userText, assistantText, ct) =>
                sp.GetRequiredService<CloudSyncService>()
                    .GenerateMolaGptTitleAsync(userText, assistantText, ct),
            message => DiagnosticLog.Write("title", message)));

        // ---- local tool gateway (Work mode) --------------------------------
        services.AddSingleton(sp => new PiSidecarRuntimeManager(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.Byok),
            Environment.GetEnvironmentVariable("MOLAGPT_PI_SIDECAR_MANIFEST_URL")));
        services.AddSingleton(sp => new PiWorkSidecarLocator(
            () => sp.GetRequiredService<PiSidecarRuntimeManager>().GetCompatibleInstalled()));
        // One runtime for the whole app: it owns the loopback shim, the tool bridge
        // and the capped sidecar pool. Per-provider runtimes would put the memory
        // ceiling back where it was, since nothing would bound the total.
        services.AddSingleton(sp => new PiRuntime(
            line => DiagnosticLog.Write("pi-runtime", line),
            activity: sp.GetRequiredService<IActivityTracker>()));
        services.AddSingleton(sp => new MolaGptLocalToolsRegistrar(
            sp.GetRequiredService<MolaGptAuthService>(),
            sp.GetRequiredService<MolaGptProxyProvider>(),
            sp.GetRequiredService<ProviderRegistry>(),
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.MolaGpt),
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.Byok),
            sp.GetRequiredService<IChatToolHost>(),
            sp.GetRequiredService<PiWorkSidecarLocator>(),
            sp.GetRequiredService<PiRuntime>()));
        services.AddSingleton(sp => new ChatGptAccount(
            sp.GetRequiredService<CredentialStore>(),
            sp.GetRequiredKeyedService<Func<HttpClient>>(HttpClientNames.Byok),
            line => DiagnosticLog.Write("chatgpt", line)));
        services.AddSingleton(sp => new PiByokProviderFactory(
            sp.GetRequiredService<PiWorkSidecarLocator>(),
            sp.GetRequiredService<IChatToolHost>(),
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.Byok),
            sp.GetRequiredService<PiRuntime>(),
            line => DiagnosticLog.Write("pi-byok", line),
            sp.GetRequiredService<ChatGptAccount>()));
        services.AddSingleton(sp => new ProviderRestorer(
            sp.GetRequiredService<ProviderRepository>(),
            sp.GetRequiredService<CredentialStore>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<ProviderRegistry>(),
            sp.GetRequiredService<PiByokProviderFactory>(),
            line => DiagnosticLog.Write("provider", line)));

        services.AddSingleton<BackgroundStreamService>();
        services.AddSingleton(sp => new McpClientManager(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.Byok),
            line => DiagnosticLog.Write("mcp", line),
            // npx and uvx fetch their packages from inside the child, which knows nothing of ours.
            () => sp.GetRequiredService<AppProxy>().Config.ToEnvironment()));

        // ---- Agent Bridge --------------------------------------------------
        services.AddSingleton<AgentCliResolver>();
        services.AddSingleton<DesktopAgentConfigProvider>();
        services.AddSingleton<IAgentConfigProvider>(sp => sp.GetRequiredService<DesktopAgentConfigProvider>());
        services.AddSingleton<IAgentBackend>(_ => new ClaudeCodeBackend(line => DiagnosticLog.Write("agentrelay", line)));
        services.AddSingleton<IAgentBackend>(_ => new CodexBackend(line => DiagnosticLog.Write("agentrelay", line)));
        services.AddSingleton(sp => new AgentSessionManager(
            sp.GetServices<IAgentBackend>(),
            sp.GetRequiredService<AgentCliResolver>(),
            sp.GetRequiredService<IAgentConfigProvider>()));
        services.AddSingleton<AgentHistoryReader>();
        services.AddSingleton(sp => new AgentBridgeService(
            sp.GetRequiredService<AgentSessionManager>(),
            sp.GetRequiredService<AgentHistoryReader>(),
            sp.GetRequiredService<IAgentConfigProvider>(),
            sp.GetRequiredService<IActivityTracker>()));
        // The third constructor parameter is baseUrl, so keep these named.
        services.AddSingleton<IRelayProducer>(sp => new HttpRelayProducer(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.MolaGpt),
            sp.GetRequiredService<MolaGptAuthService>(),
            machineId: sp.GetRequiredService<IAgentConfigProvider>().MachineId,
            machineName: sp.GetRequiredService<IAgentConfigProvider>().MachineName,
            proxy: sp.GetRequiredService<AppProxy>().Official));
        services.AddSingleton(sp => new AgentRelayClient(
            sp.GetRequiredService<AgentBridgeService>(),
            sp.GetRequiredService<IRelayProducer>(),
            sp.GetRequiredService<IAgentConfigProvider>(),
            line => DiagnosticLog.Write("AgentRelay", line),
            new RelayCommandJournal(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MolaGPT", "agent-command-journal")),
            sp.GetRequiredService<IActivityTracker>()));
        services.AddSingleton<AgentBridgeStatusViewModel>();

        // ---- remote control of MolaGPT conversations ---------------------------
        services.AddSingleton(sp => new PairedPhoneStore(
            sp.GetRequiredService<CredentialStore>(),
            () => sp.GetRequiredService<MolaGptAuthService>().CurrentUsername));
        services.AddSingleton(sp => new AnonymousDeviceIdentity(sp.GetRequiredService<CredentialStore>()));
        services.AddSingleton(sp => new AnonApiClient(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.MolaGpt),
            sp.GetRequiredService<AnonymousDeviceIdentity>(),
            baseUrl: "https://chatgpt.wljay.cn/v2"));
        services.AddSingleton(sp => new RemoteControlHost(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.MolaGpt),
            sp.GetRequiredService<MolaGptAuthService>(),
            sp.GetRequiredService<ChatViewModel>(),
            sp.GetRequiredService<ComposerViewModel>(),
            sp.GetRequiredService<MessageRepository>(),
            sp.GetRequiredService<ConversationRepository>(),
            sp.GetRequiredService<ProviderRegistry>(),
            sp.GetRequiredService<PairedPhoneStore>(),
            sp.GetRequiredService<RemoteApprovalRelay>(),
            onUi: work => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(work),
            machineId: sp.GetRequiredService<IAgentConfigProvider>().MachineId,
            machineName: sp.GetRequiredService<IAgentConfigProvider>().MachineName,
            dataRoot: Path.GetDirectoryName(sp.GetRequiredService<MolaGptDatabase>().FilePath)!,
            log: line => DiagnosticLog.Write("RemoteWork", line),
            proxy: sp.GetRequiredService<AppProxy>().Official,
            anonApi: sp.GetRequiredService<AnonApiClient>(),
            grantStore: sp.GetRequiredService<IToolGrantStore>(),
            backgroundStreams: sp.GetRequiredService<BackgroundStreamService>(),
            activity: sp.GetRequiredService<IActivityTracker>()));
        services.AddSingleton(sp => new RemoteWorkSettingsViewModel(
            sp.GetRequiredService<RemoteControlHost>(),
            sp.GetRequiredService<SettingsRepository>(),
            action => Avalonia.Threading.Dispatcher.UIThread.Post(action)));

        // ---- tools ---------------------------------------------------------
        services.AddSingleton(sp => new VisionProxyTool(
            sp.GetRequiredService<ProviderRegistry>(),
            sp.GetRequiredKeyedService<Func<HttpClient>>(HttpClientNames.Byok)));

        // Skia handles image normalization without a UI-framework imaging type.
        services.AddSingleton(sp => new ImageAnalysisTool(
            sp.GetRequiredService<ProviderRegistry>(),
            sp.GetRequiredKeyedService<Func<HttpClient>>(HttpClientNames.Byok),
            (bytes, mime, name) =>
            {
                var processed = SkiaImageNormalizer.Process(bytes, mime, name);
                return processed.Error is not null
                    ? NormalizedImage.Rejected(processed.Error)
                    : new NormalizedImage(processed.Bytes, processed.MimeType);
            }));

        services.AddSingleton(sp => new ImageGenerationTool(
            sp.GetRequiredKeyedService<Func<HttpClient>>(HttpClientNames.Byok),
            sp.GetRequiredService<AttachmentStore>().Save));

        services.AddSingleton<IPythonSessionAllowList, PythonSessionAllowList>();
        services.AddSingleton<IToolGrantStore>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsRepository>();
            var store = new ToolGrantStore(
                loadPersisted: () => ToolGrantSettings.Read(settings),
                savePersisted: names => ToolGrantSettings.Write(settings, names));

            sp.GetRequiredService<SettingsViewModel>().ToolGrantsRevoked +=
                (_, _) => store.ClearSessionGrants();
            return store;
        });

        // Remote control plugs into approvals later; the relay is what approvals hold.
        services.AddSingleton<RemoteApprovalRelay>();
        services.AddSingleton<IRemoteApprovalChannel>(sp => sp.GetRequiredService<RemoteApprovalRelay>());
        services.AddSingleton<ApprovalModeState>();
        services.AddSingleton<IApprovalModeSource>(sp => sp.GetRequiredService<ApprovalModeState>());
        services.AddSingleton<ToolApprovalService>();
        services.AddSingleton<IPythonExecutionApprovalService>(sp => sp.GetRequiredService<ToolApprovalService>());
        services.AddSingleton<IToolApprovalService>(sp => sp.GetRequiredService<ToolApprovalService>());

        // Background tasks outlive the turn, and the Pi lease, that started them, so
        // one registry for the whole app holds them.
        services.AddSingleton<Func<SubagentRuntimeOptions>>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsRepository>();
            return () => new SubagentRuntimeOptions(
                settings.Get(SettingsViewModel.SubagentProviderIdKey),
                settings.Get(SettingsViewModel.SubagentModelIdKey),
                int.TryParse(settings.Get(SettingsViewModel.SubagentMaxPerConversationKey), out var perConversation)
                    ? Math.Clamp(perConversation, 1, 4) : 4);
        });
        services.AddSingleton(sp => new AgentTaskRegistry(
            sp.GetRequiredService<Func<SubagentRuntimeOptions>>(),
            sp.GetRequiredService<IActivityTracker>()));
        services.AddSingleton<ISubagentRunner>(sp => new PiSubagentRunner(
            sp.GetRequiredService<ProviderRegistry>(),
            sp.GetRequiredService<Func<SubagentRuntimeOptions>>(),
            sp.GetRequiredService<AgentTaskRegistry>()));
        services.AddSingleton<IPythonAutoReviewer>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsRepository>();
            var providers = sp.GetRequiredService<ProviderRegistry>();
            var http = sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.Byok);
            return new PythonAutoReviewer(
                () => PythonAutoReviewer.ResolveTarget(
                    providers,
                    settings.Get(SettingsViewModel.ReviewProviderIdKey),
                    settings.Get(SettingsViewModel.ReviewModelIdKey)),
                () => new OneShotCompletionClient(http));
        });
        services.AddSingleton<IToolAutoReviewer>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsRepository>();
            var providers = sp.GetRequiredService<ProviderRegistry>();
            var http = sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.Byok);
            return new ToolAutoReviewer(
                () => PythonAutoReviewer.ResolveTarget(
                    providers,
                    settings.Get(SettingsViewModel.ReviewProviderIdKey),
                    settings.Get(SettingsViewModel.ReviewModelIdKey)),
                () => new OneShotCompletionClient(http));
        });
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<PythonExecutionTool>(
            sp, (Action<string>)(line => DiagnosticLog.Write("python", line))));
        services.AddSingleton(sp => new WebBridgeClient(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.Byok)));
        services.AddSingleton<BrowserControlTool>();
        // 浏览器流水账落在 settings 表里而不是新开一张表：200 条元数据的 JSON 很小，
        // 而这份记录的价值在于「重启之后还在」——出事之后才去翻的记录已经晚了。
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsRepository>();
            return new BrowserActivityLog(
                load: () => settings.Get(BrowserActivityStorageKey),
                save: json => settings.Set(BrowserActivityStorageKey, json));
        });
        // Local memory. The files are the source of truth and the index beside
        // them is rebuildable, so the store is constructed without touching the
        // database and the repository never becomes something the user can lose.
        services.AddSingleton(_ => new MemoryFileStore());
        services.AddSingleton(sp => new MemoryService(
            sp.GetRequiredService<MemoryFileStore>(),
            sp.GetRequiredService<MemoryIndexRepository>(),
            sp.GetRequiredService<SettingsViewModel>()));
        services.AddSingleton<IMemoryToolBackend>(sp => sp.GetRequiredService<MemoryService>());
        services.AddSingleton(sp => new MemoryConsolidator(
            sp.GetRequiredService<MemoryService>(),
            sp.GetRequiredService<MemoryIndexRepository>(),
            sp.GetRequiredService<MessageRepository>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<ProviderRegistry>(),
            () => new OneShotCompletionClient(
                sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.Byok)),
            sp.GetRequiredService<PersonaListViewModel>()));
        // MolaGPT's built-in search for signed-in accounts; goes through the MolaGPT client
        // so it carries the same User-Agent and device cookie as the rest of the account's traffic.
        services.AddSingleton(sp => new MolaAccountSearchClient(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.MolaGpt),
            sp.GetRequiredService<MolaGptAuthService>()));
        services.AddSingleton<IChatToolHost, ChatToolHost>();

        // ---- view models ---------------------------------------------------
        services.AddSingleton(sp =>
        {
            var library = new RoleLibraryViewModel(sp.GetRequiredService<SettingsRepository>(),
                sp.GetRequiredService<PersonaRepository>(), sp.GetRequiredService<ConversationRepository>());
            // 「沿用个人资料」. Resolved lazily — the file is the truth and the user
            // may have edited it since the app started — and deliberately only the
            // name: see UserPersona.ProfileId.
            library.ProfileSource = () =>
            {
                var name = sp.GetRequiredService<MemoryService>().Profile().Get(MemoryProfile.PreferredName);
                return string.IsNullOrWhiteSpace(name)
                    ? null
                    : new Core.Models.UserPersona { Id = Core.Models.UserPersona.ProfileId, Name = name };
            };
            return library;
        });
        services.AddSingleton(sp => new PersonaListViewModel(sp.GetRequiredService<PersonaRepository>(),
            library: sp.GetRequiredService<RoleLibraryViewModel>()));
        services.AddSingleton(sp =>
        {
            var list = new ConversationListViewModel(
                sp.GetRequiredService<ConversationRepository>(),
                sp.GetRequiredService<PersonaListViewModel>(),
                sp.GetRequiredService<SettingsRepository>())
            {
                SearchCloudAsync = sp.GetRequiredService<CloudSyncService>().SearchConversationsAsync
            };
            list.SearchFailed += (_, message) => sp.GetRequiredService<NotificationCenter>()
                .Error("对话搜索失败", message, key: "conversation-search");
            return list;
        });
        services.AddSingleton(sp =>
        {
            var chat = new ChatViewModel(
                sp.GetRequiredService<ProviderRegistry>(),
                sp.GetRequiredService<MessageRepository>(),
                sp.GetRequiredService<ConversationRepository>(),
                sp.GetRequiredService<PersonaListViewModel>(),
                sp.GetRequiredService<SettingsRepository>(),
                piSessionRoot: PiWorkSidecarLocator.SessionRoot);
            chat.AttachTaskRegistry(
                sp.GetRequiredService<AgentTaskRegistry>(),
                action =>
                {
                    if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
                    else Avalonia.Threading.Dispatcher.UIThread.Post(action);
                });
            return chat;
        });
        services.AddSingleton(sp => new NetworkProxyViewModel(
            sp.GetRequiredService<AppProxy>(),
            sp.GetRequiredService<SettingsRepository>(),
            sp.GetRequiredService<CredentialStore>(),
            sp.GetRequiredKeyedService<Func<HttpClient>>(HttpClientNames.Byok),
            line => DiagnosticLog.Write("proxy", line)));
        services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<CredentialStore>(),
            sp.GetRequiredService<SettingsRepository>(),
            sp.GetRequiredService<ProviderRegistry>(),
            sp.GetRequiredService<MolaGptProxyProvider>())
        {
            NetworkProxy = sp.GetRequiredService<NetworkProxyViewModel>()
        });
        services.AddSingleton(sp => new MemoryPageViewModel(
            sp.GetRequiredService<MemoryService>(),
            sp.GetRequiredService<MemoryConsolidator>(),
            sp.GetRequiredService<SettingsViewModel>(),
            action =>
            {
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
                else Avalonia.Threading.Dispatcher.UIThread.Post(action);
            }));
        services.AddSingleton(sp => new PersonalizationViewModel(
            sp.GetRequiredService<MolaPersonalizationService>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<MolaGptProxyProvider>()));
        services.AddSingleton<SkillManager>();
        services.AddSingleton(sp => new SkillsViewModel(
            sp.GetRequiredService<SkillManager>(),
            sp.GetRequiredService<SettingsRepository>(),
            sp.GetRequiredService<SettingsViewModel>()));
        services.AddSingleton(sp => new ComposerViewModel(
            sp.GetRequiredService<ChatViewModel>(),
            sp.GetRequiredService<BackgroundStreamService>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<PersonaListViewModel>(),
            sp.GetRequiredService<AttachmentStore>(),
            sp.GetRequiredService<SkillsViewModel>(),
            sp.GetRequiredService<MemoryService>(),
            sp.GetRequiredService<MemoryConsolidator>(),
            sp.GetRequiredService<ApprovalModeState>(),
            sp.GetRequiredService<IActivityTracker>()));
        services.AddSingleton(sp => new MainViewModel(
            sp.GetRequiredService<ConversationListViewModel>(),
            sp.GetRequiredService<ChatViewModel>(),
            sp.GetRequiredService<ComposerViewModel>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<PersonaListViewModel>(),
            sp.GetRequiredService<BackgroundStreamService>()));
        services.AddSingleton<AccountSessionCoordinator>();
        services.AddSingleton(sp => new AppNotificationService(
            conversationId =>
            {
                var conversations = sp.GetRequiredService<ConversationListViewModel>();
                if (string.Equals(conversations.SelectedId, conversationId, StringComparison.Ordinal))
                    conversations.SelectedId = null;
                conversations.SelectById(conversationId);
            },
            onWake: () => sp.GetRequiredService<AppPowerModeCoordinator>().NotifyForegroundIntent("toast-click")));

        // ---- runtime / updates ---------------------------------------------
        services.AddSingleton(sp => new UpdateCheckService(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.Byok),
            Environment.GetEnvironmentVariable("MOLAGPT_UPDATE_API_URL"),
            Environment.GetEnvironmentVariable("MOLAGPT_UPDATE_MANIFEST_URL")));
        services.AddSingleton(sp => new AppAutoUpdateService(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.Byok)));
        services.AddSingleton(sp => new PythonRuntimeManager(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.Byok),
            Environment.GetEnvironmentVariable("MOLAGPT_PYTHON_RUNTIME_MANIFEST_URL")));
        services.AddSingleton<PythonRuntimeSetup>();
        services.AddSingleton<AgentRuntimeSetup>();
        services.AddSingleton<NotificationCenter>();
        services.AddSingleton(sp => new ImageWorkbenchTasks(
            sp.GetRequiredService<ImageGenerationTool>(),
            sp.GetRequiredService<AttachmentStore>(),
            sp.GetRequiredService<ConversationRepository>(),
            sp.GetRequiredService<MessageRepository>(),
            sp.GetRequiredService<ConversationListViewModel>(),
            sp.GetRequiredService<NotificationCenter>(),
            sp.GetRequiredService<IActivityTracker>(),
            message => DiagnosticLog.Write("images", message)));
        services.AddSingleton<StorageService>();
        services.AddTransient<LoginWindow>();
        services.AddTransient(sp => new SettingsWindow(
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<MolaGptAuthService>(),
            sp.GetRequiredService<CloudSyncService>(),
            sp.GetRequiredService<ConversationListViewModel>(),
            sp.GetRequiredService<AgentBridgeStatusViewModel>(),
            sp.GetRequiredService<PersonaListViewModel>(),
            sp.GetRequiredService<McpClientManager>(),
            sp.GetRequiredService<ImageGenerationTool>(),
            sp.GetRequiredService<PythonRuntimeManager>(),
            sp.GetRequiredService<PythonRuntimeSetup>(),
            sp.GetRequiredService<PiSidecarRuntimeManager>(),
            sp.GetRequiredService<AgentRuntimeSetup>(),
            sp.GetRequiredService<NotificationCenter>(),
            sp.GetRequiredService<SkillsViewModel>(),
            sp.GetRequiredService<BrowserActivityLog>(),
            sp.GetRequiredService<WebBridgeClient>(),
            sp.GetRequiredKeyedService<Func<HttpClient>>(HttpClientNames.Byok),
            sp.GetRequiredService<ProviderConnection>(),
            sp.GetRequiredService<ProviderRegistry>(),
            sp.GetRequiredService<ProviderRestorer>(),
            sp.GetRequiredService<PiByokProviderFactory>(),
            sp.GetRequiredService<PersonalizationViewModel>(),
            sp.GetRequiredService<MemoryPageViewModel>(),
            sp.GetRequiredService<PersonalDataService>(),
            sp.GetRequiredService<MolaAccountSearchClient>(),
            sp.GetRequiredService<RemoteWorkSettingsViewModel>(),
            sp.GetRequiredService<StorageService>()));
        services.AddSingleton(sp => new SponsorsService(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.MolaGpt)));
        services.AddSingleton(sp => new MembershipService(
            sp.GetRequiredKeyedService<HttpClient>(HttpClientNames.MolaGpt),
            sp.GetRequiredService<MolaGptAuthService>()));

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        // Attachment intake needs an imaging stack; give it the Skia one.
        AttachmentIntake.ImageNormalizer = SkiaImageNormalizer.Process;

        return provider;
    }
}
