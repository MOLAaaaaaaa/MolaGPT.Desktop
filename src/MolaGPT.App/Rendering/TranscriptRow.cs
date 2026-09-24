using System.ComponentModel;
using MolaGPT.Presentation;
using MolaGPT.Presentation.Artifacts;
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
public sealed class HeaderRow : TranscriptRow, INotifyPropertyChanged
{
    private string _label;

    public HeaderRow(MessageViewModel message) : base(message, message.RowKey() + ":head")
        => _label = LabelFor(message);

    /// <summary>
    /// Notifying rather than computed, for the same reason
    /// <see cref="ProseRow.IsFadingTail"/> is: this row's key never changes, so
    /// the splice always carries the existing instance — and its container —
    /// forward. A plain getter is read once when that container is realized and
    /// never again, which is how a retry on a different model kept showing the
    /// name of the model that produced the attempt it had just replaced.
    /// </summary>
    public string Label
    {
        get => _label;
        private set
        {
            if (string.Equals(_label, value, StringComparison.Ordinal)) return;
            _label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    /// <summary>Re-read the model name onto a row the splice is about to carry
    /// forward.</summary>
    public void Refresh() => Label = LabelFor(Message);

    private static string LabelFor(MessageViewModel message) =>
        message.PersonaName is { Length: > 0 } persona ? persona
            : message.ModelLabel is { Length: > 0 } model ? model : "Assistant";

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// A parsed markdown block: paragraph, heading, code fence, table…
///
/// Keyed by content, except tables and code fences, which are keyed by where
/// they start. Both stream in piece by piece; keyed by content, every delta
/// replaced the row and built its view from scratch — a new grid of cells for
/// a table, a new code view for a fence, which then never saw its code grow.
/// Keyed by position the row is carried across deltas, its block is swapped in
/// place, and the view updates only what changed.
/// </summary>
public sealed class ProseRow : TranscriptRow, INotifyPropertyChanged
{
    private bool _isFadingTail;
    private RenderBlock _block;

    public ProseRow(MessageViewModel message, RenderBlock block, int segment)
        : base(message, block is TableBlock or CodeBlock
            ? $"{message.RowKey()}:{segment}:{(block is TableBlock ? "table" : "code")}:{block.SourceStart}"
            : $"{message.RowKey()}:{segment}:{block.Key}")
        => _block = block;

    public RenderBlock Block
    {
        get => _block;
        private set
        {
            if (ReferenceEquals(_block, value)) return;
            _block = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Block)));
        }
    }

    /// <summary>A carried table or fence row takes the grown block. Other blocks
    /// are keyed by content, so a carried one already shows what it should.
    /// Returns whether the row now shows something different.</summary>
    public bool Refresh(RenderBlock block)
    {
        if (block is not (TableBlock or CodeBlock) || ReferenceEquals(block, _block)) return false;
        Block = block;
        return true;
    }

    /// <summary>
    /// This is the last block of a message whose text is still being revealed,
    /// so its final line gets the trailing fade.
    ///
    /// Lives on the row rather than being derived in the template because a
    /// splice carries unchanged rows forward by reference: the row that was the
    /// tail a moment ago is often the same object, and only an assignment with
    /// notification can take the flag back off it.
    /// </summary>
    public bool IsFadingTail
    {
        get => _isFadingTail;
        set
        {
            if (_isFadingTail == value) return;
            _isFadingTail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFadingTail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// A streamed MolaGPT status or analysis card. Unlike ordinary prose, its
/// identity is its position in the answer: the source grows while a tool is
/// running, but the card under the pointer must remain the same control.
/// </summary>
public sealed class MarkupRow : TranscriptRow, INotifyPropertyChanged
{
    private MarkupUnitBlock _block;

    public MarkupRow(MessageViewModel message, MarkupUnitBlock block, int segment)
        : base(message,
            $"{message.RowKey()}:{segment}:markup:{block.SourceStart}:{block.UnitKind}:{block.Unit.Tag?.ToLowerInvariant()}")
        => _block = block;

    public MarkupUnitBlock Block
    {
        get => _block;
        private set
        {
            if (ReferenceEquals(_block, value)) return;
            _block = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Block)));
        }
    }

    public void Refresh(MarkupUnitBlock block) => Block = block;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// A fence that belongs on the canvas (a page, an SVG, a diagram, a table),
/// shown in the answer as a chip — or, by the user's choice, as the ordinary
/// code block with a way onto the canvas.
///
/// Keyed by position like <see cref="MarkupRow"/>: while the page is being
/// written its content changes on every delta, and a content-keyed row would
/// rebuild the chip under the pointer — a click on 「在画布打开」 mid-stream would
/// land on a control that no longer exists.
/// </summary>
public sealed class ArtifactFenceRow : TranscriptRow, INotifyPropertyChanged
{
    private readonly Action<ArtifactFenceRow, bool>? _remember;
    private CodeBlock _block;
    private bool _messageDone;
    private bool _showCode;

