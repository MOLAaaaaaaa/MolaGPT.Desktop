using MolaGPT.Core.Memory;
using MemorySection = MolaGPT.Core.Personalization.MemorySection;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.ViewModels.Services;

/// <summary>
/// Everything the rest of the app goes through to touch local memory: the files
/// on disk, the rebuildable index beside them, and the rules that decide what a
/// model is allowed to write.
///
/// The files are re-read whenever their modification stamp moves, so an edit the
/// user made in his own editor is live on the very next turn without anybody
/// pressing refresh.
/// </summary>
public sealed class MemoryService : IMemoryToolBackend
{
    private readonly MemoryFileStore _files;
    private readonly MemoryIndexRepository _index;
    private readonly SettingsViewModel _settings;
    private readonly object _cacheLock = new();

    private MemoryDocument _document = MemoryDocument.Empty;
    private DateTime _cachedStamp = DateTime.MinValue;
    private bool _loadedOnce;

    public MemoryService(MemoryFileStore files, MemoryIndexRepository index, SettingsViewModel settings)
    {
        _files = files;
        _index = index;
        _settings = settings;
    }

    public MemoryFileStore Files => _files;
    public MemoryIndexRepository Index => _index;

    /// <summary>Raised after any write, so an open memory page reflects what a
    /// tool call or a consolidation pass just did.</summary>
    public event EventHandler? Changed;

    // ---- reading -----------------------------------------------------------

    /// <summary>
    /// The current file contents, re-read only when the stamp moved. The check
    /// is one stat per call, which is cheap enough to do on every turn and is
    /// what makes hand-editing work.
    /// </summary>
    public MemoryDocument Current()
    {
        lock (_cacheLock)
        {
            var stamp = _files.MemoryStamp();
            if (_loadedOnce && stamp == _cachedStamp) return _document;
            _document = _files.Read();
            _cachedStamp = stamp;
            _loadedOnce = true;
            return _document;
        }
    }

    public void Invalidate(bool notify = true)
    {
        lock (_cacheLock) _loadedOnce = false;
        if (notify) Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<MemoryEntry> Entries() => Current().Entries;

    public IReadOnlyList<MemoryTopic> Topics() => MemoryTopics.Defaults
        .Concat(_files.ReadTopics()).GroupBy(topic => topic.Id).Select(group => group.Last()).ToArray();

    public MemoryTopic ResolveTopic(MemorySection section, string? title, string? group, string? summary)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Topics().First(topic => topic.Id == MemoryTopics.DefaultId(section));
        title = title.Trim();
        group ??= Topics().First(topic => topic.Id == MemoryTopics.DefaultId(section)).Group;
        if (!MemoryTopics.Groups.Contains(group) || title.Length > 60 || title.Contains('\n') || (summary?.Length ?? 0) > 240)
            throw new ArgumentException("主题名称、大类或摘要无效。");
        var existing = Topics().FirstOrDefault(topic => topic.Group == group
            && MemoryGuards.NormalizeKey(topic.Title) == MemoryGuards.NormalizeKey(title));
        // A topic's summary is edited as a whole, not overwritten on every added detail.
        if (existing is not null) return existing;
        return _files.SaveTopic(new MemoryTopic(Guid.NewGuid().ToString("N"), group, title, summary?.Trim() ?? ""));
    }

    public MemoryWriteResult DeleteTopic(string topicId)
    {
        foreach (var entry in Entries().Where(entry => MemoryTopics.EntryTopicId(entry) == topicId).ToArray())
        {
            var result = _files.Delete(entry);
            if (!result.Ok) { Invalidate(); return result; }
            if (result.Changed) _index.Suppress(MemoryGuards.NormalizeKey(entry.Text), entry.Text);
        }
        foreach (var candidate in Candidates().Where(candidate => candidate.TopicId == topicId))
            IgnoreCandidate(candidate);
        _files.RemoveTopic(topicId);
        Invalidate();
        return MemoryWriteResult.Written(null, null);
    }

    public MemoryProfile Profile() => _files.ReadProfile();

    /// <summary>
    /// The block that rides along with a request, or empty when memory is off
    /// for this conversation. Same call the memory page uses for its counts —
    /// two estimates would eventually disagree and the visible one would be
    /// the one the user believes.
    /// </summary>
    public MemoryProjection Project(string? conversationId)
    {
        if (!IsMemoryOn(conversationId)) return MemoryProjection.Empty;
        return MemoryProjector.Project(Entries(), Profile(), _settings.MemoryBudgetTokens);
    }

