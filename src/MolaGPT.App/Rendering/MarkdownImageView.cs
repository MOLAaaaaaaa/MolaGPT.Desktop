using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MolaGPT.App.Rendering;

/// <summary>
/// A markdown image shared by standalone blocks and inline layouts, rendered
/// as a fixed-ratio card rather than as a naked <see cref="Image"/>.
///
/// The card exists because the image's dimensions are unknown until it has been
/// fetched. Letting the row size itself to the decoded bitmap means every image
/// that finishes loading reflows the transcript underneath the reader — the
/// worst thing a streaming answer can do. Reserving the space up front costs a
/// letterbox on unusually-shaped images and buys a layout that never jumps.
///
/// Ordinary images reserve 16:9 space; generated ones reserve a square.
/// The preferred minimum width yields to narrow inline and table layouts.
/// </summary>
public sealed class MarkdownImageView : TemplatedControl
{
    private const double CardMaxWidth = 640;
    private const double CardMinWidth = 240;
    private const double AiMaxSize = 480;
    private const double AspectRatio = 16d / 9d;

    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<MarkdownImageView, string?>(nameof(Url));

    public static readonly StyledProperty<string?> AltProperty =
        AvaloniaProperty.Register<MarkdownImageView, string?>(nameof(Alt));

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    public string? Alt
    {
        get => GetValue(AltProperty);
        set => SetValue(AltProperty, value);
    }

    private readonly Image _image;
    private readonly TextBlock _fallback;
    private readonly Border _card;
    private CancellationTokenSource? _load;
    private Bitmap? _bitmap;
    private TopLevel? _topLevel;
    private IDisposable? _resizeReload;
    private int _requestedDecodeWidth;
    private Point? _pressedPosition;

    public MarkdownImageView()
    {
        _image = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.HighQuality);

