using Dapper;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Tools.PythonExecution;
using Microsoft.Data.Sqlite;

namespace MolaGPT.Storage.Repositories;

public sealed class MessageRepository
{
    private static readonly JsonSerializerOptions CompactJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly MolaGptDatabase _db;
    public MessageRepository(MolaGptDatabase db) => _db = db;

    /// <summary>A message was inserted or rewritten: (conversationId, messageId).
    /// Raised on the writing thread, after the write is committed.</summary>
    public event Action<string, string>? MessageWritten;

    /// <summary>Messages of a conversation were removed or replaced wholesale, so
    /// anyone mirroring it should read it again.</summary>
    public event Action<string>? ConversationRewritten;

    public MessageRow? Get(string id)
    {
        using var conn = _db.Open();
        return conn.QueryFirstOrDefault<MessageRow>(
            @"SELECT id AS Id, conversation_id AS ConversationId, role AS Role, content AS Content,
                     meta AS Meta, created_at AS CreatedAt, parent_id AS ParentId
              FROM messages WHERE id = @id",
            new { id });
    }

    public IReadOnlyList<MessageRow> List(string conversationId)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction(deferred: true);
        var leaf = conn.ExecuteScalar<string?>("SELECT t.leaf_message_id" + ActiveTimelineJoin
            + " WHERE c.id = @conversationId", new { conversationId }, tx);
        if (leaf is null) return [];
        var rows = conn.Query<MessageRow>(
            @"WITH RECURSIVE path(id) AS (
                SELECT id FROM messages WHERE id = @leaf AND conversation_id = @conversationId
                UNION
                SELECT parent.id FROM path
                JOIN messages AS child ON child.id = path.id
                JOIN messages AS parent ON parent.id = child.parent_id AND parent.conversation_id = @conversationId
              )
              SELECT m.id AS Id, m.conversation_id AS ConversationId, m.role AS Role, m.content AS Content,
                     m.meta AS Meta, m.created_at AS CreatedAt, m.parent_id AS ParentId
              FROM path JOIN messages AS m ON m.id = path.id",
            new { leaf, conversationId }, tx).ToDictionary(row => row.Id, StringComparer.Ordinal);
        tx.Commit();
        var chain = new List<MessageRow>(rows.Count);
        while (leaf is not null && rows.Remove(leaf, out var row))
        {
            chain.Add(row);
            leaf = row.ParentId;
        }
        chain.Reverse();
        return chain;
    }

    public HashSet<string> ListConversationIdsWithMessages()
    {
        using var conn = _db.Open();
        return conn.Query<string>("SELECT DISTINCT conversation_id FROM messages").ToHashSet(StringComparer.Ordinal);
    }

    public bool HasActiveMessages(string conversationId)
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<bool>(@"SELECT EXISTS (
            SELECT 1" + ActiveTimelineJoin + @"
            JOIN messages AS m ON m.id = t.leaf_message_id AND m.conversation_id = c.id
            WHERE c.id = @conversationId
        )", new { conversationId });
    }

    private const string ListAllSql = @"SELECT id AS Id, conversation_id AS ConversationId, role AS Role, content AS Content,
                     meta AS Meta, created_at AS CreatedAt, parent_id AS ParentId
              FROM messages
              WHERE conversation_id = @conversationId
              ORDER BY created_at ASC, rowid ASC";

    private const string ActiveTimelineJoin = @" FROM conversations AS c
             JOIN conversation_timelines AS t ON t.id = c.active_timeline_id AND t.conversation_id = c.id";

    public IReadOnlyList<MessageRow> ListAll(string conversationId)
    {
        using var conn = _db.Open();
        return conn.Query<MessageRow>(ListAllSql, new { conversationId }).AsList();
    }

    public MessageRow? GetActiveLeaf(string conversationId)
    {
        using var conn = _db.Open();
        return conn.QuerySingleOrDefault<MessageRow>(@"SELECT m.id AS Id, m.conversation_id AS ConversationId,
            m.role AS Role, m.content AS Content, '' AS Meta, m.created_at AS CreatedAt, m.parent_id AS ParentId" +
            ActiveTimelineJoin + @" JOIN messages AS m ON m.id = t.leaf_message_id AND m.conversation_id = c.id
            WHERE c.id = @conversationId", new { conversationId });
    }

    public (IReadOnlyList<MessageRow> All, string? ActiveLeafId) ReadConversationSnapshot(string conversationId)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction(deferred: true);
        var parameters = new { conversationId };
        var leaf = conn.QuerySingleOrDefault<string>(
            "SELECT t.leaf_message_id" + ActiveTimelineJoin + " WHERE c.id = @conversationId", parameters, tx);
        var rows = conn.Query<MessageRow>(ListAllSql, parameters, tx).AsList();
        tx.Commit();
        return (rows, leaf);
    }

    public IReadOnlyList<string>? ListSubagentSessionKeys(string? conversationId = null)
    {
        using var conn = _db.Open();
        using var command = conn.CreateCommand();
        // GetStream needs the rowid and the original column to open a SqliteBlob.
        command.CommandText = @"SELECT m.rowid, m.meta FROM messages AS m
              JOIN conversations AS c ON c.id = m.conversation_id
              WHERE (@conversationId IS NULL OR c.id = @conversationId) AND m.role = 'assistant'
                AND (m.meta LIKE '%spawn_agent%' OR m.meta LIKE '%followup_agent%')";
        command.Parameters.AddWithValue("@conversationId", (object?)conversationId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            using var stream = reader.GetStream(1);
            if (!SubagentSessionMetadata.TryCollectKeys(stream, keys)) return null;
        }
        return keys.ToArray();
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

    /// <summary>
    /// Names of the Python workspaces any stored message points into, across all
    /// conversations including soft-deleted ones. Runs without a conversation id
    /// land in a timestamped directory nothing else records, so the message that
    /// links their output is the only evidence one is still in use.
    /// </summary>
    public IReadOnlySet<string> ListReferencedWorkspaceNames(string? conversationId = null)
    {
        return ListWorkspaceReferences(conversationId).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, HashSet<string>> ListWorkspaceReferences(string? conversationId = null)
    {
        var references = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        using var conn = _db.Open();
        // Unbuffered: the matching rows are tens of megabytes and only the names survive.
        foreach (var (id, content, meta) in conn.Query<(string Id, string? Content, string? Meta)>(
                     @"SELECT conversation_id, content, meta FROM messages
                       WHERE (@conversationId IS NULL OR conversation_id = @conversationId)
                         AND (meta LIKE '%python-tool%' OR content LIKE '%python-tool%')",
                     new { conversationId },
                     buffered: false))
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            PythonWorkspaceStore.CollectReferencedNames(content, names);
            PythonWorkspaceStore.CollectReferencedNames(meta, names);
            foreach (var name in names)
            {
                if (!references.TryGetValue(name, out var owners))
                    references[name] = owners = new HashSet<string>(StringComparer.Ordinal);
                owners.Add(id);
            }
        }
        return references;
    }

    /// <summary>Rewrite tool metadata as equivalent compact JSON. The original
    /// text must still match so a concurrent message update cannot be lost.</summary>
    public long CompactToolMetadata()
    {
        using var conn = _db.Open();
        long lastRowId = 0, saved = 0;
        while (true)
        {
            var rows = conn.Query<(long RowId, string Id, string Meta)>(
                @"SELECT rowid, id, meta FROM messages WHERE rowid > @lastRowId
                  AND (meta LIKE '%result_preview_json%' OR meta LIKE '%resultPreviewJson%')
                  ORDER BY rowid LIMIT 32", new { lastRowId }).ToArray();
            if (rows.Length == 0) break;
            using var tx = conn.BeginTransaction();
            foreach (var row in rows)
            {
                lastRowId = row.RowId;
                try
                {
                    using var document = JsonDocument.Parse(row.Meta);
                    var compact = JsonSerializer.Serialize(document.RootElement, CompactJson);
                    var difference = Encoding.UTF8.GetByteCount(row.Meta) - Encoding.UTF8.GetByteCount(compact);
                    if (difference > 0 && conn.Execute(
                            "UPDATE messages SET meta = @compact WHERE id = @id AND meta = @original",
                            new { compact, id = row.Id, original = row.Meta }, tx) == 1)
                        saved += difference;
                }
                catch (JsonException) { /* Preserve malformed historical metadata unchanged. */ }
            }
            tx.Commit();
        }
        return saved;
    }

    public string? Insert(MessageRow row) => Insert(row, null);

    public void InsertImageWorkbenchRun(MessageRow row, string title, string? modelId) =>
        Insert(row, (title, modelId));

    private string? Insert(MessageRow row, (string Title, string? ModelId)? imageRun)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var activeTimelineId = conn.ExecuteScalar<string?>(
            @"SELECT active_timeline_id FROM conversations
              WHERE id = @conversationId AND (deleted_at IS NULL OR @imageResult = 1)",
            new { conversationId = row.ConversationId, imageResult = imageRun.HasValue }, tx);
        if (activeTimelineId is null)
            throw new InvalidOperationException("对话已不存在，无法保存消息。");
        var parentId = row.ParentId;
        if (parentId is null && activeTimelineId is not null)
            parentId = conn.ExecuteScalar<string?>(
                "SELECT leaf_message_id FROM conversation_timelines WHERE id = @activeTimelineId AND conversation_id = @ConversationId",
                new { activeTimelineId, row.ConversationId }, tx);
        if (parentId is not null && !conn.ExecuteScalar<bool>(
                "SELECT EXISTS(SELECT 1 FROM messages WHERE id = @parentId AND conversation_id = @ConversationId)",
                new { parentId, row.ConversationId }, tx))
            throw new InvalidDataException("父消息不存在或不属于当前会话。");
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
        if (imageRun is { } result)
            conn.Execute(
                @"UPDATE conversations
                  SET title = CASE WHEN trim(title) = '' OR title IN ('图像工作台', '新对话') THEN @Title ELSE title END,
                      model_id = COALESCE(@ModelId, model_id), updated_at = MAX(updated_at, @now)
                  WHERE id = @ConversationId",
                new { result.Title, result.ModelId, row.ConversationId, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx);
        tx.Commit();
        MessageWritten?.Invoke(row.ConversationId, row.Id);
        return parentId;
    }

    public bool Update(string id, string content, string? meta)
    {
        using var conn = _db.Open();
        var updated = conn.Execute(@"UPDATE messages SET content = @content, meta = @meta WHERE id = @id
            AND EXISTS (SELECT 1 FROM conversations WHERE id = messages.conversation_id AND deleted_at IS NULL)",
            new { id, content, meta }) != 0;
        if (!updated) return false;
        if (MessageWritten is { } written
            && conn.ExecuteScalar<string?>("SELECT conversation_id FROM messages WHERE id = @id", new { id }) is { } conversationId)
            written(conversationId, id);
        return true;
    }

    public void UpdateBackgroundTaskState(string conversationId, string taskId, string state)
    {
        using var conn = _db.Open();
        var rows = conn.Query<MessageRow>(
            @"SELECT id AS Id, conversation_id AS ConversationId, role AS Role,
                     content AS Content, meta AS Meta, created_at AS CreatedAt, parent_id AS ParentId
              FROM messages
              WHERE conversation_id = @conversationId AND role = 'assistant' AND meta LIKE @needle",
            new { conversationId, needle = "%" + taskId + "%" });
        foreach (var row in rows)
        {
            if (JsonNode.Parse(row.Meta ?? "{}") is not JsonObject meta) continue;
            var changed = UpdateToolStates(meta["tool_calls"] as JsonArray, taskId, state);
            if (meta["retry"] is JsonObject retry && retry["attempts"] is JsonArray attempts)
                foreach (var attempt in attempts.OfType<JsonObject>())
                    changed |= UpdateToolStates(attempt["tool_calls"] as JsonArray, taskId, state);
            if (changed)
            {
                conn.Execute("UPDATE messages SET meta = @meta WHERE id = @id",
                    new { id = row.Id, meta = meta.ToJsonString() });
                MessageWritten?.Invoke(conversationId, row.Id);
            }
        }
    }

    private static bool UpdateToolStates(JsonArray? tools, string taskId, string state)
    {
        if (tools is null) return false;
        var changed = false;
        foreach (var tool in tools.OfType<JsonObject>())
        {
            if (!MatchesBackgroundTask(tool, taskId)) continue;
            if ((string?)tool["background_task_id"] == taskId
                && (string?)tool["task_state"] == state) continue;
            tool["background_task_id"] = taskId;
            tool["task_state"] = state;
            changed = true;
        }
        return changed;
    }

    private static bool MatchesBackgroundTask(JsonObject tool, string taskId)
    {
        if ((string?)tool["background_task_id"] == taskId) return true;
        if ((string?)tool["result_preview_json"] is not { } preview) return false;
        try
        {
            using var doc = JsonDocument.Parse(preview);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("background", out var background)
                   && background.ValueKind == JsonValueKind.True
                   && root.TryGetProperty("task_id", out var id)
                   && id.ValueKind == JsonValueKind.String
                   && id.GetString() == taskId;
        }
        catch (JsonException) { return false; }
    }

    public void Delete(string id)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var conversationId = conn.ExecuteScalar<string?>(
            "SELECT conversation_id FROM messages WHERE id = @id", new { id }, tx);
        var parentId = conn.ExecuteScalar<string?>(
            "SELECT parent_id FROM messages WHERE id = @id", new { id }, tx);
        conn.Execute(
            "UPDATE conversation_timelines SET leaf_message_id = @parentId WHERE leaf_message_id = @id",
            new { parentId, id }, tx);
        conn.Execute("DELETE FROM messages WHERE id = @id", new { id }, tx);
        tx.Commit();
        if (conversationId is not null) ConversationRewritten?.Invoke(conversationId);
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
        MessageWritten?.Invoke(conversationId, id);
    }

    internal static void ReplaceConversationMessages(
        SqliteConnection conn, SqliteTransaction tx, string conversationId, IEnumerable<MessageRow> rows)
    {
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
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var stored = row with { ParentId = row.ParentId ?? parentId };
            if (row.ConversationId != conversationId || !parents.TryAdd(row.Id, stored.ParentId))
                throw new InvalidDataException("导入消息的会话或 ID 无效。");
            conn.Execute(
                @"INSERT INTO messages (id, conversation_id, role, content, meta, created_at, parent_id)
                  VALUES (@Id, @ConversationId, @Role, @Content, @Meta, @CreatedAt, @ParentId)",
                stored,
                tx);
            parentId = stored.Id;
        }
        // Validate the batch, not the old database. A parent may follow its child on the wire.
        var valid = new HashSet<string>(StringComparer.Ordinal);
        var chain = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in parents.Keys)
        {
            chain.Clear();
            for (string? current = id; current is not null && !valid.Contains(current);)
                if (!chain.Add(current) || !parents.TryGetValue(current, out current))
                    throw new InvalidDataException("导入消息的父子关系无效。");
            valid.UnionWith(chain);
        }
        var timelineId = conversationId + ":main";
        conn.Execute(
            @"INSERT INTO conversation_timelines
                (id, conversation_id, leaf_message_id, role_context_json, created_at, updated_at)
              VALUES (@timelineId, @conversationId, @parentId, @roleContext, @createdAt, @updatedAt)",
            new { timelineId, conversationId, parentId, roleContext, createdAt, updatedAt }, tx);
        conn.Execute("UPDATE conversations SET active_timeline_id = @timelineId WHERE id = @conversationId",
            new { timelineId, conversationId }, tx);
    }
}
