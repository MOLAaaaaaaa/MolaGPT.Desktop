using System.Security.Cryptography;
using System.Text.Json;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Models;

namespace MolaGPT.Storage;

public static class ProviderRowCodec
{
    public static ProviderEntry Decode(ProviderRow row, CredentialStore credentials)
    {
        try
        {
            var apiKey = row.ApiKeyEnc is { Length: > 0 } cipher ? credentials.Decrypt(cipher) : null;
            var models = ReadModels(row.Models);
            var headers = ReadHeaders(row.CustomHeaders is { } stored ? credentials.DecryptText(stored) : null);
            var entry = new ProviderEntry(row.Id, row.Type, row.Name, row.BaseUrl, apiKey, models, row.Enabled,
                row.SortOrder, row.Purpose, row.ApiPath, row.ImageEditPath, row.ImageFormat, headers, row.ImageChatPath,
                row.BuiltinSearch);
            if (ProviderPurpose.IsImage(entry.Purpose) && entry.ImageChatPath is null)
            {
                var legacyChat = ImageApiFormat.IsChatImage(entry.ImageFormat)
                    || entry.Type == "gemini" && entry.ApiPath?.Contains(":generateContent", StringComparison.OrdinalIgnoreCase) == true;
                entry = entry with
                {
                    ImageChatPath = legacyChat && entry.ApiPath is { } path ? path : ImageApiFormat.DefaultChatPath(entry.Type),
                    ApiPath = legacyChat ? ImageApiFormat.DefaultImagesPath(entry.BaseUrl) : entry.ApiPath,
                    ImageFormat = legacyChat ? ImageApiFormat.OpenAiChatImage : entry.ImageFormat
                };
            }
            return entry;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException)
        {
            throw new InvalidDataException("无法读取模型服务配置或解密凭据。", ex);
        }
    }

    public static ProviderRow Encode(ProviderEntry entry, CredentialStore credentials) => new(
        Id: entry.Id,
        Type: entry.Type,
        Name: entry.Name,
        BaseUrl: entry.BaseUrl,
        ApiKeyEnc: string.IsNullOrEmpty(entry.ApiKey) ? null : credentials.Encrypt(entry.ApiKey),
        Models: JsonSerializer.Serialize(entry.Models),
        Enabled: entry.Enabled,
        SortOrder: entry.SortOrder,
        Purpose: entry.Purpose,
        ApiPath: entry.ApiPath,
        ImageEditPath: entry.ImageEditPath,
        ImageFormat: entry.ImageFormat,
        CustomHeaders: entry.CustomHeaders is { Count: > 0 }
            ? credentials.EncryptText(JsonSerializer.Serialize(entry.CustomHeaders)) : null,
        ImageChatPath: entry.ImageChatPath,
        BuiltinSearch: entry.BuiltinSearch);

    public static List<ProviderModelEntry> ReadModels(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var models = JsonSerializer.Deserialize<List<ProviderModelEntry>>(json);
        if (models is null || models.Any(model => model is null || string.IsNullOrWhiteSpace(model.Id)
            || model.CustomBody?.Any(parameter => parameter is null) == true))
            throw new InvalidDataException("模型列表内容无效。");
        return models;
    }

    public static List<CustomHeaderEntry>? ReadHeaders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var headers = JsonSerializer.Deserialize<List<CustomHeaderEntry>>(json);
        if (headers is null || headers.Any(header => header is null))
            throw new InvalidDataException("自定义请求头内容无效。");
        return headers;
    }

    public static ProviderRow ProtectLegacyCredentials(ProviderRow row, ProviderEntry entry, CredentialStore credentials)
    {
        var key = row.ApiKeyEnc;
        if (key is { Length: > 0 } && !AsymmetricEncryption.IsEncrypted(key) && entry.ApiKey is { } plainKey)
            key = credentials.Encrypt(plainKey);
        var headers = row.CustomHeaders;
        if (headers is not null && !CredentialStore.IsEncryptedText(headers))
            headers = credentials.EncryptText(headers);
        return row with { ApiKeyEnc = key, CustomHeaders = headers };
    }
}
