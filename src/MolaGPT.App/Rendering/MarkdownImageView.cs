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
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Panel { Children = { _image, _fallback } }
        };

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
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (Url is { Length: > 0 }
            && (_bitmap is null && _load is null
                || scale > 1.5 && (_bitmap?.PixelSize.Width ?? 0) < CardSize(Bounds.Width).Width * scale))
            Reload();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = null;
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

    private async void Reload()
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = null;

        _image.IsVisible = false;
        _image.Source = null;
        _fallback.IsVisible = true;
        _bitmap = null;
        UpdateFallbackText();
        ToolTip.SetTip(_card, Url);

        if (Url is not { Length: > 0 } url) return;

        var cts = new CancellationTokenSource();
        _load = cts;

        try
        {
            var scale = Math.Max(1.5, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
            var decodeWidth = (int)Math.Ceiling(CardSize(Bounds.Width).Width * scale);
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
