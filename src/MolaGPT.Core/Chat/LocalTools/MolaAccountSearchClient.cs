using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MolaGPT.Core.Auth;

namespace MolaGPT.Core.Chat.LocalTools;

/// <summary>One <c>search_web</c> query as MolaGPT's own search understands it.</summary>
public sealed record MolaSearchQuery(string Query, string? Topic, string? TimeRange, string? Country, bool IncludeImages);

/// <summary>What one account-search call cost, and what the shared weekly allowance has left.</summary>
public sealed record MolaSearchCredits(double Charged, double Remaining, double Allowance, int WindowDays);

public sealed record MolaAccountSearchResponse(JsonElement Queries, MolaSearchCredits? Credits);

/// <summary>
/// A failure the user can act on. The message is already worded for the model and for
/// the tool card — sign in, switch provider, wait for the allowance — never a stack trace.
/// </summary>
public sealed class MolaSearchException(string message) : Exception(message);

/// <summary>
/// MolaGPT's built-in web search for signed-in accounts: one POST to
/// <c>account_search.php</c>, authenticated with the same long-lived JWT the chat
/// proxy uses. The server routes each query between its search providers and bills a
/// fixed number of credits per call against the same rolling allowance as the models,
/// so nothing here needs an API key.
/// </summary>
public sealed class MolaAccountSearchClient(HttpClient http, MolaGptAuthService auth, string? endpoint = null)
{
    public const string DefaultEndpoint = "https://chatgpt.wljay.cn/v2/api/auth/account_search.php";

    public const string NotSignedInMessage =
        "未登录 MolaGPT 账号，无法使用 MolaGPT Web Search。请先登录，或在「设置 → 联网搜索」中更换搜索服务。";

    private const string SessionExpiredMessage =
        "MolaGPT 登录已失效，无法使用 MolaGPT Web Search。请重新登录，或在「设置 → 联网搜索」中更换搜索服务。";

    private readonly Uri _endpoint = new(endpoint ?? DefaultEndpoint);

    public bool IsSignedIn => !string.IsNullOrWhiteSpace(auth.CurrentJwt);

    public string? Username => auth.CurrentUsername;

    public async Task<MolaAccountSearchResponse> SearchAsync(
        IReadOnlyList<MolaSearchQuery> queries,
        int maxResults,
        int snippetMaxCharacters,
        CancellationToken ct)
    {
        var jwt = auth.CurrentJwt;
        if (string.IsNullOrWhiteSpace(jwt)) throw new MolaSearchException(NotSignedInMessage);

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(new
            {
                queries = queries.Select(q => new
                {
                    query = q.Query,
                    topic = q.Topic,
                    time_range = q.TimeRange,
                    country = q.Country,
                    include_images = q.IncludeImages ? (bool?)true : null
                }),
                max_results = Math.Clamp(maxResults, 1, 6),
                snippet_max_chars = snippetMaxCharacters
            }, options: new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        // The JWT carries a hash of the User-Agent it was issued for; the server compares it on every call.
        req.Headers.UserAgent.Clear();
        req.Headers.UserAgent.ParseAdd(UserAgentProvider.FixedUa);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MolaSearchException("MolaGPT Web Search 超时，请稍后重试。");
        }
        catch (HttpRequestException ex)
        {
            throw new MolaSearchException("无法连接 MolaGPT Web Search：" + ex.Message);
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Do not clear a newer session if the user logged in while this
                // request was in flight.
                if (string.Equals(auth.CurrentJwt, jwt, StringComparison.Ordinal))
                    auth.Logout();
                throw new MolaSearchException(SessionExpiredMessage);
            }
            if (!resp.IsSuccessStatusCode) throw new MolaSearchException(ReadError(text, resp.StatusCode));

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("queries", out var queriesElement) || queriesElement.ValueKind != JsonValueKind.Array)
                throw new MolaSearchException("MolaGPT Web Search 返回了无法识别的结果。");
            return new MolaAccountSearchResponse(queriesElement.Clone(), ReadCredits(root));
        }
    }

    private static MolaSearchCredits? ReadCredits(JsonElement root)
    {
        if (!root.TryGetProperty("credits", out var credits) || credits.ValueKind != JsonValueKind.Object) return null;
        return new MolaSearchCredits(
            Number(credits, "charged"),
            Number(credits, "remaining"),
            Number(credits, "allowance"),
            credits.TryGetProperty("window_days", out var days) && days.TryGetInt32(out var d) ? d : 0);
    }

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static string ReadError(string body, HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(message.GetString()))
                return message.GetString()!;
        }
        catch (JsonException)
        {
        }

        return $"MolaGPT Web Search 失败（HTTP {(int)status}）。";
    }
}
