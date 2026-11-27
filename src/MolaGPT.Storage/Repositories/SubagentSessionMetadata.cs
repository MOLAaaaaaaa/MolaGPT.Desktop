using System.Buffers;
using System.Text.Json;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Tasks;

namespace MolaGPT.Storage.Repositories;

internal static class SubagentSessionMetadata
{
    internal static bool TryCollectKeys(Stream stream, HashSet<string> keys)
    {
        var reader = new JsonStreamReader(stream);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            List<string>? tools = null;
            List<string>? retry = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isTools = reader.Matches("tool_calls"u8);
                var isRetry = reader.Matches("retry"u8);
                reader.Read();
                if (isTools) tools = ReadTools(ref reader);
                else if (isRetry) retry = ReadRetry(ref reader);
                else reader.Skip();
            }

            // Validate the complete row before adding any of its keys.
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) return false;
            if (tools is not null) keys.UnionWith(tools);
            if (retry is not null) keys.UnionWith(retry);
            return true;
        }
        catch (JsonException) { return false; }
        finally { reader.Dispose(); }
    }

    private static List<string>? ReadRetry(ref JsonStreamReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        List<string>? keys = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isAttempts = reader.Matches("attempts"u8);
            reader.Read();
            if (!isAttempts || reader.TokenType != JsonTokenType.StartArray)
            {
                if (isAttempts) keys = null;
                reader.Skip();
                continue;
            }

            keys = [];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                List<string>? attemptKeys = null;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var isTools = reader.Matches("tool_calls"u8);
                    reader.Read();
                    if (isTools) attemptKeys = ReadTools(ref reader);
                    else reader.Skip();
                }
                if (attemptKeys is not null) keys.AddRange(attemptKeys);
            }
        }
        return keys;
    }

    private static List<string>? ReadTools(ref JsonStreamReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return null;
        }

        List<string>? keys = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            string? name = null;
            (long Offset, int Length)? preview = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isName = reader.Matches("name"u8);
                var isPreview = reader.Matches("result_preview_json"u8);
                reader.Read();
                if (isName)
                {
                    name = reader.Matches("spawn_agent"u8) ? SubagentTool.ToolName
                        : reader.Matches("followup_agent"u8) ? SubagentTool.FollowupToolName
                        : null;
                }
                if (isPreview) preview = reader.StringRange;
                reader.Skip();
            }

            if (name is not null && preview is { } range && SubagentTool.ReadStored(new ToolCallDelta(
                    string.Empty, name, string.Empty, ResultPreviewJson: reader.GetString(range))) is { } stored)
                (keys ??= []).Add("subagent_" + stored.AgentId);
        }
        return keys;
    }

    private ref struct JsonStreamReader(Stream stream)
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        private Utf8JsonReader _reader;
        private int _length;
        private long _bufferOffset;
        private bool _eof;

        public JsonTokenType TokenType => _reader.TokenType;

        public bool Read()
        {
            while (!_reader.Read())
            {
                if (_eof) return false;
                var consumed = (int)_reader.BytesConsumed;
                var remaining = _length - consumed;
                _bufferOffset += consumed;
                var state = _reader.CurrentState;
                if (remaining == _buffer.Length)
                {
                    // Utf8JsonReader needs an entire token. Rent a larger buffer
                    // only for rows containing a string that spans the buffer.
                    if (remaining >= 16 * 1024 * 1024) throw new JsonException("Pi metadata token exceeds 16 MiB.");
                    var larger = ArrayPool<byte>.Shared.Rent(remaining * 2);
                    _buffer.AsSpan(consumed, remaining).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = larger;
                }
                else
                {
                    _buffer.AsSpan(consumed, remaining).CopyTo(_buffer);
                }

                var read = stream.Read(_buffer.AsSpan(remaining));
                _eof = read == 0;
                _length = remaining + read;
                _reader = new Utf8JsonReader(_buffer.AsSpan(0, _length), _eof, state);
            }
            return true;
        }

        public bool Matches(ReadOnlySpan<byte> text) =>
            (TokenType is JsonTokenType.String or JsonTokenType.PropertyName) && _reader.ValueTextEquals(text);

        public (long Offset, int Length)? StringRange => TokenType == JsonTokenType.String
            ? (_bufferOffset + _reader.TokenStartIndex, (int)(_reader.BytesConsumed - _reader.TokenStartIndex))
            : null;

        public string? GetString((long Offset, int Length) range)
        {
            var index = range.Offset - _bufferOffset;
            if (index >= 0 && index + range.Length <= _length)
                return DecodeString(_buffer.AsSpan((int)index, range.Length));

            var position = stream.Position;
            var bytes = ArrayPool<byte>.Shared.Rent(range.Length);
            try
            {
                stream.Position = range.Offset;
                stream.ReadExactly(bytes.AsSpan(0, range.Length));
                return DecodeString(bytes.AsSpan(0, range.Length));
            }
            finally
            {
                stream.Position = position;
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

        private static string? DecodeString(ReadOnlySpan<byte> bytes)
        {
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            return reader.GetString();
        }

        public void Skip()
        {
            if (TokenType is not (JsonTokenType.StartArray or JsonTokenType.StartObject)) return;
            var depth = _reader.CurrentDepth;
            while (Read() && _reader.CurrentDepth > depth) { }
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
    }
}
