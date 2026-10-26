using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Remote;

namespace MolaGPT.Desktop.Services.Remote;

/// <summary>
/// This desktop's anonymous identity: one P-256 key, generated on first use
/// and stored DPAPI-protected. The key is the identity —
/// deviceId = Base32(SHA-256(SPKI))[:26] — and can be reset, which retires
/// the whole namespace.
/// </summary>
public sealed class AnonymousDeviceIdentity
{
    private const string KeyName = "anon.remote.device.key";
    private readonly CredentialStore _credentials;
    private readonly object _gate = new();
    private ECDsa? _key;
    private string? _deviceId;

    public AnonymousDeviceIdentity(CredentialStore credentials) => _credentials = credentials;

    public string DeviceId
    {
        get { lock (_gate) { EnsureKey(); return _deviceId!; } }
    }

    public byte[] PublicKey { get { lock (_gate) { EnsureKey(); return _key!.ExportSubjectPublicKeyInfo(); } } }

    public byte[] Sign(byte[] data)
    {
        lock (_gate)
        {
            EnsureKey();
            return AnonCrypto.SignDer(_key!, data);
        }
    }

    private const string DirectoryKeyName = "anon.remote.directoryKey";

    /// <summary>The key the encrypted offline directory is sealed with. Shared
    /// with the paired phones over the end-to-end channel only.</summary>
    public byte[] DirectoryKeyGetOrCreate()
    {
        var stored = _credentials.LoadSecret(DirectoryKeyName);
        if (stored is { Length: > 0 } && Convert.FromBase64String(stored) is { Length: 32 } key) return key;
        var fresh = RandomNumberGenerator.GetBytes(32);
        _credentials.SaveSecret(DirectoryKeyName, Convert.ToBase64String(fresh));
        return fresh;
    }

    private void EnsureKey()
    {
        if (_key is not null) return;
        var stored = _credentials.LoadSecret(KeyName);
        var key = ECDsa.Create();
        if (stored is { Length: > 0 })
        {
            key.ImportECPrivateKey(Convert.FromBase64String(stored), out _);
        }
        else
        {
            key.GenerateKey(ECCurve.NamedCurves.nistP256);
            _credentials.SaveSecret(KeyName, Convert.ToBase64String(key.ExportECPrivateKey()));
        }
        _key = key;
        _deviceId = AnonText.CrockfordBase32(SHA256.HashData(key.ExportSubjectPublicKeyInfo()), 26);
    }
}

public sealed class AnonApiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The desktop's signed HTTP channel to the anonymous endpoints. Every POST
/// carries the device-key signature over method, authority, path, identity,
/// timestamp, nonce and the exact body bytes; the nonce store makes each
/// request single-use.
/// </summary>
public sealed class AnonApiClient
{
    private readonly HttpClient _http;
    private readonly AnonymousDeviceIdentity _identity;
    private readonly string _base;
    private readonly string _authority;
    private readonly object _gate = new();
    private long _skewSeconds;

    public AnonApiClient(HttpClient http, AnonymousDeviceIdentity identity, string baseUrl)
    {
        _http = http;
        _identity = identity;
        _base = baseUrl.TrimEnd('/');
        var uri = new Uri(_base);
        _authority = uri.Host.ToLowerInvariant() + (uri.IsDefaultPort ? "" : ":" + uri.Port);
    }

    public AnonymousDeviceIdentity Identity => _identity;

    public async Task<JsonObject> DevicePostAsync(string file, JsonObject body, CancellationToken ct)
    {
        var path = new Uri(_base).AbsolutePath.TrimEnd('/') + "/api/auth/" + file;
        var raw = Encoding.UTF8.GetBytes(body.ToJsonString());
        using var request = new HttpRequestMessage(HttpMethod.Post, _base + "/api/auth/" + file)
        {
            Content = new ByteArrayContent(raw)
        };
        request.Content.Headers.ContentType = new("application/json");
        Sign(request, path, raw);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync(response, ct).ConfigureAwait(false);
    }

    private void Sign(HttpRequestMessage request, string path, byte[] raw)
    {
        long ts;
        byte[] nonce;
        byte[] signature;
        lock (_gate)
        {
            ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _skewSeconds;
            nonce = AnonCrypto.NewNonce(16);
            var input = AnonWire.E("molagpt-anon-http-v2", "POST", _authority, path, "device",
                _identity.DeviceId, Array.Empty<byte>(), ts.ToString(), nonce, SHA256.HashData(raw));
            signature = _identity.Sign(input);
        }
        request.Headers.TryAddWithoutValidation("X-Mola-Device", _identity.DeviceId);
        request.Headers.TryAddWithoutValidation("X-Mola-Ts", ts.ToString());
        request.Headers.TryAddWithoutValidation("X-Mola-Nonce", AnonText.ToBase64Url(nonce));
        request.Headers.TryAddWithoutValidation("X-Mola-Signature", AnonText.ToBase64Url(signature));
    }

