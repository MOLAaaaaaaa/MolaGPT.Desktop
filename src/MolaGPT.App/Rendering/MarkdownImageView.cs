using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace MolaGPT.App.Rendering;

/// <summary>
/// A standalone markdown image, rendered as the fixed-ratio card the WPF build
/// used rather than as a naked <see cref="Image"/>.
///
/// The card exists because the image's dimensions are unknown until it has been
/// fetched. Letting the row size itself to the decoded bitmap means every image
/// that finishes loading reflows the transcript underneath the reader — the
/// worst thing a streaming answer can do. Reserving the space up front costs a
/// letterbox on unusually-shaped images and buys a layout that never jumps.
///
/// Geometry is carried over verbatim: 16:9 clamped to 240–640 for ordinary
/// images, square clamped to 240–480 for MolaGPT's own generated ones, radius 12,
/// Bg.Tertiary behind, left-aligned.
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

    public MarkdownImageView()
    {
        _image = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Opacity = 0,
            Transitions =
            [
                new Avalonia.Animation.DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(180)
                }
            ]
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
            var size = Math.Max(CardMinWidth, Math.Min(AiMaxSize, available - 8));
            return (size, size);
        }

        var width = Math.Max(CardMinWidth, Math.Min(CardMaxWidth, available - 8));
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
        _image.Opacity = 0;
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
            // 1.5× the layout width so the card still looks sharp on a 150% DPI
            // display, which is the common Windows default.
            var decodeWidth = (int)Math.Ceiling(CardSize(Bounds.Width).Width * 1.5);
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
                _image.Opacity = 1;
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
    /// The already-decoded bitmap is handed over rather than the URL: it is
    /// on screen, so re-fetching it would only add a delay and a second copy.
    /// The URL still goes along, because saving from the preview wants the
    /// original encoded bytes and the decoded surface cannot supply them.
    /// </summary>
    private void OnCardReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (_bitmap is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        byte[]? bytes = null;
        string? fileName = null;
        if (Url is { Length: > 0 } url && ImageSourceLoader.TryResolveLocalPath(url, out var path))
        {
            fileName = System.IO.Path.GetFileName(path);
            try { bytes = System.IO.File.ReadAllBytes(path); }
            catch { /* Deleted between render and click; the preview still works. */ }
        }

        var caption = Alt is { Length: > 0 } alt ? alt : fileName;
        _ = Views.ImagePreviewWindow.ShowAsync(owner, _bitmap, caption, bytes);
    }
}
