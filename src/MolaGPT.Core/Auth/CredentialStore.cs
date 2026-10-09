using System.Security.Cryptography;
using System.Text;

namespace MolaGPT.Core.Auth;

/// <summary>
/// RSA-based local credential storage. Windows DPAPI protects the private key.
/// Legacy DPAPI credentials remain readable until the startup migration runs.
/// </summary>
public sealed class CredentialStore : IDisposable
{
    private readonly string _filePath;
    private RSA? _rsa;
    private const string TextPrefix = "molaenc1:";
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
        try
        {
            lock (_gate) return AsymmetricEncryption.Encrypt(bytes, GetRsaLocked());
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string? Decrypt(byte[] cipher)
    {
        if (cipher.Length == 0) return null;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("本地凭据存储需要 Windows 用户密钥保护。");
        byte[] bytes;
        lock (_gate)
            bytes = AsymmetricEncryption.IsEncrypted(cipher)
                ? AsymmetricEncryption.Decrypt(cipher, GetRsaLocked())
                : ProtectedData.Unprotect(cipher, s_entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static bool IsEncryptedText(string value) => value.StartsWith(TextPrefix, StringComparison.Ordinal);

    public string EncryptText(string plaintext) => TextPrefix + Convert.ToBase64String(Encrypt(plaintext));

    public string DecryptText(string value) => IsEncryptedText(value)
        ? Decrypt(Convert.FromBase64String(value[TextPrefix.Length..]))
            ?? throw new CryptographicException("加密内容为空。")
        : value;

    public void UpgradeEncryption()
    {
        lock (_gate)
        {
            var current = LoadMapLocked();
            var next = new Dictionary<string, string>(current, current.Comparer);
            foreach (var (key, value) in current)
            {
                var cipher = Convert.FromBase64String(value);
                if (AsymmetricEncryption.IsEncrypted(cipher)) continue;
                next[key] = Convert.ToBase64String(Encrypt(Decrypt(cipher)
                    ?? throw new CryptographicException("凭据内容为空。")));
            }
            if (next.Any(pair => pair.Value != current[pair.Key])) CommitMapLocked(next);
        }
    }

    private RSA GetRsaLocked()
    {
        if (_rsa is not null) return _rsa;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("本地凭据存储需要 Windows 用户密钥保护。");

        var keyPath = _filePath + ".rsa";
        var rsa = RSA.Create();
        try
        {
            if (File.Exists(keyPath))
            {
                var privateKey = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), s_entropy, DataProtectionScope.CurrentUser);
                try { rsa.ImportPkcs8PrivateKey(privateKey, out _); }
                finally { CryptographicOperations.ZeroMemory(privateKey); }
            }
            else
            {
                rsa.KeySize = 3072;
                var privateKey = rsa.ExportPkcs8PrivateKey();
                try
                {
                    File.WriteAllBytes(keyPath + ".tmp", ProtectedData.Protect(privateKey, s_entropy, DataProtectionScope.CurrentUser));
                    File.Move(keyPath + ".tmp", keyPath);
                }
                finally { CryptographicOperations.ZeroMemory(privateKey); }
            }
            _rsa = rsa;
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate) _rsa?.Dispose();
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
        _map = ReadMap();
        _mapFileWriteTimeUtc = lastWrite;
        return _map;
    }

    private Dictionary<string, string> ReadMap()
    {
        if (!File.Exists(_filePath)) return new();
        var json = File.ReadAllText(_filePath);
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new InvalidDataException("凭据文件内容无效。");
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
        File.WriteAllText(_filePath + ".tmp", json);
        File.Move(_filePath + ".tmp", _filePath, overwrite: true);
    }
}
