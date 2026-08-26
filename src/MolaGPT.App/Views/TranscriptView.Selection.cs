using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MolaGPT.App.Views;

public partial class TranscriptView
{
    private SelectableTextBlock? _selectionAnchor;
    private int _selectionAnchorOffset;
    private bool _selectionSpansBlocks;
    private string _selectedTextAcrossBlocks = string.Empty;

    private void HookBlockSpanningSelection()
    {
        AddHandler(PointerPressedEvent, OnSelectionPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnSelectionMoved, RoutingStrategies.Tunnel);
        AddHandler(
            SelectableTextBlock.CopyingToClipboardEvent,
            OnCopyingToClipboard,
            RoutingStrategies.Bubble);
    }

    private void OnSelectionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var anchor = (e.Source as Visual)?.FindAncestorOfType<SelectableTextBlock>(includeSelf: true);
        foreach (var block in SelectableBlocks())
            if (!ReferenceEquals(block, anchor)) block.ClearSelection();

        _selectionAnchor = anchor;
        _selectionAnchorOffset = anchor is null ? 0 : CharacterUnder(anchor, e.GetPosition(anchor));
        _selectionSpansBlocks = false;
        _selectedTextAcrossBlocks = string.Empty;
    }

    private void OnSelectionMoved(object? sender, PointerEventArgs e)
    {
        if (_selectionAnchor is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _selectionAnchor = null;
            return;
        }

        var blocks = SelectableBlocks();
        var anchorAt = blocks.IndexOf(_selectionAnchor);
        if (anchorAt < 0)
        {
            _selectionAnchor = null;
            return;
        }

        var focusAt = NearestBlock(blocks, e.GetPosition(this));
        if (focusAt < 0) return;

        if (focusAt == anchorAt)
        {
            if (!_selectionSpansBlocks) return;
            for (var i = 0; i < blocks.Count; i++)
                if (i != anchorAt) blocks[i].ClearSelection();
            _selectionSpansBlocks = false;
            _selectedTextAcrossBlocks = string.Empty;
            return;
        }

        _selectionSpansBlocks = true;
        var first = Math.Min(anchorAt, focusAt);
        var last = Math.Max(anchorAt, focusAt);
        var downward = focusAt > anchorAt;
        var focusOffset = CharacterUnder(blocks[focusAt], e.GetPosition(blocks[focusAt]));

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (i < first || i > last)
            {
                block.ClearSelection();
            }
            else if (i == anchorAt)
            {
                block.SelectionStart = _selectionAnchorOffset;
                block.SelectionEnd = downward ? TextLength(block) : 0;
            }
            else if (i == focusAt)
            {
                block.SelectionStart = downward ? 0 : TextLength(block);
                block.SelectionEnd = focusOffset;
            }
            else
            {
                block.SelectAll();
            }
        }

        _selectedTextAcrossBlocks = BuildSelectedText(
            blocks, anchorAt, focusAt, _selectionAnchorOffset, focusOffset);
        e.Handled = true;
    }

    private void OnCopyingToClipboard(object? sender, RoutedEventArgs e)
    {
        if (!_selectionSpansBlocks || _selectedTextAcrossBlocks.Length == 0) return;

        e.Handled = true;
        _ = CopyAsync(_selectedTextAcrossBlocks);
    }

    internal static string BuildSelectedText(
        IReadOnlyList<SelectableTextBlock> blocks,
        int anchorAt,
        int focusAt,
        int anchorOffset,
        int focusOffset)
    {
        var first = Math.Min(anchorAt, focusAt);
        var last = Math.Max(anchorAt, focusAt);
        var downward = focusAt > anchorAt;
        var parts = new List<string>();

        for (var i = first; i <= last; i++)
        {
            var text = TextOf(blocks[i]);
            var start = 0;
            var end = text.Length;

            if (downward)
            {
                if (i == anchorAt) start = anchorOffset;
                if (i == focusAt) end = focusOffset;
            }
            else
            {
                if (i == focusAt) start = focusOffset;
                if (i == anchorAt) end = anchorOffset;
            }

            start = Math.Clamp(start, 0, text.Length);
            end = Math.Clamp(end, start, text.Length);
            if (end > start) parts.Add(text[start..end]);
        }

        return string.Join("\n\n", parts);
    }

    private List<SelectableTextBlock> SelectableBlocks()
    {
        if (PART_Rows.ItemsPanelRoot is not { } panel) return [];

        var found = new List<(int Row, int Depth, SelectableTextBlock Block)>();
        foreach (var container in panel.Children.OfType<Control>())
        {
            var row = PART_Rows.IndexFromContainer(container);
            if (row < 0) continue;

            var depth = 0;
            foreach (var block in container.GetVisualDescendants().OfType<SelectableTextBlock>())
                found.Add((row, depth++, block));
        }

        return found
            .OrderBy(x => x.Row)
            .ThenBy(x => x.Depth)
            .Select(x => x.Block)
            .ToList();
    }

    private int NearestBlock(List<SelectableTextBlock> blocks, Point point)
    {
        var best = -1;
        var bestDistance = double.MaxValue;

        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].TranslatePoint(default, this) is not { } origin) continue;

            var top = origin.Y;
            var bottom = top + blocks[i].Bounds.Height;
            var distance = point.Y < top
                ? top - point.Y
                : point.Y > bottom
                    ? point.Y - bottom
                    : 0;

            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }

        return best;
    }

    private static string TextOf(SelectableTextBlock block) =>
        block.Inlines?.Text ?? block.Text ?? string.Empty;

    private static int TextLength(SelectableTextBlock block) => TextOf(block).Length;

    private static int CharacterUnder(SelectableTextBlock block, Point point)
    {
        var length = TextLength(block);
        if (length == 0) return 0;

        var local = point - new Point(block.Padding.Left, block.Padding.Top);
        var hit = block.TextLayout.HitTestPoint(local);
        return Math.Clamp(hit.TextPosition + (hit.IsTrailing ? 1 : 0), 0, length);
    }
}
