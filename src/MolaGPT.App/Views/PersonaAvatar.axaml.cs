using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MolaGPT.ViewModels.Services;
using SkiaSharp;

namespace MolaGPT.App.Views;

public partial class PersonaAvatar : UserControl
{
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<PersonaAvatar, string?>(nameof(Value));

    /// <summary>
    /// The .axaml root carries Width/Height, so the very first Height
    /// notification is raised from inside InitializeComponent — before the named
    /// fields exist. Anything reaching for PART_* has to wait for this.
    /// </summary>
    private bool _ready;

    public PersonaAvatar()
    {
        InitializeComponent();
        _ready = true;
        Refresh();
    }

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Height matters as much as Value: the glyph is drawn, not scaled, so one
        // fixed size only fits the box it was picked for. Call sites run from a
        // 20px composer chip to a 44px settings preview, and at 20 a 17px glyph
        // spilled past its own clip.
        if (change.Property == ValueProperty || change.Property == HeightProperty) Refresh();
    }

    private static double GlyphSizeFor(double height) =>
        double.IsNaN(height) ? 17 : Math.Max(9, Math.Round(height * 0.6));

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Refresh();
    }

    /// <summary>
    /// Falls back to the glyph rather than throwing: an unreadable avatar — a
    /// truncated data URI from a half-written import — must not take down the
    /// window that was trying to show the row. The decode itself, and the
    /// decision of how big to decode, live in
    /// <see cref="PersonaAvatarImages"/>; the bitmap belongs to that table and
    /// is shared, so nothing here disposes it.
    /// </summary>
    private void Refresh()
    {
        if (!_ready) return;
        PART_Glyph.FontSize = GlyphSizeFor(Height);
        if (Value?.StartsWith("data:image/", StringComparison.Ordinal) == true
            && PersonaAvatarImages.Get(Value, TargetPixels()) is { } bitmap)
        {
            PART_Image.Source = bitmap;
            PART_Image.IsVisible = true;
            PART_Glyph.IsVisible = false;
            return;
        }
        PART_Image.Source = null;
        PART_Image.IsVisible = false;
        PART_Glyph.IsVisible = true;
        PART_Glyph.Text = PersonaIconCatalog.Resolve(Value);
    }

    /// <summary>Device pixels the portrait is actually drawn at, rounded up to a
    /// 16px step: the call sites are 20, 28, 38 and 44 logical px and every
    /// screen scaling multiplies that set again, which without the step would
    /// be a cache entry each.</summary>
    private int TargetPixels()
    {
        var logical = double.IsNaN(Height) ? 28 : Height;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return Math.Max(16, (int)Math.Ceiling(logical * scaling / 16) * 16);
    }

    internal static string EncodeImage(byte[] bytes)
    {
        using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("无法读取这张图片。");
        var scale = Math.Min(1d, 384d / Math.Max(bitmap.Width, bitmap.Height));
        using var resized = bitmap.Resize(new SKImageInfo(
            Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale))),
            new SKSamplingOptions(SKFilterMode.Linear));
        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + Convert.ToBase64String(encoded.ToArray());
    }
}