    private async Task<JsonObject> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonObject? parsed = null;
        try { parsed = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { }
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && parsed?["code"]?.GetValue<string>() == "stale"
            && response.Headers.TryGetValues("X-Mola-Server-Time", out var times)
            && long.TryParse(times.FirstOrDefault(), out var serverTime))
        {
            lock (_gate) _skewSeconds = serverTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        if (!response.IsSuccessStatusCode)
        {
            var code = parsed?["code"]?.GetValue<string>() ?? "http_" + (int)response.StatusCode;
            var message = parsed?["message"]?.GetValue<string>() ?? "服务不可用";
            throw new AnonApiException(code, message);
        }
        return parsed ?? throw new AnonApiException("bad_reply", "服务响应无效");
    }

    /// <summary>Registers this device; the workload proof is solved inline
    /// (up to a few million SHA-256 attempts, about a second).</summary>
    public async Task RegisterDeviceAsync(CancellationToken ct)
    {
        var deviceId = _identity.DeviceId;
        var challenge = await DevicePostAsync("anon_device.php", new JsonObject
        {
            ["op"] = "challenge", ["deviceId"] = deviceId,
        }, ct).ConfigureAwait(false);
        var solution = SolveAltcha(
            challenge["challenge"]!.GetValue<string>(),
            challenge["salt"]!.GetValue<string>(),
            challenge["maxnumber"]!.GetValue<int>());
        if (solution is null) throw new AnonApiException("pow", "工作量证明求解失败，请重试");
        await DevicePostAsync("anon_device.php", new JsonObject
        {
            ["op"] = "register",
            ["deviceId"] = deviceId,
            ["pubKey"] = AnonText.ToBase64Url(_identity.PublicKey),
            ["altcha"] = new JsonObject
            {
                ["algorithm"] = challenge["algorithm"]!.GetValue<string>(),
                ["challenge"] = challenge["challenge"]!.GetValue<string>(),
                ["salt"] = challenge["salt"]!.GetValue<string>(),
                ["signature"] = challenge["signature"]!.GetValue<string>(),
                ["number"] = solution.Value,
            },
        }, ct).ConfigureAwait(false);
    }

    public Task<JsonObject> OpenRoomAsync(CancellationToken ct) =>
        DevicePostAsync("anon_pair.php", new JsonObject { ["op"] = "open" }, ct);

    public Task CloseRoomAsync(CancellationToken ct) =>
        DevicePostAsync("anon_pair.php", new JsonObject { ["op"] = "close" }, ct);

    public Task GrantAddAsync(string pairIdB64u, string grantPubB64u, CancellationToken ct) =>
        DevicePostAsync("anon_grant.php", new JsonObject
        {
            ["op"] = "add", ["pairId"] = pairIdB64u, ["grantPub"] = grantPubB64u,
        }, ct);

    public Task GrantRemoveAsync(string pairIdB64u, CancellationToken ct) =>
        DevicePostAsync("anon_grant.php", new JsonObject { ["op"] = "remove", ["pairId"] = pairIdB64u }, ct);

    public async Task<JsonObject[]> GrantListAsync(CancellationToken ct)
    {
        var reply = await DevicePostAsync("anon_grant.php", new JsonObject { ["op"] = "list" }, ct).ConfigureAwait(false);
        return reply["grants"] is JsonArray grants
            ? grants.OfType<JsonObject>().ToArray()
            : [];
    }

    public async Task<string> FetchTicketAsync(CancellationToken ct)
    {
        var reply = await DevicePostAsync("anon_ticket.php", new JsonObject(), ct).ConfigureAwait(false);
        return reply["ticket"]?.GetValue<string>() ?? throw new AnonApiException("bad_reply", "票据响应无效");
    }

    public Task DirectoryPutAsync(JsonObject payload, CancellationToken ct) =>
        DevicePostAsync("anon_directory.php", new JsonObject { ["op"] = "put", ["payload"] = payload }, ct);

    public Task DirectoryDeleteAsync(CancellationToken ct) =>
        DevicePostAsync("anon_directory.php", new JsonObject { ["op"] = "delete" }, ct);

    /// <summary>ALTCHA: find the number whose digest matches. Returns null when
    /// the challenge is unsolvable (expired or malformed).</summary>
    private static int? SolveAltcha(string challengeHex, string salt, int maxNumber)
    {
        var target = Convert.FromHexString(challengeHex);
        Span<byte> digest = stackalloc byte[32];
        for (var number = 0; number <= maxNumber; number++)
        {
            SHA256.HashData(Encoding.UTF8.GetBytes(salt + number.ToString(System.Globalization.CultureInfo.InvariantCulture)), digest);
            if (digest.SequenceEqual(target)) return number;
        }
        return null;
    }
}
