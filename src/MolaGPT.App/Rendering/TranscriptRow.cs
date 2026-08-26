using MolaGPT.Presentation;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Rendering;

/// <summary>
/// One realizable item in the transcript.
///
/// The split is asymmetric on purpose, because the two roles have different
/// shapes and different costs:
///
///   assistant — flattened into header / block / block / … / actions. An answer
///               can be thousands of lines, and one container per message is
///               what made the WPF list drift its extent by 803% and refuse to
///               land at the bottom. Paragraph-sized rows fix that.
///
///   user      — one row, always. The WPF layout puts the avatar beside the
///               bubble and top-aligns it against the bubble's first line;
///               splitting that across rows cannot reproduce it. Prompts are
///               short, so keeping them whole costs nothing.
/// </summary>
public abstract class TranscriptRow
{
    protected TranscriptRow(MessageViewModel message, string key)
    {
        Message = message;
        Key = key;
        IsUser = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase);
    }

    public MessageViewModel Message { get; }

    /// <summary>Stable identity across re-flattens, so a row the user is looking
    /// at is reused rather than torn down and rebuilt on the next delta.</summary>
    public string Key { get; }

    public bool IsUser { get; }
}

/// <summary>
/// A whole user turn: tinted bubble (max 640) on the right, 32px avatar beside
/// it. Mirrors the UserStack branch of MessageItemView.
/// </summary>
public sealed class UserMessageRow : TranscriptRow
{
    public UserMessageRow(MessageViewModel message, IReadOnlyList<RenderBlock> blocks)
        : base(message, message.RowKey() + ":user")
        => Blocks = blocks;

    public IReadOnlyList<RenderBlock> Blocks { get; }
}

/// <summary>Assistant avatar plus model name — row 0 of the AssistantGrid.</summary>
public sealed class HeaderRow : TranscriptRow
{
    public HeaderRow(MessageViewModel message) : base(message, message.RowKey() + ":head") { }

    public string Label =>
        Message.ModelLabel is { Length: > 0 } model ? model : "Assistant";
}

/// <summary>A parsed markdown block: paragraph, heading, code fence, table…</summary>
public sealed class ProseRow : TranscriptRow
{
    public ProseRow(MessageViewModel message, RenderBlock block, int segment)
        : base(message, $"{message.RowKey()}:{segment}:{block.Key}")
        => Block = block;

    public RenderBlock Block { get; }
}

public sealed class ToolRow : TranscriptRow
{
    public ToolRow(MessageViewModel message, ToolCallViewModel tool, int segment)
        : base(message, $"{message.RowKey()}:{segment}:tool")
        => Tool = tool;

    public ToolCallViewModel Tool { get; }

    public bool IsExpanded { get; set; }
}

public sealed class ToolGroupRow : TranscriptRow
{
    public ToolGroupRow(MessageViewModel message, ToolGroupViewModel group, int segment)
        : base(message, $"{message.RowKey()}:{segment}:toolgroup")
        => Group = group;

    public ToolGroupViewModel Group { get; }
}

public sealed class ThinkingRow : TranscriptRow
{
    public ThinkingRow(MessageViewModel message, ThinkingSegmentViewModel segmentVm, int segment)
        : base(message, $"{message.RowKey()}:{segment}:think")
        => Segment = segmentVm;

    public ThinkingSegmentViewModel Segment { get; }
}

/// <summary>The three-dot "回复处理中" placeholder shown before the first delta.</summary>
public sealed class PendingRow : TranscriptRow
{
    public PendingRow(MessageViewModel message) : base(message, message.RowKey() + ":pending") { }
}

/// <summary>
/// "已停止生成，未产生回答". Emitted only when the user cut a turn short before
/// it produced anything — without it the bubble is a blank gap between the model
/// name and the action bar, and the turn looks like it vanished. A partial
/// answer needs no marker: the text already shows where it stopped.
/// </summary>
public sealed class StoppedRow : TranscriptRow
{
    public StoppedRow(MessageViewModel message) : base(message, message.RowKey() + ":stopped") { }
}

/// <summary>Retry / copy / stats strip under a finished assistant message.</summary>
public sealed class ActionRow : TranscriptRow
{
    public ActionRow(MessageViewModel message) : base(message, message.RowKey() + ":actions") { }
}

internal static class MessageRowKeys
{
    /// <summary>
    /// Identity for a message's rows. MessageId is null until the row is
    /// persisted, so fall back to the instance's hash — stable for the lifetime
    /// of the view model, which is all this needs to be.
    /// </summary>
    public static string RowKey(this MessageViewModel message) =>
        message.MessageId is { Length: > 0 } id
            ? id
            : "m" + message.GetHashCode().ToString("x8");
}
