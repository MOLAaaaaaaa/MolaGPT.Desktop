using System.Security.Cryptography;
using System.Text;

namespace MolaGPT.Core.Auth;

/// <summary>
/// Encrypted local credential storage using Windows DPAPI (CurrentUser scope).
/// Used for: MolaGPT JWT, BYOK API keys.
///
/// On non-Windows hosts (e.g. running a unit test on Linux), the store falls
/// back to plain bytes so tests can run; the production desktop host is always
/// on Windows.
/// </summary>
public sealed class CredentialStore
{
    private readonly string _filePath;
    private static readonly byte[] s_entropy = Encoding.UTF8.GetBytes("MolaGPT.Desktop.v1.entropy");

    // LoadSecret sits on request hot paths (JWT per chat request, MCP server
    // tokens, cloud-sync auth) and used to re-read + re-deserialize + re-decrypt
    // the whole file every call. Cache the parsed map in memory, invalidate on
    // any local write, and re-read only when the file's last-write time changed
    // (e.g. an external process edited it) — keeps single-instance behavior
    // byte-identical while dropping the per-call disk I/O.
    private readonly object _gate = new();
    private Dictionary<string, string>? _map;
    private DateTime _mapFileWriteTimeUtc;

    public CredentialStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public byte[] Encrypt(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        return OperatingSystem.IsWindows()
            ? ProtectedData.Protect(bytes, s_entropy, DataProtectionScope.CurrentUser)
            : bytes; // non-Windows fallback (tests only)
    }

    public string? Decrypt(byte[] cipher)
    {
        if (cipher.Length == 0) return null;
        try
        {
            var bytes = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(cipher, s_entropy, DataProtectionScope.CurrentUser)
                : cipher;
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    public void SaveSecret(string key, string plaintext)
    {
        lock (_gate)
        {
            var current = LoadMapLocked();
            var next = new Dictionary<string, string>(current, current.Comparer)
            {
                [key] = Convert.ToBase64String(Encrypt(plaintext))
            };
            CommitMapLocked(next);
        }
    }

    public string? LoadSecret(string key)
    {
        lock (_gate)
        {
            var map = LoadMapLocked();
            if (!map.TryGetValue(key, out var b64)) return null;
            return Decrypt(Convert.FromBase64String(b64));
        }
    }

    public void RemoveSecret(string key)
    {
        lock (_gate)
        {
            var current = LoadMapLocked();
            if (!current.ContainsKey(key)) return;

            var next = new Dictionary<string, string>(current, current.Comparer);
            next.Remove(key);
            CommitMapLocked(next);
        }
    }

    private Dictionary<string, string> LoadMapLocked()
    {
        var lastWrite = File.Exists(_filePath) ? File.GetLastWriteTimeUtc(_filePath) : DateTime.MinValue;
        if (_map is not null && _mapFileWriteTimeUtc == lastWrite)
            return _map;
        _mapFileWriteTimeUtc = lastWrite;
        _map = ReadMap();
        return _map;
    }

    private Dictionary<string, string> ReadMap()
    {
        if (!File.Exists(_filePath)) return new();
        try
        {
            var json = File.ReadAllText(_filePath);
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private void CommitMapLocked(Dictionary<string, string> map)
    {
        try
        {
            WriteMap(map);
        }
        catch
        {
            // A failed write may still have touched or truncated the file. Drop
            // the old cache so the next read reflects whatever remains on disk.
            _map = null;
            _mapFileWriteTimeUtc = default;
            throw;
        }

        // Publish the new in-memory state only after the disk write succeeds.
        _map = map;
        _mapFileWriteTimeUtc = File.GetLastWriteTimeUtc(_filePath);
    }

    private void WriteMap(Dictionary<string, string> map)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(map);
        File.WriteAllText(_filePath, json);
    }
}
