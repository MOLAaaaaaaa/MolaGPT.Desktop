using Dapper;

namespace MolaGPT.Storage.Repositories;

public sealed class SettingsRepository
{
    private readonly MolaGptDatabase _db;
    public SettingsRepository(MolaGptDatabase db) => _db = db;

    public string? Get(string key)
    {
        using var conn = _db.Open();
        return conn.QueryFirstOrDefault<string>("SELECT value FROM settings WHERE key = @key", new { key });
    }

    public void Set(string key, string value)
    {
        using var conn = _db.Open();
        conn.Execute(
            "INSERT INTO settings (key, value) VALUES (@key, @value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key, value });
    }

    public void Remove(string key)
    {
        using var conn = _db.Open();
        conn.Execute("DELETE FROM settings WHERE key = @key", new { key });
    }

    public void RemoveByPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return;
        using var conn = _db.Open();
        conn.Execute("DELETE FROM settings WHERE key LIKE @pattern ESCAPE '\\'",
            new { pattern = prefix.Replace("%", "\\%").Replace("_", "\\_") + "%" });
    }

    /// <summary>All (key, value) pairs whose key starts with <paramref name="prefix"/>.</summary>
    public IReadOnlyList<(string Key, string Value)> GetByPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return Array.Empty<(string, string)>();
        using var conn = _db.Open();
        return conn.Query<(string Key, string Value)>(
            "SELECT key AS Key, value AS Value FROM settings WHERE key LIKE @pattern ESCAPE '\\'",
            new { pattern = prefix.Replace("%", "\\%").Replace("_", "\\_") + "%" }).ToList();
    }
}

public sealed class ProviderRepository
{
    private readonly MolaGptDatabase _db;
    public ProviderRepository(MolaGptDatabase db) => _db = db;

    public IReadOnlyList<ProviderRow> List()
    {
        using var conn = _db.Open();
        return conn.Query<ProviderRow>(
            "SELECT id AS Id, type AS Type, name AS Name, base_url AS BaseUrl, " +
            "api_key_enc AS ApiKeyEnc, models AS Models, enabled AS Enabled, sort_order AS SortOrder, " +
            "purpose AS Purpose, api_path AS ApiPath, image_edit_path AS ImageEditPath, image_format AS ImageFormat " +
            "FROM providers ORDER BY sort_order ASC, name ASC").ToList();
    }

    public void Upsert(ProviderRow row)
    {
        using var conn = _db.Open();
        conn.Execute(
            @"INSERT INTO providers (id, type, name, base_url, api_key_enc, models, enabled, sort_order, purpose, api_path, image_edit_path, image_format)
              VALUES (@Id, @Type, @Name, @BaseUrl, @ApiKeyEnc, @Models, @Enabled, @SortOrder, @Purpose, @ApiPath, @ImageEditPath, @ImageFormat)
              ON CONFLICT(id) DO UPDATE SET
                type=excluded.type, name=excluded.name, base_url=excluded.base_url,
                api_key_enc=excluded.api_key_enc, models=excluded.models,
                enabled=excluded.enabled, sort_order=excluded.sort_order, purpose=excluded.purpose,
                api_path=excluded.api_path, image_edit_path=excluded.image_edit_path, image_format=excluded.image_format",
            row);
    }

    public void Delete(string id)
    {
        using var conn = _db.Open();
        conn.Execute("DELETE FROM providers WHERE id = @id", new { id });
    }
}
