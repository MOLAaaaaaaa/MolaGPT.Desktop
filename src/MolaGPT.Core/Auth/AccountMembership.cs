using System.Globalization;
using System.Text.Json.Serialization;

namespace MolaGPT.Core.Auth;

/// <summary>A server-owned entitlement snapshot, used only for account presentation.</summary>
public sealed record AccountMembership
{
    [JsonPropertyName("group_key")] public string? GroupKey { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("tier")] public string Tier { get; init; } = "registered";
    [JsonPropertyName("expires_at")] public long? ExpiresAt { get; init; }
    [JsonPropertyName("server_time")] public long ServerTime { get; init; }
    [JsonPropertyName("policy_version")] public int PolicyVersion { get; init; }
    [JsonPropertyName("donor_until")] public long? DonorUntil { get; init; }
    [JsonPropertyName("premium_until")] public long? PremiumUntil { get; init; }
    [JsonPropertyName("source_status")] public string SourceStatus { get; init; } = "none";
    [JsonPropertyName("is_permanent")] public bool IsPermanent { get; init; }

    public string TierLabel => NormalizeSupporterLabel(DisplayName) ?? Tier switch
    {
        "premium" => "高级支持者",
        "donor" => "支持者",
        _ => "已注册用户"
    };

    private static string? NormalizeSupporterLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace("高级捐赠者", "高级支持者", StringComparison.Ordinal)
            .Replace("超级捐赠者", "超级支持者", StringComparison.Ordinal)
            .Replace("基本捐赠者", "基本支持者", StringComparison.Ordinal)
            .Replace("捐赠者", "支持者", StringComparison.Ordinal)
            .Replace("捐赠", "支持", StringComparison.Ordinal)
            .Replace("会员", "支持者", StringComparison.Ordinal);
        while (normalized.Contains("支持者支持者", StringComparison.Ordinal))
            normalized = normalized.Replace("支持者支持者", "支持者", StringComparison.Ordinal);
        return normalized;
    }

    public string ExpiryLabel
    {
        get
        {
            if (SourceStatus == "legacy_pending_review") return "历史支持权益待核对";
            if (Tier is not ("donor" or "premium"))
                return SourceStatus == "expired" ? "支持期限已到期" : string.Empty;
            if (IsPermanent) return "长期权益";
            if (ExpiresAt is > 0 and <= 253402300799)
                return "有效期至 " + DateTimeOffset.FromUnixTimeSeconds(ExpiresAt.Value)
                    .ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return "有效期请在支持页面查看";
        }
    }
}
