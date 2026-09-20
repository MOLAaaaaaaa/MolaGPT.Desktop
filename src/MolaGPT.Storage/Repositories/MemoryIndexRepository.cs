using Dapper;
using Microsoft.Data.Sqlite;
using MolaGPT.Core.Chat;

namespace MolaGPT.Storage.Repositories;

public sealed record LocalMemoryHit(
    string MessageId,
    string ConversationId,
    string ConversationTitle,
    string Role,
    long CreatedAt,
    string Snippet);

public sealed record LocalMemoryCandidateRow(
    string Id,
    string Section,
    string Text,
    string NormalizedKey,
    string Quote,
    double Confidence,
    string? ConversationId,
    string? MessageId,
    long CreatedAt,
    string? TopicId = null);

/// <summary>
/// The rebuildable half of local memory: a full-text index over past messages,
/// the suppression tombstones, the pending candidates, and the consolidation
/// watermarks. Deleting this database loses none of the user's memories — those
/// are Markdown files — which is why nothing here is treated as authoritative.
///
/// Memory entries themselves are deliberately not indexed. There are tens of
/// them, they are re-read from disk whenever the file changes, and a second copy
/// in SQLite would only introduce a way for the two to disagree.
/// </summary>
public sealed class MemoryIndexRepository
{
    /// <summary>
    /// The trigram tokenizer cannot match a query shorter than three characters,
    /// and two-character words — 天气, 记忆, 上海, 工作 — are exactly what people
    /// search for in Chinese. Anything shorter goes to LIKE instead; this is not
    /// a fallback for failures but a second arm that is always used.
    /// </summary>
    public const int MinimumFtsQueryLength = 3;

    private const string IndexWatermarkKey = "message_fts.rowid";

    /// <summary>
    /// Hard ceiling on one tool result, on top of the row limit. Row caps alone
    /// do not bound anything when a single matched message is itself enormous,
    /// and a result that has to be both rendered and fed back into the next
    /// request is the wrong place to discover that.
    /// </summary>
    private const int MaxSnippetCharacters = 400;

    private readonly MolaGptDatabase _db;

    public MemoryIndexRepository(MolaGptDatabase db) => _db = db;

    // ---- index maintenance -------------------------------------------------

    /// <summary>
    /// Fold new messages into the index. Incremental by rowid, so the usual call
    /// costs one query against an empty range. Returns how many rows were added.
    /// </summary>
    public int IndexNewMessages(int batchLimit = 5000)
    {
        using var conn = _db.Open();
        var watermark = ReadState(conn, IndexWatermarkKey) is { } raw && long.TryParse(raw, out var parsed)
            ? parsed
            : 0;

        var rows = conn.Query<(long RowId, string Id, string Content)>(
            @"SELECT rowid AS RowId, id AS Id, content AS Content
              FROM messages
              WHERE rowid > @watermark AND role IN ('user','assistant') AND content <> ''
              ORDER BY rowid ASC
              LIMIT @batchLimit",
            new { watermark, batchLimit }).ToList();

        if (rows.Count == 0) return 0;

        using var tx = conn.BeginTransaction();
        foreach (var row in rows)
        {
            conn.Execute(
                "INSERT INTO message_fts (body, message_id) VALUES (@Content, @Id)",
                new { row.Content, row.Id }, tx);
        }
        WriteState(conn, IndexWatermarkKey, rows[^1].RowId.ToString(), tx);
        tx.Commit();
        return rows.Count;
    }

    /// <summary>Drop and refill. Offered in the memory page because an index that
    /// can be rebuilt should never be something the user has to work around.</summary>
    public void RebuildIndex()
    {
        using (var conn = _db.Open())
        {
            conn.Execute("DELETE FROM message_fts");
            conn.Execute("DELETE FROM memory_index_state WHERE key = @key", new { key = IndexWatermarkKey });
        }

        while (IndexNewMessages() > 0) { }
    }

