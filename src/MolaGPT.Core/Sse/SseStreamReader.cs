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
        var parser = new SseEventParser(1024 * 1024, 4 * 1024 * 1024);
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            foreach (var item in parser.Feed(buffer.AsMemory(0, count))) yield return item;
        if (parser.Complete() is { } last) yield return last;
    }
}

// The relay observes the same frames with a smaller budget. It skips an entire
// oversized event, whereas a consumer of the stream must report interruption.
internal sealed class SseEventParser(int maxLine, int maxEvent, bool skipOversized = false)
{
    private readonly MemoryStream _line = new();
    private readonly StringBuilder _data = new();
    private string? _name, _id;
    private int _eventBytes, _dataLines;
    private bool _first = true, _afterCr, _nonemptyLine, _discard;

    public IEnumerable<SsePayload> Feed(ReadOnlyMemory<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            if (_afterCr)
            {
                _afterCr = false;
                if (bytes.Span[0] == '\n') { bytes = bytes[1..]; continue; }
            }
            var end = bytes.Span.IndexOfAny((byte)'\r', (byte)'\n');
            var count = end < 0 ? bytes.Length : end;
            if (count > 0)
            {
                _nonemptyLine = true;
                if (!_discard)
                {
                    var length = _line.Length + count;
                    if (length > maxLine || _eventBytes + length > maxEvent)
                    {
                        if (!skipOversized)
                            throw new InvalidDataException(length > maxLine
                                ? $"SSE line exceeds {maxLine} bytes." : $"SSE event exceeds {maxEvent} bytes.");
                        _discard = true;
                    }
                    else _line.Write(bytes.Span[..count]);
                }
            }
            if (end < 0) yield break;
            _afterCr = bytes.Span[end] == '\r';
            bytes = bytes[(end + 1)..];
            if (EndLine() is { } item) yield return item;
        }
    }

    public SsePayload? Complete()
    {
        if (_nonemptyLine) EndLine();
        return Dispatch(terminated: false);
    }

    private SsePayload? EndLine()
    {
        if (!_nonemptyLine) return Dispatch();
        var line = _line.GetBuffer().AsSpan(0, (int)_line.Length);
        if (_first && line.StartsWith(Encoding.UTF8.Preamble)) line = line[Encoding.UTF8.Preamble.Length..];
        _first = false;
        if (!_discard && !line.IsEmpty && line[0] != ':')
        {
            _eventBytes += line.Length + 1;
            int colon = line.IndexOf((byte)':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? default : line[(colon + 1)..];
            if (!value.IsEmpty && value[0] == ' ') value = value[1..];
            if (field.SequenceEqual("data"u8))
            {
                if (_dataLines++ > 0) _data.Append('\n');
                _data.Append(Encoding.UTF8.GetString(value));
            }
            else if (field.SequenceEqual("event"u8)) _name = Encoding.UTF8.GetString(value);
            else if (field.SequenceEqual("id"u8)) _id = Encoding.UTF8.GetString(value);
        }
        _line.SetLength(0);
        _nonemptyLine = false;
        return null;
    }

    private SsePayload? Dispatch(bool terminated = true)
    {
        var item = !_discard && (_dataLines > 0 || _name is not null)
            ? new SsePayload(_name, _data.ToString(), _id) { DataLines = _dataLines, IsTerminated = terminated }
            : null;
        if (item is not null || _discard) _id = null;
        _data.Clear();
        _name = null;
        _eventBytes = _dataLines = 0;
        _discard = false;
        _first = false;
        return item;
    }
}

public sealed record SsePayload(string? EventName, string Data, string? Id)
{
    public bool IsDone => Data == "[DONE]";
    internal int DataLines { get; init; }
    internal bool IsTerminated { get; init; } = true;
}
