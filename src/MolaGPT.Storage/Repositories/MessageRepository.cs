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
            "SELECT id AS Id, conversation_id AS ConversationId, role AS Role, content AS Content, " +
            "meta AS Meta, created_at AS CreatedAt FROM messages " +
            "WHERE conversation_id = @c ORDER BY created_at ASC, rowid ASC",
            new { c = conversationId }).ToList();
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

    public void Insert(MessageRow row)
    {
        using var conn = _db.Open();
        conn.Execute(
            @"INSERT INTO messages (id, conversation_id, role, content, meta, created_at)
              VALUES (@Id, @ConversationId, @Role, @Content, @Meta, @CreatedAt)",
            row);
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
        conn.Execute("DELETE FROM messages WHERE id = @id", new { id });
    }

    public void ReplaceConversationMessages(string conversationId, IEnumerable<MessageRow> rows)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        conn.Execute("DELETE FROM messages WHERE conversation_id = @c", new { c = conversationId }, tx);
        foreach (var row in rows)
        {
            conn.Execute(
                @"INSERT INTO messages (id, conversation_id, role, content, meta, created_at)
                  VALUES (@Id, @ConversationId, @Role, @Content, @Meta, @CreatedAt)",
                row,
                tx);
        }
        tx.Commit();
    }
}
