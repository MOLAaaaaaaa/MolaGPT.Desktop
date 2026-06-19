using System.Net.Http;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Chat.Tools;

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

    public MolaGptLocalToolsRegistrar(
        MolaGptAuthService auth,
        MolaGptProxyProvider proxy,
        ProviderRegistry registry,
        IHttpClientFactory httpClientFactory,
        IChatToolHost toolHost)
    {
        _auth = auth;
        _proxy = proxy;
        _registry = registry;
        _httpClientFactory = httpClientFactory;
        _toolHost = toolHost;
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrWhiteSpace(jwt))
        {
            _registry.Unregister(MolaGptProviderIds.LocalTools);
            return false;
        }

        var models = await _proxy.FetchLocalToolsModelsAsync(ct);
        if (models.Count == 0)
        {
            _registry.Unregister(MolaGptProviderIds.LocalTools);
            return false;
        }

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
