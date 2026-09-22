using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat;

/// <summary>
/// One turn's ledger of everything <c>search_web</c> found, and the numbering the
/// answer's <c>&lt;ref source="N" /&gt;</c> markers point at.
///
/// MolaGPT's own backend does this server-side (chatcli.php numbers the hits,
/// states the citation rule next to them, and sends the source table down a
/// second channel). BYOK rows have no backend, so a client that wants the same
/// citation pills has to keep the same books itself.
///
/// The numbering is per <b>turn</b>, not per call, on purpose: a model that
/// searches twice would otherwise get two sources both called 1, and every
/// <c>&lt;ref source="1" /&gt;</c> in the answer would be ambiguous. A URL seen
/// again keeps the number it already had.
///
/// Written from the tool-bridge thread and read from the streaming loop, so
/// everything is under one lock.
/// </summary>
internal sealed class SearchCitations
{
    private const string LocalSearchMarker = "local_search_web";

    private readonly Lock _gate = new();
    private readonly List<SourceReference> _sources = [];
    private readonly Dictionary<string, int> _byUrl = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    /// <summary>
    /// Numbers the hits in a <c>search_web</c> result and hands back the same JSON
    /// with an <c>id</c> on every hit plus the citation rule.
    ///
    /// Anything that is not a local search result is returned untouched — MCP
    /// servers and future providers may answer the same tool name with a shape
    /// this knows nothing about, and mangling it would be worse than not citing.
    /// </summary>
    public string Number(string toolResultJson)
    {
        if (string.IsNullOrWhiteSpace(toolResultJson)) return toolResultJson;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(toolResultJson); }
        catch (JsonException) { return toolResultJson; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return toolResultJson;
            if (!root.TryGetProperty("source", out var source)
                || source.ValueKind != JsonValueKind.String
                || !string.Equals(source.GetString(), LocalSearchMarker, StringComparison.Ordinal))
                return toolResultJson;
            if (!root.TryGetProperty("queries", out var queries) || queries.ValueKind != JsonValueKind.Array)
                return toolResultJson;

            var buffer = new ArrayBufferWriter<byte>();
            using var writer = new Utf8JsonWriter(
                buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            lock (_gate)
            {
                var before = _sources.Count;
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                {
                    if (property.NameEquals("queries")) continue;
                    property.WriteTo(writer);
                }

                writer.WritePropertyName("queries");
                writer.WriteStartArray();
                foreach (var query in queries.EnumerateArray()) WriteQuery(writer, query);
                writer.WriteEndArray();

                writer.WriteString("citation_rule", Rule(_sources.Count));
                writer.WriteEndObject();
                writer.Flush();

                if (_sources.Count > before) _dirty = true;
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    /// <summary>
    /// Hands over the full list when it has grown since the last call.
    ///
    /// Always the whole list, never a delta: the UI replaces the turn's sources
    /// wholesale, and the earlier hits have to stay — the answer's existing
    /// <c>&lt;ref&gt;</c> markers still point at them.
    /// </summary>
    public bool TryTakeUpdate(out IReadOnlyList<SourceReference> sources)
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                sources = [];
                return false;
            }
            _dirty = false;
            sources = _sources.ToArray();
            return true;
        }
    }

    private void WriteQuery(Utf8JsonWriter writer, JsonElement query)
    {
        if (query.ValueKind != JsonValueKind.Object)
        {
            query.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (var property in query.EnumerateObject())
        {
            if (!property.NameEquals("results") || property.Value.ValueKind != JsonValueKind.Array)
            {
                property.WriteTo(writer);
                continue;
            }

            writer.WritePropertyName("results");
            writer.WriteStartArray();
            foreach (var hit in property.Value.EnumerateArray()) WriteHit(writer, hit);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private void WriteHit(Utf8JsonWriter writer, JsonElement hit)
    {
        if (hit.ValueKind != JsonValueKind.Object)
        {
            hit.WriteTo(writer);
            return;
        }

        var url = hit.TryGetProperty("url", out var urlValue) && urlValue.ValueKind == JsonValueKind.String
            ? urlValue.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            // A hit with no URL cannot be a source — it has nothing to open and
            // nothing to draw a favicon from. Left in for the model to read,
            // left out of the numbering.
            hit.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("id", Assign(hit, url));
        foreach (var property in hit.EnumerateObject())
        {
            if (property.NameEquals("id")) continue;
            property.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private int Assign(JsonElement hit, string url)
    {
        if (_byUrl.TryGetValue(url, out var existing)) return existing;

        var id = _sources.Count + 1;
        _sources.Add(new SourceReference(
            id,
            hit.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                ? title.GetString() ?? url
                : url,
            url,
            hit.TryGetProperty("published_date", out var date) && date.ValueKind == JsonValueKind.String
                ? date.GetString()
                : null));
        _byUrl[url] = id;
        return id;
    }

    /// <summary>
    /// The rule, kept short on purpose. It rides along on every search result
    /// rather than sitting in the system prompt once, because the usable range
    /// is only known after a search and because the rule works far better when
    /// it is next to the numbered hits it talks about — which is also how the
    /// backend does it. Short matters: a turn may search five times.
    /// </summary>
    private static string Rule(int total) =>
        $"引用规则：直接用到某条来源的信息时，在该句句末写 <ref source=\"N\" />，N 取该来源的 id；"
        + $"多条来源支持同一句可写 <ref source=\"1,3\" />。常识性、总结性、过渡性的句子不要加。可用 id 1-{total}。";
}
