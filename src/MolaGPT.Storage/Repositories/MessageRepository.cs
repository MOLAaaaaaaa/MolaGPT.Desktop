using Dapper;

namespace MolaGPT.Storage.Repositories;

public sealed class MessageRepository
{
    private readonly MolaGptDatabase _db;
    public MessageRepository(MolaGptDatabase db) => _db = db;

    public IReadOnlyList<MessageRow> List(string conversationId)
    {
        using var conn = _db.Open();
        return conn.Query<MessageRow>(
            @"WITH RECURSIVE path AS (
                SELECT message.id, message.conversation_id, message.role, message.content,
                       message.meta, message.created_at, message.parent_id, 0 AS depth
                FROM messages AS message
                JOIN conversation_timelines AS timeline ON timeline.leaf_message_id = message.id
                JOIN conversations AS conversation ON conversation.active_timeline_id = timeline.id
                WHERE conversation.id = @conversationId
                UNION ALL
                SELECT parent.id, parent.conversation_id, parent.role, parent.content,
                       parent.meta, parent.created_at, parent.parent_id, path.depth + 1
                FROM messages AS parent
                JOIN path ON path.parent_id = parent.id
              )
              SELECT id AS Id, conversation_id AS ConversationId, role AS Role, content AS Content,
                     meta AS Meta, created_at AS CreatedAt, parent_id AS ParentId
              FROM path
              ORDER BY depth DESC",
            new { conversationId }).ToList();
    }

    public IReadOnlyList<MessageRow> ListAll(string conversationId)
    {
        using var conn = _db.Open();
        return conn.Query<MessageRow>(
            @"SELECT id AS Id, conversation_id AS ConversationId, role AS Role, content AS Content,
                     meta AS Meta, created_at AS CreatedAt, parent_id AS ParentId
              FROM messages
              WHERE conversation_id = @conversationId
              ORDER BY created_at ASC, rowid ASC",
            new { conversationId }).ToList();
    }

    public IReadOnlyList<MessageRow> ListSiblings(string conversationId, string? parentId, string role)
    {
        using var conn = _db.Open();
        return conn.Query<MessageRow>(
            @"SELECT id AS Id, conversation_id AS ConversationId, role AS Role, content AS Content,
                     meta AS Meta, created_at AS CreatedAt, parent_id AS ParentId
              FROM messages
              WHERE conversation_id = @conversationId AND role = @role
                AND (parent_id = @parentId OR (parent_id IS NULL AND @parentId IS NULL))
              ORDER BY created_at ASC, rowid ASC",
            new { conversationId, parentId, role }).ToList();
    }

    public IReadOnlyList<ImageWorkbenchMessageRow> ListImageWorkbenchMessages(string providerId)
    {
        using var conn = _db.Open();
        return conn.Query<ImageWorkbenchMessageRow>(
            "SELECT m.id AS Id, m.conversation_id AS ConversationId, m.role AS Role, " +
            "m.content AS Content, m.meta AS Meta, m.created_at AS CreatedAt, " +
            "c.title AS ConversationTitle FROM messages m " +
            "JOIN conversations c ON c.id = m.conversation_id " +
            "WHERE c.provider_id = @p AND c.deleted_at IS NULL " +
            "AND m.meta LIKE '%\"image_workbench\"%' " +
            "ORDER BY m.created_at DESC",
            new { p = providerId }).ToList();
    }

    /// <summary>
    /// Every message meta blob that mentions a stored attachment, across all
    /// conversations including soft-deleted ones — a conversation sitting in the
    /// undo window must not have its attachments swept out from under it.
    /// </summary>
    public IReadOnlyList<string> ListAttachmentMetas()
    {
        using var conn = _db.Open();
        return conn.Query<string>(
            "SELECT meta FROM messages WHERE meta LIKE '%localName%'").ToList();
    }

    public string? Insert(MessageRow row)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var activeTimelineId = conn.ExecuteScalar<string?>(
            "SELECT active_timeline_id FROM conversations WHERE id = @conversationId",
            new { conversationId = row.ConversationId }, tx);
        var parentId = row.ParentId;
        if (parentId is null && activeTimelineId is not null)
            parentId = conn.ExecuteScalar<string?>(
                "SELECT leaf_message_id FROM conversation_timelines WHERE id = @activeTimelineId",
                new { activeTimelineId }, tx);
        var stored = row with { ParentId = parentId };
        conn.Execute(
            @"INSERT INTO messages (id, conversation_id, role, content, meta, created_at, parent_id)
              VALUES (@Id, @ConversationId, @Role, @Content, @Meta, @CreatedAt, @ParentId)",
            stored, tx);

        var timelineId = conn.ExecuteScalar<string?>(
            @"SELECT id FROM conversation_timelines
              WHERE conversation_id = @ConversationId
                AND (leaf_message_id = @parentId OR (leaf_message_id IS NULL AND @parentId IS NULL))
              ORDER BY CASE WHEN id = @activeTimelineId THEN 0 ELSE 1 END, updated_at DESC, rowid DESC
              LIMIT 1",
            new { row.ConversationId, parentId, activeTimelineId }, tx);
        if (timelineId is not null)
            conn.Execute(
                @"UPDATE conversation_timelines
                  SET leaf_message_id = @messageId, updated_at = @updatedAt
                  WHERE id = @timelineId",
                new { messageId = row.Id, updatedAt = row.CreatedAt, timelineId }, tx);
        tx.Commit();
        return parentId;
    }

    public void Update(string id, string content, string? meta)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE messages SET content = @content, meta = @meta WHERE id = @id",
            new { id, content, meta });
    }

    public void Delete(string id)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var parentId = conn.ExecuteScalar<string?>(
            "SELECT parent_id FROM messages WHERE id = @id", new { id }, tx);
        conn.Execute(
            "UPDATE conversation_timelines SET leaf_message_id = @parentId WHERE leaf_message_id = @id",
            new { parentId, id }, tx);
        conn.Execute("DELETE FROM messages WHERE id = @id", new { id }, tx);
        tx.Commit();
    }

    public void UpdateRoleMessage(string conversationId, string id, string content, string? meta, string roleContext)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var updated = conn.Execute(
            "UPDATE messages SET content = @content, meta = @meta WHERE id = @id AND conversation_id = @conversationId",
            new { id, conversationId, content, meta }, tx);
        if (updated != 1) throw new InvalidOperationException("消息已不存在。");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        conn.Execute("UPDATE conversations SET role_context_json = @roleContext, updated_at = @now WHERE id = @conversationId",
            new { roleContext, conversationId, now }, tx);
        conn.Execute(
            @"UPDATE conversation_timelines
              SET role_context_json = @roleContext, updated_at = @now
              WHERE id = (SELECT active_timeline_id FROM conversations WHERE id = @conversationId)",
            new { roleContext, conversationId, now }, tx);
        tx.Commit();
    }

    public void ReplaceConversationMessages(string conversationId, IEnumerable<MessageRow> rows)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var roleContext = conn.ExecuteScalar<string?>(
            "SELECT role_context_json FROM conversations WHERE id = @conversationId",
            new { conversationId }, tx) ?? "{}";
        var createdAt = conn.ExecuteScalar<long>(
            "SELECT created_at FROM conversations WHERE id = @conversationId",
            new { conversationId }, tx);
        var updatedAt = conn.ExecuteScalar<long>(
            "SELECT updated_at FROM conversations WHERE id = @conversationId",
            new { conversationId }, tx);
        conn.Execute("DELETE FROM conversation_timelines WHERE conversation_id = @conversationId",
            new { conversationId }, tx);
        conn.Execute("DELETE FROM messages WHERE conversation_id = @c", new { c = conversationId }, tx);
        string? parentId = null;
        foreach (var row in rows)
        {
            var stored = row with { ParentId = row.ParentId ?? parentId };
            conn.Execute(
                @"INSERT INTO messages (id, conversation_id, role, content, meta, created_at, parent_id)
                  VALUES (@Id, @ConversationId, @Role, @Content, @Meta, @CreatedAt, @ParentId)",
                stored,
                tx);
            parentId = stored.Id;
        }
        var timelineId = conversationId + ":main";
        conn.Execute(
            @"INSERT INTO conversation_timelines
                (id, conversation_id, leaf_message_id, role_context_json, created_at, updated_at)
              VALUES (@timelineId, @conversationId, @parentId, @roleContext, @createdAt, @updatedAt)",
            new { timelineId, conversationId, parentId, roleContext, createdAt, updatedAt }, tx);
        conn.Execute("UPDATE conversations SET active_timeline_id = @timelineId WHERE id = @conversationId",
            new { timelineId, conversationId }, tx);
        tx.Commit();
    }
}
