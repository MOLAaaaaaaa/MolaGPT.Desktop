using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using MolaGPT.Core.Auth;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;

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

    public void UpgradeCredentialStorage()
    {
        credentials.UpgradeEncryption();
        var changed = new List<ProviderRow>();
        foreach (var row in providers.List())
        {
            var upgraded = row;
            if (row.ApiKeyEnc is { Length: > 0 } cipher && !AsymmetricEncryption.IsEncrypted(cipher))
            {
                var key = credentials.Decrypt(cipher) ?? throw new CryptographicException("API Key 内容为空。");
                upgraded = upgraded with { ApiKeyEnc = credentials.Encrypt(key) };
            }
            if (row.CustomHeaders is { } headers && !CredentialStore.IsEncryptedText(headers))
                upgraded = upgraded with { CustomHeaders = credentials.EncryptText(headers) };
            if (upgraded != row) changed.Add(upgraded);
        }
        if (changed.Count > 0) providers.UpsertMany(changed);
    }

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

    public bool TryReadModelConfigurations(Stream input, string password,
        [NotNullWhen(true)] out IReadOnlyList<ProviderRow>? configurations)
    {
        configurations = null;
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
            return false;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<ModelConfiguration>>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("模型配置内容无效。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<ProviderRow>(entries.Count);
            foreach (var entry in entries)
            {
                var row = entry?.Configuration;
                if (row is null || string.IsNullOrWhiteSpace(row.Id) || !ids.Add(row.Id)
                    || string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Type)
                    || string.IsNullOrWhiteSpace(row.Purpose) || row.ApiKeyEnc is not null)
                    throw new InvalidDataException("模型配置中的服务信息无效或重复。");
                var models = JsonSerializer.Deserialize<List<ProviderModelEntry>>(row.Models);
                if (models is null || models.Any(model => model is null || string.IsNullOrWhiteSpace(model.Id)))
                    throw new InvalidDataException("模型列表内容无效。");
                if (row.CustomHeaders is not null
                    && JsonSerializer.Deserialize<List<CustomHeaderEntry>>(row.CustomHeaders) is null)
                    throw new InvalidDataException("自定义请求头内容无效。");
                rows.Add(row with
                {
                    ApiKeyEnc = string.IsNullOrEmpty(entry!.ApiKey) ? null : credentials.Encrypt(entry.ApiKey),
                    CustomHeaders = row.CustomHeaders is { } headers ? credentials.EncryptText(headers) : null
                });
            }
            configurations = rows;
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void ImportModelConfigurations(IReadOnlyList<ProviderRow> rows)
    {
        var existing = providers.List().ToDictionary(row => row.Id, StringComparer.Ordinal);
        var nextOrder = existing.Count == 0 ? 0 : existing.Values.Max(row => row.SortOrder) + 1;
        providers.UpsertMany(rows.Select(row => row with
        {
            SortOrder = existing.TryGetValue(row.Id, out var current) ? current.SortOrder : nextOrder++
        }).ToList());
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
