using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Reactive;
using Avalonia.Threading;
using Avalonia.VisualTree;      // FindAncestorOfType

namespace MolaGPT.App.Views;

public partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();

        PART_ToggleTheme.Click += (_, _) => ThemeToggleRequested?.Invoke(this, EventArgs.Empty);
        PART_Minimize.Click += (_, _) => WithWindow(window => window.WindowState = WindowState.Minimized);
        PART_Maximize.Click += (_, _) => WithWindow(ToggleMaximize);
        // The tray host decides whether this means exit, hide or ask first.
        PART_Close.Click += (_, _) =>
        {
            if (CloseRequested is { } handler) handler(this, EventArgs.Empty);
            else WithWindow(w => w.Close());
        };

        PART_ModeChat.Click += (_, _) => ModeRequested?.Invoke(this, "chat");
        PART_ModeWork.Click += (_, _) => ModeRequested?.Invoke(this, "work");

        PART_Settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        PART_About.Click += (_, _) => AboutRequested?.Invoke(this, EventArgs.Empty);
        PART_Login.Click += (_, _) => LoginRequested?.Invoke(this, EventArgs.Empty);
        PART_AgentStatus.Click += (_, _) => AgentStatusRequested?.Invoke(this, EventArgs.Empty);

        AddHandler(PointerPressedEvent, OnPointerPressed, handledEventsToo: false);

        // SetMode is usually called before the first arrange, when the segments
        // still measure zero and there is nowhere to park the capsule. Watching
        // the bounds also covers the font-scale setting and a window resize.
        PART_ModeChat.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver<Rect>(_ => SyncModeThumb()));
        PART_ModeWork.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver<Rect>(_ => SyncModeThumb()));

        // The glyph has to follow the window, not the button. Aero Snap, a drag
        // to the top edge, Win+Up and a double-click on the caption all change
        // WindowState without going through PART_Maximize, and updating it only
        // in the click handler left the icon lying about the current state.
        AttachedToVisualTree += (_, _) => TrackWindowState();
        DetachedFromVisualTree += (_, _) => UntrackWindowState();
    }

    private Window? _tracked;

    private void TrackWindowState()
    {
        UntrackWindowState();
        if (TopLevel.GetTopLevel(this) is not Window window) return;

        _tracked = window;
        _tracked.PropertyChanged += OnWindowPropertyChanged;
        SyncMaximizeGlyph(window.WindowState);
    }

    private void UntrackWindowState()
    {
        if (_tracked is null) return;
        _tracked.PropertyChanged -= OnWindowPropertyChanged;
        _tracked = null;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && e.NewValue is WindowState state)
            SyncMaximizeGlyph(state);
    }

    private void SyncMaximizeGlyph(WindowState state)
    {
        var maximized = state == WindowState.Maximized;
        PART_MaximizeGlyph.Text = maximized ? "" : "";
        ToolTip.SetTip(PART_Maximize, maximized ? "还原" : "最大化");
    }

    /// <summary>Raised so the tray host can apply the configured close behavior.
    /// Unsubscribed, the button closes the window directly.</summary>
    public event EventHandler? CloseRequested;

    public event EventHandler<string>? ModeRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? AboutRequested;
    public event EventHandler? LoginRequested;
    public event EventHandler? AgentStatusRequested;
    public event EventHandler? ThemeToggleRequested;

    /// <summary>
    /// Highlights the active segment. "Work" stays lit for BYOK too — the
    /// account-vs-own-key split is chosen in the model selector, not here, which
    /// is the same rule the WPF DataTriggers encoded.
    /// </summary>
    public void SetMode(bool chatActive, bool workActive)
    {
        PART_ModeChat.Classes.Set("active", chatActive);
        PART_ModeWork.Classes.Set("active", workActive);
        SyncModeThumb();
    }

    private bool _thumbPlaced;

    /// <summary>
    /// Parks the capsule over whichever segment is active.
    /// </summary>
    /// <remarks>
    /// Driven from code because the two segments are different widths and the
    /// width is whatever the text measures to — there is no fixed geometry a
    /// style could name. The first placement is deliberately not animated: with
    /// transitions already attached the capsule would slide in from the left
    /// edge of the track every time the title bar is realized.
    /// </remarks>
    private void SyncModeThumb()
    {
        var target =
            PART_ModeChat.Classes.Contains("active") ? PART_ModeChat :
            PART_ModeWork.Classes.Contains("active") ? PART_ModeWork : null;

        if (target is null)
        {
            PART_ModeThumb.IsVisible = false;
            return;
        }

        var bounds = target.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;   // before first arrange

        if (!_thumbPlaced) PART_ModeThumb.Transitions = null;

        PART_ModeThumb.IsVisible = true;
        PART_ModeThumb.Width = bounds.Width;
        // Height is left to VerticalAlignment=Stretch. Assigning it too would
        // put the capsule back into the Panel's desired-size calculation.
        PART_ModeThumb.RenderTransform =
            TransformOperations.Parse($"translateX({bounds.X.ToString("0.##", CultureInfo.InvariantCulture)}px)");

        if (_thumbPlaced) return;
        _thumbPlaced = true;
        // Restored on the next layout pass so the placement above lands first.
        Dispatcher.UIThread.Post(() => PART_ModeThumb.Transitions = ThumbTransitions(),
            DispatcherPriority.Loaded);
    }

    private static Transitions ThumbTransitions() =>
    [
        new TransformOperationsTransition
        {
            Property = RenderTransformProperty,
            Duration = TimeSpan.FromMilliseconds(260),
            Easing = new CubicEaseOut()
        },
        new DoubleTransition
        {
            Property = WidthProperty,
            Duration = TimeSpan.FromMilliseconds(260),
            Easing = new CubicEaseOut()
        }
    ];

    public void SetAccountState(bool loggedIn, string? username)
    {
        var tip = loggedIn
            ? $"账户：{(string.IsNullOrWhiteSpace(username) ? "MolaGPT 用户" : username)}"
            : "登录 / 账户";
        ToolTip.SetTip(PART_Login, tip);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // A press that lands on a control must not also drag the window.
        if (e.Source is not Control source) return;
        if (source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            WithWindow(ToggleMaximize);
            e.Handled = true;
            return;
        }

        WithWindow(w => w.BeginMoveDrag(e));
    }

    private void ToggleMaximize(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void WithWindow(Action<Window> action)
    {
        if (TopLevel.GetTopLevel(this) is Window window) action(window);
    }
}
