using Dapper;

namespace MolaGPT.Storage.Repositories;

public sealed class PersonaRepository
{
    private const string SelectColumns =
        "id AS Id, name AS Name, avatar AS Avatar, system_prompt AS SystemPrompt, " +
        "default_enable_network AS DefaultEnableNetwork, default_enable_web_fetch AS DefaultEnableWebFetch, " +
        "default_thinking AS DefaultThinking, default_reasoning_effort AS DefaultReasoningEffort, " +
        "sort_order AS SortOrder, pinned AS Pinned, is_builtin AS IsBuiltin, " +
        "created_at AS CreatedAt, updated_at AS UpdatedAt, deleted_at AS DeletedAt";

    private readonly MolaGptDatabase _db;
    public PersonaRepository(MolaGptDatabase db) => _db = db;

    public IReadOnlyList<PersonaRow> ListActive()
    {
        using var conn = _db.Open();
        return conn.Query<PersonaRow>(
            $"SELECT {SelectColumns} FROM personas " +
            "WHERE deleted_at IS NULL ORDER BY pinned DESC, sort_order ASC, name ASC")
            .ToList();
    }

    public PersonaRow? Get(string id)
    {
        using var conn = _db.Open();
        return conn.QueryFirstOrDefault<PersonaRow>(
            $"SELECT {SelectColumns} FROM personas WHERE id = @id", new { id });
    }

    public int CountAll()
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM personas");
    }

    public void Upsert(PersonaRow row)
    {
        using var conn = _db.Open();
        conn.Execute(
            @"INSERT INTO personas (id, name, avatar, system_prompt,
                default_enable_network, default_enable_web_fetch, default_thinking, default_reasoning_effort,
                sort_order, pinned, is_builtin, created_at, updated_at, deleted_at)
              VALUES (@Id, @Name, @Avatar, @SystemPrompt,
                @DefaultEnableNetwork, @DefaultEnableWebFetch, @DefaultThinking, @DefaultReasoningEffort,
                @SortOrder, @Pinned, @IsBuiltin, @CreatedAt, @UpdatedAt, @DeletedAt)
              ON CONFLICT(id) DO UPDATE SET
                name=excluded.name, avatar=excluded.avatar, system_prompt=excluded.system_prompt,
                default_enable_network=excluded.default_enable_network,
                default_enable_web_fetch=excluded.default_enable_web_fetch,
                default_thinking=excluded.default_thinking,
                default_reasoning_effort=excluded.default_reasoning_effort,
                sort_order=excluded.sort_order, pinned=excluded.pinned,
                updated_at=excluded.updated_at, deleted_at=excluded.deleted_at",
            row);
    }

    public void SoftDelete(string id, long timestampMs)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE personas SET deleted_at = @t WHERE id = @id AND is_builtin = 0",
            new { id, t = timestampMs });
    }

    public void InsertManyIfEmpty(IEnumerable<PersonaRow> seeds)
    {
        using var conn = _db.Open();
        var count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM personas");
        if (count > 0) return;

        using var tx = conn.BeginTransaction();
        foreach (var row in seeds)
        {
            conn.Execute(
                @"INSERT INTO personas (id, name, avatar, system_prompt,
                    default_enable_network, default_enable_web_fetch, default_thinking, default_reasoning_effort,
                    sort_order, pinned, is_builtin, created_at, updated_at, deleted_at)
                  VALUES (@Id, @Name, @Avatar, @SystemPrompt,
                    @DefaultEnableNetwork, @DefaultEnableWebFetch, @DefaultThinking, @DefaultReasoningEffort,
                    @SortOrder, @Pinned, @IsBuiltin, @CreatedAt, @UpdatedAt, @DeletedAt)",
                row,
                tx);
        }
        tx.Commit();
    }
}
