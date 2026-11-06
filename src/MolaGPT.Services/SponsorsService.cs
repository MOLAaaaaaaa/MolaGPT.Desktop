using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace MolaGPT.Desktop.Services;

public static class CommunityLinks
{
    public const string GitHubUrl = "https://github.com/MOLAaaaaaaa/MolaGPT.Desktop";
    public const string AfdianHomeUrl = "https://afdian.com/a/molagpt";
    public const string AfdianPlan6Url = "https://afdian.com/order/create?plan_id=c459a9a2558011f0ab0d52540025c377&product_type=0";
    public const string AfdianPlan66Url = "https://afdian.com/order/create?plan_id=a01180ce558011f099fd52540025c377&product_type=0";

    public static bool TryOpenBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record SponsorItem(string Name, string PlanName, string DonateDateText);

public sealed record SponsorsSnapshot(
    int TotalCount,
    string LatestDateText,
    IReadOnlyList<SponsorItem> Items);

public sealed class SponsorsService
{
    public const string DefaultApiUrl = "https://chatgpt.wljay.cn/v2/api/sponsors.php?page=1";

    private readonly HttpClient _http;
    private readonly string _apiUrl;

    public SponsorsService(HttpClient http, string? apiUrl = null)
    {
        _http = http;
        _apiUrl = string.IsNullOrWhiteSpace(apiUrl) ? DefaultApiUrl : apiUrl;
    }

    public async Task<SponsorsSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        using var response = await _http.GetAsync(_apiUrl, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var ec = root.TryGetProperty("ec", out var ecProp) && ecProp.ValueKind == JsonValueKind.Number
            ? ecProp.GetInt32()
            : 0;
        if (ec != 200 || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            var msg = root.TryGetProperty("em", out var emProp) && emProp.ValueKind == JsonValueKind.String
                ? emProp.GetString()
                : null;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(msg) ? "服务返回异常" : msg);
        }

        var items = new List<SponsorItem>();
        long latestTs = 0;

        if (data.TryGetProperty("list", out var listEl) && listEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in listEl.EnumerateArray())
            {
                var name = "匿名";
                if (entry.TryGetProperty("user", out var userEl)
                    && userEl.ValueKind == JsonValueKind.Object
                    && userEl.TryGetProperty("name", out var nameEl)
                    && nameEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(nameEl.GetString()))
                {
                    name = nameEl.GetString()!;
                }

                var planName = "发电支持";
                if (entry.TryGetProperty("current_plan", out var planEl)
                    && planEl.ValueKind == JsonValueKind.Object
                    && planEl.TryGetProperty("name", out var planNameEl)
                    && planNameEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(planNameEl.GetString()))
                {
                    planName = planNameEl.GetString()!;
                }

                var lastPay = ReadUnixSeconds(entry, "last_pay_time");
                var firstPay = ReadUnixSeconds(entry, "first_pay_time");
                var created = ReadUnixSeconds(entry, "create_time");
                var effectiveTs = lastPay ?? firstPay ?? created;
                if (effectiveTs is > 0 && effectiveTs.Value > latestTs)
                    latestTs = effectiveTs.Value;

                items.Add(new SponsorItem(
                    name,
                    planName,
                    FormatUnixDate(lastPay ?? effectiveTs)));
            }
        }

        var totalCount = data.TryGetProperty("total_count", out var totalProp) && totalProp.ValueKind == JsonValueKind.Number
            ? totalProp.GetInt32()
            : items.Count;

        return new SponsorsSnapshot(
            totalCount,
            latestTs > 0 ? FormatUnixDate(latestTs) : "—",
            items);
    }

    private static long? ReadUnixSeconds(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var n) && n > 0)
            return n;
        if (prop.ValueKind == JsonValueKind.String
            && long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
            return parsed;
        return null;
    }

    private static string FormatUnixDate(long? unixSeconds)
    {
        if (unixSeconds is not > 0) return "—";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value)
                .ToLocalTime()
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "—";
        }
    }
}