        _fallback = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16),
            FontSize = 12,
            Opacity = 0.65
        };

        _card = new Border
        {
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Panel { Children = { _image, _fallback } }
        }.WithRadius("Radius.Md");

        LogicalChildren.Add(_card);
        VisualChildren.Add(_card);

        _card.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(_card).Properties.IsLeftButtonPressed) return;
            _pressedPosition = e.GetPosition(_card);
            // An inline image must own the press before SelectableTextBlock captures it.
            e.Handled = true;
        };
        _card.PointerExited += (_, _) => _pressedPosition = null;
        _card.PointerReleased += OnCardReleased;
    }

    static MarkdownImageView()
    {
        UrlProperty.Changed.AddClassHandler<MarkdownImageView>((x, _) => x.Reload());
        AltProperty.Changed.AddClassHandler<MarkdownImageView>((x, _) => x.UpdateFallbackText());
        BackgroundProperty.Changed.AddClassHandler<MarkdownImageView>((x, e) =>
            x._card.Background = e.NewValue as IBrush);
        BorderBrushProperty.Changed.AddClassHandler<MarkdownImageView>((x, e) =>
            x._card.BorderBrush = e.NewValue as IBrush);
        ForegroundProperty.Changed.AddClassHandler<MarkdownImageView>((x, e) =>
            x._fallback.Foreground = e.NewValue as IBrush);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _requestedDecodeWidth = _bitmap?.PixelSize.Width ?? 0;
        InvalidateArrange();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _resizeReload?.Dispose();
        _resizeReload = null;
        _load?.Cancel();
        _load = null;
        _topLevel = null;
        _pressedPosition = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (width, height) = CardSize(availableSize.Width);
        _card.Width = width;
        _card.Height = height;
        _card.Measure(new Size(width, height));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (width, height) = CardSize(finalSize.Width);
        _card.Arrange(new Rect(0, 0, width, height));
        RequestBitmap();
        return new Size(width, height);
    }

    private (double Width, double Height) CardSize(double available)
    {
        if (double.IsNaN(available) || double.IsInfinity(available) || available <= 0)
            available = CardMaxWidth;

        if (IsGenerated(Url))
        {
            var size = Math.Min(available, Math.Max(CardMinWidth, Math.Min(AiMaxSize, available - 8)));
            return (size, size);
        }

        var width = Math.Min(available, Math.Max(CardMinWidth, Math.Min(CardMaxWidth, available - 8)));
        return (width, width / AspectRatio);
    }

    /// <summary>MolaGPT's own image generation returns these URLs; they are
    /// square, so a 16:9 card would letterbox every one of them.</summary>
    private static bool IsGenerated(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && (url.Contains("=imgtemp", StringComparison.OrdinalIgnoreCase)
            || url.Contains("imgtempdel", StringComparison.OrdinalIgnoreCase));

    private void UpdateFallbackText() =>
        _fallback.Text = Alt is { Length: > 0 } alt ? alt : Url;

    private void Reload()
    {
        _resizeReload?.Dispose();
        _resizeReload = null;
        _load?.Cancel();
        _load = null;
        _requestedDecodeWidth = 0;

        _image.IsVisible = false;
        _image.Source = null;
        _fallback.IsVisible = true;
        _bitmap = null;
        UpdateFallbackText();
        ToolTip.SetTip(_card, Url);
        InvalidateMeasure();
    }

    private int DecodeWidth()
    {
        if (_topLevel is null || this.TransformToVisual(_topLevel) is not { } transform) return 0;
        var scale = Math.Sqrt(transform.M11 * transform.M11 + transform.M12 * transform.M12);
        return (int)Math.Ceiling(_card.Bounds.Width * scale * _topLevel.RenderScaling);
    }

    private void RequestBitmap()
    {
        if (Url is not { Length: > 0 }) return;
        var width = DecodeWidth();
        if (width <= _requestedDecodeWidth) return;

        _resizeReload?.Dispose();
        var firstLoad = _requestedDecodeWidth == 0;
        _requestedDecodeWidth = width;
        if (firstLoad)
        {
            _resizeReload = null;
            LoadBitmap(width);
            return;
        }

        // Keep the current image while a resize settles, then decode only if it grew.
        _resizeReload = DispatcherTimer.RunOnce(() =>
        {
            _resizeReload = null;
            var needed = DecodeWidth();
            if (needed <= 0)
            {
                _requestedDecodeWidth = _bitmap?.PixelSize.Width ?? 0;
                return;
            }
            if (_bitmap is not null && _bitmap.PixelSize.Width >= needed)
                _requestedDecodeWidth = _bitmap.PixelSize.Width;
            else
                LoadBitmap(needed);
        }, TimeSpan.FromMilliseconds(150));
    }

    private async void LoadBitmap(int decodeWidth)
    {
        _load?.Cancel();

        if (Url is not { Length: > 0 } url) return;

        var cts = new CancellationTokenSource();
        _load = cts;
        _requestedDecodeWidth = decodeWidth;

        try
        {
            var bitmap = await ImageSourceLoader.LoadAsync(url, decodeWidth, cts.Token);
            if (cts.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested || !ReferenceEquals(_load, cts)) return;
                if (bitmap is null) return;

                _bitmap = bitmap;
                _image.Source = bitmap;
                _image.IsVisible = true;
                _fallback.IsVisible = false;
            });
        }
        catch (OperationCanceledException)
        {
            // Row recycled or URL changed mid-flight; nothing to report.
        }
        finally
        {
            if (ReferenceEquals(_load, cts)) _load = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// Click opens the image full size, in the same preview window the composer
    /// and the image workbench use.
    ///
    /// The card uses a smaller bitmap; the preview reloads the original image.
    /// </summary>
    private void OnCardReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        var pressed = _pressedPosition;
        _pressedPosition = null;
        if (pressed is null) return;
        e.Handled = true;
        var released = e.GetPosition(_card);
        if (Math.Abs(released.X - pressed.Value.X) > 4 || Math.Abs(released.Y - pressed.Value.Y) > 4) return;
        if (_bitmap is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        if (Url is not { Length: > 0 } url) return;
        var caption = Alt is { Length: > 0 } alt ? alt : null;
        _ = Views.ImagePreviewWindow.ShowAsync(owner, url, caption, _bitmap);
    }
}
