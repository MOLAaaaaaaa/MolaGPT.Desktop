using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Agents.Relay;
using MolaGPT.Core.Chat.Attachments;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Chat.Tools.Mcp;
using MolaGPT.Core.Chat.Tools.PythonExecution;
using MolaGPT.Core.Chat.Tools.Vision;
using MolaGPT.Desktop.Services;
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
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        // ---- storage -------------------------------------------------------
        services.AddSingleton(_ => new MolaGptDatabase(MolaGptDatabase.DefaultPath()));
        services.AddSingleton<ConversationRepository>();
        services.AddSingleton<MessageRepository>();
        services.AddSingleton<SettingsRepository>();
        services.AddSingleton<ProviderRepository>();
        services.AddSingleton<PersonaRepository>();
        services.AddSingleton(_ => new AttachmentStore());

        services.AddSingleton(_ => new CredentialStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT", "creds.json")));

        // ---- http ----------------------------------------------------------
        // One CookieContainer shared by the molagpt client so Cloudflare's
        // __cf_bm and the backend's mola_did cookie persist across
        // warmup → login → chat. The User-Agent is hashed into JWT.ua and must
        // stay constant for the process lifetime.
        services.AddSingleton<CookieContainer>();

        services.AddHttpClient(HttpClientNames.MolaGpt, (_, client) =>
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
            .ConfigurePrimaryHttpMessageHandler(sp => new HttpClientHandler
            {
                CookieContainer = sp.GetRequiredService<CookieContainer>(),
                UseCookies = true,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All
            })
            .SetHandlerLifetime(TimeSpan.FromHours(24));

        services.AddHttpClient(HttpClientNames.Byok, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentProvider.FixedUa);
        });

        // ---- auth / providers ---------------------------------------------
        services.AddSingleton(sp => new MolaGptAuthService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.MolaGpt),
            sp.GetRequiredService<CredentialStore>()));

        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton(sp => new MolaGptProxyProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.MolaGpt),
            sp.GetRequiredService<MolaGptAuthService>()));

        services.AddSingleton(sp => new CloudSyncService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.MolaGpt),
            sp.GetRequiredService<MolaGptAuthService>(),
            sp.GetRequiredService<ConversationRepository>(),
            sp.GetRequiredService<MessageRepository>(),
            sp.GetRequiredService<SettingsRepository>()));

        services.AddSingleton(sp => new ConversationTitleService(
            sp.GetRequiredService<ConversationRepository>(),
            sp.GetRequiredService<MessageRepository>(),
            sp.GetRequiredService<ProviderRegistry>(),
            sp.GetRequiredService<SettingsRepository>(),
            () => sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Byok),
            (userText, assistantText, ct) =>
                sp.GetRequiredService<CloudSyncService>()
                    .GenerateMolaGptTitleAsync(userText, assistantText, ct),
            message => DiagnosticLog.Write("title", message)));

        // ---- local tool gateway (Work mode) --------------------------------
        services.AddSingleton(sp => new PiSidecarRuntimeManager(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.MolaGpt)));
        services.AddSingleton(sp => new PiWorkSidecarLocator(
            () => sp.GetRequiredService<PiSidecarRuntimeManager>().GetCompatibleInstalled()));
        // One runtime for the whole app: it owns the loopback shim, the tool bridge
        // and the capped sidecar pool. Per-provider runtimes would put the memory
        // ceiling back where it was, since nothing would bound the total.
        services.AddSingleton(sp => new PiRuntime(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Byok),
            line => DiagnosticLog.Write("pi-runtime", line)));
        services.AddSingleton<MolaGptLocalToolsRegistrar>();
        services.AddSingleton(sp => new PiByokProviderFactory(
            sp.GetRequiredService<PiWorkSidecarLocator>(),
            sp.GetRequiredService<IChatToolHost>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<PiRuntime>(),
            line => DiagnosticLog.Write("pi-byok", line)));

        services.AddSingleton<BackgroundStreamService>();
        services.AddSingleton(sp => new McpHttpClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Byok)));
        services.AddSingleton<McpClientManager>();

        // ---- Agent Bridge --------------------------------------------------
        services.AddSingleton<AgentCliResolver>();
        services.AddSingleton<DesktopAgentConfigProvider>();
        services.AddSingleton<IAgentConfigProvider>(sp => sp.GetRequiredService<DesktopAgentConfigProvider>());
        services.AddSingleton<IAgentBackend, ClaudeCodeBackend>();
        services.AddSingleton<IAgentBackend, CodexBackend>();
        services.AddSingleton(sp => new AgentSessionManager(
            sp.GetServices<IAgentBackend>(),
            sp.GetRequiredService<AgentCliResolver>(),
            sp.GetRequiredService<IAgentConfigProvider>()));
        services.AddSingleton<AgentHistoryReader>();
        services.AddSingleton(sp => new AgentBridgeService(
            sp.GetRequiredService<AgentSessionManager>(),
            sp.GetRequiredService<AgentHistoryReader>(),
            sp.GetRequiredService<IAgentConfigProvider>()));
        // The third constructor parameter is baseUrl, so keep these named.
        services.AddSingleton<IRelayProducer>(sp => new HttpRelayProducer(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.MolaGpt),
            sp.GetRequiredService<MolaGptAuthService>(),
            machineId: sp.GetRequiredService<IAgentConfigProvider>().MachineId,
            machineName: sp.GetRequiredService<IAgentConfigProvider>().MachineName));
        services.AddSingleton(sp => new AgentRelayClient(
            sp.GetRequiredService<AgentBridgeService>(),
            sp.GetRequiredService<IRelayProducer>(),
            sp.GetRequiredService<IAgentConfigProvider>(),
            line => DiagnosticLog.Write("AgentRelay", line)));
        services.AddSingleton<AgentBridgeStatusViewModel>();

        // ---- tools ---------------------------------------------------------
        services.AddSingleton(sp => new VisionProxyTool(
            sp.GetRequiredService<ProviderRegistry>(),
            () => sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Byok)));

        // Skia handles image normalization without a UI-framework imaging type.
        services.AddSingleton(sp => new ImageAnalysisTool(
            sp.GetRequiredService<ProviderRegistry>(),
            () => sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Byok),
            (bytes, mime, name) =>
            {
                var processed = SkiaImageNormalizer.Process(bytes, mime, name);
                return processed.Error is not null
                    ? NormalizedImage.Rejected(processed.Error)
                    : new NormalizedImage(processed.Bytes, processed.MimeType);
            }));

        services.AddSingleton(sp => new ImageGenerationTool(
            () => sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Byok),
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

        services.AddSingleton<ToolApprovalService>();
        services.AddSingleton<IPythonExecutionApprovalService>(sp => sp.GetRequiredService<ToolApprovalService>());
        services.AddSingleton<IToolApprovalService>(sp => sp.GetRequiredService<ToolApprovalService>());

        services.AddSingleton<PythonExecutionTool>();
        services.AddSingleton<IChatToolHost, ChatToolHost>();

        // ---- view models ---------------------------------------------------
        services.AddSingleton(sp => new PersonaListViewModel(sp.GetRequiredService<PersonaRepository>()));
        services.AddSingleton(sp => new ConversationListViewModel(
            sp.GetRequiredService<ConversationRepository>(),
            sp.GetRequiredService<PersonaListViewModel>(),
            sp.GetRequiredService<SettingsRepository>()));
        services.AddSingleton(sp => new ChatViewModel(
            sp.GetRequiredService<ProviderRegistry>(),
            sp.GetRequiredService<MessageRepository>(),
            sp.GetRequiredService<ConversationRepository>(),
            sp.GetRequiredService<PersonaListViewModel>(),
            sp.GetRequiredService<SettingsRepository>()));
        services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<ProviderRepository>(),
            sp.GetRequiredService<CredentialStore>(),
            sp.GetRequiredService<SettingsRepository>()));
        services.AddSingleton<SkillManager>();
        services.AddSingleton(sp => new SkillsViewModel(
            sp.GetRequiredService<SkillManager>(),
            sp.GetRequiredService<SettingsRepository>()));
        services.AddSingleton(sp => new ComposerViewModel(
            sp.GetRequiredService<ChatViewModel>(),
            sp.GetRequiredService<BackgroundStreamService>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<PersonaListViewModel>(),
            sp.GetRequiredService<AttachmentStore>(),
            sp.GetRequiredService<SkillsViewModel>()));
        services.AddSingleton(sp => new MainViewModel(
            sp.GetRequiredService<ConversationListViewModel>(),
            sp.GetRequiredService<ChatViewModel>(),
            sp.GetRequiredService<ComposerViewModel>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<PersonaListViewModel>(),
            sp.GetRequiredService<BackgroundStreamService>(),
            sp.GetRequiredService<MolaGptProxyProvider>()));
        services.AddSingleton<AccountSessionCoordinator>();
        services.AddSingleton(sp => new AppNotificationService(
            conversationId =>
            {
                var conversations = sp.GetRequiredService<ConversationListViewModel>();
                if (string.Equals(conversations.SelectedId, conversationId, StringComparison.Ordinal))
                    conversations.SelectedId = null;
                conversations.SelectById(conversationId);
            }));

        // ---- runtime / updates ---------------------------------------------
        services.AddSingleton(sp => new UpdateCheckService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Byok),
            Environment.GetEnvironmentVariable("MOLAGPT_UPDATE_API_URL"),
            Environment.GetEnvironmentVariable("MOLAGPT_UPDATE_MANIFEST_URL")));
        services.AddSingleton(sp => new AppAutoUpdateService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Byok)));
        services.AddSingleton(sp => new PythonRuntimeManager(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Byok),
            Environment.GetEnvironmentVariable("MOLAGPT_PYTHON_RUNTIME_MANIFEST_URL")));
        services.AddSingleton<NotificationCenter>();

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
