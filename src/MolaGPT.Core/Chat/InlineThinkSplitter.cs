using System.Text;
using System.Buffers;

namespace MolaGPT.Core.Chat;

/// <summary>Incrementally separates the relay's flat think tags while preserving Markdown literals and comments.</summary>
public sealed class InlineThinkSplitter
{
    private const string OpenTag = "<think";
    private const string CloseTag = "</think";
    private static readonly SearchValues<char> SyntaxCharacters = SearchValues.Create("<\u0060~\\\t\r\n>");
    private readonly StringBuilder _tag = new();
    private bool _inside, _comment, _attributes, _sawThinking, _separateThinking;
    private char _attributeQuote, _runCharacter, _fenceCharacter;
    private int _commentDashes, _runLength, _fenceLength, _inlineLength;
    private bool _runAtLineStart, _closeFenceAtEol;
    private bool _lineStart = true, _quotedLine, _escaped;
    private int _indent;

    public readonly record struct SplitResult(string Visible, string Thinking);
    public bool IsThinking => _inside;

    private readonly StringBuilder _visible = new();
    private readonly StringBuilder _thinking = new();
    private StringBuilder? _part;
    private Action<SplitResult>? _onPart;
    private bool _partThinking;

    public SplitResult Feed(string chunk, Action<SplitResult>? onPart = null)
    {
        _visible.Clear();
        _thinking.Clear();
        _part?.Clear();
        if (onPart is not null) _part ??= new StringBuilder();
        _onPart = onPart;
        _partThinking = false;

        for (var index = 0; index < chunk.Length; index++)
        {
            var c = chunk[index];
            if (_runLength > 0 && c != _runCharacter)
                FinishRun();
            if (_tag.Length == 0 && !_comment && _runLength == 0 && !_escaped)
            {
                var count = chunk.AsSpan(index).IndexOfAny(SyntaxCharacters);
                if (count < 0) count = chunk.Length - index;
                if (count > 0)
                {
                    PrepareOutput();
                    if (_onPart is not null) _part!.Append(chunk, index, count);
                    (_inside ? _thinking : _visible).Append(chunk, index, count);
                    if (_inside) _sawThinking = true;
                    if (_lineStart)
                    {
                        var spaces = 0;
                        while (spaces < count && chunk[index + spaces] == ' ') spaces++;
                        _indent += spaces;
                        _lineStart = spaces == count;
                    }
                    if (_closeFenceAtEol && !chunk.AsSpan(index, count).Trim().IsEmpty) _closeFenceAtEol = false;
                    index += count - 1;
                    continue;
                }
            }

            if (_comment)
            {
                EmitCharacter(c);
                if (c == '>' && _commentDashes >= 2) _comment = false;
                _commentDashes = c == '-' ? _commentDashes + 1 : 0;
            }
            else if (_tag.Length > 0)
            {
                ReadTag(c);
            }
            else if (!_escaped && (c == '`' || c == '~' && (_runLength > 0 || _fenceLength > 0 || _lineStart)))
            {
                if (_runLength == 0)
                {
                    _runAtLineStart = _lineStart && !_quotedLine && _indent <= 3;
                    _runCharacter = c;
                }
                _runLength++;
            }
            else if (c == '<' && !_escaped && _fenceLength == 0 && _inlineLength == 0 && !_quotedLine && _indent < 4)
            {
                _tag.Append(c);
            }
            else EmitCharacter(c);

            TrackLine(c);
        }
        FlushPart();
        _onPart = null;
        return new SplitResult(_visible.ToString(), _thinking.ToString());
    }

    private void FlushPart()
    {
        if (_onPart is null || _part is not { Length: > 0 }) return;
        _onPart!(_partThinking ? new SplitResult("", _part.ToString()) : new SplitResult(_part.ToString(), ""));
        _part.Clear();
    }

    private void PrepareOutput()
    {
        if (_inside && _separateThinking)
        {
            _separateThinking = false;
            _thinking.Append("\n\n");
            if (_onPart is not null)
            {
                if (!_partThinking) FlushPart();
                _partThinking = true;
                _part!.Append("\n\n");
            }
        }
        if (_onPart is not null)
        {
            if (_partThinking != _inside) FlushPart();
            _partThinking = _inside;
        }
    }

