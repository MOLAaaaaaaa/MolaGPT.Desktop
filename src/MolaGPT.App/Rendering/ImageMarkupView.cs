using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MolaGPT.App.Rendering;

internal enum MarkupTool
{
    None,
    Pen,
    Crop,
}

/// <summary>
/// A picture on the canvas that can be zoomed and drawn on, then
/// flattened into one PNG for the composer.
///
/// Strokes and the optional crop are stored over the untouched bitmap and
/// flattened only on export. Undo removes the last edit.
///
/// Pixels, not <see cref="Bitmap.Size"/>: DrawImage reads its source rect in
/// pixels, and a rasterized SVG carries its scale in its DPI, so DIPs drew
/// only its top-left third.
///
/// The wheel zooms around the pointer and a drag pans, the way a picture
/// viewer behaves; there is no scroll bar to fall back on, so the view is
/// clamped to keep the picture on screen.
/// </summary>
internal sealed class ImageMarkupView : Control
{
    private const double DefaultInset = 12;
    private const double MaxZoom = 16;
    private const double MinBand = 6;
    private const double MarkThickness = 3;
    private const int MaxExportEdge = 2400;
    private static readonly IBrush MarkBrush = new SolidColorBrush(Color.Parse("#E5383B"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
    private static readonly IPen BandOuter = new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), 3);
    private static readonly IPen BandInner = new Pen(Brushes.White, 1);

    private readonly Bitmap _bitmap;
    private readonly bool _ownsBitmap;
    private readonly bool _paper;
    private readonly bool _allowCrop;
    private readonly bool _allowZoom;
    private readonly Rect _full;
    // Screen DIPs per bitmap pixel at 100%.
    private readonly double _natural;
    // Space kept around a fitted picture.
    private readonly double _inset;
    private readonly List<Mark> _marks = new();
    private readonly List<Edit> _history = new();

    private Rect _crop;
    private double _scale;
    private Point _origin;
    private bool _fitted = true;
    private MarkupTool _tool;

    private Drag _drag;
    private Point _dragStart;
    private Point _dragOrigin;
    private Point _bandStart;
    private Point _bandEnd;
    private List<Point>? _penPoints;

