using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MolaGPT.Core.Auth;

namespace MolaGPT.Desktop.Services;

public sealed class ServerToolActions(HttpClient http, MolaGptAuthService auth)
{
    private const string Endpoint = "https://chatgpt.wljay.cn/v2/api/auth/tool_retry.php";

    private async Task<HttpResponseMessage> PostAsync(string ticket, string action, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.CurrentJwt ?? throw new InvalidOperationException("请先登录"));
        request.Content = JsonContent.Create(new { ticket, action });
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            throw new HttpRequestException(body?["message"]?.GetValue<string>() ?? $"请求失败（{(int)response.StatusCode}）");
        }
    }

    public async Task<string> RetryAsync(string ticket, CancellationToken ct)
    {
        using var response = await PostAsync(ticket, "retry", ct);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return body?["html"]?.GetValue<string>() ?? throw new InvalidOperationException("服务端未返回工具结果");
    }

    public async Task DownloadAsync(string ticket, Stream destination, CancellationToken ct)
    {
        using var response = await PostAsync(ticket, "file", ct);
        await response.Content.CopyToAsync(destination, ct);
    }
}
