using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MolaGPT.Core.Chat.Agents.Relay;

/// <summary>Keep dispatch outcomes independently of the relay acknowledgement.</summary>
public sealed class RelayCommandJournal(string? directory = null)
{
    public sealed record Entry(bool? Succeeded, string? Error);
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _gate = new();

    public Entry? Read(string sessionId, string commandId)
    {
        var key = Key(sessionId, commandId);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry)) return entry;
            if (directory is null) return null;
            var path = Path.Combine(directory, key + ".json");
            return File.Exists(path) ? JsonSerializer.Deserialize<Entry>(File.ReadAllText(path)) : null;
        }
    }

    public void Write(string sessionId, string commandId, Entry entry)
    {
        var key = Key(sessionId, commandId);
        lock (_gate)
        {
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, key + ".json");
                using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, entry);
                    file.Flush(flushToDisk: true);
                }
                File.Move(path + ".tmp", path, overwrite: true);
            }
            else _entries[key] = entry;
        }
    }

    private static string Key(string sessionId, string commandId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId + "\0" + commandId)));
}
