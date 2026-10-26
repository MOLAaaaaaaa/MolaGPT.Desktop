using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MolaGPT.Desktop.Services;

namespace MolaGPT.App.Rendering;

public sealed class WorkbenchThumbnail : Image
{
    public static readonly StyledProperty<string?> ImagePathProperty =
        AvaloniaProperty.Register<WorkbenchThumbnail, string?>(nameof(ImagePath));
    public static readonly StyledProperty<PixelSize> ImageSizeProperty =
        AvaloniaProperty.Register<WorkbenchThumbnail, PixelSize>(nameof(ImageSize));

    private static readonly SemaphoreSlim DecodeGate = new(1, 1);
    private readonly List<Visual> _ancestors = [];
    private Rect _viewport;
    private Window? _window;
    private CancellationTokenSource? _load;
    private Bitmap? _bitmap;

    public string? ImagePath
    {
        get => GetValue(ImagePathProperty);
        set => SetValue(ImagePathProperty, value);
    }

    public PixelSize ImageSize
    {
        get => GetValue(ImageSizeProperty);
        set => SetValue(ImageSizeProperty, value);
    }

    static WorkbenchThumbnail() => AffectsMeasure<WorkbenchThumbnail>(ImageSizeProperty);

    public WorkbenchThumbnail()
    {
        EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            UpdateLoading();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        foreach (var ancestor in this.GetVisualAncestors())
        {
            _ancestors.Add(ancestor);
            ancestor.PropertyChanged += OnAncestorChanged;
        }
        UpdateLoading();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var ancestor in _ancestors) ancestor.PropertyChanged -= OnAncestorChanged;
        _ancestors.Clear();
        _window = null;
        _viewport = default;
        ReleaseBitmap();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ImagePathProperty) ReleaseBitmap();
        if (change.Property == ImagePathProperty || change.Property == BoundsProperty
            || change.Property == IsVisibleProperty) UpdateLoading();
    }

    private void OnAncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty || e.Property == Window.WindowStateProperty) UpdateLoading();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var pixels = ImageSize;
        if (pixels.Width <= 0 || pixels.Height <= 0) return default;
        var width = Math.Min(640, pixels.Width);
        var height = width * (double)pixels.Height / pixels.Width;
        var scale = Math.Min(availableSize.Width / width, availableSize.Height / height);
        if (double.IsPositiveInfinity(scale)) scale = 1;
        if (StretchDirection == Avalonia.Media.StretchDirection.DownOnly) scale = Math.Min(1, scale);
        return new Size(width * scale, height * scale);
    }

    private void UpdateLoading()
    {
        var visible = _window is { IsVisible: true } window && window.WindowState != WindowState.Minimized
            && IsEffectivelyVisible && _viewport.Width > 0 && _viewport.Height > 0
            && new Rect(Bounds.Size).Intersects(_viewport.Inflate(160));
        if (!visible)
        {
            ReleaseBitmap();
            return;
        }
        if (_bitmap is null && _load is null && ImagePath is { Length: > 0 } path)
            _ = LoadAsync(path);
    }

    private async Task LoadAsync(string path)
    {
        var load = new CancellationTokenSource();
        _load = load;
        Bitmap? bitmap = null;
        try
        {
            await DecodeGate.WaitAsync(load.Token);
            try
            {
                bitmap = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(path);
                    return Bitmap.DecodeToWidth(stream, 640);
                }, load.Token);
            }
            finally { DecodeGate.Release(); }

            if (load.IsCancellationRequested) return;
            _bitmap = bitmap;
            Source = bitmap;
            bitmap = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DiagnosticLog.Write("images", "载入作品缩略图失败：" + ex.Message);
        }
        finally
        {
            bitmap?.Dispose();
            if (ReferenceEquals(_load, load)) _load = null;
            load.Dispose();
        }
    }

    private void ReleaseBitmap()
    {
        _load?.Cancel();
        _load = null;
        Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