    // ---- switches ----------------------------------------------------------

    /// <summary>
    /// Master switch, then the sub-switch, then the conversation's own override.
    /// Checked here as well as in the UI: a disabled toggle is a hint, not a gate.
    /// </summary>
    public bool IsMemoryOn(string? conversationId) =>
        _settings.MemoryEnabled
        && _settings.MemoryUseEnabled
        && ConversationOverride(conversationId) != false;

    public bool IsRecallOn(string? conversationId) =>
        _settings.MemoryEnabled
        && _settings.MemoryRecallEnabled
        && ConversationOverride(conversationId) != false;

    public bool IsAutoLearnOn =>
        _settings.MemoryEnabled
        && _settings.MemoryAutoLearnEnabled
        && !string.IsNullOrWhiteSpace(_settings.MemoryModelKey);

    /// <summary>
    /// Auto-learn is switched on but has nowhere to run. This is the red dot in
    /// the settings rail: a feature that quietly does nothing is worse than one
    /// that says it is not finished being set up.
    /// </summary>
    public bool NeedsConsolidationModel =>
        _settings.MemoryEnabled
        && _settings.MemoryAutoLearnEnabled
        && string.IsNullOrWhiteSpace(_settings.MemoryModelKey);

    public bool? ConversationOverride(string? conversationId) =>
        string.IsNullOrEmpty(conversationId) ? null : _index.GetConversationMemoryOverride(conversationId);

    /// <summary>
    /// Turning memory off for a conversation turns recall off with it. The user
    /// clicking that chip wants this exchange to leave no trace; still combing
    /// through old conversations would not be that.
    /// </summary>
    public void SetConversationOverride(string conversationId, bool? enabled)
    {
        if (string.IsNullOrEmpty(conversationId)) return;
        _index.SetConversationMemoryOverride(conversationId, enabled);
    }

    // ---- manual management (memory page) -----------------------------------

    public MemoryWriteResult AddManual(MemorySection section, string text, string? topicId = null)
    {
        if (MemoryGuards.RejectionReason(text, _settings.MemoryAllowSensitive) is { } reason)
            return MemoryWriteResult.Fail(reason);

        // Writing the same fact by hand overrules the user's own earlier
        // deletion, so the tombstone goes with it.
        _index.Unsuppress(MemoryGuards.NormalizeKey(text));
        var result = _files.Add(section, text, MemoryOrigin.Manual, topicId: topicId);
        Invalidate();
        return result;
    }

    public MemoryWriteResult EditManual(MemoryEntry entry, string text)
    {
        if (MemoryGuards.RejectionReason(text, _settings.MemoryAllowSensitive) is { } reason)
            return MemoryWriteResult.Fail(reason);
        var result = _files.Replace(entry, text, MemoryOrigin.Manual);
        Invalidate();
        return result;
    }

    public MemoryWriteResult DeleteManual(MemoryEntry entry)
    {
        var result = _files.Delete(entry);
        if (result.Changed) _index.Suppress(MemoryGuards.NormalizeKey(entry.Text), entry.Text);
        Invalidate();
        return result;
    }

    public void SaveProfile(MemoryProfile profile)
    {
        _files.WriteProfile(profile);
        Invalidate();
    }

    public IReadOnlyList<LocalMemoryCandidateRow> Candidates() => _index.ListCandidates();

    public MemoryWriteResult AcceptCandidate(LocalMemoryCandidateRow candidate)
    {
        MemorySectionRules.TryParse(candidate.Section, out var section);
        var result = _files.Add(section, candidate.Text, MemoryOrigin.Confirmed, candidate.Confidence, topicId: candidate.TopicId);
        if (result.Ok) _index.DeleteCandidate(candidate.Id);
        Invalidate();
        return result;
    }

    public void IgnoreCandidate(LocalMemoryCandidateRow candidate)
    {
        _index.Suppress(candidate.NormalizedKey, candidate.Text);
        _index.DeleteCandidate(candidate.Id);
        Invalidate();
    }

