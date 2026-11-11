using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Net;

namespace MolaGPT.Desktop.Services;

public sealed class MembershipService(HttpClient http, MolaGptAuthService auth)
{
    private const string PortalEndpoint = "https://chatgpt.wljay.cn/v2/api/auth/membership_portal.php";

    public async Task<IReadOnlyList<MembershipProduct>> GetProductsAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await http.GetAsync("https://chatgpt.wljay.cn/v2/api/auth/membership_products.php", timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ProductsResponse>(cancellationToken: timeout.Token).ConfigureAwait(false);
        return payload is { Success: true, Products: not null }
            ? payload.Products : throw new InvalidOperationException("无法加载支持方案。");
    }

    public async Task<Uri> CreatePortalAsync(string? productKey = null, CancellationToken ct = default)
    {
        var generation = auth.SessionGeneration;
        var token = auth.CurrentJwt ?? throw new MolaGptAuthExpiredException();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, PortalEndpoint)
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<PortalResponse>(cancellationToken: timeout.Token)
            .ConfigureAwait(false);
        if (generation != auth.SessionGeneration)
            throw new OperationCanceledException("账户已切换，原请求已取消。");
        if (payload is not { Success: true } || !Uri.TryCreate(payload.Url, UriKind.Absolute, out var url))
            throw new InvalidOperationException(payload?.Message ?? "暂时无法打开支持页面，请稍后重试。");

        NetworkSecurity.RequireMolaGptAccountOrigin(url, "MolaGPT 支持页面");
        if (url.AbsolutePath != "/v2/membership" || url.Query.Length != 0
            || !url.Fragment.StartsWith("#ticket=", StringComparison.Ordinal) || url.Fragment.Length <= 8)
            throw new InvalidOperationException("服务器返回的支持页面地址无效。");
        return productKey is null ? url : new UriBuilder(url) { Query = "product_key=" + Uri.EscapeDataString(productKey) }.Uri;
    }

    private sealed record ProductsResponse
    {
        [JsonPropertyName("success")] public bool Success { get; init; }
        [JsonPropertyName("products")] public List<MembershipProduct>? Products { get; init; }
    }

    private sealed record PortalResponse
    {
        [JsonPropertyName("success")] public bool Success { get; init; }
        [JsonPropertyName("url")] public string? Url { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
    }
}

public sealed record MembershipProduct
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("price_cny")] public required string Price { get; init; }
    [JsonPropertyName("duration_days")] public int DurationDays { get; init; }
    public string Title => $"{Name} · ¥{Price} / {DurationDays} 天";
}
