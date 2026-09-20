using System.Text.Json;
using MolaGPT.Storage;

namespace MolaGPT.ViewModels.Services;

/// <summary>What one model cost across a conversation.</summary>
public sealed record ModelSpend(string Model, double CostUsd, int Turns);

/// <summary>A conversation's total spend, and how it splits across models.</summary>
public sealed record ConversationSpend(double CostUsd, IReadOnlyList<ModelSpend> ByModel)
{
    public static readonly ConversationSpend Empty = new(0, []);

    public bool HasSpend => CostUsd > 0;
}

/// <summary>
/// Adds up the per-turn costs snapshotted into message metadata.
///
/// The number this produces is money actually spent, which is deliberately not
/// the same as "add up the answers currently on screen": a retried turn was paid
/// for once per attempt, and a branch the user regenerated away from was paid for
/// too. Both are counted. The UI has to say so, because the total will otherwise
/// look too high to anyone checking it by hand.
/// </summary>
public static class ConversationSpendCalculator
{
    /// <param name="rows">Every row of the conversation — <see cref="Storage.Repositories.MessageRepository.ListAll"/>,
    /// not the active-path query, or abandoned branches go missing from the bill.</param>
    public static ConversationSpend From(IEnumerable<MessageRow> rows)
    {
        var totals = new Dictionary<string, (double Cost, int Turns)>(StringComparer.OrdinalIgnoreCase);
        var total = 0d;

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Meta)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(row.Meta); }
            catch (JsonException) { continue; }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                var model = ReadModel(doc.RootElement) ?? "未知模型";

                foreach (var (cost, attemptModel) in ReadCosts(doc.RootElement, model))
                {
                    total += cost;
                    var current = totals.GetValueOrDefault(attemptModel);
                    totals[attemptModel] = (current.Cost + cost, current.Turns + 1);
                }
            }
        }

        var byModel = totals
            .Select(pair => new ModelSpend(pair.Key, pair.Value.Cost, pair.Value.Turns))
            .OrderByDescending(item => item.CostUsd)
            .ToList();
        return new ConversationSpend(total, byModel);
    }

    /// <summary>
    /// The message's own cost plus one per retry attempt. A retried turn stores the
    /// live answer in <c>response_stats</c> and the earlier ones under
    /// <c>retry.attempts</c>; the live answer is itself one of those entries, so it
    /// is only read when there is no retry list to read instead.
    /// </summary>
    private static IEnumerable<(double Cost, string Model)> ReadCosts(JsonElement meta, string model)
    {
        if (meta.TryGetProperty("retry", out var retry) && retry.ValueKind == JsonValueKind.Object
            && retry.TryGetProperty("attempts", out var attempts) && attempts.ValueKind == JsonValueKind.Array
            && attempts.GetArrayLength() > 0)
        {
            foreach (var attempt in attempts.EnumerateArray())
            {
                if (attempt.ValueKind != JsonValueKind.Object) continue;
                if (ReadCost(attempt) is { } cost)
                    yield return (cost, ReadModel(attempt) ?? model);
            }
            yield break;
        }

        if (ReadCost(meta) is { } own) yield return (own, model);
    }

    private static double? ReadCost(JsonElement holder) =>
        holder.TryGetProperty("response_stats", out var stats) && stats.ValueKind == JsonValueKind.Object
        && stats.TryGetProperty("costUsd", out var cost) && cost.ValueKind == JsonValueKind.Number
        && cost.TryGetDouble(out var value)
            ? value
            : null;

    private static string? ReadModel(JsonElement holder)
    {
        foreach (var name in (ReadOnlySpan<string>)["model", "model_label"])
        {
            if (holder.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
                && node.GetString() is { Length: > 0 } label)
                return label;
        }
        return null;
    }
}
