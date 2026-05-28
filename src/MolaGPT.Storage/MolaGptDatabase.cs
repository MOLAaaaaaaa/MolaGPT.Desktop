using System.Reflection;
using Microsoft.Data.Sqlite;

namespace MolaGPT.Storage;

/// <summary>
/// Lightweight SQLite database wrapper. Owns the connection string and
/// applies <c>Migrations/*.sql</c> embedded resources at startup.
///
/// Default path: %LocalAppData%\\MolaGPT\\molagpt.db
/// </summary>
public sealed class MolaGptDatabase
{
    public string ConnectionString { get; }

    public MolaGptDatabase(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) throw new ArgumentException("filePath required", nameof(filePath));
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT", "molagpt.db");

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    /// <summary>Apply all embedded migration scripts in order. Idempotent.</summary>
    public void EnsureSchema()
    {
        using var conn = Open();
        var assembly = typeof(MolaGptDatabase).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        foreach (var resourceName in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Missing migration {resourceName}");
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column"))
            {
                // ALTER TABLE ADD COLUMN on an already-migrated database - safe to ignore.
            }
        }
    }
}
