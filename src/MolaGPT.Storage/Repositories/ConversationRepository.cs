using Dapper;

namespace MolaGPT.Storage.Repositories;

public sealed class ConversationRepository
{
    private const string SelectColumns =
        "id AS Id, title AS Title, model_id AS ModelId, provider_id AS ProviderId, " +
        "created_at AS CreatedAt, updated_at AS UpdatedAt, pinned AS Pinned, deleted_at AS DeletedAt, " +
        "system_prompt AS SystemPrompt, persona_id AS PersonaId, system_prompt_mode AS SystemPromptMode, " +
        "role_context_json AS RoleContextJson, active_timeline_id AS ActiveTimelineId";

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

    public int CountRoleReference(string id, bool identity)
    {
        using var conn = _db.Open();
        var condition = identity ? "json_extract(role_context_json, '$.userPersonaId') = @id"
            : "EXISTS (SELECT 1 FROM json_each(role_context_json, '$.sharedLorebookIds') WHERE value = @id)";
        return conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM conversations WHERE deleted_at IS NULL AND {condition}", new { id });
    }

    public void Upsert(ConversationRow row)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        conn.Execute(
            @"INSERT INTO conversations (id, title, model_id, provider_id, created_at, updated_at, pinned, deleted_at, system_prompt, persona_id, system_prompt_mode, role_context_json, active_timeline_id)
              VALUES (@Id, @Title, @ModelId, @ProviderId, @CreatedAt, @UpdatedAt, @Pinned, @DeletedAt, @SystemPrompt, @PersonaId, @SystemPromptMode, @RoleContextJson, @ActiveTimelineId)
              ON CONFLICT(id) DO UPDATE SET
                title=excluded.title, model_id=excluded.model_id, provider_id=excluded.provider_id,
                updated_at=excluded.updated_at, pinned=excluded.pinned, deleted_at=excluded.deleted_at,
                system_prompt=excluded.system_prompt, persona_id=excluded.persona_id,
                system_prompt_mode=excluded.system_prompt_mode, role_context_json=excluded.role_context_json,
                active_timeline_id=COALESCE(excluded.active_timeline_id, conversations.active_timeline_id)",
            row, tx);

