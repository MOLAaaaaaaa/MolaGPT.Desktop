using System.IO;
using System.Text.Json;
using MolaGPT.Core.Auth;

namespace MolaGPT.Desktop.Services.Remote;

public enum RemotePairKind
{
    Account,
    Anonymous,
}

/// <summary>A phone allowed to control this desktop.</summary>
public sealed record PairedPhone(
    string PairId,
    string DeviceName,
    string? DeviceId,
    long CreatedAtMs,
    long LastSeenAtMs,
    RemotePairKind Kind = RemotePairKind.Account,
    string State = "active");

/// <summary>
/// Paired phones and the secret each signs with. The secrets are encrypted with the
/// app's local credential key, like provider API keys. Account pairings belong to the
/// MolaGPT account they were made under; anonymous pairings hold the end-to-end
/// master key and belong to this machine alone, whichever account is signed in.
/// </summary>
public sealed class PairedPhoneStore
{
    public const string Active = "active";
    public const string PendingActivate = "pendingActivate";
    public const string PendingRevoke = "pendingRevoke";

    private sealed record Stored(
        string PairId,
        string DeviceName,
        string? DeviceId,
        long CreatedAtMs,
        long LastSeenAtMs,
        string Secret,
        string? Account = null,
        string Kind = "account",
        string State = "active");

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
            return Mine().Select(s => Row(s, RemotePairKind.Account)).ToList();
    }

    /// <summary>Anonymous pairings the user still has; ones being revoked are gone
    /// from the user's point of view.</summary>
    public IReadOnlyList<PairedPhone> AnonymousList()
    {
        lock (_gate)
            return Load().Where(s => s.Kind == "anon" && s.State != PendingRevoke)
                .Select(s => Row(s, RemotePairKind.Anonymous)).ToList();
    }

    /// <summary>Removed here but the server may still hold the grant. Persisted,
    /// so a revoke made offline survives a restart.</summary>
    public IReadOnlyList<string> AnonymousPendingRevokes()
    {
        lock (_gate)
            return Load().Where(s => s.Kind == "anon" && s.State == PendingRevoke).Select(s => s.PairId).ToList();
    }

    public bool Any() { lock (_gate) return Mine().Any(); }
    public bool AnonymousAny() { lock (_gate) return Load().Any(s => s.Kind == "anon" && s.State != PendingRevoke); }

    private static PairedPhone Row(Stored s, RemotePairKind kind) =>
        new(s.PairId, s.DeviceName, s.DeviceId, s.CreatedAtMs, s.LastSeenAtMs, kind, s.State);

    public byte[]? Secret(string pairId)
    {
        lock (_gate)
        {
            var item = Mine().FirstOrDefault(s => s.PairId == pairId);
            return item is null ? null : Decrypt(item.Secret);
        }
    }

    /// <summary>The end-to-end master of an anonymous pairing; only while it is
    /// locally usable (pending revocation must stop working immediately).</summary>
    public byte[]? AnonymousSecret(string pairId)
    {
        lock (_gate)
        {
            var item = Load().FirstOrDefault(s => s.Kind == "anon" && s.PairId == pairId);
            if (item is null || item.State is PendingRevoke or PendingActivate) return null;
            return Decrypt(item.Secret);
        }
    }

    private byte[]? Decrypt(string stored)
    {
        try { return Convert.FromBase64String(_credentials.DecryptText(stored)); }
        catch (Exception) { return null; }
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
                _credentials.EncryptText(Convert.ToBase64String(secret)), account, "account", Active));
            Save(items);
        }
        Changed?.Invoke();
    }

    /// <summary>An anonymous pairing as approved at this desk. It only becomes
    /// usable once the server also has the grant (ActivateAnonymous).</summary>
    public void AddAnonymous(string pairId, string deviceName, byte[] master, string state)
    {
        lock (_gate)
        {
            var items = Load();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            items.RemoveAll(s => s.Kind == "anon" && s.PairId == pairId);
            items.Add(new Stored(pairId, deviceName, null, now, now,
                _credentials.EncryptText(Convert.ToBase64String(master)), null, "anon", state));
            Save(items);
        }
        Changed?.Invoke();
    }

    public void SetAnonymousState(string pairId, string state)
    {
        lock (_gate)
        {
            var items = Load();
            var index = items.FindIndex(s => s.Kind == "anon" && s.PairId == pairId);
            if (index < 0) return;
            items[index] = items[index] with { State = state };
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

    /// <summary>The phone also paired by code: its account pairing is redundant, under any account.</summary>
    public void RemoveAccountDevice(string deviceId)
    {
        lock (_gate)
        {
            var items = Load();
            if (items.RemoveAll(s => s.Kind == "account" && s.DeviceId == deviceId) == 0) return;
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