    private void Emit(string value)
    {
        if (value.Length == 0) return;
        PrepareOutput();
        if (_onPart is not null) _part!.Append(value);
        (_inside ? _thinking : _visible).Append(value);
        if (_inside) _sawThinking = true;
    }

    private void EmitCharacter(char value)
    {
        PrepareOutput();
        if (_onPart is not null) _part!.Append(value);
        (_inside ? _thinking : _visible).Append(value);
        if (_inside) _sawThinking = true;
    }

    private void ReadTag(char c)
    {
        _tag.Append(c);
        if (!_attributes)
        {
            var target = _inside ? CloseTag : OpenTag;
            var length = _tag.Length;
            if (length <= target.Length && char.ToLowerInvariant(c) == target[length - 1])
                return;
            if (length <= 4 && _tag[1] == '!' && c == "<!--"[length - 1])
            {
                if (length == 4)
                {
                    Emit(_tag.ToString());
                    _tag.Clear();
                    _comment = true;
                    _commentDashes = 0;
                }
                return;
            }
            if (length == target.Length + 1 && (c == '>' || IsTagSpace(c)))
            {
                if (c == '>') CompleteTag();
                else _attributes = true;
                return;
            }
            Emit(_tag.ToString());
            _tag.Clear();
            return;
        }

        if (_attributeQuote != '\0')
        {
            if (c == _attributeQuote) _attributeQuote = '\0';
        }
        else if (!_inside && c is '\'' or '"') _attributeQuote = c;
        else if (c == '>') CompleteTag();
        else if (c == '<' || _inside && !IsTagSpace(c))
        {
            Emit(_tag.ToString());
            _tag.Clear();
            _attributes = false;
        }
    }

    private void CompleteTag()
    {
        if (_tag.Length > 1 && _tag[^2] == '/')
            Emit(_tag.ToString());
        else
        {
            _inside = !_inside;
            _separateThinking = _inside && _sawThinking;
        }
        _tag.Clear();
        _attributes = false;
        _attributeQuote = '\0';
    }

    private void FinishRun()
    {
        if (_runAtLineStart && _runLength >= 3 && _inlineLength == 0)
        {
            if (_fenceLength == 0)
            {
                _fenceCharacter = _runCharacter;
                _fenceLength = _runLength;
            }
            else if (_runCharacter == _fenceCharacter && _runLength >= _fenceLength)
                _closeFenceAtEol = true;
        }
        else if (_fenceLength == 0 && _runCharacter == '`')
        {
            if (_inlineLength == 0) _inlineLength = _runLength;
            else if (_inlineLength == _runLength) _inlineLength = 0;
        }
        Emit(new string(_runCharacter, _runLength));
        _runLength = 0;
    }

    private void TrackLine(char c)
    {
        if (c is '\r' or '\n')
        {
            if (_closeFenceAtEol) _fenceLength = 0;
            _closeFenceAtEol = false;
            _lineStart = true;
            _indent = 0;
            _quotedLine = _escaped = false;
            return;
        }
        if (_closeFenceAtEol && !IsTagSpace(c)) _closeFenceAtEol = false;
        if (_lineStart && c is ' ' or '\t') _indent += c == ' ' ? 1 : 4 - _indent % 4;
        else
        {
            if (_lineStart && c == '>') _quotedLine = true;
            _lineStart = false;
        }
        _escaped = c == '\\' && !_escaped;
    }

    public SplitResult Flush()
    {
        _visible.Clear();
        _thinking.Clear();
        _part?.Clear();
        _onPart = null;
        var remaining = _tag.ToString();
        _tag.Clear();
        Emit(remaining);
        if (_runLength > 0) FinishRun();
        var result = new SplitResult(_visible.ToString(), _thinking.ToString());
        _inside = _comment = _attributes = _escaped = false;
        _fenceLength = _inlineLength = 0;
        _attributeQuote = '\0';
        return result;
    }

    private static bool IsTagSpace(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f';
}
