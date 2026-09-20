using System.Text;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Memory;
using MolaGPT.Core.Models;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.ViewModels.Services;

/// <summary>
/// What one consolidation pass did. The three "nothing happened" cases are kept
/// apart on purpose: half of all runs end without a single request being sent,
/// and folding those into 「没有需要记录的新内容」 leaves the user clicking a button
/// that never ran.
/// </summary>
public sealed record MemoryConsolidationReport(
    bool Ran,
    int ConversationsScanned,
    int Added,
    int Candidates,
    int Failures,
    string? BlockedReason = null,
    int TopicsOrganized = 0)
{
    public static MemoryConsolidationReport Blocked(string reason) => new(false, 0, 0, 0, 0, reason);

    public string Describe() => BlockedReason is { } reason
        ? reason
        : TopicsOrganized > 0
            ? $"已整理 {TopicsOrganized} 个主题，新增 {Added} 条记录。" + (Failures > 0 ? $" {Failures} 次整理未完成。" : "")
        : ConversationsScanned == 0 && Failures == 0
            ? "暂无待整理对话。"
            : Added == 0 && Candidates == 0
                ? Failures > 0
                    ? $"已整理 {ConversationsScanned} 段对话，{Failures} 次请求失败，未写入新记忆。"
                    : $"已检查 {ConversationsScanned} 段对话，无需新增记忆。"
                : $"已从 {ConversationsScanned} 段对话中记录 {Added} 条，另有 {Candidates} 条待确认。";
}

/// <summary>
/// Automatic learning. Window-based rather than turn-based: a turn-by-turn
/// extractor cannot see a correction made two messages later — 「下周去上海」 and
/// 「改成下下周」 would each be recorded as a standing fact.
///
/// Two model calls per window. The gate question is short, has no memories in
/// it and answers with one tag, so a window with nothing in it costs one small
/// request instead of one large one.
/// </summary>
public sealed class MemoryConsolidator
{
    /// <summary>Auto scans stop here; a manual 「立即整理」 does not.</summary>
    private const int AutoConversationLimit = 10;

    /// <summary>Assistant turns that trigger a pass on their own.</summary>
    public const int TurnThreshold = 5;

    /// <summary>Checked when a turn finishes; not a periodic background timer.</summary>
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromHours(6);

    private readonly MemoryService _memory;
    private readonly MemoryIndexRepository _index;
    private readonly MessageRepository _messages;
    private readonly SettingsViewModel _settings;
    private readonly ProviderRegistry _providers;
    private readonly Func<OneShotCompletionClient> _clientFactory;
    private readonly PersonaListViewModel? _personas;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset _lastRun = DateTimeOffset.UtcNow;
    private int _turnsSinceRun;

    public MemoryConsolidator(
        MemoryService memory,
        MemoryIndexRepository index,
        MessageRepository messages,
        SettingsViewModel settings,
        ProviderRegistry providers,
        Func<OneShotCompletionClient> clientFactory,
        PersonaListViewModel? personas = null)
    {
        _memory = memory;
        _index = index;
        _messages = messages;
        _settings = settings;
        _providers = providers;
        _clientFactory = clientFactory;
        _personas = personas;
    }

    /// <summary>
    /// 氛围模式的对话不进整理。The user's lines there are in character — 「我是来自
    /// 旧城的调查员」 is a verbatim role=user statement that passes every write
    /// check we have — and one extracted fact of that kind is not undone by
    /// deleting it, because the same conversation extracts it again. The server's
    /// Tracks pipeline reached the same conclusion and skips roleplay sessions
    /// wholesale; this is that gate.
    /// </summary>
    private IReadOnlyCollection<string> AtmospherePersonaIds => _personas is null
        ? []
        : _personas.Personas
            .Where(persona => persona.Mode == ConversationMode.Atmosphere)
            .Select(persona => persona.Id)
            .ToArray();

    public event EventHandler<MemoryConsolidationReport>? Completed;
    public event Action<bool>? RunningChanged;
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Called when a local-agent turn finishes. Whether anything is actually sent
    /// is decided here, not by the caller — the same signal fires for every turn
    /// in every conversation.
    /// </summary>
    public void NoteTurnFinished()
    {
        if (!_memory.IsAutoLearnOn) return;
        _turnsSinceRun++;
        if (_turnsSinceRun < TurnThreshold && DateTimeOffset.UtcNow - _lastRun < IdleThreshold) return;
        _ = RunInBackgroundAsync();
    }

    private async Task RunInBackgroundAsync()
    {
        try
        {
            var report = await RunAsync(manual: false, CancellationToken.None).ConfigureAwait(false);
            if (report.Ran && (report.Added > 0 || report.Candidates > 0))
                Completed?.Invoke(this, report);
        }
        catch (Exception)
        {
            // Background learning must never surface as an error in the chat.
        }
    }

