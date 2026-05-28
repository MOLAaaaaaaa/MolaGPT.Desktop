using System.Text;

namespace MolaGPT.Core.Chat;

/// <summary>
/// Streaming-friendly state-machine that splits a chat completion's
/// <c>delta.content</c> text into (visible_text, thinking_text) pairs by
/// recognizing inline <c>&lt;think&gt;…&lt;/think&gt;</c> tags.
///
/// Why this exists: DeepSeek-R1, QwQ-32B, and a handful of other reasoning
/// models don't use OpenAI's <c>delta.reasoning_content</c> channel — they
/// emit reasoning as <c>&lt;think&gt;</c> blocks inside the regular content
/// stream. Without this splitter the desktop app would show the model's
/// chain-of-thought as raw <c>&lt;think&gt;</c> tagged text in the message
/// body, which is both ugly and breaks the ThinkBlock UI.
///
/// Usage: keep one instance per assistant message (per <see cref="IChatProvider"/>
/// stream). Feed every chunk's text via <see cref="Feed(string)"/>; the
/// returned <see cref="SplitResult"/> tells you what to push as visible text
/// and what to route into the thinking channel.
///
/// Notes:
/// <list type="bullet">
///   <item>Tags can straddle chunk boundaries — we buffer up to a tag's
///         length before emitting tail bytes that look like a partial open
///         tag (e.g. "&lt;th") so we never split through a tag.</item>
///   <item>Nesting is not supported (R1's protocol is flat); a stray
///         <c>&lt;think&gt;</c> inside an already-open block is treated as
///         literal text.</item>
///   <item>Tags are matched case-insensitively. Whitespace and attributes
///         inside the open tag are tolerated (e.g. <c>&lt;think id="0"&gt;</c>).</item>
/// </list>
/// </summary>
public sealed class InlineThinkSplitter
{
    private const string OpenTag = "<think";   // partial; we accept any closing >
    private const string CloseTag = "</think>";

    private enum Mode { Outside, Inside }

    private Mode _mode = Mode.Outside;
    /// <summary>Pending bytes that look like the start of a tag. Carried over to next chunk.</summary>
    private string _pending = string.Empty;

    /// <summary>The raw "&lt;think...&gt;" we're currently parsing the open tag of.</summary>
    private bool _scanningOpenTag;

    public readonly record struct SplitResult(string Visible, string Thinking);

    /// <summary>
    /// Feed the next chunk of streamed content. Returns the portion that
    /// should be appended to the visible message body and the portion that
    /// should be appended to the thinking channel.
    /// </summary>
    public SplitResult Feed(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return new SplitResult(string.Empty, string.Empty);

        // Prepend any unflushed pending bytes from the previous call.
        var input = _pending + chunk;
        _pending = string.Empty;

        var visible = new StringBuilder();
        var thinking = new StringBuilder();
        int i = 0;

        while (i < input.Length)
        {
            if (_scanningOpenTag)
            {
                // Inside "<think...". Look for the closing '>'.
                int gt = input.IndexOf('>', i);
                if (gt < 0)
                {
                    // No close yet — buffer the rest.
                    _pending = input.Substring(i - OpenTag.Length); // include "<think" itself
                    return new SplitResult(visible.ToString(), thinking.ToString());
                }
                _scanningOpenTag = false;
                _mode = Mode.Inside;
                i = gt + 1;
                continue;
            }

            if (_mode == Mode.Outside)
            {
                int lt = input.IndexOf('<', i);
                if (lt < 0)
                {
                    visible.Append(input, i, input.Length - i);
                    return new SplitResult(visible.ToString(), thinking.ToString());
                }

                // Push everything up to '<' as visible.
                if (lt > i) visible.Append(input, i, lt - i);

                // Could this be "<think"? We need at least 6 chars from lt.
                int needed = OpenTag.Length;
                if (lt + needed > input.Length)
                {
                    // Buffer "<..." and wait for more bytes.
                    _pending = input.Substring(lt);
                    return new SplitResult(visible.ToString(), thinking.ToString());
                }

                if (input.AsSpan(lt, needed).Equals(OpenTag.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    // Match. Advance past "<think" and start scanning for close-bracket.
                    _scanningOpenTag = true;
                    i = lt + needed;
                    continue;
                }

                // Not a think tag — emit '<' as literal and continue past it.
                visible.Append('<');
                i = lt + 1;
                continue;
            }
            else
            {
                // Inside <think>...</think>. Look for the closing tag.
                int closeIdx = input.IndexOf(CloseTag, i, StringComparison.OrdinalIgnoreCase);
                if (closeIdx < 0)
                {
                    // No close — buffer the trailing bytes that could be a partial close.
                    int safeEnd = Math.Max(i, input.Length - (CloseTag.Length - 1));
                    if (safeEnd > i) thinking.Append(input, i, safeEnd - i);
                    if (safeEnd < input.Length) _pending = input.Substring(safeEnd);
                    return new SplitResult(visible.ToString(), thinking.ToString());
                }

                if (closeIdx > i) thinking.Append(input, i, closeIdx - i);
                _mode = Mode.Outside;
                i = closeIdx + CloseTag.Length;
            }
        }

        return new SplitResult(visible.ToString(), thinking.ToString());
    }

    /// <summary>
    /// Flush any pending state. Called at end-of-stream so a malformed
    /// trailing <c>&lt;think&gt;</c> without a close doesn't lose the bytes —
    /// we surface them as visible text.
    /// </summary>
    public SplitResult Flush()
    {
        if (string.IsNullOrEmpty(_pending) && _mode == Mode.Outside)
            return new SplitResult(string.Empty, string.Empty);

        var visible = new StringBuilder();
        var thinking = new StringBuilder();

        // If we're inside an unterminated <think>, treat the remainder as thinking.
        if (_mode == Mode.Inside) thinking.Append(_pending);
        else visible.Append(_pending);

        _pending = string.Empty;
        _mode = Mode.Outside;
        _scanningOpenTag = false;

        return new SplitResult(visible.ToString(), thinking.ToString());
    }

    /// <summary>True if we're currently mid-tag (i.e. expecting more bytes).</summary>
    public bool IsThinking => _mode == Mode.Inside || _scanningOpenTag;
}
