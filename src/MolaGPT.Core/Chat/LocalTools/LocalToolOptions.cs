using System.Reflection;
using System.Text.Json;

namespace MolaGPT.Core.Chat.LocalTools;

public sealed record LocalToolOptions(
    bool Network,
    bool WebPage,
    string SearchProvider = "duckduckgo",
    string? SearchApiKey = null,
    string? SearchBaseUrl = null,
    int SearchMaxResults = 6,
    int WebPageMaxCharacters = 12000)
{
    public bool HasAny => Network || WebPage;

    public static LocalToolOptions FromExtraBody(IReadOnlyDictionary<string, object>? extraBody)
    {
        if (extraBody is null || !extraBody.TryGetValue("enabled_tools", out var raw) || raw is null)
            return new LocalToolOptions(false, false);

        return new LocalToolOptions(
            ReadBool(raw, "network"),
            ReadBool(raw, "steelBrowser"),
            ReadString(raw, "searchProvider") ?? "duckduckgo",
            ReadString(raw, "searchApiKey"),
            ReadString(raw, "searchBaseUrl"),
            ReadInt(raw, "searchMaxResults") is { } maxResults ? Math.Clamp(maxResults, 1, 10) : 6,
            ReadInt(raw, "webPageMaxCharacters") is { } maxChars ? Math.Clamp(maxChars, 1000, 30000) : 12000);
    }

    private static bool ReadBool(object raw, string name)
    {
        if (TryReadDictionaryValue(raw, name, out var dictionaryValue))
            return dictionaryValue is bool boolValue && boolValue;

        if (raw is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Object
                   && element.TryGetProperty(name, out var prop)
                   && prop.ValueKind == JsonValueKind.True;
        }

        var propInfo = raw.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        return propInfo?.GetValue(raw) is bool value && value;
    }

    private static string? ReadString(object raw, string name)
    {
        if (TryReadDictionaryValue(raw, name, out var dictionaryValue))
            return dictionaryValue as string;

        if (raw is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Object
                   && element.TryGetProperty(name, out var prop)
                   && prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : null;
        }

        var propInfo = raw.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        return propInfo?.GetValue(raw) as string;
    }

    private static int? ReadInt(object raw, string name)
    {
        if (TryReadDictionaryValue(raw, name, out var dictionaryValue))
            return dictionaryValue switch
            {
                int intValue => intValue,
                long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
                JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var jsonInt) => jsonInt,
                _ => null
            };

        if (raw is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Object
                   && element.TryGetProperty(name, out var prop)
                   && prop.ValueKind == JsonValueKind.Number
                   && prop.TryGetInt32(out var parsed)
                ? parsed
                : null;
        }

        var propInfo = raw.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        var rawValue = propInfo?.GetValue(raw);
        return rawValue is int value ? value : null;
    }

    private static bool TryReadDictionaryValue(object raw, string name, out object? value)
    {
        if (raw is IReadOnlyDictionary<string, object?> readOnly)
        {
            foreach (var kv in readOnly)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value;
                    return true;
                }
            }
        }

        if (raw is IDictionary<string, object?> dictionary)
        {
            foreach (var kv in dictionary)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value;
                    return true;
                }
            }
        }

        value = null;
        return false;
    }
}