    public async Task<MemoryConsolidationReport> RunAsync(bool manual, CancellationToken ct)
    {
        if (!_settings.MemoryEnabled) return MemoryConsolidationReport.Blocked("本地记忆未开启。");
        if (!_settings.MemoryAutoLearnEnabled) return MemoryConsolidationReport.Blocked("自动整理未开启。");
        if (ResolveTarget() is not { } resolved)
            return MemoryConsolidationReport.Blocked("请选择整理模型。");

        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return MemoryConsolidationReport.Blocked("上一轮整理尚未完成。");

        try
        {
            SetRunning(true);
            var resetAt = _index.GetResetAt();
            var conversations = _index.ConversationsAwaitingConsolidation(
                resetAt, manual ? int.MaxValue : AutoConversationLimit, AtmospherePersonaIds);

            var scanned = 0;
            var added = 0;
            var candidates = 0;
            var failures = 0;

            foreach (var conversationId in conversations)
            {
                ct.ThrowIfCancellationRequested();
                // 立即整理 is expected to finish the backlog, not nibble one
                // window off it and report success. Auto keeps to a single
                // window per conversation so a background pass stays cheap.
                var windows = manual ? ManualWindowsPerConversation : 1;
                var touched = false;
                for (var i = 0; i < windows; i++)
                {
                    var outcome = await ConsolidateConversationAsync(conversationId, resolved, resetAt, ct)
                        .ConfigureAwait(false);
                    added += outcome.Added;
                    candidates += outcome.Candidates;
                    if (outcome.Failed) failures++;
                    touched |= outcome.Looked;
                    if (!outcome.Looked || outcome.Failed) break;
                }
                if (touched) scanned++;
            }

            var organized = manual ? await OrganizeTopicsAsync(resolved, ct).ConfigureAwait(false) : 0;
            if (organized < 0) failures++;
            _lastRun = DateTimeOffset.UtcNow;
            _turnsSinceRun = 0;
            return new MemoryConsolidationReport(true, scanned, added, candidates, failures, TopicsOrganized: Math.Max(0, organized));
        }
        finally
        {
            SetRunning(false);
            _gate.Release();
            _memory.Invalidate();
        }
    }

    private void SetRunning(bool running)
    {
        if (IsRunning == running) return;
        IsRunning = running;
        RunningChanged?.Invoke(running);
    }

    /// <summary>How many windows one manual pass will chew through per
    /// conversation before moving on. Bounded so a very long history cannot turn
    /// one click into an unbounded run of billed requests.</summary>
    private const int ManualWindowsPerConversation = 6;

