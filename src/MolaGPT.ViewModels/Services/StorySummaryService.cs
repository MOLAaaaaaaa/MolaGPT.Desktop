using System.Text.Json;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Models;
using MolaGPT.Storage;

namespace MolaGPT.ViewModels.Services;

public sealed class StorySummaryDraft
{
    public string Summary { get; set; } = "";
    public List<StoryMemory> Memories { get; set; } = [];
}

public static class StorySummaryService
{
    public static async Task<ConversationRoleContext> GenerateAsync(
        OneShotCompletionClient client, IChatProvider provider, ProviderModel model,
        IReadOnlyList<MessageRow> history, ConversationRoleContext current, CancellationToken ct, bool incremental = false)
    {
        var target = (provider as IOneShotTarget)?.DescribeOneShot(model.Id)
            ?? throw new InvalidOperationException("当前模型不能整理剧情。");
        var window = new List<MessageRow>();
        var length = 0;
        var pending = incremental ? history.Skip(FindWatermark(history, current) + 1) : history.Reverse();
        foreach (var row in pending.Where(row => !current.ExcludedSummaryMessageIds.Contains(row.Id)).Take(80))
        {
            if (length + row.Content.Length > 40000) break;
            window.Add(row);
            length += row.Content.Length;
        }
        if (!incremental) window.Reverse();
        if (window.Count == 0) throw new InvalidOperationException("没有可整理的对话内容。");
        var prompt = "整理这段对话的剧情、当前场景、人物关系与已经发生的关键事件。只依据提供的内容，不续写故事。" +
            "返回 JSON：{\"summary\":\"简洁的剧情摘要\",\"memories\":[{\"text\":\"事件\",\"sourceMessageIds\":[\"消息 id\"],\"sourceQuote\":\"从该消息逐字引用的原文\"}]}。" +
            "每个事件都必须引用下方消息中的 id 和原文；最多 8 个事件。已有摘要仅作为背景。";
        var payload = RoleJson.Serialize(new
        {
            previousSummary = current.Summary,
            messages = window.Select(row => new { id = row.Id, role = row.Role, content = row.Content })
        });
        var response = await client.CompleteAsync(target, model.Id,
            [new ChatMessage(ChatMessage.RoleSystem, prompt), new ChatMessage(ChatMessage.RoleUser, payload)],
            maxTokens: 2048, useThinking: false, thinkingKind: model.ThinkingConfig?.Kind, ct: ct);
        response = response.Trim();
        if (response.StartsWith("```", StringComparison.Ordinal) && response.EndsWith("```", StringComparison.Ordinal))
            response = response[(response.IndexOf('\n') + 1)..^3].Trim();
        var draft = RoleJson.Deserialize<StorySummaryDraft>(response);
        if (string.IsNullOrWhiteSpace(draft.Summary)) throw new JsonException("没有生成剧情摘要。");
        var sources = window.ToDictionary(row => row.Id, row => row.Content);

        var grounded = SelectGrounded(draft.Memories, sources);
        var result = RoleJson.Deserialize<ConversationRoleContext>(RoleJson.Serialize(current));
        result.Summary = draft.Summary;
        result.LastSummarizedMessageId = window[^1].Id;
        result.SummarySourceIds = current.SummarySourceIds.Concat(sources.Keys).Distinct(StringComparer.Ordinal).ToList();
        result.Memories = current.Memories
            .Where(memory => memory.Pinned || memory.SourceQuote.Length == 0 || !memory.SourceMessageIds.Any(sources.ContainsKey))
            .Concat(grounded).GroupBy(memory => memory.Text.Trim(), StringComparer.Ordinal)
            .Select(group => group.First()).ToList();
        return result;
    }

    internal static int FindWatermark(IReadOnlyList<MessageRow> history, ConversationRoleContext context)
    {
        if (context.Summary.Length == 0) return -1;
        for (var i = history.Count - 1; i >= 0; i--)
            if (context.LastSummarizedMessageId is { } id ? history[i].Id == id : context.SummarySourceIds.Contains(history[i].Id)) return i;
        return -1;
    }

    /// <summary>
    /// Keeps only the events that quote a message verbatim.
    ///
    /// This check is the one thing standing between "the model summarised the
    /// story" and "the model invented an event and the app filed it as something
    /// that happened", so it stays strict — a paraphrased quote is indistinguishable
    /// from a fabricated one from here.
    ///
    /// It filters per event rather than rejecting the batch. Models paraphrase
    /// often enough that one loose quote used to throw away a whole run, summary
    /// included, and 「整理剧情」 mostly just produced an error banner.
    /// </summary>
    internal static List<StoryMemory> SelectGrounded(
        IEnumerable<StoryMemory> memories, IReadOnlyDictionary<string, string> sources) =>
        memories.Where(memory =>
            !string.IsNullOrWhiteSpace(memory.Text) && !string.IsNullOrWhiteSpace(memory.SourceQuote)
            && memory.SourceMessageIds.Count > 0 && memory.SourceMessageIds.All(sources.ContainsKey)
            && memory.SourceMessageIds.Any(id => sources[id].Contains(memory.SourceQuote, StringComparison.Ordinal)))
            .ToList();
}
