using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MolaGPT.App.Rendering;

namespace MolaGPT.App.Views;

/// <summary>
/// Drag selection across the whole transcript.
///
/// Every paragraph, heading, list item and table cell is its own
/// SelectableTextBlock, so anything longer than one of them is a selection this
/// view has to assemble. Once a drag starts it owns the pointer outright: the
/// base class's own move handler never runs, because it hit-tests a text layout
/// that the previous move's selection change has just thrown away (see
/// <see cref="EnsureLayout"/>), and on the frames where no layout pass got in
/// between it read offset 0 and snapped the selection back to the start of the
/// block.
///
/// Three rules the earlier version broke, each one visible on screen:
///
/// <list type="bullet">
/// <item>The block under the pointer is found in two dimensions. Picking it by
/// height alone made every cell in a table row tie, the leftmost cell won the
/// tie, and dragging right inside any other cell selected leftwards into it.</item>
/// <item><c>TextHitTestResult.TextPosition</c> already includes the trailing
/// half of the character; adding <c>IsTrailing</c> on top landed one past it.</item>
/// <item>What is copied is read back from what is highlighted, block by block,
/// rather than kept as a string on the side — the two used to disagree whenever
/// the anchor's own part of the selection was empty, and Ctrl+C then copied
/// nothing.</item>
/// </list>
/// </summary>
public partial class TranscriptView
{
    /// <summary>How far past the top or bottom edge a drag must go before the
    /// transcript starts scrolling under it, and how fast it goes from there.
    /// Pixels per 16 ms: two at the edge, capped where text is still legible
    /// going past.</summary>
    private const double AutoScrollMinStep = 2;
    private const double AutoScrollMaxStep = 48;
    private const double AutoScrollGain = 0.25;

    private enum SelectionUnit { Character, Word, Block }

    /// <summary>
    /// The selection as the reader made it: where it started and in what unit.
    /// <see cref="First"/>..<see cref="Last"/> is what is highlighted now, as
    /// indices into <see cref="Blocks"/>, the transcript's text blocks in reading
    /// order when the list was last taken.
    /// </summary>
    private sealed class TextSelection
    {
        public required SelectableTextBlock Anchor { get; init; }
        public required int AnchorStart { get; init; }
        public required int AnchorEnd { get; init; }
        public required SelectionUnit Unit { get; init; }
        public List<SelectableTextBlock> Blocks { get; set; } = [];
        public int First { get; set; } = -1;
        public int Last { get; set; } = -1;
    }

    private TextSelection? _selection;
    private bool _selectionDragging;
    private Point _selectionPointer;
    private SelectableTextBlock? _selectionCaptureOwner;

    /// <summary>The block a left press landed in, until the press has bubbled
    /// back up and it is known whether the block took it.</summary>
    private SelectableTextBlock? _pressedBlock;
    private int _pressedCharacter = -1;

    /// <summary>Set when rows are realized, recycled or moved: the block list a
    /// drag holds has to be taken again before the next hit test.</summary>
    private bool _selectionBlocksStale;
    private bool _selectionFollowQueued;

    private DispatcherTimer? _autoScroll;
    private readonly Stopwatch _autoScrollClock = new();

    private void HookBlockSpanningSelection()
    {
        AddHandler(PointerPressedEvent, OnSelectionPressing, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnSelectionPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnSelectionMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnSelectionReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnSelectionKeyDown, RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent, OnSelectionLostFocus, RoutingStrategies.Bubble);
        AddHandler(
            SelectableTextBlock.CopyingToClipboardEvent,
            OnCopyingToClipboard,
            RoutingStrategies.Bubble);

        PART_Rows.ContainerPrepared += (_, _) => _selectionBlocksStale = true;
        PART_Rows.ContainerIndexChanged += (_, _) => _selectionBlocksStale = true;
        PART_Rows.ContainerClearing += OnRowContainerClearing;
    }

    // ---- press -------------------------------------------------------------

