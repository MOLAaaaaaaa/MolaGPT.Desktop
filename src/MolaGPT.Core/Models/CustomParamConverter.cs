using System.Globalization;
using System.Text.Json;

namespace MolaGPT.Core.Models;

/// <summary>Converts persisted <see cref="CustomBodyEntry"/>/<see cref="CustomHeaderEntry"/>
/// lists into the runtime forms the providers consume.</summary>
public static class CustomParamConverter
{
    public static IReadOnlyDictionary<string, JsonElement>? ToBodyDict(IReadOnlyList<CustomBodyEntry>? entries)
    {
        if (entries is null || entries.Count == 0) return null;
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Key)) continue;
            dict[e.Key.Trim()] = ConvertValue(e.Type, e.Value);
        }
        return dict.Count == 0 ? null : dict;
    }

    public static IReadOnlyList<KeyValuePair<string, string>>? ToHeaderList(IReadOnlyList<CustomHeaderEntry>? entries)
    {
        if (entries is null || entries.Count == 0) return null;
        var list = entries
            .Where(h => !string.IsNullOrWhiteSpace(h.Name))
            .Select(h => new KeyValuePair<string, string>(h.Name.Trim(), h.Value ?? string.Empty))
            .ToList();
        return list.Count == 0 ? null : list;
    }

    private static JsonElement ConvertValue(string? type, string? value)
    {
        value ??= string.Empty;
        switch (type?.Trim().ToLowerInvariant())
        {
            case "number":
                if (long.TryParse(value, out var l))
                    return JsonSerializer.SerializeToElement(l);
                if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                    return JsonSerializer.SerializeToElement(d);
                return JsonSerializer.SerializeToElement(value);
            case "boolean":
                return JsonSerializer.SerializeToElement(bool.TryParse(value, out var b) && b);
            case "json":
                try
                {
                    using var doc = JsonDocument.Parse(value);
                    return doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    return JsonSerializer.SerializeToElement(value);
                }
            default: // "string"
                return JsonSerializer.SerializeToElement(value);
        }
    }

}
