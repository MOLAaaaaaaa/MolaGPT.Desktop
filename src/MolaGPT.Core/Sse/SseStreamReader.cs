using System.Runtime.CompilerServices;
using System.Text;

namespace MolaGPT.Core.Sse;

/// <summary>
/// Generic Server-Sent Events parser. Handles:
///   - "data: ..." lines (concatenated within a single event when multi-line)
///   - ": ..." comment / heartbeat lines (ignored)
///   - "event: ..." lines (Anthropic uses these — surfaced via SsePayload.EventName)
///   - blank line ends current event
///   - "data: [DONE]" terminator (caller decides what to do with the literal "[DONE]" payload)
/// Spec: https://html.spec.whatwg.org/multipage/server-sent-events.html
/// </summary>
public static class SseStreamReader
{
    public static async IAsyncEnumerable<SsePayload> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var data = new StringBuilder();
        string? eventName = null;
        string? id = null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break; // EOF

            if (line.Length == 0)
            {
                // Dispatch event
                if (data.Length > 0 || eventName is not null)
                {
                    yield return new SsePayload(eventName, data.ToString(), id);
                    data.Clear();
                    eventName = null;
                    id = null;
                }
                continue;
            }

            if (line[0] == ':')
            {
                // Comment / heartbeat — ignore.
                continue;
            }

            int colon = line.IndexOf(':');
            string field;
            string value;
            if (colon < 0)
            {
                field = line;
                value = string.Empty;
            }
            else
            {
                field = line[..colon];
                value = line[(colon + 1)..];
                if (value.Length > 0 && value[0] == ' ') value = value[1..];
            }

            switch (field)
            {
                case "data":
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value);
                    break;
                case "event":
                    eventName = value;
                    break;
                case "id":
                    id = value;
                    break;
                case "retry":
                    // Reconnection time; we don't auto-reconnect at this layer.
                    break;
            }
        }

        // Flush trailing event without final blank line
        if (data.Length > 0 || eventName is not null)
        {
            yield return new SsePayload(eventName, data.ToString(), id);
        }
    }
}

public sealed record SsePayload(string? EventName, string Data, string? Id)
{
    public bool IsDone => Data == "[DONE]";
}