        var timelineId = conn.ExecuteScalar<string?>(
            "SELECT active_timeline_id FROM conversations WHERE id = @id", new { id = row.Id }, tx);
        timelineId ??= row.Id + ":main";
        conn.Execute(
            @"INSERT OR IGNORE INTO conversation_timelines
                (id, conversation_id, leaf_message_id, role_context_json, created_at, updated_at)
              VALUES (@timelineId, @conversationId, NULL, @roleContext, @createdAt, @updatedAt)",
            new
            {
                timelineId,
                conversationId = row.Id,
                roleContext = row.RoleContextJson,
                createdAt = row.CreatedAt,
                updatedAt = row.UpdatedAt
            }, tx);
        conn.Execute(
            @"UPDATE conversations SET active_timeline_id = @timelineId WHERE id = @conversationId;
              UPDATE conversation_timelines
              SET role_context_json = @roleContext, updated_at = @updatedAt
              WHERE id = @timelineId AND conversation_id = @conversationId;",
            new { timelineId, conversationId = row.Id, roleContext = row.RoleContextJson, updatedAt = row.UpdatedAt }, tx);
        tx.Commit();
    }

    public void SoftDelete(string id, long timestampMs)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE conversations SET deleted_at = @t WHERE id = @id", new { id, t = timestampMs });
    }

    public void CreateWithMessages(ConversationRow row, IReadOnlyList<MessageRow> messages)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var timelineId = row.ActiveTimelineId ?? row.Id + ":main";
        conn.Execute(
            @"INSERT INTO conversations (id, title, model_id, provider_id, created_at, updated_at,
                pinned, deleted_at, system_prompt, persona_id, system_prompt_mode, role_context_json, active_timeline_id)
              VALUES (@Id, @Title, @ModelId, @ProviderId, @CreatedAt, @UpdatedAt,
                @Pinned, @DeletedAt, @SystemPrompt, @PersonaId, @SystemPromptMode, @RoleContextJson, @timelineId)",
            new
            {
                row.Id, row.Title, row.ModelId, row.ProviderId, row.CreatedAt, row.UpdatedAt,
                row.Pinned, row.DeletedAt, row.SystemPrompt, row.PersonaId, row.SystemPromptMode,
                row.RoleContextJson, timelineId
            }, tx);

        string? parentId = null;
        var normalized = new List<MessageRow>(messages.Count);
        foreach (var message in messages)
        {
            var item = message with { ParentId = message.ParentId ?? parentId };
            normalized.Add(item);
            parentId = item.Id;
        }
        conn.Execute(
            @"INSERT INTO messages (id, conversation_id, role, content, meta, created_at, parent_id)
              VALUES (@Id, @ConversationId, @Role, @Content, @Meta, @CreatedAt, @ParentId)", normalized, tx);
        conn.Execute(
            @"INSERT INTO conversation_timelines
                (id, conversation_id, leaf_message_id, role_context_json, created_at, updated_at)
              VALUES (@timelineId, @conversationId, @leafMessageId, @roleContext, @createdAt, @updatedAt)",
            new
            {
                timelineId,
                conversationId = row.Id,
                leafMessageId = normalized.LastOrDefault()?.Id,
                roleContext = row.RoleContextJson,
                createdAt = row.CreatedAt,
                updatedAt = row.UpdatedAt
            }, tx);
        tx.Commit();
    }

    public IReadOnlyList<ConversationTimelineRow> ListTimelines(string conversationId)
    {
        using var conn = _db.Open();
        return conn.Query<ConversationTimelineRow>(
            @"SELECT id AS Id, conversation_id AS ConversationId, leaf_message_id AS LeafMessageId,
                     role_context_json AS RoleContextJson, created_at AS CreatedAt, updated_at AS UpdatedAt
              FROM conversation_timelines
              WHERE conversation_id = @conversationId
              ORDER BY updated_at DESC, rowid DESC",
            new { conversationId }).ToList();
    }

    public string CreateTimelineWithMessage(MessageRow message, string roleContext)
    {
        var timelineId = Guid.NewGuid().ToString("N");
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        conn.Execute(
            @"INSERT INTO messages (id, conversation_id, role, content, meta, created_at, parent_id)
              VALUES (@Id, @ConversationId, @Role, @Content, @Meta, @CreatedAt, @ParentId)", message, tx);
        conn.Execute(
            @"INSERT INTO conversation_timelines
                (id, conversation_id, leaf_message_id, role_context_json, created_at, updated_at)
              VALUES (@timelineId, @conversationId, @leafMessageId, @roleContext, @now, @now)",
            new
            {
                timelineId,
                conversationId = message.ConversationId,
                leafMessageId = message.Id,
                roleContext,
                now = message.CreatedAt
            }, tx);
        var updated = conn.Execute(
            @"UPDATE conversations
              SET active_timeline_id = @timelineId, role_context_json = @roleContext, updated_at = @now
              WHERE id = @conversationId",
            new { timelineId, conversationId = message.ConversationId, roleContext, now = message.CreatedAt }, tx);
        if (updated != 1) throw new InvalidOperationException("找不到当前对话。");
        tx.Commit();
        return timelineId;
    }

    public void SelectTimeline(string conversationId, string timelineId)
    {
        using var conn = _db.Open();
        var updated = conn.Execute(
            @"UPDATE conversations
              SET active_timeline_id = @timelineId,
                   role_context_json = (
                     SELECT role_context_json FROM conversation_timelines
                     WHERE id = @timelineId AND conversation_id = @conversationId)
              WHERE id = @conversationId
                AND EXISTS (
                  SELECT 1 FROM conversation_timelines
                  WHERE id = @timelineId AND conversation_id = @conversationId)",
            new { conversationId, timelineId });
        if (updated != 1) throw new InvalidOperationException("找不到所选时间线。");
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
    /// Reverse a soft-delete by clearing the tombstone. Backs the sidebar's
    /// "undo delete" affordance. We only touch <c>deleted_at</c> and leave
    /// <c>updated_at</c> untouched so the row returns to its exact prior
    /// ordering; cloud sync re-learns the active state on its next pass.
    /// </summary>
    public void RestoreMany(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var id in ids)
            conn.Execute("UPDATE conversations SET deleted_at = NULL WHERE id = @id",
                new { id }, tx);
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
