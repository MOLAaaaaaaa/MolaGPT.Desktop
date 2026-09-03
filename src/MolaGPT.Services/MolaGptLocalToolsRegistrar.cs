using System.Net.Http;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Models;

namespace MolaGPT.Desktop.Services;

public sealed class MolaGptLocalToolsRegistrar
{
    private const string RequiredBaseHost = "chatgpt.wljay.cn";
    private const string RequiredBasePathPrefix = "/v2/";

    private readonly MolaGptAuthService _auth;
    private readonly MolaGptProxyProvider _proxy;
    private readonly ProviderRegistry _registry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IChatToolHost _toolHost;
    private readonly PiWorkSidecarLocator _piLocator;
    private readonly PiRuntime _runtime;

    /// <summary>The Pi provider currently registered, if any. Held so its sidecar
    /// processes and loopback listeners are torn down when Work is re-registered,
    /// logged out, or falls back to the in-process provider.</summary>
    private PiWorkProvider? _activePi;

    public MolaGptLocalToolsRegistrar(
        MolaGptAuthService auth,
        MolaGptProxyProvider proxy,
        ProviderRegistry registry,
        IHttpClientFactory httpClientFactory,
        IChatToolHost toolHost,
        PiWorkSidecarLocator piLocator,
        PiRuntime runtime)
    {
        _auth = auth;
        _proxy = proxy;
        _registry = registry;
        _httpClientFactory = httpClientFactory;
        _toolHost = toolHost;
        _piLocator = piLocator;
        _runtime = runtime;
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrWhiteSpace(jwt))
        {
            Deactivate();
            return false;
        }

        var models = await _proxy.FetchLocalToolsModelsAsync(ct);
        if (models.Count == 0)
        {
            Deactivate();
            return false;
        }

        if (TryRegisterPiWork(models))
            return true;

        // No runtime, no Work. There is no second engine to fall back to, and
        // pretending otherwise is what used to make this state invisible: the app
        // silently ran a different agent and nothing said so.
        Deactivate();
        return false;
    }

    public void Deactivate()
    {
        _registry.Unregister(MolaGptProviderIds.LocalTools);
        RetireActivePi();
    }

    /// <summary>
    /// Register Work backed by the Pi harness. Returns false when the machine has
    /// no usable runtime, which the caller turns into a visible failure rather than
    /// a quieter engine.
    /// </summary>
    private bool TryRegisterPiWork(IReadOnlyList<ProviderModel> models)
    {
        PiSidecarAssets? assets;
        try { assets = _piLocator.TryResolve(); }
        catch (Exception ex)
        {
            DiagnosticLog.Write("pi-work", "定位 Agent 运行时失败：" + ex.Message);
            return false;
        }
        if (assets is null)
        {
            DiagnosticLog.Write("pi-work", "未找到本地 Agent 运行时，Work 不可用。");
            return false;
        }

        // The relay URL and the account JWT stay in this process: the shim inside
        // PiWorkProvider stamps the live token per request, so nothing sensitive is
        // handed to the Node child. Model comes from the request, since it's baked
        // into the sidecar at spawn (a model switch respawns it).
        var endpoint = new Uri(new Uri(ValidateMolaGptBaseUrl(_proxy.BaseUrl)), MolaGptProxyProvider.LocalToolsChatPath).ToString();
        const string api = "openai-completions";
        var config = new PiWorkProviderConfig(
            MolaGptProviderIds.LocalTools,
            MolaGptProxyProvider.LocalToolsDisplayName,
            models,
            new PiSidecarSpec(
                MolaGptProviderIds.LocalTools,
                assets.NodePath,
                assets.CliJsPath,
                assets.ExtensionPath,
                assets.WorkingDirectory,
                // Pi's own session files live beside MolaGPT's other state, one per
                // conversation, so a sidecar can be pointed at any of them.
                PiWorkSidecarLocator.SessionRoot,
                PiModelCatalog.BuildJson(models, api, MolaGptProxyProvider.LocalToolsDisplayName, endpoint),
                models[0].Id,
                api,
                AuthHeader: true,
                Reasoning: models.Any(m => m.SupportsThinking)),
            request => new PiProviderCreds(
                endpoint,
                _ => Task.FromResult(_auth.CurrentJwt),
                request.ModelId,
                OnUnauthorized: () => _auth.Logout()));

        var pi = new PiWorkProvider(
            config,
            _toolHost,
            _httpClientFactory.CreateClient(HttpClientNames.MolaGpt),
            _runtime,
            // To the diagnostic log, not just the debugger: Work is where sidecar
            // trouble actually shows up, and a line only a debugger can see is one
            // nobody can send us from a shipped build.
            log: line => DiagnosticLog.Write("pi-work", line));

        RetireActivePi();
        _activePi = pi;
        _registry.Register(pi);
        return true;
    }

    /// <summary>Dispose the previously registered Pi provider (kills its sidecars).
    /// Fire-and-forget: teardown must never block a registry refresh.</summary>
    private void RetireActivePi()
    {
        var previous = Interlocked.Exchange(ref _activePi, null);
        if (previous is null) return;
        _ = Task.Run(async () =>
        {
            try { await previous.DisposeAsync(); }
            catch (Exception ex) { DiagnosticLog.Write("pi-work", "释放 sidecar 失败：" + ex.Message); }
        });
    }

    private static string ValidateMolaGptBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri))
            throw new InvalidOperationException("MolaGPT 本地工具服务地址无效。");

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MolaGPT 本地工具服务必须使用 HTTPS。");

        var hostAllowed = string.Equals(uri.Host, RequiredBaseHost, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + RequiredBaseHost, StringComparison.OrdinalIgnoreCase);
        if (!hostAllowed)
            throw new InvalidOperationException("MolaGPT 本地工具服务地址必须位于官方域名。");

        if (!uri.AbsolutePath.StartsWith(RequiredBasePathPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MolaGPT 本地工具服务地址必须位于 /v2/ 路径下。");

        return uri.ToString();
    }
}