    private void OnSelectionPressing(object? sender, PointerPressedEventArgs e)
    {
        _pressedBlock = null;
        _pressedCharacter = -1;

        // A right press leaves the selection alone: it is how the context menu
        // gets to copy it.
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;

        EndSelectionDrag();

        var hit = TranscriptBlockOf(e.Source);
        if (hit is not null
            && e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            && e.ClickCount == 1
            && _selection is { } current
            && current.Anchor.IsAttachedToVisualTree())
        {
            // Shift+click extends what is already there, from the same anchor
            // and in the same unit. The press never reaches the block, which
            // would otherwise restart the selection inside itself.
            RetakeBlocks(current);
            e.Pointer.Capture(current.Anchor);
            BeginSelectionDrag(current, e.GetPosition(this));
            ExtendSelectionTo(_selectionPointer);
            e.Handled = true;
            return;
        }

        foreach (var block in CollectBlocks(visibleOnly: false))
        {
            if (!ReferenceEquals(block, hit)) Clear(block);
        }
        ForgetSelection();

        if (hit is null) return;

        // The base class hit-tests this block as soon as the press reaches it.
        EnsureLayout(hit);
        _pressedBlock = hit;
        if (e.ClickCount == 2) _pressedCharacter = CharacterAt(hit, e.GetPosition(this));
    }

    private void OnSelectionPressed(object? sender, PointerPressedEventArgs e)
    {
        var pressed = _pressedBlock;
        var character = _pressedCharacter;
        _pressedBlock = null;
        _pressedCharacter = -1;

        if (_selectionDragging) return;
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;

        if (pressed is not null)
        {
            // The block captures the pointer when it starts a selection. A pill
            // or an inline control that swallowed the press never got that far,
            // and there is nothing to drag from.
            if (!ReferenceEquals(e.Pointer.Captured, pressed)) return;

            var start = Math.Min(pressed.SelectionStart, pressed.SelectionEnd);
            var end = Math.Max(pressed.SelectionStart, pressed.SelectionEnd);
            var unit = e.ClickCount switch
            {
                1 => SelectionUnit.Character,
                2 => SelectionUnit.Word,
                _ => SelectionUnit.Block
            };

            // The base class's word is a run of letters and digits, which runs
            // straight through Chinese into the Latin beside it. Redrawn here with
            // the same boundaries a word-wise drag extends by, so the two agree.
            if (unit == SelectionUnit.Word && character >= 0)
            {
                (start, end) = WordAt(TextOf(pressed), character);
                SetRange(pressed, start, end);
            }

            BeginSelectionDrag(NewSelection(pressed, start, end, unit), e.GetPosition(this));
            return;
        }

        // A press on bare space inside a text block's own area — a table cell's
        // padding, the margin beside a paragraph — starts a selection at the
        // nearest character, the way a page does. Anything that handled the
        // press itself (buttons, cards, pills) is left alone, and so is touch,
        // where a drag on bare space means scroll.
        if (e.Handled || e.Pointer.Type == PointerType.Touch) return;
        if (e.Source is not Visual source
            || !(ReferenceEquals(source, PART_Rows) || PART_Rows.IsVisualAncestorOf(source)))
        {
            return;
        }

        var blocks = CollectBlocks(visibleOnly: true);
        var point = e.GetPosition(this);
        var at = BlockWhoseAreaContains(blocks, point);
        if (at < 0) return;

        var block = blocks[at];
        var offset = OffsetAt(block, point);
        block.Focus(NavigationMethod.Pointer);
        e.Pointer.Capture(block);
        SetRange(block, offset, offset);
        BeginSelectionDrag(
            NewSelection(block, offset, offset, SelectionUnit.Character, blocks),
            point);
        e.Handled = true;
    }

    /// <summary>A selection that so far covers only its anchor — everything
    /// else was cleared by the press that started it.</summary>
    private TextSelection NewSelection(
        SelectableTextBlock anchor, int start, int end, SelectionUnit unit,
        List<SelectableTextBlock>? blocks = null)
    {
        var selection = new TextSelection
        {
            Anchor = anchor,
            AnchorStart = start,
            AnchorEnd = end,
            Unit = unit,
            Blocks = blocks ?? CollectBlocks(visibleOnly: true)
        };
        selection.First = selection.Last = selection.Blocks.IndexOf(anchor);
        return selection;
    }

