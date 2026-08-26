using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MolaGPT.App.Rendering;

/// <summary>
/// A 32×32 preview of an attachment that only exists as bytes in memory.
///
/// The WPF build did this with a BytesToImageConverter. A converter is the wrong
/// shape here: the decoded <see cref="Bitmap"/> is disposable, and a converter
/// has no point at which it can let one go, so every re-render of the chip strip
/// leaks another decode. Owning the bitmap in the control means it is released
/// when the bytes change or the chip is removed.
/// </summary>
public sealed class AttachmentThumbnail : Image
{
    public static readonly StyledProperty<byte[]?> BytesProperty =
        AvaloniaProperty.Register<AttachmentThumbnail, byte[]?>(nameof(Bytes));

    public byte[]? Bytes
    {
        get => GetValue(BytesProperty);
        set => SetValue(BytesProperty, value);
    }

    private Bitmap? _owned;

    static AttachmentThumbnail()
    {
        BytesProperty.Changed.AddClassHandler<AttachmentThumbnail>((x, _) => x.Refresh());
    }

    public AttachmentThumbnail()
    {
        Stretch = Stretch.UniformToFill;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    private void Refresh()
    {
        Source = null;
        _owned?.Dispose();
        _owned = null;

        if (Bytes is not { Length: > 0 } bytes) return;

        try
        {
            using var stream = new MemoryStream(bytes);
            // Decoded to the chip size, not the source size: a 12MP photo behind
            // a 32px square is 48MB of surface for 1024 visible pixels.
            _owned = Bitmap.DecodeToWidth(stream, 96, BitmapInterpolationMode.HighQuality);
            Source = _owned;
        }
        catch
        {
            // A queued file that is not decodable as an image just shows the
            // generic glyph beneath this control.
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Source = null;
        _owned?.Dispose();
        _owned = null;
    }
}
