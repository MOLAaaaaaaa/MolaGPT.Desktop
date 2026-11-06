using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MolaGPT.App.Views;

namespace MolaGPT.App.Selection;

public partial class SelectionResultWindow
{
    private SelectableTextBlock? _answerAnchor;
    private int _answerAnchorOffset;
    private bool _answerDragging;
    private List<SelectableTextBlock> _answerBlocks = [];
    private Point _answerPointer;
    private DispatcherTimer? _answerScrollTimer;

    private void HookAnswerSelection()
    {
        AnswerScroll.AddHandler(PointerPressedEvent, AnswerPressing, RoutingStrategies.Tunnel);
        AnswerScroll.AddHandler(PointerPressedEvent, AnswerPressed, RoutingStrategies.Bubble, true);
        AnswerScroll.AddHandler(PointerMovedEvent, AnswerMoved, RoutingStrategies.Tunnel);
        AnswerScroll.AddHandler(PointerReleasedEvent, AnswerReleased, RoutingStrategies.Bubble, true);
        AnswerScroll.AddHandler(KeyDownEvent, AnswerKeyDown, RoutingStrategies.Tunnel);
        AnswerScroll.AddHandler(SelectableTextBlock.CopyingToClipboardEvent, AnswerCopying, RoutingStrategies.Bubble);
    }

    private void AnswerPressing(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(AnswerScroll).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        foreach (var block in _answerBlocks) block.ClearSelection();
        _answerBlocks.Clear();
        _answerAnchor = null;
        _answerDragging = false;
        if ((e.Source as Visual)?.FindAncestorOfType<SelectableTextBlock>(true) is { } hit)
            TranscriptView.EnsureLayout(hit);
    }

    private void AnswerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 1 || e.GetCurrentPoint(AnswerScroll).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        if ((e.Source as Visual)?.FindAncestorOfType<SelectableTextBlock>(true) is not { } block
            || !ReferenceEquals(e.Pointer.Captured, block)) return;
        _answerAnchor = block;
        _answerAnchorOffset = block.SelectionStart;
        _answerDragging = true;
        _answerBlocks = AnswerTextBlocks();
        _followTail = false;
    }

    private List<SelectableTextBlock> AnswerTextBlocks() =>
        Answers.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.IsEffectivelyVisible).ToList();

    private void AnswerMoved(object? sender, PointerEventArgs e)
    {
        if (!_answerDragging || _answerAnchor is null) return;
        _answerPointer = e.GetPosition(AnswerScroll);
        UpdateAnswerSelection();
        if (_answerPointer.Y < 0 || _answerPointer.Y > AnswerScroll.Bounds.Height)
        {
            _answerScrollTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _answerScrollTimer.Tick -= ScrollAnswerSelection;
            _answerScrollTimer.Tick += ScrollAnswerSelection;
            _answerScrollTimer.Start();
        }
        else _answerScrollTimer?.Stop();
        e.Handled = true;
    }

    private void UpdateAnswerSelection()
    {
        if (_answerAnchor is null) return;
        var blocks = AnswerTextBlocks();
        var anchorIndex = blocks.IndexOf(_answerAnchor);
        if (anchorIndex < 0) { _answerDragging = false; return; }
        var point = _answerPointer;
        var nearest = -1;
        var distance = double.MaxValue;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].TranslatePoint(default, AnswerScroll) is not { } origin) continue;
            var bounds = new Rect(origin, blocks[i].Bounds.Size);
            var dx = Math.Max(0, Math.Max(bounds.Left - point.X, point.X - bounds.Right));
            var dy = Math.Max(0, Math.Max(bounds.Top - point.Y, point.Y - bounds.Bottom));
            var d = dx * dx + dy * dy;
            if (d < distance) { distance = d; nearest = i; }
        }
        if (nearest < 0) return;
        var target = blocks[nearest];
        TranscriptView.EnsureLayout(target);
        var local = AnswerScroll.TranslatePoint(point, target) ?? default;
        var length = AnswerText(target).Length;
        var offset = Math.Clamp(target.TextLayout.HitTestPoint(new Point(local.X - target.Padding.Left, local.Y - target.Padding.Top)).TextPosition, 0, length);
        var first = Math.Min(anchorIndex, nearest);
        var last = Math.Max(anchorIndex, nearest);
        foreach (var old in _answerBlocks.Except(blocks)) old.ClearSelection();
        for (var i = 0; i < blocks.Count; i++)
        {
            var start = i < first || i > last ? 0 : i == first ? (anchorIndex <= nearest ? _answerAnchorOffset : offset) : 0;
            var end = i < first || i > last ? 0 : i == last ? (anchorIndex <= nearest ? offset : _answerAnchorOffset) : AnswerText(blocks[i]).Length;
            if (blocks[i].SelectionStart != start) blocks[i].SetCurrentValue(SelectableTextBlock.SelectionStartProperty, start);
            if (blocks[i].SelectionEnd != end) blocks[i].SetCurrentValue(SelectableTextBlock.SelectionEndProperty, end);
        }
        _answerBlocks = blocks;
    }

    private void ScrollAnswerSelection(object? sender, EventArgs e)
    {
        if (!_answerDragging) { _answerScrollTimer?.Stop(); return; }
        var distance = _answerPointer.Y < 0 ? _answerPointer.Y : _answerPointer.Y - AnswerScroll.Bounds.Height;
        var max = Math.Max(0, AnswerScroll.Extent.Height - AnswerScroll.Viewport.Height);
        AnswerScroll.Offset = AnswerScroll.Offset.WithY(Math.Clamp(AnswerScroll.Offset.Y + Math.Clamp(distance * 0.25, -30, 30), 0, max));
        AnswerScroll.UpdateLayout();
        UpdateAnswerSelection();
    }

    private void AnswerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _answerDragging = false;
        _answerScrollTimer?.Stop();
    }

    private static string AnswerText(SelectableTextBlock block) =>
        block.Inlines is { Count: > 0 } inlines ? inlines.Text ?? string.Empty : block.Text ?? string.Empty;

    private string SelectedAnswerText()
    {
        var result = new StringBuilder();
        SelectableTextBlock? previous = null;
        foreach (var block in _answerBlocks)
        {
            var start = Math.Min(block.SelectionStart, block.SelectionEnd);
            var end = Math.Max(block.SelectionStart, block.SelectionEnd);
            if (start == end) continue;
            if (previous is not null) result.Append(TranscriptView.Separator(previous, block));
            TranscriptView.AppendPlainText(result, block, start, end);
            previous = block;
        }
        return result.ToString();
    }

    private void AnswerKeyDown(object? sender, KeyEventArgs e)
    {
        if (Application.Current?.PlatformSettings?.HotkeyConfiguration.Copy.Any(g => g.Matches(e)) != true) return;
        CopyAnswerSelection(e);
    }

    private void AnswerCopying(object? sender, RoutedEventArgs e) => CopyAnswerSelection(e);

    private async void CopyAnswerSelection(RoutedEventArgs e)
    {
        var text = SelectedAnswerText();
        if (text.Length == 0 || Clipboard is null) return;
        e.Handled = true;
        try { await Clipboard.SetTextAsync(text); }
        catch (Exception ex) { if (!_closing) ShowError(ex.Message, false); }
    }
}
