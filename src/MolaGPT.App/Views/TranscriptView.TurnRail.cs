using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MolaGPT.App.Rendering;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

/// <summary>
/// The turn rail: an outline of the conversation down the right edge, for
/// finding your way back to an earlier question in a long one.
///
/// Two things make it more than a list of buttons. The outline covers the whole
/// conversation, parked history included, so jumping to an early turn first has
/// the view model build the messages down to it. And the destination is a row
/// in a virtualizing panel whose position is an estimate until the rows on the
/// way have been measured, so the jump re-reads it every frame and settles once
/// layout stops revising it.
/// </summary>
public partial class TranscriptView
{
    /// <summary>Space between the rail's ends and the header above / composer below.</summary>
    private const double TurnRailTop = 72;
    private const double TurnRailBottomGap = 16;
    private const double TurnRailEdge = 10;
    private const double TurnPreviewGap = 8;

    /// <summary>
    /// How far below the scroller's top padding a row counts as "being read".
    /// The padding is where the floating header ends.
    /// </summary>
    private const double TurnReadingLine = 24;

    private IReadOnlyList<ChatTurn> _turns = [];
    private readonly Dictionary<MessageViewModel, int> _turnOfMessage = new();
    private bool _turnsQueued;

    /// <summary>
    /// The turn the user jumped to, held as the active tick until they scroll
    /// themselves. A jump near the end clamps at the bottom, where the reading
    /// line would otherwise name a later turn than the one they asked for.
    /// </summary>
    private int _pinnedTurn = -1;

    /// <summary>Destination of the running jump; null means the bottom.</summary>
    private MessageViewModel? _jumpTarget;

    /// <summary>Bumped by every jump and every user scroll, so a settle loop
    /// left over from an earlier jump stops writing offsets.</summary>
    private int _turnJumpVersion;

    private void InitializeTurnRail()
    {
        PART_TurnRail.HoverChanged += (_, index) => ShowTurnPreview(index);
        PART_TurnRail.TurnInvoked += (_, index) => JumpToTurn(index);
    }

    private void AttachTurns(ChatViewModel? previous, ChatViewModel? next)
    {
        if (previous is not null) previous.Messages.CollectionChanged -= OnMessagesChangedForTurns;
        if (next is not null) next.Messages.CollectionChanged += OnMessagesChangedForTurns;
        ReleaseTurnJump();
        RebuildTurns();
    }

