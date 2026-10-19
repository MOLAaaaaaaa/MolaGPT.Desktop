using System.IO;
using System.Text.Json;
using MolaGPT.Core.Auth;

namespace MolaGPT.Desktop.Services.Remote;

/// <summary>A phone allowed to control this desktop.</summary>
public sealed record PairedPhone(string PairId, string DeviceName, string? DeviceId, long CreatedAtMs, long LastSeenAtMs);

/// <summary>
/// Paired phones and the secret each signs with. The secrets are encrypted with the
/// app's local credential key, like provider API keys. Each pairing belongs to the
/// MolaGPT account it was made under; another account signed in here sees none of them.
/// </summary>
public sealed class PairedPhoneStore
{
    private sealed record Stored(string PairId, string DeviceName, string? DeviceId, long CreatedAtMs, long LastSeenAtMs, string Secret, string? Account = null);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly CredentialStore _credentials;
    private readonly Func<string?> _account;
    private readonly object _gate = new();
    private List<Stored>? _items;

    public PairedPhoneStore(CredentialStore credentials, Func<string?> account, string? path = null)
    {
        _credentials = credentials;
        _account = account;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MolaGPT", "remote-phones.json");
    }

    public event Action? Changed;

    /// <summary>The signed-in account changed, so the visible list did too.</summary>
    public void AccountChanged() => Changed?.Invoke();

    public IReadOnlyList<PairedPhone> List()
    {
        lock (_gate)
            return Mine().Select(s => new PairedPhone(s.PairId, s.DeviceName, s.DeviceId, s.CreatedAtMs, s.LastSeenAtMs)).ToList();
    }

    public bool Any()
    {
        lock (_gate) return Mine().Any();
    }

    public byte[]? Secret(string pairId)
    {
        lock (_gate)
        {
            var item = Mine().FirstOrDefault(s => s.PairId == pairId);
            if (item is null) return null;
            try { return Convert.FromBase64String(_credentials.DecryptText(item.Secret)); }
            catch (Exception) { return null; }
        }
    }

    public void Add(string pairId, string deviceName, string? deviceId, byte[] secret)
    {
        var account = _account() ?? throw new InvalidOperationException("Pairing needs a signed-in account.");
        lock (_gate)
        {
            var items = Load();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Pairing the same phone again replaces it rather than leaving a stale entry.
            if (!string.IsNullOrEmpty(deviceId)) items.RemoveAll(s => s.DeviceId == deviceId && s.Account == account);
            items.Add(new Stored(pairId, deviceName, deviceId, now, now,
                _credentials.EncryptText(Convert.ToBase64String(secret)), account));
            Save(items);
        }
        Changed?.Invoke();
    }

    public void Remove(string pairId)
    {
        lock (_gate)
        {
            var items = Load();
            if (items.RemoveAll(s => s.PairId == pairId) == 0) return;
            Save(items);
        }
        Changed?.Invoke();
    }

    /// <summary>Record that a phone was heard from. Written at most once a minute.</summary>
    public void Touch(string pairId)
    {
        var changed = false;
        lock (_gate)
        {
            var items = Load();
            var index = items.FindIndex(s => s.PairId == pairId);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (index >= 0 && now - items[index].LastSeenAtMs > 60_000)
            {
                items[index] = items[index] with { LastSeenAtMs = now };
                Save(items);
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    private IEnumerable<Stored> Mine()
    {
        var account = _account();
        return account is null ? [] : Load().Where(s => s.Account == account);
    }

    private List<Stored> Load()
    {
        if (_items is not null) return _items;
        try
        {
            _items = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(_path), Json) ?? []
                : [];
        }
        catch (Exception)
        {
            // An unreadable file pairs nothing; the user pairs again.
            _items = [];
        }
        return _items;
    }

    private void Save(List<Stored> items)
    {
        _items = items;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(items, Json));
        File.Move(temp, _path, overwrite: true);
    }
}