    /// <param name="naturalSize">Size in DIPs at 100%: the bitmap's own size for
    /// a file, the size it had on screen for a capture taken at device pixels.</param>
    /// <param name="inset">0 for a capture of the stage itself, so it lands
    /// exactly over what it replaces instead of shrinking into a margin.</param>
    /// <param name="allowCrop">Enables the crop tool for an attachment editor;
    /// normal artifact previews leave it disabled.</param>
    /// <param name="allowZoom">Enables wheel, double-click and public zoom
    /// commands; artifact previews keep this enabled by default.</param>
    public ImageMarkupView(
        Bitmap bitmap,
        bool ownsBitmap,
        bool paper,
        Size? naturalSize = null,
        double inset = DefaultInset,
        bool allowCrop = false,
        bool allowZoom = true)
    {
        _bitmap = bitmap;
        _ownsBitmap = ownsBitmap;
        _paper = paper;
        _allowCrop = allowCrop;
        _allowZoom = allowZoom;
        _inset = inset;
        _full = new Rect(0, 0, Math.Max(1, bitmap.PixelSize.Width), Math.Max(1, bitmap.PixelSize.Height));
        _crop = _full;
        _natural = (naturalSize is { Width: > 0 } natural ? natural.Width : bitmap.Size.Width) / _full.Width;

        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        UpdateCursor();
        DoubleTapped += (_, e) =>
        {
            if (_allowZoom && _tool == MarkupTool.None) ToggleFit(e.GetPosition(this));
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_ownsBitmap) _bitmap.Dispose();
        };
    }

    /// <summary>Zoom, tool or history changed.</summary>
    public event EventHandler? StateChanged;

    public MarkupTool Tool
    {
        get => _tool;
        set
        {
            if (value == MarkupTool.Crop && !_allowCrop) value = MarkupTool.None;
            if (_tool == value) return;
            _tool = value;
            CancelDrag();
            UpdateCursor();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Relative to the natural size, 1 = 100%.</summary>
    public double Zoom => _scale / _natural;

    public bool IsFitted => _fitted;

    public bool CanUndo => _history.Count > 0;

    public bool IsDragging => _drag != Drag.None;

    public bool HasMarks => _marks.Count > 0;

    public void ZoomBy(double factor)
    {
        if (_allowZoom) ZoomTo(_scale * factor, new Point(Bounds.Width / 2, Bounds.Height / 2));
    }

    /// <summary>Fit ↔ 100%, around the given point (the centre by default).</summary>
    public void ToggleFit(Point? anchor = null)
    {
        if (!_allowZoom) return;
        if (_fitted && Math.Abs(_scale - _natural) > 0.001)
            ZoomTo(_natural, anchor ?? new Point(Bounds.Width / 2, Bounds.Height / 2));
        else
            Fit();
    }

    public void Undo()
    {
        if (_history.Count == 0) return;
        var edit = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        if (edit.Mark is { } mark)
            _marks.Remove(mark);
        else
        {
            _crop = edit.PreviousCrop;
            Fit();
        }

        InvalidateVisual();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The picture with its strokes, at the bitmap's own
    /// resolution (capped), as PNG.</summary>
    public byte[] ExportPng()
    {
        var width = _crop.Width;
        var height = _crop.Height;
        var shrink = Math.Min(1, MaxExportEdge / Math.Max(1, Math.Max(width, height)));
        var size = new PixelSize(
            Math.Max(1, (int)Math.Round(width * shrink)),
            Math.Max(1, (int)Math.Round(height * shrink)));
        var k = size.Width / _crop.Width;

        using var target = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            var frame = new Rect(0, 0, size.Width, size.Height);
            if (_paper) context.FillRectangle(Brushes.White, frame);
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                context.DrawImage(_bitmap, _crop, frame);
            using (context.PushClip(frame))
            {
                foreach (var mark in _marks)
                    DrawStroke(context, mark.Points, mark.Thickness * k, _crop.TopLeft, k, default);
            }
        }

        using var stream = new MemoryStream();
        target.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    // ---- view ------------------------------------------------------------------

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_fitted || _scale <= 0) Fit();
        else ClampOrigin();
    }

    private void Fit()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        _scale = FitScale();
        _fitted = true;
        _origin = new Point(
            (Bounds.Width - _crop.Width * _scale) / 2,
            (Bounds.Height - _crop.Height * _scale) / 2);
        InvalidateVisual();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whole picture in view, never enlarged past 100%.</summary>
    private double FitScale()
    {
        var width = Math.Max(1, Bounds.Width - 2 * _inset);
        var height = Math.Max(1, Bounds.Height - 2 * _inset);
        var fit = Math.Min(width / Math.Max(1e-6, _crop.Width), height / Math.Max(1e-6, _crop.Height));
        var cropped = _crop != _full;
        return Math.Max(1e-4, Math.Min(fit, _natural * (cropped ? 4 : 1)));
    }

    private void ZoomTo(double scale, Point anchor)
    {
        if (_scale <= 0) return;
        var min = Math.Min(FitScale(), _natural) / 2;
        scale = Math.Clamp(scale, min, _natural * MaxZoom);
        var ratio = scale / _scale;
        _origin = new Point(anchor.X - (anchor.X - _origin.X) * ratio, anchor.Y - (anchor.Y - _origin.Y) * ratio);
        _scale = scale;
        _fitted = false;
        ClampOrigin();
        InvalidateVisual();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Centred while it fits; otherwise no further than the inset past
    /// either edge.</summary>
    private void ClampOrigin()
    {
        _origin = new Point(
            ClampAxis(_origin.X, _crop.Width * _scale, Bounds.Width),
            ClampAxis(_origin.Y, _crop.Height * _scale, Bounds.Height));
    }

    private double ClampAxis(double origin, double extent, double viewport) =>
        extent + 2 * _inset <= viewport
            ? (viewport - extent) / 2
            : Math.Clamp(origin, viewport - extent - _inset, _inset);

    private Rect PictureRect => new(_origin, new Size(_crop.Width * _scale, _crop.Height * _scale));

    private Point ToImage(Point p) => new(_crop.X + (p.X - _origin.X) / _scale, _crop.Y + (p.Y - _origin.Y) / _scale);

    private Point ClampToCrop(Point p) => new(Math.Clamp(p.X, _crop.Left, _crop.Right), Math.Clamp(p.Y, _crop.Top, _crop.Bottom));

    private static Point Map(Point p, Point from, double k, Point to) =>
        new(to.X + (p.X - from.X) * k, to.Y + (p.Y - from.Y) * k);

    private static IPen MarkPen(double thickness) => new Pen(MarkBrush, thickness, lineCap: PenLineCap.Round);

    private static void DrawStroke(
        DrawingContext context,
        IReadOnlyList<Point> points,
        double thickness,
        Point from,
        double k,
        Point to)
    {
        if (points.Count == 0) return;

        var first = Map(points[0], from, k, to);
        var radius = thickness / 2;
        if (points.Count == 1)
        {
            context.DrawEllipse(MarkBrush, null, first, radius, radius);
            return;
        }

        var pen = MarkPen(thickness);
        var previous = first;
        for (var i = 1; i < points.Count; i++)
        {
            var current = Map(points[i], from, k, to);
            context.DrawLine(pen, previous, current);
            previous = current;
        }
    }

    // ---- drawing ---------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        // Hit-testable everywhere, not only over the picture.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_scale <= 0) return;

        var picture = PictureRect;
        if (_paper)
            context.DrawRectangle(Brushes.White, null, new RoundedRect(picture.Inflate(8), 6));
        context.DrawImage(_bitmap, _crop, picture);

        using (context.PushClip(picture))
        {
            foreach (var mark in _marks)
                DrawStroke(context, mark.Points, mark.Thickness * _scale, _crop.TopLeft, _scale, _origin);

            if (_drag == Drag.Pen && _penPoints is { } penPoints)
                DrawStroke(context, penPoints,
                    Math.Max(MarkThickness / _scale, 1.5) * _scale,
                    _crop.TopLeft, _scale, _origin);
        }

        if (_drag == Drag.Crop)
        {
            var band = MapRect(Band(), _crop.TopLeft, _scale, _origin);
            context.FillRectangle(Dim, new Rect(picture.Left, picture.Top, picture.Width, band.Top - picture.Top));
            context.FillRectangle(Dim, new Rect(picture.Left, band.Bottom, picture.Width, picture.Bottom - band.Bottom));
            context.FillRectangle(Dim, new Rect(picture.Left, band.Top, band.Left - picture.Left, band.Height));
            context.FillRectangle(Dim, new Rect(band.Right, band.Top, picture.Right - band.Right, band.Height));
            context.DrawRectangle(null, BandOuter, band);
            context.DrawRectangle(null, BandInner, band);
        }
    }

    private static Rect MapRect(Rect r, Point from, double k, Point to) =>
        new(to.X + (r.X - from.X) * k, to.Y + (r.Y - from.Y) * k, r.Width * k, r.Height * k);

    private Rect Band()
    {
        var x = Math.Min(_bandStart.X, _bandEnd.X);
        var y = Math.Min(_bandStart.Y, _bandEnd.Y);
        return new Rect(x, y, Math.Abs(_bandEnd.X - _bandStart.X), Math.Abs(_bandEnd.Y - _bandStart.Y));
    }

    // ---- input -----------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_scale <= 0) return;
        var point = e.GetCurrentPoint(this);
        var position = point.Position;
        Focus();

        if (point.Properties.IsMiddleButtonPressed
            || (point.Properties.IsLeftButtonPressed && _tool == MarkupTool.None))
        {
            _drag = Drag.Pan;
            _dragStart = position;
            _dragOrigin = _origin;
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            _drag = _tool == MarkupTool.Crop ? Drag.Crop : Drag.Pen;
            if (_drag == Drag.Pen)
            {
                _penPoints = new List<Point> { ClampToCrop(ToImage(position)) };
                InvalidateVisual();
            }
            else
            {
                _bandStart = _bandEnd = ClampToCrop(ToImage(position));
                InvalidateVisual();
            }
        }
        else
        {
            return;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        switch (_drag)
        {
            case Drag.Pan:
                _origin = _dragOrigin + (position - _dragStart);
                _fitted = false;
                ClampOrigin();
                InvalidateVisual();
                break;
            case Drag.Crop:
                _bandEnd = ClampToCrop(ToImage(position));
                InvalidateVisual();
                break;
            case Drag.Pen:
                if (AddPenPoint(position)) InvalidateVisual();
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var drag = _drag;
        if (drag == Drag.None) return;
        if (drag == Drag.Pen) AddPenPoint(e.GetPosition(this));
        _drag = Drag.None;
        e.Pointer.Capture(null);
        UpdateCursor();

        if (drag == Drag.Pan)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (drag == Drag.Pen)
        {
            var points = _penPoints;
            _penPoints = null;
            if (points is not { Count: > 0 })
            {
                InvalidateVisual();
                return;
            }

            var mark = new Mark(points, Math.Max(MarkThickness / _scale, 1.5));
            _marks.Add(mark);
            _history.Add(new Edit(mark, _crop));
            InvalidateVisual();
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (drag == Drag.Crop)
        {
            var band = Band();
            if (band.Width * _scale < MinBand || band.Height * _scale < MinBand)
            {
                InvalidateVisual();
                return;
            }

            _history.Add(new Edit(null, _crop));
            _crop = band;
            Fit();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CancelDrag();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!_allowZoom || _scale <= 0 || e.Delta.Y == 0) return;
        ZoomTo(_scale * Math.Pow(1.2, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            if (_drag != Drag.None) CancelDrag();
            else Tool = MarkupTool.None;
            e.Handled = true;
        }
        else if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Undo();
            e.Handled = true;
        }
    }

    private void CancelDrag()
    {
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        _penPoints = null;
        UpdateCursor();
        InvalidateVisual();
    }

    private bool AddPenPoint(Point position)
    {
        if (_penPoints is not { } points) return false;

        var point = ClampToCrop(ToImage(position));
        var spacing = 0.5 / _scale;
        var delta = point - points[^1];
        if (delta.X * delta.X + delta.Y * delta.Y < spacing * spacing) return false;
        points.Add(point);
        return true;
    }

    private void UpdateCursor() =>
        Cursor = new Cursor(_tool == MarkupTool.None ? StandardCursorType.Arrow : StandardCursorType.Cross);

    private enum Drag
    {
        None,
        Pan,
        Crop,
        Pen,
    }

    private sealed record Mark(IReadOnlyList<Point> Points, double Thickness);

    private sealed record Edit(Mark? Mark, Rect PreviousCrop);
}
