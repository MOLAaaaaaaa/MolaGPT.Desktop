using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MolaGPT.Core.Remote;

/// <summary>
/// Wire encoding shared by every anonymous-protocol hash, MAC and signature:
/// each field is length-prefixed with uint32_be and concatenated behind the
/// label, so no field can bleed into its neighbour. Frozen by the reference
/// implementation; phones and the PHP side match it byte for byte.
/// </summary>
public static class AnonWire
{
    public static byte[] E(string label, params object?[] fields)
    {
        var parts = new List<byte[]>(1 + fields.Length) { Encoding.UTF8.GetBytes(label) };
        foreach (var field in fields) parts.Add(FieldBytes(field));
        var size = parts.Sum(part => 4 + part.Length);
        var output = new byte[size];
        var at = 0;
        foreach (var part in parts)
        {
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(at), (uint)part.Length);
            part.CopyTo(output, at + 4);
            at += 4 + part.Length;
        }
        return output;
    }

    private static byte[] FieldBytes(object? field) => field switch
    {
        null => [],
        byte[] bytes => bytes,
        string text => Encoding.UTF8.GetBytes(text),
        int number => Encoding.UTF8.GetBytes(number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        long number => Encoding.UTF8.GetBytes(number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        _ => throw new ArgumentException($"E() field of {field.GetType().Name}"),
    };

    public static byte[] Sha256(string label, params object?[] fields) => SHA256.HashData(E(label, fields));
}

/// <summary>Base64url without padding, and Crockford's Base32 for room codes.</summary>
public static class AnonText
{
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        // Frames can carry megabytes of ciphertext, so no stack buffers here.
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    /// <summary>First <paramref name="length"/> Crockford characters of the hash.</summary>
    public static string CrockfordBase32(byte[] hash, int length)
    {
        var bits = new StringBuilder(hash.Length * 8);
        foreach (var b in hash) bits.Append(Convert.ToString(b, 2).PadLeft(8, '0'));
        var output = new char[length];
        for (var i = 0; i < length; i++)
            output[i] = CrockfordAlphabet[Convert.ToInt32(bits.ToString(i * 5, 5), 2)];
        return new string(output);
    }

    public static bool FixedTimeEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        CryptographicOperations.FixedTimeEquals(a, b);
}

/// <summary>
/// The primitives of the anonymous protocol. Only platform crypto: P-256
/// ECDH/ECDSA (raw X coordinate, DER signatures), HKDF-SHA256, HMAC-SHA256 and
/// AES-256-GCM — no new dependencies.
/// </summary>
public static class AnonCrypto
{
    public static byte[] NewNonce(int bytes = 32) => RandomNumberGenerator.GetBytes(bytes);

    public static byte[] Hmac(byte[] key, byte[] message) => HMACSHA256.HashData(key, message);

    public static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] info, int length = 32) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, info);

    public static ECDiffieHellman NewEphemeralKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The 32-byte big-endian X coordinate, leading zeros kept.</summary>
    public static byte[] EcdhRaw(ECDiffieHellman mine, byte[] theirSpki)
    {
        using var theirs = ECDiffieHellman.Create();
        theirs.ImportSubjectPublicKeyInfo(theirSpki, out _);
        if (theirs.KeySize != 256) throw new CryptographicException("Unexpected key size.");
        return mine.DeriveRawSecretAgreement(theirs.PublicKey);
    }

    /// <summary>ECDSA P-256 over SHA-256 with the DER (r,s) signature format.</summary>
    public static byte[] SignDer(ECDsa key, byte[] data) =>
        key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public static bool VerifyDer(byte[] data, byte[] signatureDer, byte[] theirSpki)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(theirSpki, out _);
            return key.VerifyData(data, signatureDer, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException) { return false; }
    }

    public static (byte[] Ciphertext, byte[] Tag) Seal(byte[] key, byte[] nonce12, byte[] aad, ReadOnlySpan<byte> plaintext)
    {
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce12, plaintext, ciphertext, tag, aad);
        return (ciphertext, tag);
    }

    public static byte[] Open(byte[] key, byte[] nonce12, byte[] aad, byte[] ciphertext, byte[] tag)
    {
        var plaintext = new byte[ciphertext.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce12, ciphertext, tag, plaintext, aad);
        return plaintext;
    }

    /// <summary>Checks a public key is exactly P-256 before it is trusted.</summary>
    public static bool IsP256Spki(byte[] spki)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            return key.KeySize == 256;
        }
        catch (CryptographicException) { return false; }
    }
}
