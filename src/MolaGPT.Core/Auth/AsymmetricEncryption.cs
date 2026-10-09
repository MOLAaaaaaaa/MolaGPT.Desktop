using System.Security.Cryptography;

namespace MolaGPT.Core.Auth;

/// <summary>RSA-OAEP wraps a fresh AES-256-GCM key for each payload.</summary>
public static class AsymmetricEncryption
{
    private static ReadOnlySpan<byte> Header => "MOLAENC1"u8;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static bool IsEncrypted(ReadOnlySpan<byte> data) => data.StartsWith(Header);

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, RSA rsa)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var wrappedKey = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
            var result = new byte[Header.Length + wrappedKey.Length + NonceSize + TagSize + plaintext.Length];
            Header.CopyTo(result);
            wrappedKey.CopyTo(result, Header.Length);
            var offset = Header.Length + wrappedKey.Length;
            var nonce = result.AsSpan(offset, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, result.AsSpan(offset + NonceSize + TagSize),
                result.AsSpan(offset + NonceSize, TagSize), Header);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> data, RSA rsa)
    {
        var wrappedKeySize = rsa.KeySize / 8;
        var offset = Header.Length + wrappedKeySize;
        if (!IsEncrypted(data) || data.Length < offset + NonceSize + TagSize)
            throw new CryptographicException("加密数据格式无效。");

        var key = rsa.Decrypt(data.Slice(Header.Length, wrappedKeySize), RSAEncryptionPadding.OaepSHA256);
        var plaintext = new byte[data.Length - offset - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(data.Slice(offset, NonceSize), data[(offset + NonceSize + TagSize)..],
                data.Slice(offset + NonceSize, TagSize), plaintext, Header);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
