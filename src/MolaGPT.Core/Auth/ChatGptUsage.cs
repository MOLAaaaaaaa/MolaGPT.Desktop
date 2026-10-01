using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MolaGPT.Core.Auth;

/// <summary>One of the plan's rolling limits.</summary>
/// <param name="WindowMinutes">Length of the window: 300 for the five-hour limit,
/// 10080 for the weekly one.</param>
/// <param name="UsedPercent">0–100.</param>
/// <param name="ResetsAt">When the window rolls over, when OpenAI says.</param>
public sealed record ChatGptUsageWindow(int WindowMinutes, double UsedPercent, DateTimeOffset? ResetsAt);

/// <summary>Where a ChatGPT plan stands against its limits, shortest window first.</summary>
public sealed record ChatGptUsage(IReadOnlyList<ChatGptUsageWindow> Windows, bool LimitReached)
{
    /// <summary>The Codex usage endpoint's <c>rate_limit</c> block.</summary>
    internal static ChatGptUsage? FromStatus(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("rate_limit", out var limit)
            || limit.ValueKind != JsonValueKind.Object)
            return null;

        var windows = new List<ChatGptUsageWindow>();
        foreach (var name in new[] { "primary_window", "secondary_window" })
        {
            if (!limit.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            if (!window.TryGetProperty("used_percent", out var used) || !used.TryGetDouble(out var percent)) continue;
            var seconds = window.TryGetProperty("limit_window_seconds", out var length) && length.TryGetInt32(out var s) ? s : 0;
            var resetAt = window.TryGetProperty("reset_at", out var reset) && reset.TryGetInt64(out var at)
                ? DateTimeOffset.FromUnixTimeSeconds(at)
                : (DateTimeOffset?)null;
            windows.Add(new ChatGptUsageWindow(seconds / 60, percent, resetAt));
        }

        var reached = limit.TryGetProperty("limit_reached", out var flag) && flag.ValueKind == JsonValueKind.True
                      || limit.TryGetProperty("allowed", out var allowed) && allowed.ValueKind == JsonValueKind.False;
        return Create(windows, reached);
    }

    /// <summary>The same figures as the <c>x-codex-*</c> headers on every Codex response.</summary>
    internal static ChatGptUsage? FromHeaders(HttpResponseHeaders headers)
    {
        var windows = new List<ChatGptUsageWindow>();
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (Header(headers, $"x-codex-{name}-used-percent") is not { } used
                || !double.TryParse(used, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                continue;
            var minutes = int.TryParse(Header(headers, $"x-codex-{name}-window-minutes"), out var m) ? m : 0;
            var resetAt = long.TryParse(Header(headers, $"x-codex-{name}-reset-at"), out var at)
                ? DateTimeOffset.FromUnixTimeSeconds(at)
                : (DateTimeOffset?)null;
            windows.Add(new ChatGptUsageWindow(minutes, percent, resetAt));
        }
        return Create(windows, reached: false);
    }

    private static ChatGptUsage? Create(List<ChatGptUsageWindow> windows, bool reached)
    {
        if (windows.Count == 0) return null;
        windows.Sort((a, b) => a.WindowMinutes.CompareTo(b.WindowMinutes));
        return new ChatGptUsage(windows, reached || windows.Any(w => w.UsedPercent >= 100));
    }

    private static string? Header(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;
}
