using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using MolaGPT.Core.Auth;

namespace MolaGPT.Desktop.Services;

public sealed class MolaDeviceCookieHandler : DelegatingHandler
{
    private static readonly Uri Origin = new("https://chatgpt.wljay.cn/");
    private const string StorageKey = "molagpt.device_cookie";
    private const int MaxTransientRetries = 1;
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMilliseconds(500);
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
        // A reset/early response is safe to retry for read-only requests. Do not
        // retry POST here: the server may have accepted it before the connection
        // was closed, so repeating it could duplicate a chat or sync operation.
        var retryable = IsRetryableMethod(request.Method);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                SaveDeviceCookie(request);
                return response;
            }
            catch (Exception ex) when (
                retryable
                && attempt < MaxTransientRetries
                && IsTransientTransportFailure(ex))
            {
                await Task.Delay(TransientRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void SaveDeviceCookie(HttpRequestMessage request)
    {
        if (request.RequestUri is { Scheme: "https", Host: "chatgpt.wljay.cn" })
        {
            var value = _cookies.GetCookies(Origin)["mola_did"]?.Value;
            if (!string.IsNullOrEmpty(value) && value != _credentials.LoadSecret(StorageKey))
                _credentials.SaveSecret(StorageKey, value);
        }
    }

    private static bool IsRetryableMethod(HttpMethod method) =>
        string.Equals(method.Method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method.Method, "HEAD", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransientTransportFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException
                {
                    HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded
                }
                || current is HttpIOException
                {
                    HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded
                })
                return true;

            if (current is SocketException
                {
                    SocketErrorCode: SocketError.ConnectionReset
                        or SocketError.ConnectionAborted
                        or SocketError.TimedOut
                })
                return true;
        }

        return false;
    }
}
