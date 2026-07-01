using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace MolaGPT.Core.Sse;

/// <summary>
/// Newline-delimited JSON reader. Sibling to <see cref="SseStreamReader"/>, but
/// for agent CLIs (Claude Code <c>--output-format stream-json</c>, Codex
/// <c>--json</c>) that emit one complete JSON object per line over stdout.
///
/// Blank lines are skipped. A line that fails to parse is surfaced via
/// <see cref="NdJsonLine.ParseError"/> rather than throwing, so a single
/// malformed line never tears down the whole stream.
/// </summary>
public static class NdJsonStreamReader
{
    public static async IAsyncEnumerable<NdJsonLine> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Process exited / stream closed mid-read — treat as EOF.
                break;
            }

            if (line is null) break; // EOF
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement parsed = default;
            string? parseError = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                // Clone so the element outlives the using-scope of the document.
                parsed = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                parseError = ex.Message;
            }

            yield return parseError is null
                ? new NdJsonLine(parsed, line, null)
                : new NdJsonLine(default, line, parseError);
        }
    }
}

/// <summary>One parsed NDJSON line. <see cref="ParseError"/> is non-null when the
/// raw text could not be parsed as JSON (in which case <see cref="Root"/> is default).</summary>
public readonly record struct NdJsonLine(JsonElement Root, string Raw, string? ParseError)
{
    public bool IsValid => ParseError is null;
}
