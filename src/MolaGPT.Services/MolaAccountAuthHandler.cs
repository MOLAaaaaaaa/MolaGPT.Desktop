using System.Net.Http;
using System.Net.Http.Headers;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Net;

namespace MolaGPT.Desktop.Services;

/// <summary>Obtains a current account token before a request is sent, without replaying it.</summary>
public sealed class MolaAccountAuthHandler(MolaGptAuthService auth) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is { Scheme: "Bearer", Parameter: { Length: > 0 } token })
        {
            NetworkSecurity.RequireMolaGptAccountOrigin(
                request.RequestUri ?? throw new InvalidOperationException("MolaGPT 请求地址为空。"),
                "MolaGPT 账户请求");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", await auth.GetAccessTokenAsync(token, cancellationToken).ConfigureAwait(false));
            // One ID per logical HTTP operation. A caller that explicitly retries
            // supplies the original key; this handler never replays a POST.
            if (request.Method == HttpMethod.Post && !request.Headers.Contains("Idempotency-Key"))
                request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        }
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            auth.LogoutIfCurrent(request.Headers.Authorization?.Parameter);
        return response;
    }
}
