using System.Security.Cryptography;
using System.Text;

namespace MolaGPT.Core.Auth;

/// <summary>
/// Encrypted local credential storage using Windows DPAPI (CurrentUser scope).
/// Used for: MolaGPT JWT, BYOK API keys.
///
/// On non-Windows hosts (e.g. running a unit test on Linux), the store falls
/// back to plain bytes so tests can run; production WPF host will always be
/// on Windows.
/// </summary>
public sealed class CredentialStore
{
    private readonly string _filePath;
    private static readonly byte[] s_entropy = Encoding.UTF8.GetBytes("MolaGPT.Desktop.v1.entropy");

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
        var map = LoadMap();
        map[key] = Convert.ToBase64String(Encrypt(plaintext));
        WriteMap(map);
    }

    public string? LoadSecret(string key)
    {
        var map = LoadMap();
        if (!map.TryGetValue(key, out var b64)) return null;
        return Decrypt(Convert.FromBase64String(b64));
    }

    public void RemoveSecret(string key)
    {
        var map = LoadMap();
        if (map.Remove(key)) WriteMap(map);
    }

    private Dictionary<string, string> LoadMap()
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

    private void WriteMap(Dictionary<string, string> map)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(map);
        File.WriteAllText(_filePath, json);
    }
}