    public int IndexedMessageCount()
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<int>("SELECT count(*) FROM message_fts");
    }

    // ---- search ------------------------------------------------------------

    /// <summary>
    /// Search past messages. Deleted conversations, the conversation the user is
    /// in right now, and rows whose message no longer exists all drop out via the
    /// join — the index is allowed to be stale precisely because it is never the
    /// thing being read from.
    /// </summary>
    public IReadOnlyList<LocalMemoryHit> SearchMessages(string query, string? excludeConversationId, int limit)
    {
        var needle = query?.Trim() ?? string.Empty;
        if (needle.Length == 0) return Array.Empty<LocalMemoryHit>();
        limit = Math.Clamp(limit, 1, 20);

        using var conn = _db.Open();

        if (needle.Length >= MinimumFtsQueryLength)
        {
            try
            {
                var hits = Query(conn,
                    @"SELECT m.id AS MessageId, m.conversation_id AS ConversationId,
                             c.title AS ConversationTitle, m.role AS Role,
                             m.created_at AS CreatedAt, m.content AS Snippet
                      FROM message_fts f
                      JOIN messages m ON m.id = f.message_id
                      JOIN conversations c ON c.id = m.conversation_id
                      WHERE f.body MATCH @match
                        AND c.deleted_at IS NULL
                        AND c.provider_id IS NOT NULL AND c.provider_id <> @proxy COLLATE NOCASE
                        AND (c.memory_enabled IS NULL OR c.memory_enabled = 1)
                        AND (@exclude IS NULL OR m.conversation_id <> @exclude)
                      ORDER BY m.created_at DESC
                      LIMIT @limit",
                    new { match = Quote(needle), exclude = excludeConversationId, limit, proxy = MolaGptProviderIds.Proxy },
                    needle);
                if (hits.Count > 0) return hits;
            }
            catch (SqliteException)
            {
                // A query the FTS grammar rejects is not an error the user should
                // ever see: fall through to the substring arm and answer anyway.
            }
        }

        return Query(conn,
            @"SELECT m.id AS MessageId, m.conversation_id AS ConversationId,
                     c.title AS ConversationTitle, m.role AS Role,
                     m.created_at AS CreatedAt, m.content AS Snippet
              FROM messages m
              JOIN conversations c ON c.id = m.conversation_id
              WHERE m.role IN ('user','assistant')
                AND c.deleted_at IS NULL
                AND c.provider_id IS NOT NULL AND c.provider_id <> @proxy COLLATE NOCASE
                AND (c.memory_enabled IS NULL OR c.memory_enabled = 1)
                AND (@exclude IS NULL OR m.conversation_id <> @exclude)
                AND m.content LIKE @pattern ESCAPE '\'
              ORDER BY m.created_at DESC
              LIMIT @limit",
            new
            {
                pattern = "%" + needle.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%",
                exclude = excludeConversationId,
                proxy = MolaGptProviderIds.Proxy,
                limit
            },
            needle);
    }

    private static IReadOnlyList<LocalMemoryHit> Query(
        SqliteConnection conn, string sql, object parameters, string needle) =>
        conn.Query<LocalMemoryHit>(sql, parameters)
            .Select(hit => hit with
            {
                ConversationTitle = string.IsNullOrWhiteSpace(hit.ConversationTitle) ? "新对话" : hit.ConversationTitle,
                Snippet = Excerpt(hit.Snippet, needle)
            })
            .ToList();

    /// <summary>
    /// A window around the match, not the head of the message. A keyword 3000
    /// characters into a long answer is the case where returning the opening
    /// paragraph looks like a hit and tells the model nothing.
    /// </summary>
    private static string Excerpt(string? content, string needle)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        if (content.Length <= MaxSnippetCharacters) return content;

        var at = content.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return content[..MaxSnippetCharacters] + "…";

        var start = Math.Max(0, at - MaxSnippetCharacters / 3);
        var length = Math.Min(MaxSnippetCharacters, content.Length - start);
        var slice = content.Substring(start, length);
        return (start > 0 ? "…" : string.Empty) + slice + (start + length < content.Length ? "…" : string.Empty);
    }

    // ---- suppressions ------------------------------------------------------

    public bool IsSuppressed(string normalizedKey)
    {
        if (string.IsNullOrEmpty(normalizedKey)) return false;
        using var conn = _db.Open();
        return conn.ExecuteScalar<long>(
            "SELECT count(*) FROM memory_suppressions WHERE normalized_key = @normalizedKey",
            new { normalizedKey }) > 0;
    }

    public void Suppress(string normalizedKey, string text)
    {
        if (string.IsNullOrEmpty(normalizedKey)) return;
        using var conn = _db.Open();
        conn.Execute(
            @"INSERT INTO memory_suppressions (normalized_key, text, created_at)
              VALUES (@normalizedKey, @text, @now)
              ON CONFLICT(normalized_key) DO UPDATE SET text = excluded.text, created_at = excluded.created_at",
            new { normalizedKey, text, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    /// <summary>Adding the same fact by hand is the user overruling his own
    /// earlier deletion, so the tombstone goes with it.</summary>
    public void Unsuppress(string normalizedKey)
    {
        if (string.IsNullOrEmpty(normalizedKey)) return;
        using var conn = _db.Open();
        conn.Execute("DELETE FROM memory_suppressions WHERE normalized_key = @normalizedKey", new { normalizedKey });
    }

    // ---- candidates --------------------------------------------------------

    public IReadOnlyList<LocalMemoryCandidateRow> ListCandidates(int limit = 20)
    {
        using var conn = _db.Open();
        return conn.Query<LocalMemoryCandidateRow>(
            @"SELECT id AS Id, section AS Section, text AS Text, normalized_key AS NormalizedKey,
                     quote AS Quote, confidence AS Confidence, conversation_id AS ConversationId,
                     message_id AS MessageId, created_at AS CreatedAt, topic_id AS TopicId
              FROM memory_candidates ORDER BY created_at DESC LIMIT @limit",
            new { limit }).ToList();
    }

    public void AddCandidate(LocalMemoryCandidateRow row)
    {
        using var conn = _db.Open();
        conn.Execute(
            @"INSERT INTO memory_candidates
                (id, section, text, normalized_key, quote, confidence, conversation_id, message_id, created_at, topic_id)
              VALUES (@Id, @Section, @Text, @NormalizedKey, @Quote, @Confidence, @ConversationId, @MessageId, @CreatedAt, @TopicId)
              ON CONFLICT(id) DO NOTHING",
            row);
    }

    public bool HasCandidate(string normalizedKey)
    {
        if (string.IsNullOrEmpty(normalizedKey)) return false;
        using var conn = _db.Open();
        return conn.ExecuteScalar<long>(
            "SELECT count(*) FROM memory_candidates WHERE normalized_key = @normalizedKey",
            new { normalizedKey }) > 0;
    }

    public void DeleteCandidate(string id)
    {
        using var conn = _db.Open();
        conn.Execute("DELETE FROM memory_candidates WHERE id = @id", new { id });
    }

    public int CandidateCount()
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<int>("SELECT count(*) FROM memory_candidates");
    }

    // ---- consolidation bookkeeping -----------------------------------------

    /// <summary>
    /// Conversations with messages newer than their own watermark. Filtering in
    /// SQL rather than in code is what keeps an already-consolidated conversation
    /// from occupying a slot in every scan while one with real new content waits.
    /// </summary>
    /// <summary>
    /// <paramref name="excludedPersonaIds"/> is how 氛围模式 stays out of memory.
    /// The mode is not a column — it is computed from the persona's card — so the
    /// caller resolves it and hands down the ids. An empty list excludes nothing.
    /// </summary>
    public IReadOnlyList<string> ConversationsAwaitingConsolidation(
        long notBefore, int limit, IReadOnlyCollection<string>? excludedPersonaIds = null)
    {
        // A list Dapper can always expand: the sentinel is not a persona id, so
        // "exclude nothing" and "exclude these" take the same code path.
        var excluded = (excludedPersonaIds ?? []).Concat([""]).Distinct(StringComparer.Ordinal).ToArray();
        using var conn = _db.Open();
        return conn.Query<string>(
            @"SELECT c.id
              FROM conversations c
              WHERE c.deleted_at IS NULL
                AND c.provider_id IS NOT NULL AND c.provider_id <> @proxy COLLATE NOCASE
                AND (c.memory_enabled IS NULL OR c.memory_enabled = 1)
                AND (c.persona_id IS NULL OR c.persona_id NOT IN @excluded)
                AND EXISTS (
                  SELECT 1 FROM messages m
                  WHERE m.conversation_id = c.id
                    AND m.created_at > c.memory_watermark_at
                    AND m.created_at >= @notBefore
                    AND m.role = 'user')
              ORDER BY c.updated_at DESC
              LIMIT @limit",
            new { notBefore, limit, proxy = MolaGptProviderIds.Proxy, excluded }).ToList();
    }

    public long GetWatermark(string conversationId)
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<long>(
            "SELECT memory_watermark_at FROM conversations WHERE id = @conversationId",
            new { conversationId });
    }

    public void SetWatermark(string conversationId, long watermark)
    {
        using var conn = _db.Open();
        conn.Execute(
            "UPDATE conversations SET memory_watermark_at = @watermark WHERE id = @conversationId",
            new { conversationId, watermark });
    }

    public bool? GetConversationMemoryOverride(string conversationId)
    {
        using var conn = _db.Open();
        return conn.QueryFirstOrDefault<long?>(
            "SELECT memory_enabled FROM conversations WHERE id = @conversationId",
            new { conversationId }) switch
        {
            null => null,
            0 => false,
            _ => true
        };
    }

    public void SetConversationMemoryOverride(string conversationId, bool? enabled)
    {
        using var conn = _db.Open();
        conn.Execute(
            "UPDATE conversations SET memory_enabled = @enabled WHERE id = @conversationId",
            new { conversationId, enabled = enabled is null ? (long?)null : enabled.Value ? 1 : 0 });
    }

    /// <summary>
    /// Clearing memory records a new learning start line instead of rewinding the
    /// watermarks. Old messages must not be re-scanned: the user deleted what was
    /// learned from them, and re-reading them would learn it straight back.
    /// </summary>
    public long GetResetAt()
    {
        using var conn = _db.Open();
        return ReadState(conn, "memory.reset_at") is { } raw && long.TryParse(raw, out var parsed) ? parsed : 0;
    }

    public void SetResetAt(long stamp)
    {
        using var conn = _db.Open();
        WriteState(conn, "memory.reset_at", stamp.ToString(), null);
    }

    public void ClearSuppressionsAndCandidates()
    {
        using var conn = _db.Open();
        conn.Execute("DELETE FROM memory_suppressions");
        conn.Execute("DELETE FROM memory_candidates");
    }

    // ---- state helpers -----------------------------------------------------

    private static string? ReadState(SqliteConnection conn, string key) =>
        conn.QueryFirstOrDefault<string>("SELECT value FROM memory_index_state WHERE key = @key", new { key });

    private static void WriteState(SqliteConnection conn, string key, string value, SqliteTransaction? tx) =>
        conn.Execute(
            @"INSERT INTO memory_index_state (key, value) VALUES (@key, @value)
              ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key, value }, tx);

    /// <summary>
    /// Wrap the needle as an FTS5 string literal. Users type quotes, hyphens and
    /// colons in ordinary sentences, and every one of them is grammar to FTS5.
    /// </summary>
    private static string Quote(string needle) => "\"" + needle.Replace("\"", "\"\"") + "\"";
}
