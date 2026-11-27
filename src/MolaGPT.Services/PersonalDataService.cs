using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using MolaGPT.Core.Auth;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.Desktop.Services;

public sealed class PersonalDataService(
    ConversationRepository conversations,
    MessageRepository messages,
    ProviderRepository providers,
    CredentialStore credentials)
{
    private const string ModelFormat = "molagpt-model-configurations";
    private const string Encryption = "RSA-OAEP-SHA256+AES-256-GCM";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public int ExportConversations(Stream output)
    {
        var rows = conversations.ListActive();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JsonOptions.Encoder
        });
        writer.WriteStartObject();
        writer.WriteString("format", "molagpt-conversations");
        writer.WriteNumber("version", 1);
        writer.WriteString("exportedAt", DateTimeOffset.UtcNow);
        writer.WriteStartArray("conversations");
        foreach (var conversation in rows)
        {
            JsonSerializer.Serialize(writer, new
            {
                conversation,
                timelines = conversations.ListTimelines(conversation.Id),
                messages = messages.ListAll(conversation.Id)
            }, JsonOptions);
            writer.Flush();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return rows.Count;
    }

    public byte[] ExportModelConfigurations(string password)
    {
        if (password.Length < 8) throw new ArgumentException("导出密码至少需要 8 个字符。", nameof(password));
        var entries = providers.List().Select(row => new ModelConfiguration
        {
            Configuration = row with
            {
                ApiKeyEnc = null,
                CustomHeaders = row.CustomHeaders is { } headers ? credentials.DecryptText(headers) : null
            },
            ApiKey = row.ApiKeyEnc is { Length: > 0 } cipher ? credentials.Decrypt(cipher) : null
        }).ToList();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);
        try
        {
            // Each export gets its own key pair, separate from the local credential key.
            using var rsa = RSA.Create(3072);
            return JsonSerializer.SerializeToUtf8Bytes(new EncryptedModelFile
            {
                Format = ModelFormat,
                Version = 1,
                Encryption = Encryption,
                ExportedAt = DateTimeOffset.UtcNow,
                EncryptedPrivateKey = rsa.ExportEncryptedPkcs8PrivateKey(password,
                    new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000)),
                Data = AsymmetricEncryption.Encrypt(plaintext, rsa)
            }, JsonOptions);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public (IReadOnlyList<ProviderRow> Rows, int Rejected)? ReadModelConfigurations(Stream input, string password)
    {
        var file = JsonSerializer.Deserialize<EncryptedModelFile>(input, JsonOptions)
            ?? throw new InvalidDataException("模型配置文件内容为空。");
        if (file.Format != ModelFormat || file.Version != 1 || file.Encryption != Encryption)
            throw new InvalidDataException("不支持此模型配置文件格式或版本。");
        if (file.EncryptedPrivateKey is null || file.Data is null)
            throw new InvalidDataException("模型配置文件缺少加密数据。");

        using var rsa = RSA.Create();
        byte[] plaintext;
        try
        {
            rsa.ImportEncryptedPkcs8PrivateKey(password, file.EncryptedPrivateKey, out _);
            plaintext = AsymmetricEncryption.Decrypt(file.Data, rsa);
        }
        catch (CryptographicException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(plaintext);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("模型配置内容无效。");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<ProviderRow>();
            var rejected = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                ModelConfiguration? entry;
                ProviderRow row;
                try
                {
                    entry = element.Deserialize<ModelConfiguration>(JsonOptions);
                    if (entry?.Configuration is not { } configuration
                        || string.IsNullOrWhiteSpace(configuration.Id) || ids.Contains(configuration.Id)
                        || string.IsNullOrWhiteSpace(configuration.Name) || string.IsNullOrWhiteSpace(configuration.Type)
                        || string.IsNullOrWhiteSpace(configuration.Purpose) || configuration.ApiKeyEnc is not null)
                    {
                        rejected++;
                        continue;
                    }
                    ProviderRowCodec.ReadModels(configuration.Models);
                    ProviderRowCodec.ReadHeaders(configuration.CustomHeaders);
                    row = configuration;
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    rejected++;
                    continue;
                }
                ids.Add(row.Id);
                rows.Add(row with
                {
                    ApiKeyEnc = string.IsNullOrEmpty(entry.ApiKey) ? null : credentials.Encrypt(entry.ApiKey),
                    CustomHeaders = row.CustomHeaders is { } headers ? credentials.EncryptText(headers) : null
                });
            }
            return (rows, rejected);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private sealed class EncryptedModelFile
    {
        public required string Format { get; init; }
        public required int Version { get; init; }
        public required string Encryption { get; init; }
        public required DateTimeOffset ExportedAt { get; init; }
        public required byte[] EncryptedPrivateKey { get; init; }
        public required byte[] Data { get; init; }
    }

    private sealed class ModelConfiguration
    {
        public required ProviderRow Configuration { get; init; }
        public required string? ApiKey { get; init; }
    }
}