    public ArtifactFenceRow(MessageViewModel message, CodeBlock block, ArtifactRenderKind kind, int ordinal, int segment,
        bool messageDone, bool showCode, Action<ArtifactFenceRow, bool>? remember)
        : base(message, $"{message.RowKey()}:{segment}:artifact:{block.SourceStart}")
    {
        _block = block;
        Kind = kind;
        Ordinal = ordinal;
        _messageDone = messageDone;
        _showCode = showCode;
        _remember = remember;
    }

    /// <summary>Shown as its source rather than as a chip.</summary>
    public bool ShowCode
    {
        get => _showCode;
        private set
        {
            if (_showCode == value) return;
            _showCode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowCode)));
        }
    }

    /// <summary>The user switched this fence. Remembered past the row: the
    /// message's rows are rebuilt when it is saved and gets its id.</summary>
    public void Choose(bool showCode)
    {
        ShowCode = showCode;
        _remember?.Invoke(this, showCode);
    }

    /// <summary>The default changed in settings, or a rebuilt row resolved
    /// differently; not a choice of the user's.</summary>
    internal void Apply(bool showCode) => ShowCode = showCode;

    public ArtifactRenderKind Kind { get; }

    /// <summary>Index among this message's canvas fences — what the workspace
    /// knows the version by.</summary>
    public int Ordinal { get; private set; }

    public CodeBlock Block => _block;

    /// <summary>Still being written: open fence in a message that is streaming.</summary>
    public bool IsGenerating => !_block.IsClosed && !_messageDone;

    public void Refresh(ArtifactFenceRow next)
    {
        ShowCode = next.ShowCode;
        if (ReferenceEquals(_block, next._block) && _messageDone == next._messageDone && Ordinal == next.Ordinal) return;
        _block = next._block;
        _messageDone = next._messageDone;
        Ordinal = next.Ordinal;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Block)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// A <c>mola-ui</c> fence: an inline component (plot, chart, table, cards) drawn in
/// place. Position-keyed for the same reason as <see cref="ArtifactFenceRow"/>:
/// the placeholder must turn into the component without its row being replaced.
/// </summary>
public sealed class UiBlockRow : TranscriptRow, INotifyPropertyChanged
{
    private CodeBlock _block;
    private bool _messageDone;

    public UiBlockRow(MessageViewModel message, CodeBlock block, int segment, bool messageDone)
        : base(message, $"{message.RowKey()}:{segment}:ui:{block.SourceStart}")
    {
        _block = block;
        _messageDone = messageDone;
    }

    public CodeBlock Block => _block;

    /// <summary>No more text is coming for this fence: its closing marker has
    /// arrived, or the answer ended without one.</summary>
    public bool IsFinal => _block.IsClosed || _messageDone;

    public void Refresh(UiBlockRow next)
    {
        if (ReferenceEquals(_block, next._block) && _messageDone == next._messageDone) return;
        _block = next._block;
        _messageDone = next._messageDone;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Block)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ToolRow : TranscriptRow, INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _bodyLoaded;

    public ToolRow(MessageViewModel message, ToolCallViewModel tool, int segment)
        : base(message, $"{message.RowKey()}:{segment}:tool")
        => Tool = tool;

    public ToolCallViewModel Tool { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));

            if (!value || _bodyLoaded) return;
            _bodyLoaded = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BodyContent)));
        }
    }

    /// <summary>The expensive payload view is materialized only after the card
    /// has actually been opened. It stays enabled afterwards so closing can
    /// animate and reopening the same realized card is immediate.</summary>
    public ToolRow? BodyContent => _bodyLoaded ? this : null;

    // The raw folds keep their own open state here rather than in the control,
    // so scrolling a card out of view and back does not undo the choice.
    public bool IsArgumentsExpanded { get; set; }
    public bool IsResultExpanded { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ToolGroupRow : TranscriptRow, INotifyPropertyChanged
{
    private bool _isExpanded;

    public ToolGroupRow(MessageViewModel message, ToolGroupViewModel group, int segment)
        : base(message, $"{message.RowKey()}:{segment}:toolgroup")
        => Group = group;

    public ToolGroupViewModel Group { get; }

    /// <summary>Fold state, kept on the row (not the control) so scrolling the
    /// card out of view and back preserves the choice. Starts collapsed — a run
    /// of calls reads as one line; the per-call detail is behind the fold.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
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

/// <summary>
/// "已达输出上限". The provider ended the turn because it ran out of output
/// tokens, which a truncated answer does not say by itself — and a turn whose
/// budget all went on thinking has no answer at all to say it.
/// </summary>
public sealed class OutputLimitRow : TranscriptRow
{
    public OutputLimitRow(MessageViewModel message) : base(message, message.RowKey() + ":output-limit") { }
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
