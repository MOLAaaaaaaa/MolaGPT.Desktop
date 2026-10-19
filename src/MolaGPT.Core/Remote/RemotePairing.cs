using System.Security.Cryptography;
using System.Text;

namespace MolaGPT.Core.Remote;

/// <summary>
/// The cryptography of pairing a phone and authenticating what it sends.
///
/// Code pairing authenticates the exchange with the code shown on screen.
/// Account pairing relies on the relay's authenticated same-account route.
/// Both derive a separate request-signing secret over ECDH.
/// </summary>
public static class RemotePairing
{
    /// <summary>Crockford's Base32: no I, L, O or U, so what is read aloud or typed
    /// survives the trip.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int CodeLength = 8;
    public const string AccountPairingContext = "molagpt-account-pair-v1";
    private const string MacPrefix = "molagpt-pair-1|";
    private static readonly byte[] SecretInfo = Encoding.UTF8.GetBytes("molagpt-remote-pair-v1");

    public static string NewCode()
    {
        Span<char> code = stackalloc char[CodeLength];
        for (var i = 0; i < CodeLength; i++) code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(code);
    }

    /// <summary>The code as typed, read leniently: case, separators and the letters
    /// Crockford folds onto digits.</summary>
    public static string NormalizeCode(string input)
    {
        var builder = new StringBuilder(input.Length);
        foreach (var raw in input)
        {
            var c = char.ToUpperInvariant(raw);
            c = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            if (Alphabet.Contains(c)) builder.Append(c);
        }
        return builder.ToString();
    }

    public static string FormatCode(string code) =>
        code.Length == CodeLength ? code[..4] + "-" + code[4..] : code;

    public static string NewPairId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static byte[] Mac(string code, string message) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes(MacPrefix + message));

    public static bool MacMatches(string code, string message, string? macBase64)
    {
        if (string.IsNullOrEmpty(macBase64)) return false;
        byte[] given;
        try { given = Convert.FromBase64String(macBase64); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(Mac(code, message), given);
    }

    /// <summary>
    /// Answer a phone's pairing request using its code or account context as salt.
    /// Returns our public key and the shared request-signing secret.
    /// </summary>
    public static (string PublicKey, byte[] Secret) Agree(string code, string phonePublicKeyBase64)
    {
        using var phone = ECDiffieHellman.Create();
        phone.ImportSubjectPublicKeyInfo(Convert.FromBase64String(phonePublicKeyBase64), out _);
        if (phone.KeySize != 256) throw new CryptographicException("Unexpected key size.");
        using var mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var shared = mine.DeriveRawSecretAgreement(phone.PublicKey);
        try
        {
            var secret = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, Encoding.UTF8.GetBytes(code), SecretInfo);
            return (Convert.ToBase64String(mine.ExportSubjectPublicKeyInfo()), secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    /// <summary>What a request's signature covers: the parameters exactly as sent,
    /// so neither side has to agree on how to serialize JSON.</summary>
    public static string Sign(byte[] secret, string id, string op, long timestampMs, string pairId, string args)
    {
        var mac = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"{id}\n{op}\n{timestampMs}\n{pairId}\n{args}"));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool SignatureMatches(byte[] secret, string id, string op, long timestampMs, string pairId, string args, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(secret, id, op, timestampMs, pairId, args));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signature));
    }
}