    private void OnMessagesChangedForTurns(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_turnsQueued) return;
        _turnsQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _turnsQueued = false;
            RebuildTurns();
        }, DispatcherPriority.Background);
    }

    private void RebuildTurns()
    {
        _turns = _chat?.GetTurns() ?? [];
        _turnOfMessage.Clear();
        if (_chat is not null)
        {
            // Parked turns come first and have no view model; every materialized
            // message belongs to the latest turn at or before it.
            var next = _turns.Count(t => t.Message is null);
            var current = next - 1;
            foreach (var message in _chat.Messages)
            {
                if (next < _turns.Count && ReferenceEquals(_turns[next].Message, message)) current = next++;
                _turnOfMessage[message] = current;
            }
        }

        if (_pinnedTurn >= _turns.Count) _pinnedTurn = -1;
        PART_TurnRail.Count = _turns.Count;
        UpdateTurnRail();
    }

    /// <summary>Shows or hides the rail and moves its active tick to the turn being read.</summary>
    private void UpdateTurnRail()
    {
        var scrollable = _scroll is { IsVisible: true } scroll
            && scroll.Extent.Height - scroll.Viewport.Height > 1;
        var show = scrollable && _turns.Count > 0;
        if (PART_TurnRail.IsVisible != show)
        {
            PART_TurnRail.IsVisible = show;
            if (!show) PART_TurnPreview.IsVisible = false;
        }
        if (show) PART_TurnRail.ActiveIndex = ActiveTurn();
    }

    private int ActiveTurn()
    {
        if (_pinnedTurn >= 0) return _pinnedTurn;
        if (_scroll is null) return -1;
        if (IsNearBottom()) return _turns.Count - 1;

        var line = _scroll.Padding.Top + TurnReadingLine;
        foreach (var container in PART_Rows.GetRealizedContainers())
        {
            if (container.TranslatePoint(default, _scroll) is not { } top) continue;
            if (top.Y + container.Bounds.Height <= line) continue;
            return container.DataContext is TranscriptRow row
                   && _turnOfMessage.TryGetValue(row.Message, out var turn)
                ? turn
                : PART_TurnRail.ActiveIndex;
        }
        return PART_TurnRail.ActiveIndex;
    }

    private void ShowTurnPreview(int index)
    {
        if (index < 0 || index >= _turns.Count || !PART_TurnRail.IsVisible)
        {
            PART_TurnPreview.IsVisible = false;
            return;
        }

        var preview = _turns[index].Preview;
        PART_TurnPreviewOrdinal.Text = $"第 {index + 1} 轮";
        PART_TurnPreviewText.Text = preview.Length > 0 ? preview : "无文字内容";
        PART_TurnPreview.IsVisible = true;
        PART_TurnPreview.Measure(Size.Infinity);

        var height = PART_TurnPreview.DesiredSize.Height;
        var layer = PART_TurnPreviewLayer.Bounds.Height;
        var center = PART_TurnRail.TranslatePoint(new Point(0, PART_TurnRail.TickCenter(index)), PART_TurnPreviewLayer)?.Y
                     ?? layer / 2;
        var top = Math.Clamp(center - (height / 2), TurnPreviewGap, Math.Max(TurnPreviewGap, layer - height - TurnPreviewGap));
        Canvas.SetTop(PART_TurnPreview, Math.Round(top));
        Canvas.SetLeft(PART_TurnPreview, TurnRailEdge + PART_TurnRail.Width + TurnPreviewGap);
    }

    private void JumpToTurn(int index)
    {
        if (_chat is null || _scroll is null) return;
        PART_TurnPreview.IsVisible = false;

        // Before materializing: the Reset it publishes runs OnRowsChanged, which
        // would otherwise pull a following view back to the bottom.
        CancelWheelAnimation();
        _settleCts?.Cancel();
        _followBottom = false;

        if (_chat.MaterializeTurn(index) is not { } message) return;
        _pinnedTurn = index;
        PART_TurnRail.ActiveIndex = index;
        AnimateToMessage(message);
    }

    private void AnimateToMessage(MessageViewModel message)
    {
        if (_scroll is null || OffsetOf(message) is not { } target) return;

        _turnJumpVersion++;
        _jumpTarget = message;

        // Animating the whole way to a distant turn would realize and measure
        // every row in between, one frame at a time. Start a screen short of it
        // instead: the motion still says which way the turn lies.
        var from = _scroll.Offset.Y;
        var span = Math.Max(1, _scroll.Viewport.Height);
        if (Math.Abs(target - from) > 2 * span)
        {
            from = target - (Math.Sign(target - from) * span);
            SetScrollOffset(from);
        }

        _jumpFrom = _scroll.Offset.Y;
        _jumpStart = DateTime.UtcNow;
        _jumping = true;
        RequestJumpFrame();
    }

    /// <summary>
    /// The offset that puts <paramref name="message"/>'s first row where a
    /// conversation's first row sits: just under the header.
    /// </summary>
    private double? OffsetOf(MessageViewModel message)
    {
        if (_scroll is null || _rows is null
            || PART_Rows.ItemsPanelRoot is not StableVirtualizingStackPanel panel) return null;
        var row = _rows.FirstRowOf(message);
        if (row < 0) return null;
        var scrollable = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        return Math.Clamp(panel.TopOf(row), 0, scrollable);
    }

    /// <summary>
    /// Lands the jump exactly. Its last frames realize the rows around the turn,
    /// and their measured heights move the turn off the estimate the animation
    /// was aiming at.
    /// </summary>
    private async Task SettleOnMessageAsync(MessageViewModel message)
    {
        var version = _turnJumpVersion;
        var stable = 0;
        for (var attempt = 0; attempt < 12 && stable < 2; attempt++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (version != _turnJumpVersion || _jumping || _wheelAnimating || _followBottom
                || OffsetOf(message) is not { } target || _scroll is null) return;

            if (Math.Abs(_scroll.Offset.Y - target) <= 0.5)
            {
                stable++;
                continue;
            }

            stable = 0;
            SetScrollOffset(target);
            await Task.Delay(16).ConfigureAwait(true);
        }
    }

    /// <summary>The user is steering again: drop the pin and any settle in flight.</summary>
    private void ReleaseTurnJump()
    {
        _turnJumpVersion++;
        _pinnedTurn = -1;
    }
}
