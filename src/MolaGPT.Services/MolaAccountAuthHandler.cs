using System.Net.Http;
using System.Net.Http.Headers;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Net;

namespace MolaGPT.Desktop.Services;

/// <summary>Obtains a current account token before a request is sent, without replaying it.</summary>
public sealed class MolaAccountAuthHandler(MolaGptAuthService auth, Action<string>? log = null) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: { Length: > 0 } token })
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var generation = auth.SessionGeneration;
        NetworkSecurity.RequireMolaGptAccountOrigin(
            request.RequestUri ?? throw new InvalidOperationException("MolaGPT 请求地址为空。"),
            "MolaGPT 账户请求");
        var accessToken = await auth.GetAccessTokenAsync(token, cancellationToken).ConfigureAwait(false);
        if (generation != auth.SessionGeneration)
            throw new OperationCanceledException("账户已切换，原请求已取消。");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        // A caller that explicitly retries supplies the original operation key.
        if (request.Method == HttpMethod.Post && !request.Headers.Contains("Idempotency-Key"))
            request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            if (generation != auth.SessionGeneration)
                throw new OperationCanceledException("账户已切换，原请求已取消。");
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                && (await ChatApiErrorHelper.ReadAccountHttpErrorAsync(response, cancellationToken).ConfigureAwait(false))?.InvalidatesAccountSession == true)
            {
                var cleared = auth.LogoutIfCurrent(accessToken, generation);
                log?.Invoke($"request unauthorized session_cleared={cleared}");
            }
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
