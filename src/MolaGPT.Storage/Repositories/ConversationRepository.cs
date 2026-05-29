using Dapper;

namespace MolaGPT.Storage.Repositories;

public sealed class ConversationRepository
{
    private const string SelectColumns =
        "id AS Id, title AS Title, model_id AS ModelId, provider_id AS ProviderId, " +
        "created_at AS CreatedAt, updated_at AS UpdatedAt, pinned AS Pinned, deleted_at AS DeletedAt, " +
        "system_prompt AS SystemPrompt, persona_id AS PersonaId, system_prompt_mode AS SystemPromptMode";

    private readonly MolaGptDatabase _db;
    public ConversationRepository(MolaGptDatabase db) => _db = db;

    public IReadOnlyList<ConversationRow> ListActive()
    {
        using var conn = _db.Open();
        return conn.Query<ConversationRow>(
            $"SELECT {SelectColumns} FROM conversations " +
            "WHERE deleted_at IS NULL ORDER BY pinned DESC, updated_at DESC")
            .ToList();
    }

    public IReadOnlyList<ConversationRow> ListDeletedSince(long timestampMs)
    {
        using var conn = _db.Open();
        return conn.Query<ConversationRow>(
            $"SELECT {SelectColumns} FROM conversations " +
            "WHERE deleted_at IS NOT NULL AND deleted_at >= @t ORDER BY deleted_at DESC",
            new { t = timestampMs })
            .ToList();
    }

    public ConversationRow? Get(string id)
    {
        using var conn = _db.Open();
        return conn.QueryFirstOrDefault<ConversationRow>(
            $"SELECT {SelectColumns} FROM conversations WHERE id = @id", new { id });
    }

    public void Upsert(ConversationRow row)
    {
        using var conn = _db.Open();
        conn.Execute(
            @"INSERT INTO conversations (id, title, model_id, provider_id, created_at, updated_at, pinned, deleted_at, system_prompt, persona_id, system_prompt_mode)
              VALUES (@Id, @Title, @ModelId, @ProviderId, @CreatedAt, @UpdatedAt, @Pinned, @DeletedAt, @SystemPrompt, @PersonaId, @SystemPromptMode)
              ON CONFLICT(id) DO UPDATE SET
                title=excluded.title, model_id=excluded.model_id, provider_id=excluded.provider_id,
                updated_at=excluded.updated_at, pinned=excluded.pinned, deleted_at=excluded.deleted_at,
                system_prompt=excluded.system_prompt, persona_id=excluded.persona_id,
                system_prompt_mode=excluded.system_prompt_mode",
            row);
    }

    public void SoftDelete(string id, long timestampMs)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE conversations SET deleted_at = @t WHERE id = @id", new { id, t = timestampMs });
    }

    public void Rename(string id, string title, long timestampMs)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE conversations SET title = @title, updated_at = @t WHERE id = @id",
            new { id, title, t = timestampMs });
    }

    public void SoftDeleteMany(IReadOnlyList<string> ids, long timestampMs)
    {
        if (ids.Count == 0) return;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var id in ids)
            conn.Execute("UPDATE conversations SET deleted_at = @t WHERE id = @id",
                new { id, t = timestampMs }, tx);
        tx.Commit();
    }

    /// <summary>
    /// Hard-deletes only the "metadata-only placeholder" conversations of a
    /// provider — active rows (<c>deleted_at IS NULL</c>) that have no rows in
    /// the messages table (i.e. cloud list entries that were never
    /// opened/downloaded locally). Conversations that carry actual local
    /// message content are left untouched.
    ///
    /// This is the logout-cleanup primitive: with cloud sync ON, the empty
    /// placeholders are safe to drop because the server keeps a full backing
    /// copy and re-login repopulates them from <c>full_metadata_list</c>; with
    /// cloud sync OFF (every conversation has local messages, nothing on the
    /// server) it deletes nothing, so no local-only data is lost.
    ///
    /// Soft-deleted placeholders are deliberately preserved: an empty
    /// conversation with <c>deleted_at</c> set is a pending tombstone awaiting
    /// propagation to the server. Hard-deleting it would drop the deletion, so
    /// re-login would resurrect it from the server list.
    ///
    /// Hard delete (not soft) is required so the next login's sync does not
    /// push these ids to the server's delete endpoint.
    /// </summary>
    public IReadOnlyList<string> HardDeleteEmptyByProvider(string providerId)
    {
        const string selectSql =
            "SELECT id FROM conversations c WHERE c.provider_id = @p " +
            "AND c.deleted_at IS NULL " +
            "AND NOT EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id = c.id)";
        using var conn = _db.Open();
        var ids = conn.Query<string>(selectSql, new { p = providerId }).ToList();
        if (ids.Count > 0)
            conn.Execute(
                "DELETE FROM conversations WHERE provider_id = @p " +
                "AND deleted_at IS NULL " +
                "AND NOT EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id = conversations.id)",
                new { p = providerId });
        return ids;
    }

    public IReadOnlyList<string> HardDeleteDeletedByProvider(IReadOnlyList<string> ids, string providerId)
    {
        if (ids.Count == 0) return Array.Empty<string>();

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var deleted = new List<string>();
        foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            var affected = conn.Execute(
                "DELETE FROM conversations WHERE id = @id AND provider_id = @p AND deleted_at IS NOT NULL",
                new { id, p = providerId }, tx);
            if (affected > 0) deleted.Add(id);
        }

        tx.Commit();
        return deleted;
    }

    /// <summary>
    /// Hard-deletes ALL conversations of a provider (and their messages, via
    /// ON DELETE CASCADE), regardless of message content or soft-delete state.
    ///
    /// This is the account-switch primitive: when the locally-bound account
    /// differs from the account now logging in, the previous account's locally
    /// retained MolaGPT conversations must be wiped before the new account's
    /// first sync, otherwise BuildDirtyConversations would upload them to the
    /// new account (cross-account data leak). Unlike the logout cleanup, this
    /// intentionally drops conversations that have local content too, because
    /// they belong to a different account and the new account must start clean.
    /// </summary>
    public IReadOnlyList<string> PurgeAllByProvider(string providerId)
    {
        using var conn = _db.Open();
        var ids = conn.Query<string>(
            "SELECT id FROM conversations WHERE provider_id = @p", new { p = providerId }).ToList();
        if (ids.Count > 0)
            conn.Execute("DELETE FROM conversations WHERE provider_id = @p", new { p = providerId });
        return ids;
    }
}
