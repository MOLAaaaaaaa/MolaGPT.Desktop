using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MolaGPT.App.Rendering;
using MolaGPT.Storage;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class TranscriptView : UserControl
{
    private const double WheelLinePixels = 32;
    private const double ScrollSpringFrequency = 20;
    private const double ScrollSettleEpsilon = 0.75;
    private const double ScrollSettleVelocity = 40;
    private const double ScrollMaxFrameSeconds = 0.05;
    private const double BottomStickTolerance = 48;
    private const double OlderLoadTrigger = 240;
    private const int OlderLoadBatch = 20;
    private const double JumpSeconds = 0.42;

    private ChatViewModel? _chat;
    private TranscriptSource? _rows;
    private ScrollViewer? _scroll;

    /// <summary>
    /// Whether new content should pull the viewport down.
    ///
    /// This is the flag three earlier WPF attempts got wrong, so it is worth
    /// being explicit: it is owned by the user, not by the renderer. Anything
    /// that scrolls on the user's behalf must check it *before* moving, not
    /// after — a jump that fires first and asks later is exactly what made the
    /// old list snap back to the bottom while someone was reading.
    /// </summary>
    private bool _followBottom = true;

    /// <summary>Offset this view asked for. A ScrollChanged carrying it is our
    /// own move, and must not be mistaken for the user scrolling away.</summary>
    private double? _expectedOffset;

    private CancellationTokenSource? _settleCts;
    private bool _wheelAnimating;
    private bool _wheelFrameRequested;
    private double _wheelCurrent;
    private double _wheelVelocity;
    private double _wheelTarget;
    private DateTime _wheelLastFrame;
    private bool _olderLoadQueued;
    private bool _loadingOlder;
    private bool _jumping;
    private bool _jumpFrameRequested;
    private double _jumpFrom;
    private DateTime _jumpStart;

    public TranscriptView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Attach(DataContext as ChatViewModel);

        // Tunnel: kill follow the instant the wheel turns upward, before layout
        // has a chance to settle and re-assert the bottom.
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        HookBlockSpanningSelection();

        _scroll = PART_Scroll;
        _scroll.ScrollChanged += OnScrollChanged;
    }


    // ---- wiring ------------------------------------------------------------

    private void Attach(ChatViewModel? chat)
    {
        CancelWheelAnimation();
        if (_chat is not null) _chat.PropertyChanged -= OnChatPropertyChanged;
        _rows?.Dispose();
        _rows = null;

        _chat = chat;
        if (_chat is null)
        {
            PART_Rows.ItemsSource = null;
            return;
        }

        _chat.PropertyChanged += OnChatPropertyChanged;
        _rows = new TranscriptSource(_chat);
        _rows.CollectionChanged += (_, _) => OnRowsChanged();
        PART_Rows.ItemsSource = _rows;
        PART_Hints.ItemsSource = _chat.HintChips;

        UpdateWelcome();
        RequestScrollToEnd(force: true);
        QueueMaybeLoadOlderMessages();
    }

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChatViewModel.ConversationId):
                // A different conversation always starts pinned to the newest
                // message, whatever the user was doing in the previous one.
                _followBottom = true;
                RequestScrollToEnd(force: true);
                break;
            case nameof(ChatViewModel.IsEmpty):
            case nameof(ChatViewModel.IsConversationLoading):
                UpdateWelcome();
                break;
        }
    }

    private void UpdateWelcome()
    {
        // Loading counts as not-empty. Opening a conversation clears Messages and
        // then awaits the database read, so for those frames the chat really is
        // empty — and the welcome page, personas and hint chips flashed up
        // between the old conversation and the new one on every switch.
        var loading = _chat?.IsConversationLoading ?? false;
        var empty = (_chat?.IsEmpty ?? true) && !loading;

        PART_Welcome.IsVisible = empty;
        PART_LoadingPill.IsVisible = loading;

        // The whole scroller goes down, not just the rows inside it. Hiding only
        // the ItemsControl left the ScrollViewer up with the extent the
        // virtualizing panel had estimated for the conversation that just went
        // away, so a brand-new chat opened with a scrollbar down its right edge.
        PART_Rows.IsVisible = !empty;
        PART_Scroll.IsVisible = !empty;

        if (empty)
        {
            // Assigned in exactly one other place — ScrollChanged — which does
            // not necessarily run when the rows are simply taken away. Left
            // alone, "回到最新" stayed on screen over the welcome page, pointing
            // at a conversation that is no longer open.
            PART_JumpLatest.IsVisible = false;
            CancelWheelAnimation();
            _followBottom = true;
            _expectedOffset = null;
            if (_scroll is not null) _scroll.Offset = default;
        }

        // Personas only exist for BYOK; a MolaGPT-account conversation gets its
        // system prompt from the server, so the row is hidden rather than shown
        // doing nothing.
        var personas = _chat?.Personas?.Personas;
        var offer = empty && _chat?.IsBYOKActive == true && personas is { Count: > 0 };
        PART_PersonaPick.IsVisible = offer;
        PART_Personas.ItemsSource = offer ? personas : null;

    }

    private void OnPersonaCard(object? sender, RoutedEventArgs e)
    {
        if (_chat is null) return;
        if (sender is Control { Tag: string id } && id.Length > 0) _chat.SaveActivePersona(id);
    }

    private void OnRowsChanged()
    {
        UpdateWelcome();
        if (_followBottom) RequestScrollToEnd(force: false);
        QueueMaybeLoadOlderMessages();
    }

    // ---- scrolling ---------------------------------------------------------

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_scroll is null || e.Delta.Y == 0) return;

        if (_jumping) CancelWheelAnimation();

        var scrollable = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        if (scrollable <= 0) return;

        var origin = _wheelAnimating ? _wheelTarget : _scroll.Offset.Y;
        var next = Math.Clamp(origin - (e.Delta.Y * WheelNotchDistance), 0, scrollable);

        _followBottom = scrollable - next <= BottomStickTolerance;
        AnimateWheelTo(next);
        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsScrollBarPart(e.Source))
        {
            CancelWheelAnimation();
            _followBottom = false;
            return;
        }

    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll is null) return;

        var atBottom = IsNearBottom();
        PART_JumpLatest.IsVisible = !atBottom && (_rows?.Count ?? 0) > 0;

        if (_loadingOlder) return;

        // ScrollViewer can clamp an offset against an extent that is still
        // changing, so an animation's landed value does not always equal the
        // value it requested. While our animation owns the viewport, its
        // ScrollChanged events are not user input.
        if (_wheelAnimating || _jumping)
        {
            _expectedOffset = null;
            return;
        }

        // Our own scroll: consume the expectation and leave the flag alone.
        if (_expectedOffset is { } expected && Math.Abs(_scroll.Offset.Y - expected) < 1.5)
        {
            _expectedOffset = null;
            return;
        }

        _expectedOffset = null;

        // Extent moving under a still viewport is content growing, not the user.
        if (Math.Abs(e.ExtentDelta.Y) > 0.5 && Math.Abs(e.OffsetDelta.Y) < 0.5)
        {
            // …and if we are following, that growth is below the fold, so chase
            // it. This cannot be left to OnRowsChanged: a thinking block or a
            // tool card grows *inside* a row whose key is position-only, so no
            // row is inserted and no collection change is raised. Prose only
            // followed because its rows are keyed by a hash of their own text
            // and therefore get swapped on every delta — following a reasoning
            // model worked or not depending on which kind of row happened to be
            // at the bottom.
            if (_followBottom) PinToBottom();
            return;
        }

        if (Math.Abs(e.OffsetDelta.Y) > 0.5)
        {
            CancelWheelAnimation();
            _followBottom = atBottom;
        }
        else if (atBottom)
        {
            _followBottom = true;
        }

        QueueMaybeLoadOlderMessages();
    }

    private void QueueMaybeLoadOlderMessages()
    {
        if (_olderLoadQueued || _loadingOlder) return;
        _olderLoadQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _olderLoadQueued = false;
            _ = MaybeLoadOlderMessagesAsync();
        }, DispatcherPriority.Loaded);
    }

    private async Task MaybeLoadOlderMessagesAsync()
    {
        if (_loadingOlder || _chat is not { HasOlderMessages: true } || _scroll is null) return;

        var scrollable = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        if (scrollable > 0 && _scroll.Offset.Y > OlderLoadTrigger) return;

        _loadingOlder = true;
        try
        {
            if (_chat.LoadOlderMessages(OlderLoadBatch) <= 0) return;

            // StableVirtualizingStackPanel registers the first visible row as a
            // ScrollViewer anchor, so prepending keeps that row in place while
            // the newly inserted history is measured.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        }
        finally
        {
            _loadingOlder = false;
        }

        if (_chat.HasOlderMessages)
        {
            scrollable = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
            if (scrollable <= 0 || _scroll.Offset.Y <= OlderLoadTrigger)
                QueueMaybeLoadOlderMessages();
        }
    }

    private bool IsNearBottom()
    {
        if (_scroll is null) return true;
        var remaining = _scroll.Extent.Height - _scroll.Viewport.Height - _scroll.Offset.Y;
        return remaining <= BottomStickTolerance;
    }

    private double WheelNotchDistance
    {
        get
        {
            _ = SystemParametersInfoW(0x0068, 0, out var lines, 0);
            return lines > 0 ? lines * WheelLinePixels : _scroll?.Viewport.Height ?? 0;
        }
    }

    private void AnimateWheelTo(double target)
    {
        _wheelTarget = target;
        if (_wheelAnimating) return;

        _wheelCurrent = _scroll?.Offset.Y ?? target;
        _wheelVelocity = 0;
        _wheelLastFrame = DateTime.UtcNow;
        _wheelAnimating = true;
        RequestWheelFrame();
    }

    private void RequestWheelFrame()
    {
        if (_wheelFrameRequested || !_wheelAnimating) return;
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            CancelWheelAnimation();
            return;
        }

        _wheelFrameRequested = true;
        topLevel.RequestAnimationFrame(AnimateWheelFrame);
    }

    private void AnimateWheelFrame(TimeSpan _)
    {
        _wheelFrameRequested = false;
        if (!_wheelAnimating || _scroll is null) return;

        var now = DateTime.UtcNow;
        var dt = Math.Clamp((now - _wheelLastFrame).TotalSeconds, 0, ScrollMaxFrameSeconds);
        _wheelLastFrame = now;

        var scrollable = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        var target = Math.Clamp(_wheelTarget, 0, scrollable);
        var clamped = Math.Clamp(_wheelCurrent, 0, scrollable);
        if (clamped != _wheelCurrent)
        {
            _wheelCurrent = clamped;
            _wheelVelocity = 0;
        }

        var offset = _wheelCurrent - target;
        if (Math.Abs(offset) <= ScrollSettleEpsilon
            && Math.Abs(_wheelVelocity) <= ScrollSettleVelocity)
        {
            SetScrollOffset(target);
            CancelWheelAnimation();
            return;
        }

        var decay = Math.Exp(-ScrollSpringFrequency * dt);
        var a = _wheelVelocity + (ScrollSpringFrequency * offset);
        _wheelCurrent = target + ((offset + (a * dt)) * decay);
        _wheelVelocity = (_wheelVelocity - (ScrollSpringFrequency * a * dt)) * decay;

        SetScrollOffset(_wheelCurrent);
        RequestWheelFrame();
    }

    private void SetScrollOffset(double offset)
    {
        if (_scroll is null) return;
        _expectedOffset = offset;
        _scroll.Offset = _scroll.Offset.WithY(offset);
    }

    private void CancelWheelAnimation()
    {
        _wheelAnimating = false;
        _wheelVelocity = 0;
        _jumping = false;
    }

    private static bool IsScrollBarPart(object? source) =>
        source is Visual visual
        && visual.GetSelfAndVisualAncestors().Any(x => x is Thumb or RepeatButton);

    private void OnJumpToLatest(object? sender, RoutedEventArgs e) => AnimateToBottom();

    private void AnimateToBottom()
    {
        if (_scroll is null) return;

        CancelWheelAnimation();

        var bottom = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        if (bottom - _scroll.Offset.Y <= 1)
        {
            _followBottom = true;
            return;
        }

        _jumpFrom = _scroll.Offset.Y;
        _jumpStart = DateTime.UtcNow;
        _jumping = true;
        RequestJumpFrame();
    }

    private void RequestJumpFrame()
    {
        if (_jumpFrameRequested || !_jumping) return;
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            _jumping = false;
            return;
        }

        _jumpFrameRequested = true;
        topLevel.RequestAnimationFrame(AnimateJumpFrame);
    }

    private void AnimateJumpFrame(TimeSpan _)
    {
        _jumpFrameRequested = false;
        if (!_jumping || _scroll is null) return;

        // The virtualizing panel revises its estimated extent while rows are
        // realized. Re-read the destination every frame, but keep a fixed time
        // curve so a moving target cannot make the animation settle early.
        var bottom = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        var progress = Math.Clamp(
            (DateTime.UtcNow - _jumpStart).TotalSeconds / JumpSeconds, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);

        SetScrollOffset(_jumpFrom + ((bottom - _jumpFrom) * eased));

        if (progress >= 1)
        {
            _jumping = false;
            _followBottom = true;
            RequestScrollToEnd(force: true);
            return;
        }

        RequestJumpFrame();
    }

    /// <summary>
    /// Corrects back to the bottom, twice: once now, and once after the layout
    /// pass that triggered it has finished.
    ///
    /// Both are needed. The immediate write keeps the viewport pinned frame by
    /// frame, but ScrollViewer clamps it against the extent as it stands at that
    /// instant, and the same pass can still grow the content further — which
    /// left the transcript settling a row or two short of the bottom.
    ///
    /// Deliberately not <see cref="RequestScrollToEnd"/>: this runs from inside
    /// a ScrollChanged that fires again for the next growth, so the settling
    /// loop's job is already being done by the event stream. Starting one here
    /// would cancel and restart a task per delta for no benefit.
    /// </summary>
    private void PinToBottom()
    {
        Pin();

        if (_pinQueued) return;
        _pinQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _pinQueued = false;
                if (_followBottom) Pin();
            },
            DispatcherPriority.Loaded);

        void Pin()
        {
            if (_scroll is null) return;

            var target = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
            if (Math.Abs(_scroll.Offset.Y - target) <= 0.5) return;

            _expectedOffset = target;
            _scroll.Offset = _scroll.Offset.WithY(target);
        }
    }

    private bool _pinQueued;

    private void RequestScrollToEnd(bool force)
    {
        if (!force && !_followBottom) return;

        CancelWheelAnimation();
        _settleCts?.Cancel();
        _settleCts = new CancellationTokenSource();
        _ = ScrollToEndConvergentAsync(_settleCts.Token);
    }

    /// <summary>
    /// Scrolls to the end and keeps re-checking until the extent stops moving.
    ///
    /// One pass is not enough: rows realize over several frames and each batch
    /// revises the estimated extent, so a single jump lands short. The loop
    /// re-reads <see cref="_followBottom"/> every iteration, which is what lets
    /// the user interrupt a still-settling conversation by scrolling up — the
    /// behaviour that was missing before.
    /// </summary>
    private async Task ScrollToEndConvergentAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 16 && !ct.IsCancellationRequested; attempt++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (ct.IsCancellationRequested || !_followBottom || _scroll is null) return;

            var extentBefore = _scroll.Extent.Height;
            var target = Math.Max(0, extentBefore - _scroll.Viewport.Height);

            if (Math.Abs(_scroll.Offset.Y - target) > 0.5)
            {
                _expectedOffset = target;
                _scroll.Offset = _scroll.Offset.WithY(target);
            }

            await Task.Delay(16, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested || _scroll is null) return;

            // Converged: nothing new realized, so the bottom we found is real.
            if (Math.Abs(_scroll.Extent.Height - extentBefore) < 1) return;
        }
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(
        uint action,
        uint parameter,
        out int value,
        uint update);

    // ---- row actions -------------------------------------------------------

    /// <summary>Clicking a suggestion drops it into the composer, as in the
    /// WPF WelcomeView.HintChip_Click.</summary>
    private void OnHintChip(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string text } && text.Length > 0)
            HintChosen?.Invoke(this, text);
    }

    public event EventHandler<string>? HintChosen;

    /// <summary>Raised for actions the composer owns rather than the row.</summary>
    public event EventHandler<MessageViewModel>? RetryRequested;

    /// <summary>Raised for the response-stats popover.</summary>
    public event EventHandler<MessageViewModel>? StatsRequested;

    private void OnRetry(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TranscriptRow row })
            RetryRequested?.Invoke(this, row.Message);
    }

    /// <summary>
    /// Response stats, shown as a flyout on the button itself. The WPF version
    /// opened a popup with the same three numbers; there is no view model
    /// command behind it, so the view assembles the text.
    /// </summary>
    private void OnShowStats(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.DataContext is not TranscriptRow row) return;

        var message = row.Message;
        var lines = new List<string>();
        if (message.ModelLabel is { Length: > 0 } model) lines.Add(model);
        if (message.Usage is { } usage)
        {
            if (usage.PromptTokens is { } prompt) lines.Add($"输入 {prompt:N0} tokens");
            if (usage.CompletionTokens is { } completion) lines.Add($"输出 {completion:N0} tokens");
            if (usage.TotalTokens is { } total) lines.Add($"合计 {total:N0} tokens");
        }
        if (lines.Count == 0) lines.Add("无统计数据");

        FlyoutBase.SetAttachedFlyout(button, new Flyout
        {
            Placement = PlacementMode.Top,
            Content = new TextBlock
            {
                Text = string.Join('\n', lines),
                FontSize = 12.5,
                LineHeight = 20
            }
        });
        FlyoutBase.ShowAttachedFlyout(button);
        StatsRequested?.Invoke(this, message);
    }

    /// <summary>
    /// Opens a citation in the system browser. Links in a transcript come from
    /// the model, so they are opened rather than navigated in-app: nothing here
    /// should render remote content inside the window.
    /// </summary>
    private void OnOpenSource(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string url } || url.Length == 0) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https")) return;

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch
        {
            // No default browser, or the shell refused; not worth a dialog.
        }
    }

    /// <summary>The one-tap fix on a recoverable failure. The only action the
    /// view model raises today is "switch model", which is the header's job.</summary>
    private void OnErrorAction(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TranscriptRow row })
            ErrorActionRequested?.Invoke(this, row.Message);
    }

    public event EventHandler<MessageViewModel>? ErrorActionRequested;

    private async void OnCopyCode(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string code }) await CopyAsync(code);
    }

    private async void OnCopyMessage(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TranscriptRow row })
            await CopyAsync(row.Message.Content);
    }

    private async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    /// <summary>Lets the shell hand over the store the sent-attachment chips need
    /// to re-read images after a reload. Same pattern as the header's providers.</summary>
    public void AttachAttachmentStore(AttachmentStore store) => _attachments = store;

    private AttachmentStore? _attachments;

    /// <summary>
    /// Opens a sent image full size, preferring whichever copy costs least.
    ///
    /// The three sources are not interchangeable: <c>Bytes</c> is only populated
    /// for the turn just sent, <c>LocalName</c> only for BYOK attachments the
    /// store kept, and <c>ThumbnailUrl</c> only for MolaGPT-account images. A
    /// reloaded conversation has exactly one of the last two, so all three
    /// branches are load-bearing.
    /// </summary>
    private void OnSentAttachment(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: AttachmentChip chip }) return;
        if (!chip.HasInlinePreview) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        if (chip.Bytes is { Length: > 0 } inline)
        {
            _ = ImagePreviewWindow.ShowAsync(owner, inline, chip.FileName);
            return;
        }

        if (chip.LocalName is { Length: > 0 } local
            && _attachments?.Load(local) is { Length: > 0 } stored)
        {
            _ = ImagePreviewWindow.ShowAsync(owner, stored, chip.FileName);
            return;
        }

        if (chip.ThumbnailUrl is { Length: > 0 } url)
            _ = ImagePreviewWindow.ShowAsync(owner, url, chip.FileName);
    }
}