    private async Task<(int Added, int Candidates, bool Failed, bool Looked)> ConsolidateConversationAsync(
        string conversationId,
        (OneShotTarget Target, string ModelId, ThinkingParamKind? Thinking) resolved,
        long resetAt,
        CancellationToken ct)
    {
        var history = _messages.ListAll(conversationId);
        var watermark = _index.GetWatermark(conversationId);
        var window = MemoryWindowBuilder.Build(history, watermark, resetAt, recentOnly: true);

        if (window.SeenThrough <= watermark) return (0, 0, false, false);
        if (!window.HasContent)
        {
            // Nothing worth reading, but it *was* read. Advancing here is what
            // keeps an all-code conversation from occupying a slot in every scan.
            _index.SetWatermark(conversationId, window.SeenThrough);
            return (0, 0, false, true);
        }

        var client = _clientFactory();
        var transcript = Render(window);

        string gateReply;
        try
        {
            gateReply = await client.CompleteAsync(
                resolved.Target, resolved.ModelId,
                [
                    new ChatMessage(ChatMessage.RoleSystem, GatePrompt),
                    new ChatMessage(ChatMessage.RoleUser, transcript)
                ],
                maxTokens: 32, useThinking: false, thinkingKind: resolved.Thinking, ct: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (0, 0, true, true);
        }

        // Neither a failed request nor an unreadable answer may advance the
        // watermark. Treating either as 「没什么可记」 walks the mark past a stretch
        // of conversation that was never actually looked at, silently.
        var gate = MemoryOpsParser.ParseGate(gateReply);
        if (gate is null) return (0, 0, true, true);
        if (gate == false)
        {
            _index.SetWatermark(conversationId, window.SeenThrough);
            return (0, 0, false, true);
        }

        string extractReply;
        try
        {
            extractReply = await client.CompleteAsync(
                resolved.Target, resolved.ModelId,
                [
                    new ChatMessage(ChatMessage.RoleSystem, ExtractPrompt(_memory.Entries(), _memory.Topics())),
                    new ChatMessage(ChatMessage.RoleUser, transcript)
                ],
                maxTokens: 1024, useThinking: false, thinkingKind: resolved.Thinking, ct: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (0, 0, true, true);
        }

        var ops = MemoryOpsParser.Parse(extractReply);
        if (ops is null) return (0, 0, true, true);
        var applied = Apply(ops, window, conversationId);
        _index.SetWatermark(conversationId, window.SeenThrough);
        return (applied.Added, applied.Candidates, false, true);
    }

    /// <summary>
    /// The application-side check. Model output is data: a whole reply that
    /// fails to parse is dropped, and a single op that fails validation is
    /// dropped while its siblings still apply.
    /// </summary>
    private (int Added, int Candidates) Apply(
        IReadOnlyList<MemoryOp> ops, MemoryWindow window, string conversationId)
    {
        var added = 0;
        var candidates = 0;
        var userText = window.UserMessages.ToList();

        foreach (var op in ops)
        {
            // Evidence has to come from something the user said inside this
            // window. An assistant sentence, a tool result or a fetched page
            // saying "the user lives in Shanghai" is not the user saying it.
            var source = userText.FirstOrDefault(row => MemoryGuards.QuoteBelongsTo(op.Quote, row.Content));
            if (source is null) continue;

            if (op.Type == MemoryOpType.Delete)
            {
                ApplyDelete(op);
                continue;
            }

            if (MemoryGuards.RejectionReason(op.Text, _settings.MemoryAllowSensitive) is not null) continue;
            var key = MemoryGuards.NormalizeKey(op.Text);
            if (_index.IsSuppressed(key)) continue;

            if (op.Type == MemoryOpType.Replace)
            {
                if (ApplyReplace(op)) added++;
                continue;
            }

            var existing = _memory.Entries().FirstOrDefault(entry =>
                string.Equals(MemoryGuards.NormalizeKey(entry.Text), key, StringComparison.Ordinal));
            if (existing is not null) continue;   // already known; nothing to add

            var topic = _memory.ResolveTopic(op.Section, op.Topic, op.Group, op.Summary);
            // Confident and quoted goes straight in; everything else waits for
            // the user, because a wrong fact that arrived silently is worse than
            // one that was never written.
            if (op.Type == MemoryOpType.Add && op.Confidence >= 0.75)
            {
                var result = _memory.Files.Add(op.Section, op.Text, MemoryOrigin.Auto, op.Confidence, topicId: topic.Id);
                if (result.Changed) added++;
            }
            else
            {
                if (_index.HasCandidate(key)) continue;
                _index.AddCandidate(new LocalMemoryCandidateRow(
                    Guid.NewGuid().ToString("N"),
                    MemorySectionRules.Heading(op.Section),
                    op.Text,
                    key,
                    op.Quote,
                    op.Confidence,
                    conversationId,
                    source.Id,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), topic.Id));
                candidates++;
            }
        }

        _memory.Invalidate(notify: false);
        return (added, candidates);
    }

    private bool ApplyReplace(MemoryOp op)
    {
        var target = FindTarget(op.Target);
        if (target is null || target.Origin == MemoryOrigin.Manual) return false;

        // One line rewritten in one write. The delete-then-add ordering has a
        // narrow path that loses both: the delete records a tombstone, and if
        // the new text normalizes to the same key the follow-up write is
        // rejected by the tombstone that was just written.
        return _memory.Files.Replace(target, op.Text, MemoryOrigin.Auto).Changed;
    }

    private void ApplyDelete(MemoryOp op)
    {
        // Deleting is irreversible and writes a tombstone, so the quote must
        // itself be a denial. A model deciding a fact looks stale is not the
        // user withdrawing it.
        if (!MemoryGuards.LooksLikeDenial(op.Quote)) return;
        var target = FindTarget(op.Target);
        if (target is null || target.Origin == MemoryOrigin.Manual) return;
        if (_memory.Files.Delete(target).Changed)
            _index.Suppress(MemoryGuards.NormalizeKey(target.Text), target.Text);
    }

    private MemoryEntry? FindTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var needle = target.Trim();
        var byId = _memory.Entries().FirstOrDefault(entry =>
            string.Equals(entry.Id, needle, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;

        var key = MemoryGuards.NormalizeKey(needle);
        var matches = _memory.Entries()
            .Where(entry => MemoryGuards.NormalizeKey(entry.Text).Contains(key, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private (OneShotTarget Target, string ModelId, ThinkingParamKind? Thinking)? ResolveTarget()
    {
        var key = _settings.MemoryModelKey;
        if (string.IsNullOrWhiteSpace(key)) return null;
        var parts = key.Split("::", 2);
        if (parts.Length != 2) return null;

        if (_providers.FindModel(parts[0], parts[1]) is not { } found) return null;
        if (found.Provider is not IOneShotTarget oneShot) return null;
        if (oneShot.DescribeOneShot(found.Model.Id) is not { } target) return null;
        return (target, found.Model.Id, found.Model.ThinkingConfig?.Kind);
    }

    private static string Render(MemoryWindow window)
    {
        var sb = new StringBuilder();
        foreach (var row in window.Messages)
        {
            sb.Append(row.Role == ChatMessage.RoleUser ? "用户：" : "助手：");
            sb.Append(row.Content.Length > 2000 ? row.Content[..2000] + "…" : row.Content);
            sb.Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }

    private const string GatePrompt = MemoryPrompts.Selection
        + "\n先判断是否有值得长期保留的信息，而不是概括这段对话发生了什么。"
        + "仅输出 <user_memory>true</user_memory> 或 <user_memory>false</user_memory>。";

    private static string ExtractPrompt(IReadOnlyList<MemoryEntry> existing, IReadOnlyList<MemoryTopic> topics)
    {
        var sb = new StringBuilder();
        sb.Append(MemoryPrompts.Selection).Append("\n\n").Append(MemoryPrompts.TopicRules).Append("\n\n")
          .Append("规则：\n")
          .Append("- 仅依据用户消息，忽略助手消息。\n")
          .Append("- 每条使用完整的第三人称陈述句，避免「这个」「刚才」等本轮指代。\n")
          .Append("- quote 必须逐字引用用户原话。\n")
          .Append("- 忽略临时上下文、未经确认的推断、密钥密码证件支付信息。\n")
          .Append("- 用户在同一段中改口时，以最后一次为准，只保留一条。\n\n")
          .Append("输出最多 ").Append(MemoryOpsParser.MaxOpsPerWindow).Append(" 个必要变更，不设最低数量；无需变更时输出 <memory_ops/>。\n")
          .Append("补充同一事实使用 replace，target 填已有 id，text 保留仍然成立的内容并合并新信息。delete 仅用于用户明确否认。\n")
          .Append("<memory_ops>\n")
          .Append("<op type=\"add\" section=\"分节名\" topic=\"主题名称\" group=\"大类\" confidence=\"0.9\">\n")
          .Append("  <summary>主题的一句话摘要</summary>\n")
          .Append("  <text>用户……</text>\n")
          .Append("  <quote>用户的原话</quote>\n")
          .Append("</op>\n")
          .Append("</memory_ops>\n\n")
          .Append("分节名只能是：")
          .Append(string.Join(" / ", MemorySectionRules.All.Select(MemorySectionRules.Heading)))
          .Append('\n');

        if (existing.Count > 0)
        {
            sb.Append("\n已有记忆（不要重复）：\n");
            foreach (var entry in existing) sb.Append("- [").Append(entry.Id).Append("] ").Append(entry.Text).Append('\n');
        }
        sb.Append("\n已有主题（优先复用）：\n");
        foreach (var topic in topics) sb.Append(topic.Group).Append(" / ").Append(topic.Title).Append('\n');
        return sb.ToString();
    }

    private async Task<int> OrganizeTopicsAsync(
        (OneShotTarget Target, string ModelId, ThinkingParamKind? Thinking) resolved, CancellationToken ct)
    {
        var entries = _memory.Entries().Where(entry => entry.TopicId is null).ToArray();
        if (entries.Length == 0) return 0;
        var prompt = "只整理已有记忆的主题归属，不添加、修改或删除事实。" + MemoryPrompts.TopicRules
            + "\n把所有输入 id 分配给合适主题，每个 id 只出现一次。输出 XML："
            + "<topics><topic title=\"主题名称\" group=\"大类\"><summary>一句摘要</summary><entry id=\"已有id\"/></topic></topics>"
            + "\n已有主题：\n" + string.Join('\n', _memory.Topics().Select(topic => topic.Group + " / " + topic.Title));
        string reply;
        try
        {
            reply = await _clientFactory().CompleteAsync(resolved.Target, resolved.ModelId,
                [new ChatMessage(ChatMessage.RoleSystem, prompt),
                 new ChatMessage(ChatMessage.RoleUser, string.Join('\n', entries.Select(entry => $"[{entry.Id}] {entry.Text}")))],
                maxTokens: 4096, useThinking: false, thinkingKind: resolved.Thinking, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return -1; }
        var assignments = MemoryTopicParser.Parse(reply);
        if (assignments is null) return -1;
        var byId = entries.GroupBy(entry => entry.Id).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var seen = new HashSet<string>();
        foreach (var assignment in assignments)
        {
            var topic = _memory.ResolveTopic(MolaGPT.Core.Personalization.MemorySection.Identity,
                assignment.Title, assignment.Group, assignment.Summary);
            foreach (var id in assignment.EntryIds)
            {
                if (!byId.TryGetValue(id, out var entry) || !seen.Add(id)) continue;
                if (!_memory.Files.AssignTopic(entry, topic.Id).Ok) return -1;
            }
        }
        _memory.Invalidate(notify: false);
        return seen.Count == entries.Length ? assignments.Count : -1;
    }
}
