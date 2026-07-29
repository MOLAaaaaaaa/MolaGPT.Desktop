using System.Diagnostics;
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
        PiWorkSidecarLocator piLocator)
    {
        _auth = auth;
        _proxy = proxy;
        _registry = registry;
        _httpClientFactory = httpClientFactory;
        _toolHost = toolHost;
        _piLocator = piLocator;
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrWhiteSpace(jwt))
        {
            _registry.Unregister(MolaGptProviderIds.LocalTools);
            RetireActivePi();
            return false;
        }

        var models = await _proxy.FetchLocalToolsModelsAsync(ct);
        if (models.Count == 0)
        {
            _registry.Unregister(MolaGptProviderIds.LocalTools);
            RetireActivePi();
            return false;
        }

        if (TryRegisterPiWork(models))
            return true;

        RetireActivePi();
        var provider = new OpenAICompatibleProvider(
            MolaGptProviderIds.LocalTools,
            MolaGptProxyProvider.LocalToolsDisplayName,
            ValidateMolaGptBaseUrl(_proxy.BaseUrl),
            () => _auth.CurrentJwt,
            models,
            _httpClientFactory.CreateClient(App.MolaGptHttpClient),
            _toolHost)
        {
            Kind = ProviderKind.MolaGptLocalTools,
            ChatPath = MolaGptProxyProvider.LocalToolsChatPath,
            UnauthorizedHandler = _ =>
            {
                _auth.Logout();
                throw new MolaGptAuthExpiredException();
            }
        };

        _registry.Register(provider);
        return true;
    }

    /// <summary>
    /// Opt-in path: register Work backed by the Pi harness under the same provider
    /// id, so the whole UI (mode slider, model picker, tool cards) is unchanged and
    /// only the engine differs. Returns false — leaving the caller on the existing
    /// provider — when the flag is off or the machine has no sidecar/Node.
    /// </summary>
    private bool TryRegisterPiWork(IReadOnlyList<ProviderModel> models)
    {
        if (!_piLocator.Enabled) return false;

        PiSidecarAssets? assets;
        try { assets = _piLocator.TryResolve(); }
        catch (Exception ex)
        {
            Debug.WriteLine("[pi-work] 定位 sidecar 失败，回退到内置 Work：" + ex.Message);
            return false;
        }
        if (assets is null) return false;

        // The relay URL and the account JWT stay in this process: the shim inside
        // PiWorkProvider stamps the live token per request, so nothing sensitive is
        // handed to the Node child. Model comes from the request, since it's baked
        // into the sidecar at spawn (a model switch respawns it).
        var endpoint = new Uri(new Uri(ValidateMolaGptBaseUrl(_proxy.BaseUrl)), MolaGptProxyProvider.LocalToolsChatPath).ToString();
        var config = new PiWorkProviderConfig(
            MolaGptProviderIds.LocalTools,
            MolaGptProxyProvider.LocalToolsDisplayName,
            models,
            assets.NodePath,
            assets.CliJsPath,
            assets.ExtensionPath,
            assets.WorkingDirectory,
            // Pi's own session files live beside MolaGPT's other state, one per
            // conversation, so a respawned sidecar resumes instead of forgetting.
            PiWorkSidecarLocator.SessionRoot,
            request => new PiProviderCreds(
                endpoint,
                _ => Task.FromResult(_auth.CurrentJwt),
                request.ModelId,
                OnUnauthorized: () => _auth.Logout()));

        var pi = new PiWorkProvider(
            config,
            _toolHost,
            _httpClientFactory.CreateClient(App.MolaGptHttpClient),
            log: line => Debug.WriteLine("[pi-work] " + line));

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
            catch (Exception ex) { Debug.WriteLine("[pi-work] 释放 sidecar 失败：" + ex.Message); }
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