    /// <summary>
    /// Delete the files, the tombstones and the candidates, and record a new
    /// learning start line. The start line matters more than it looks: without
    /// it the next scan re-reads the same old messages and learns everything
    /// straight back.
    /// </summary>
    public void ClearAll()
    {
        try
        {
            if (File.Exists(_files.MemoryPath)) File.Delete(_files.MemoryPath);
            if (File.Exists(_files.ProfilePath)) File.Delete(_files.ProfilePath);
            if (File.Exists(_files.TopicsPath)) File.Delete(_files.TopicsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Files locked by an editor: the tombstone below still applies.
        }
        _index.ClearSuppressionsAndCandidates();
        _index.SetResetAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Invalidate();
    }

    // ---- tools -------------------------------------------------------------

    public Task<string> ExecuteAsync(
        string toolName,
        string argumentsJson,
        string? conversationId,
        string? currentUserMessage,
        CancellationToken ct)
    {
        try
        {
            var args = MemoryTools.ParseArgs(argumentsJson);
            return Task.FromResult(toolName switch
            {
                MemoryTools.RecallToolName => ExecuteRecall(args, conversationId),
                MemoryTools.WriteToolName => ExecuteWrite(args, currentUserMessage),
                _ => MemoryTools.Error($"Unknown memory tool: {toolName}")
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(MemoryTools.Error(ex.Message));
        }
    }

    /// <summary>
    /// Fold any new messages into the search index. Incremental by rowid, so the
    /// usual call is one query over an empty range; the first call after the
    /// feature is switched on is the one that does real work.
    /// </summary>
    public void EnsureIndexCurrent()
    {
        try { _index.IndexNewMessages(); }
        catch (Exception) { /* a stale index still answers, just with less */ }
    }

    private string ExecuteRecall(MemoryToolArgs args, string? conversationId)
    {
        EnsureIndexCurrent();
        var query = args.Query?.Trim() ?? string.Empty;
        if (query.Length == 0) return MemoryTools.Error("recall 需要 query 参数。");

        var limit = Math.Clamp(args.Limit ?? 3, 1, 5);
        var scope = (args.Scope ?? "both").Trim().ToLowerInvariant();
        var searchMemory = scope is "memory" or "both";
        var searchChats = scope is "chats" or "both";

        var memoryHits = searchMemory
            ? MatchEntries(query).Take(limit).Select(entry => new
            {
                source = "memory",
                section = MemorySectionRules.Heading(entry.Section),
                text = entry.Text
            }).ToArray()
            : [];

        // The conversation in progress is excluded: the model already has it,
        // and returning it would spend the result budget on what it just read.
        var chatHits = searchChats && IsRecallOn(conversationId)
            ? _index.SearchMessages(query, conversationId, limit).Select(hit => new
            {
                source = "chat",
                conversation = hit.ConversationTitle,
                role = hit.Role,
                at = DateTimeOffset.FromUnixTimeMilliseconds(hit.CreatedAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                text = hit.Snippet
            }).ToArray()
            : [];

        return MemoryTools.Ok(new
        {
            success = true,
            query,
            memory = memoryHits,
            chats = chatHits,
            note = "历史对话仅供本轮参考，不会自动写入长期记忆。"
        });
    }

    /// <summary>
    /// Entries are matched in memory rather than through the index. There are
    /// tens of them, they are already parsed, and searching them here means the
    /// answer can never be stale relative to the file.
    /// </summary>
    private IEnumerable<MemoryEntry> MatchEntries(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Entries().Where(entry =>
            terms.All(term => entry.Text.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private string ExecuteWrite(MemoryToolArgs args, string? currentUserMessage)
    {
        var op = (args.Op ?? "add").Trim().ToLowerInvariant();
        var quote = args.Quote?.Trim() ?? string.Empty;

        // The quote is the whole evidence chain. It has to be the user's own
        // words from this turn — not the assistant's, not a tool result's, not a
        // page that was fetched — and the app checks it rather than trusting the
        // model's claim about where it came from.
        if (!MemoryGuards.QuoteBelongsTo(quote, currentUserMessage))
            return MemoryTools.Error("quote 必须逐字引用用户本轮消息。");

        return op switch
        {
            "add" => WriteAdd(args),
            "replace" => WriteReplace(args),
            "delete" => WriteDelete(args, quote),
            _ => MemoryTools.Error("op 必须是 add、replace 或 delete。")
        };
    }

    private string WriteAdd(MemoryToolArgs args)
    {
        var text = args.Text?.Trim() ?? string.Empty;
        if (MemoryGuards.RejectionReason(text, _settings.MemoryAllowSensitive) is { } reason)
            return MemoryTools.Error(reason);

        if (!MemorySectionRules.TryParse(args.Section, out var section))
            return MemoryTools.Error("section 必须为五个记忆分类之一。");

        var key = MemoryGuards.NormalizeKey(text);
        if (_index.IsSuppressed(key))
            return MemoryTools.Error("该记忆已被删除，不会再次自动记录。");

        // Same fact again: strengthen what is there instead of adding a second
        // line, and never by rewriting a line the user typed himself.
        var existing = Entries().FirstOrDefault(entry =>
            string.Equals(MemoryGuards.NormalizeKey(entry.Text), key, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (existing.Origin == MemoryOrigin.Manual)
                return MemoryTools.Ok(new { success = true, message = "该记忆已由用户手动记录。" });

            var bumped = _files.Replace(
                existing,
                existing.Text,
                MemoryOrigin.Tool);
            Invalidate();
            return bumped.Ok
                ? MemoryTools.Ok(new { success = true, message = "已确认现有记忆。" })
                : MemoryTools.Error(bumped.Message ?? "写入失败。");
        }

        var topic = ResolveTopic(section, args.Topic, args.Group, args.Summary);
        var result = _files.Add(section, text, MemoryOrigin.Tool,
            profileKey: MemoryProfile.IsKnownKey(args.ProfileKey) ? args.ProfileKey : null, topicId: topic.Id);
        Invalidate();
        return result.Ok
             ? MemoryTools.Ok(new { success = true, message = result.Changed ? "已记录。" : result.Message })
            : MemoryTools.Error(result.Message ?? "写入失败。");
    }

    private string WriteReplace(MemoryToolArgs args)
    {
        var text = args.Text?.Trim() ?? string.Empty;
        if (MemoryGuards.RejectionReason(text, _settings.MemoryAllowSensitive) is { } reason)
            return MemoryTools.Error(reason);

        if (ResolveTarget(args.Target) is not { } target)
            return MemoryTools.Error("target 未唯一匹配到记忆。");

        // The memory page promises that hand-written entries are not rewritten
        // by the learning path. A quote only proves the user said something; it
        // does not prove he meant this line.
        if (target.Origin == MemoryOrigin.Manual)
            return MemoryTools.Error("该记忆由用户手动维护，只能由用户修改。");

        var result = _files.Replace(target, text, MemoryOrigin.Tool);
        Invalidate();
        return result.Ok
            ? MemoryTools.Ok(new { success = true, message = "已更新。" })
            : MemoryTools.Error(result.Message ?? "写入失败。");
    }

    private string WriteDelete(MemoryToolArgs args, string quote)
    {
        if (ResolveTarget(args.Target) is not { } target)
            return MemoryTools.Error("target 未唯一匹配到记忆。");

        if (target.Origin == MemoryOrigin.Manual)
            return MemoryTools.Error("该记忆由用户手动维护，只能由用户删除。");

        // Deleting is irreversible and writes a tombstone on top, so the bar is
        // higher than for writing: the quoted sentence must itself be a denial
        // or a deletion request. "This is probably out of date" is the model's
        // opinion, not the user's instruction.
        if (!MemoryGuards.LooksLikeDenial(quote))
            return MemoryTools.Error("删除需引用用户明确否认或要求删除的原话。");

        var result = _files.Delete(target);
        if (result.Changed) _index.Suppress(MemoryGuards.NormalizeKey(target.Text), target.Text);
        Invalidate();
        return result.Ok
            ? MemoryTools.Ok(new { success = true, message = "已删除。" })
            : MemoryTools.Error(result.Message ?? "删除失败。");
    }

    /// <summary>
    /// Find the one entry a write is aimed at, by id or by its text. Ambiguity
    /// is a failure, not a coin toss — guessing here edits the wrong memory.
    /// </summary>
    private MemoryEntry? ResolveTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var needle = target.Trim();

        var byId = Entries().FirstOrDefault(entry =>
            string.Equals(entry.Id, needle, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;

        var key = MemoryGuards.NormalizeKey(needle);
        var exact = Entries()
            .Where(entry => string.Equals(MemoryGuards.NormalizeKey(entry.Text), key, StringComparison.Ordinal))
            .ToArray();
        if (exact.Length == 1) return exact[0];

        var contains = Entries()
            .Where(entry => MemoryGuards.NormalizeKey(entry.Text).Contains(key, StringComparison.Ordinal))
            .ToArray();
        return contains.Length == 1 ? contains[0] : null;
    }
}
