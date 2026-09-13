using System.Net;
using MolaGPT.Core.Auth;

namespace MolaGPT.Desktop.Services;

public sealed class MolaDeviceCookieHandler : DelegatingHandler
{
    private static readonly Uri Origin = new("https://chatgpt.wljay.cn/");
    private const string StorageKey = "molagpt.device_cookie";
    private readonly CookieContainer _cookies;
    private readonly CredentialStore _credentials;

    public MolaDeviceCookieHandler(CookieContainer cookies, CredentialStore credentials)
    {
        _cookies = cookies;
        _credentials = credentials;
        var saved = credentials.LoadSecret(StorageKey);
        if (!string.IsNullOrEmpty(saved))
            cookies.Add(Origin, new Cookie("mola_did", saved, "/") { Secure = true, HttpOnly = true });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.RequestUri is { Scheme: "https", Host: "chatgpt.wljay.cn" })
        {
            var value = _cookies.GetCookies(Origin)["mola_did"]?.Value;
            if (!string.IsNullOrEmpty(value) && value != _credentials.LoadSecret(StorageKey))
                _credentials.SaveSecret(StorageKey, value);
        }
        return response;
    }
}