    private void BeginSelectionDrag(TextSelection selection, Point pointer)
    {
        _selection = selection;
        _selectionDragging = true;
        _selectionPointer = pointer;
        _selectionCaptureOwner = selection.Anchor;
        _selectionCaptureOwner.PointerCaptureLost += OnSelectionCaptureLost;
        KeepSelectionRealized(selection);
    }

    // ---- drag --------------------------------------------------------------

    private void OnSelectionMoved(object? sender, PointerEventArgs e)
    {
        if (!_selectionDragging) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndSelectionDrag();
            return;
        }

        _selectionPointer = e.GetPosition(this);
        ExtendSelectionTo(_selectionPointer);
        UpdateAutoScroll();

        // Owned outright: the base class would hit-test a layout that is gone.
        e.Handled = true;
    }

    private void OnSelectionReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left) EndSelectionDrag();
    }

    private void OnSelectionCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        EndSelectionDrag();

    private void EndSelectionDrag()
    {
        if (_selectionCaptureOwner is { } owner)
            owner.PointerCaptureLost -= OnSelectionCaptureLost;
        _selectionCaptureOwner = null;
        _selectionDragging = false;
        _autoScroll?.Stop();
    }

    /// <summary>
    /// Content moving under a pointer that is not — the wheel turned mid-drag,
    /// or a streaming answer pulled the view down — still moves what the pointer
    /// is over. Deferred rather than run from inside ScrollChanged, which can
    /// arrive in the middle of a layout pass.
    /// </summary>
    private void FollowSelectionThroughScroll()
    {
        if (!_selectionDragging || _selectionFollowQueued) return;
        _selectionFollowQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _selectionFollowQueued = false;
            if (!_selectionDragging) return;
            _selectionBlocksStale = true;
            ExtendSelectionTo(_selectionPointer);
        }, DispatcherPriority.Background);
    }

    private void ExtendSelectionTo(Point point)
    {
        if (_selection is not { } selection) return;

        if (_selectionBlocksStale) RetakeBlocks(selection);
        var anchorAt = selection.Blocks.IndexOf(selection.Anchor);
        if (anchorAt < 0)
        {
            RetakeBlocks(selection);
            anchorAt = selection.Blocks.IndexOf(selection.Anchor);
            if (anchorAt < 0)
            {
                EndSelectionDrag();
                return;
            }
        }

        var focus = Resolve(selection.Blocks, point);
        if (focus.Index < 0) return;

        Apply(selection, anchorAt, focus);
    }

    /// <summary>Takes the block list again, carrying the highlighted range
    /// across to the new indices so the next apply knows what to un-highlight.
    /// If either end is gone the next apply walks every block instead.</summary>
    private void RetakeBlocks(TextSelection selection)
    {
        var first = selection.First >= 0 && selection.First < selection.Blocks.Count
            ? selection.Blocks[selection.First]
            : null;
        var last = selection.Last >= 0 && selection.Last < selection.Blocks.Count
            ? selection.Blocks[selection.Last]
            : null;

        selection.Blocks = CollectBlocks(visibleOnly: true);
        selection.First = first is null ? -1 : selection.Blocks.IndexOf(first);
        selection.Last = last is null ? -1 : selection.Blocks.IndexOf(last);
        if (selection.First < 0 || selection.Last < 0) selection.First = selection.Last = -1;
    }

    /// <summary>
    /// Highlights from the anchor to the focus, in reading order.
    ///
    /// Only the blocks whose state can have changed are touched: the old range,
    /// the new one, and the ends of both. A block that was wholly selected and
    /// still is keeps its highlight without being asked, so a drag across a
    /// long answer costs the same per move as a drag across two lines.
    /// </summary>
    private void Apply(TextSelection selection, int anchorAt, Focus focus)
    {
        var blocks = selection.Blocks;
        var focusAt = focus.Index;
        var forward = focusAt > anchorAt
                      || (focusAt == anchorAt && focus.Caret >= selection.AnchorStart);
        var offset = Snap(blocks[focusAt], focus, selection.Unit, forward);

        var first = Math.Min(anchorAt, focusAt);
        var last = Math.Max(anchorAt, focusAt);
        var known = selection.First >= 0;
        var lo = known ? Math.Min(first, selection.First) : 0;
        var hi = known ? Math.Max(last, selection.Last) : blocks.Count - 1;

        for (var i = lo; i <= hi; i++)
        {
            var block = blocks[i];
            if (i == anchorAt && i == focusAt)
            {
                if (forward) SetRange(block, selection.AnchorStart, Math.Max(offset, selection.AnchorEnd));
                else SetRange(block, offset, selection.AnchorEnd);
            }
            else if (i == anchorAt)
            {
                if (forward) SetRange(block, selection.AnchorStart, TextLength(block));
                else SetRange(block, 0, selection.AnchorEnd);
            }
            else if (i == focusAt)
            {
                if (forward) SetRange(block, 0, offset);
                else SetRange(block, offset, TextLength(block));
            }
            else if (i > first && i < last)
            {
                if (known && i > selection.First && i < selection.Last) continue;
                SetRange(block, 0, TextLength(block));
            }
            else
            {
                Clear(block);
            }
        }

        selection.First = first;
        selection.Last = last;
        KeepSelectionRealized(selection);
    }

    /// <summary>Rounds the focus out to the unit the drag started in: a
    /// double-click drags whole words, a triple-click whole blocks.</summary>
    private static int Snap(SelectableTextBlock block, Focus focus, SelectionUnit unit, bool forward)
    {
        switch (unit)
        {
            case SelectionUnit.Word when focus.Character >= 0:
            {
                var (start, end) = WordAt(TextOf(block), focus.Character);
                return forward ? end : start;
            }
            case SelectionUnit.Block:
                return forward ? TextLength(block) : 0;
            default:
                return focus.Caret;
        }
    }

    // ---- where the pointer is ----------------------------------------------

    /// <summary>A position in one of the blocks. <see cref="Character"/> is the
    /// glyph actually under the pointer, which a word-wise drag rounds from; the
    /// caret alone cannot say which side of it the pointer is on.</summary>
    private readonly record struct Focus(int Index, int Caret, int Character);

    /// <summary>
    /// The block and position a point means.
    ///
    /// Each block owns an area — see <see cref="AreaOf"/> — and a point inside
    /// one belongs to it. Outside every area the nearest wins, nearest in height
    /// first and then in width: that is what puts a point in the gutter of a
    /// table row in that row, in the cell it is level with.
    ///
    /// A point above or below the block it lands on selects to that block's
    /// start or end, which is what lets a drag run off the end of an answer and
    /// take the last line whole.
    /// </summary>
    private Focus Resolve(IReadOnlyList<SelectableTextBlock> blocks, Point point)
    {
        var best = -1;
        var bestArea = default(Rect);
        var bestDy = double.MaxValue;
        var bestDx = double.MaxValue;

        for (var i = 0; i < blocks.Count; i++)
        {
            if (AreaOf(blocks[i]) is not { } area) continue;

            var dy = Distance(point.Y, area.Top, area.Bottom);
            var dx = Distance(point.X, area.Left, area.Right);
            if (dy > bestDy + 0.5) continue;
            if (Math.Abs(dy - bestDy) <= 0.5 && dx >= bestDx) continue;

            best = i;
            bestArea = area;
            bestDy = dy;
            bestDx = dx;
        }

        if (best < 0) return new Focus(-1, 0, -1);

        // No character is under a point off the top or bottom, so nothing for a
        // word-wise drag to round out from.
        var block = blocks[best];
        if (point.Y < bestArea.Top) return new Focus(best, 0, -1);
        if (point.Y > bestArea.Bottom) return new Focus(best, TextLength(block), -1);

        var (caret, character) = HitTest(block, point);
        return new Focus(best, caret, character);
    }

    private int BlockWhoseAreaContains(IReadOnlyList<SelectableTextBlock> blocks, Point point)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (AreaOf(blocks[i]) is { } area && area.Contains(point)) return i;
        }

        return -1;
    }

    private static double Distance(double value, double min, double max) =>
        value < min ? min - value : value > max ? value - max : 0;

    /// <summary>
    /// The space a block answers for, in this view's coordinates: the block
    /// grown outwards through every parent it is the only child of.
    ///
    /// For a paragraph that is its whole row — margin, gutter and all — so a
    /// point beside the text or in the gap under it is still that paragraph.
    /// For a table cell it stops at the cell border, the one parent with
    /// siblings. Blocks' areas never overlap, because growing stops at the first
    /// panel with more than one child.
    /// </summary>
    private Rect? AreaOf(SelectableTextBlock block)
    {
        var panel = PART_Rows.ItemsPanelRoot;
        Visual area = block;
        while (area.GetVisualParent() is { } parent
               && !ReferenceEquals(parent, panel)
               && HasOneChild(parent))
        {
            area = parent;
        }

        if (area.TranslatePoint(default, this) is not { } origin) return null;
        return new Rect(origin, area.Bounds.Size);
    }

    private static bool HasOneChild(Visual parent)
    {
        using var children = parent.GetVisualChildren().GetEnumerator();
        return children.MoveNext() && !children.MoveNext();
    }

    private int OffsetAt(SelectableTextBlock block, Point point) => HitTest(block, point).Caret;

    private int CharacterAt(SelectableTextBlock block, Point point) => HitTest(block, point).Character;

    /// <summary>Caret position and the character under a point in this view's
    /// coordinates. <c>TextPosition</c> already counts the trailing half of the
    /// glyph; adding <c>IsTrailing</c> to it lands one character too far.</summary>
    private (int Caret, int Character) HitTest(SelectableTextBlock block, Point point)
    {
        var length = TextLength(block);
        if (length == 0) return (0, -1);

        EnsureLayout(block);
        var local = this.TranslatePoint(point, block) ?? default;
        var hit = block.TextLayout.HitTestPoint(
            new Point(local.X - block.Padding.Left, local.Y - block.Padding.Top));

        return (
            Math.Clamp(hit.TextPosition, 0, length),
            Math.Clamp(hit.CharacterHit.FirstCharacterIndex, 0, length - 1));
    }

    /// <summary>
    /// Brings a block's text layout up to date before anything reads it.
    ///
    /// SelectableTextBlock invalidates its measure on every selection change,
    /// and TextBlock drops its layout with it. Reading <c>TextLayout</c> before
    /// the next layout pass rebuilds it from <c>Text</c>, which is empty for a
    /// block built from inlines: every hit comes back 0. Worse, that empty
    /// layout is then cached and the next measure reuses it, so the block
    /// reports the height of one blank line. Re-laying out this one block with
    /// the constraints it last had is what the layout pass would do for it
    /// anyway, just before we need it rather than after.
    /// </summary>
    private static void EnsureLayout(Layoutable block)
    {
        if (block.IsMeasureValid && block.IsArrangeValid) return;
        if (LayoutInformation.GetPreviousMeasureConstraint(block) is { } constraint)
            block.Measure(constraint);
        if (LayoutInformation.GetPreviousArrangeBounds(block) is { } bounds)
            block.Arrange(bounds);
    }

    // ---- words -------------------------------------------------------------

    private enum CharacterClass { Space, Ideograph, Word, Other }

    /// <summary>
    /// The run of like characters around <paramref name="index"/>.
    ///
    /// Han, kana and hangul are a class of their own rather than letters like
    /// any other: with no spaces between words, treating them as letters made
    /// "Python环境" one word and a whole clause of Chinese with it. Without a
    /// dictionary a CJK run still ends only at punctuation or a script change,
    /// which is as far as boundaries go without one.
    /// </summary>
    private static (int Start, int End) WordAt(string text, int index)
    {
        if (text.Length == 0) return (0, 0);
        index = Math.Clamp(index, 0, text.Length - 1);

        var kind = Classify(text[index]);
        var start = index;
        while (start > 0 && Classify(text[start - 1]) == kind) start--;
        var end = index + 1;
        while (end < text.Length && Classify(text[end]) == kind) end++;
        return (start, end);
    }

    private static CharacterClass Classify(char c)
    {
        if (char.IsWhiteSpace(c)) return CharacterClass.Space;
        if (char.GetUnicodeCategory(c) == UnicodeCategory.OtherLetter) return CharacterClass.Ideograph;
        if (char.IsLetterOrDigit(c) || c == '_') return CharacterClass.Word;
        return CharacterClass.Other;
    }

    // ---- scrolling while selecting -----------------------------------------

    /// <summary>
    /// Keeps a drag going past the top or bottom of the transcript by scrolling
    /// under it. The live band is inset by the scroller's own padding, because
    /// the header floats over the top of it and the composer over the bottom:
    /// reaching either is reaching the edge.
    /// </summary>
    private void UpdateAutoScroll()
    {
        if (AutoScrollStep() == 0)
        {
            _autoScroll?.Stop();
            return;
        }

        if (_autoScroll is null)
        {
            _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _autoScroll.Tick += OnAutoScrollTick;
        }

        if (_autoScroll.IsEnabled) return;
        _autoScrollClock.Restart();
        _autoScroll.Start();
    }

    private double AutoScrollStep()
    {
        if (!_selectionDragging || _scroll is null) return 0;
        if (_scroll.TranslatePoint(default, this) is not { } origin) return 0;

        var top = origin.Y + _scroll.Padding.Top;
        var bottom = origin.Y + _scroll.Bounds.Height - _scroll.Padding.Bottom;
        var y = _selectionPointer.Y;

        if (y < top) return -Step(top - y);
        if (y > bottom) return Step(y - bottom);
        return 0;

        static double Step(double past) =>
            Math.Min(AutoScrollMaxStep, AutoScrollMinStep + (past * AutoScrollGain));
    }

    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        var step = AutoScrollStep();
        if (step == 0 || _scroll is null)
        {
            _autoScroll?.Stop();
            return;
        }

        // Per 16 ms, scaled by how long the tick actually took, so a busy frame
        // does not slow the scroll down.
        var elapsed = Math.Clamp(_autoScrollClock.Elapsed.TotalMilliseconds / 16, 0.5, 4);
        _autoScrollClock.Restart();

        var max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        var target = Math.Clamp(_scroll.Offset.Y + (step * elapsed), 0, max);
        if (Math.Abs(target - _scroll.Offset.Y) > 0.1)
        {
            CancelWheelAnimation();
            _scroll.Offset = _scroll.Offset.WithY(target);

            // Realize the rows the new offset brings in before resolving the
            // pointer against them.
            UpdateLayout();
            _selectionBlocksStale = true;
        }

        ExtendSelectionTo(_selectionPointer);
    }

    /// <summary>Keeps the rows between the two ends of the selection realized.
    /// The anchor's own row is safe already — it holds keyboard focus — but the
    /// rows a drag scrolled through would otherwise be recycled, highlight and
    /// all, and a copy would come back with a hole in it.</summary>
    private void KeepSelectionRealized(TextSelection selection)
    {
        if (PART_Rows.ItemsPanelRoot is not StableVirtualizingStackPanel panel) return;
        if (selection.First < 0 || selection.Last >= selection.Blocks.Count)
        {
            panel.KeepRealized(null, null);
            return;
        }

        panel.KeepRealized(
            RowItemOf(selection.Blocks[selection.First]),
            RowItemOf(selection.Blocks[selection.Last]));
    }

    private object? RowItemOf(Visual block)
    {
        var panel = PART_Rows.ItemsPanelRoot;
        Visual? visual = block;
        while (visual is not null && !ReferenceEquals(visual.GetVisualParent(), panel))
            visual = visual.GetVisualParent();

        return visual is Control container ? PART_Rows.ItemFromContainer(container) : null;
    }

    // ---- ending a selection ------------------------------------------------

    /// <summary>
    /// Focus leaving the transcript takes the whole selection with it, not just
    /// the anchor's part — the base class clears only the block that had focus,
    /// which left the rest highlighted with nothing able to copy it. The context
    /// menu opening is the one exception: that is how the selection gets copied.
    /// </summary>
    private void OnSelectionLostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (_selection is null || e.Source is not SelectableTextBlock block) return;
        if (e.NewFocusedElement is Visual next && this.IsVisualAncestorOf(next)) return;
        if (block.ContextFlyout?.IsOpen == true || block.ContextMenu?.IsOpen == true) return;

        EndSelectionDrag();
        foreach (var each in CollectBlocks(visibleOnly: false)) Clear(each);
        ForgetSelection();
    }

    /// <summary>A recycled row comes back with whatever text it is given next,
    /// and a selection left on it would highlight the same offsets of that.</summary>
    private void OnRowContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        _selectionBlocksStale = true;

        var dropped = false;
        foreach (var block in e.Container.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            if (ReferenceEquals(block, _selection?.Anchor)) dropped = true;
            if (block.SelectionStart != 0 || block.SelectionEnd != 0)
            {
                block.SetCurrentValue(SelectableTextBlock.SelectionStartProperty, 0);
                block.SetCurrentValue(SelectableTextBlock.SelectionEndProperty, 0);
            }
        }

        if (!dropped) return;
        EndSelectionDrag();
        ForgetSelection();
    }

    private void ForgetSelection()
    {
        _selection = null;
        (PART_Rows.ItemsPanelRoot as StableVirtualizingStackPanel)?.KeepRealized(null, null);
    }

    // ---- copying -----------------------------------------------------------

    /// <summary>Ctrl+C from any block in the selection copies all of it. Taken
    /// here rather than left to the focused block, whose own part can be empty —
    /// a drag that starts after the last character of a line — and which then
    /// declines to copy anything.</summary>
    private void OnSelectionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not SelectableTextBlock) return;

        var hotkeys = Application.Current?.PlatformSettings?.HotkeyConfiguration;
        if (hotkeys is null || !hotkeys.Copy.Any(gesture => gesture.Matches(e))) return;

        var text = SelectedText();
        if (text.Length == 0) return;

        e.Handled = true;
        _ = CopyAsync(text);
    }

    /// <summary>The context menu's Copy, and anything else that goes through
    /// <see cref="SelectableTextBlock.Copy"/>.</summary>
    private void OnCopyingToClipboard(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not SelectableTextBlock block) return;
        if (_selection is not { } selection || !selection.Blocks.Contains(block)) return;

        var text = SelectedText();
        if (text.Length == 0) return;

        e.Handled = true;
        _ = CopyAsync(text);
    }

    /// <summary>
    /// The highlighted text, read back from the blocks in reading order.
    ///
    /// Blocks inside the selection count even when they are empty, so an empty
    /// table cell still gets its tab and the columns line up when pasted.
    /// </summary>
    private string SelectedText()
    {
        if (_selection is not { First: >= 0 } selection) return string.Empty;

        var text = new StringBuilder();
        SelectableTextBlock? previous = null;
        for (var i = selection.First; i <= selection.Last && i < selection.Blocks.Count; i++)
        {
            var block = selection.Blocks[i];
            if (!block.IsAttachedToVisualTree()) continue;

            var start = Math.Min(block.SelectionStart, block.SelectionEnd);
            var end = Math.Max(block.SelectionStart, block.SelectionEnd);
            var inside = i > selection.First && i < selection.Last;
            if (end <= start && !inside) continue;

            if (previous is not null) text.Append(Separator(previous, block));
            AppendPlainText(text, block, start, end);
            previous = block;
        }

        return text.ToString();
    }

    /// <summary>
    /// What goes between two blocks when they are pasted as plain text: a tab
    /// between cells of one table row and a line break between its rows — what
    /// a spreadsheet reads back as a table — a line break between items of one
    /// list, and a blank line between everything else.
    /// </summary>
    private static string Separator(SelectableTextBlock previous, SelectableTextBlock next)
    {
        var table = previous.FindAncestorOfType<MarkdownTableView>();
        if (table is not null && ReferenceEquals(table, next.FindAncestorOfType<MarkdownTableView>()))
        {
            var sameRow = previous.GetVisualParent() is Control a
                          && next.GetVisualParent() is Control b
                          && Grid.GetRow(a) == Grid.GetRow(b);
            return sameRow ? "\t" : Environment.NewLine;
        }

        var list = previous.FindAncestorOfType<MarkdownListView>();
        if (list is not null && ReferenceEquals(list, next.FindAncestorOfType<MarkdownListView>()))
            return Environment.NewLine;

        return Environment.NewLine + Environment.NewLine;
    }

    /// <summary>
    /// A block's text between two positions, with what stands in for inline
    /// controls put back as text. The text source counts an inline formula or a
    /// source pill as one placeholder character; copied as-is, that placeholder
    /// is what a pasted sentence would have where its formula was.
    /// </summary>
    private static void AppendPlainText(StringBuilder target, SelectableTextBlock block, int start, int end)
    {
        if (block.Inlines is not { Count: > 0 } inlines)
        {
            var text = block.Text ?? string.Empty;
            start = Math.Clamp(start, 0, text.Length);
            end = Math.Clamp(end, start, text.Length);
            target.Append(text, start, end - start);
            return;
        }

        var position = 0;
        AppendInlines(target, inlines, start, end, ref position);
    }

    private static void AppendInlines(
        StringBuilder target, InlineCollection inlines, int start, int end, ref int position)
    {
        foreach (var inline in inlines)
        {
            if (position >= end) return;

            switch (inline)
            {
                case Run run:
                {
                    var text = run.Text ?? string.Empty;
                    var from = Math.Max(start, position);
                    var to = Math.Min(end, position + text.Length);
                    if (to > from) target.Append(text, from - position, to - from);
                    position += text.Length;
                    break;
                }

                case LineBreak:
                    if (position >= start) target.Append(Environment.NewLine);
                    position += Environment.NewLine.Length;
                    break;

                case InlineUIContainer container:
                    if (position >= start && container.Child is MathView { Latex: { Length: > 0 } latex })
                        target.Append('$').Append(latex).Append('$');
                    position += 1;
                    break;

                case Span span:
                    AppendInlines(target, span.Inlines, start, end, ref position);
                    break;
            }
        }
    }

    // ---- blocks ------------------------------------------------------------

    /// <summary>
    /// Every text block in the realized rows, in reading order: rows by index,
    /// and within a row in visual-tree order, which for a table is row by row.
    /// Hidden ones — a collapsed thinking block, a tool card's folded body — are
    /// left out of hit testing, or a selection would run through text nobody
    /// can see and copy it; they are still cleared with everything else.
    /// </summary>
    private List<SelectableTextBlock> CollectBlocks(bool visibleOnly)
    {
        if (visibleOnly) _selectionBlocksStale = false;
        if (PART_Rows.ItemsPanelRoot is not { } panel) return [];

        var rows = new List<(int Row, Control Container)>();
        foreach (var container in panel.Children)
        {
            var row = PART_Rows.IndexFromContainer(container);
            if (row >= 0) rows.Add((row, container));
        }
        rows.Sort((a, b) => a.Row.CompareTo(b.Row));

        var blocks = new List<SelectableTextBlock>();
        foreach (var (_, container) in rows)
        {
            foreach (var block in container.GetVisualDescendants().OfType<SelectableTextBlock>())
            {
                if (!visibleOnly || block.IsEffectivelyVisible) blocks.Add(block);
            }
        }

        return blocks;
    }

    private SelectableTextBlock? TranscriptBlockOf(object? source)
    {
        if ((source as Visual)?.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is not { } block)
            return null;

        return PART_Rows.IsVisualAncestorOf(block) ? block : null;
    }

    private static string TextOf(SelectableTextBlock block) =>
        block.Inlines is { Count: > 0 } inlines ? inlines.Text ?? string.Empty : block.Text ?? string.Empty;

    private static int TextLength(SelectableTextBlock block) => TextOf(block).Length;

    private static void SetRange(SelectableTextBlock block, int start, int end)
    {
        if (block.SelectionStart == start && block.SelectionEnd == end) return;
        block.SetCurrentValue(SelectableTextBlock.SelectionStartProperty, start);
        block.SetCurrentValue(SelectableTextBlock.SelectionEndProperty, end);
    }

    private static void Clear(SelectableTextBlock block)
    {
        if (block.SelectionStart != block.SelectionEnd) block.ClearSelection();
    }
}
