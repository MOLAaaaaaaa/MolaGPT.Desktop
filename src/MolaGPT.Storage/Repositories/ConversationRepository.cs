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
}
