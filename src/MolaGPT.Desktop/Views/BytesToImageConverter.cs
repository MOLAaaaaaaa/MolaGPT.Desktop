using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using MolaGPT.Storage;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// Decodes a <c>byte[]</c> into a frozen <see cref="BitmapImage"/> for use in
/// composer attachment thumbnails. Uses <c>DecodePixelWidth</c> so a 12MB
/// camera shot doesn't blow up memory just to render a 32×32 chip preview.
/// </summary>
public sealed class BytesToImageConverter : IValueConverter
{
    public static BytesToImageConverter Instance { get; } = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        byte[]? bytes = value switch
        {
            byte[] raw when raw.Length > 0 => raw,
            AttachmentChip { Bytes.Length: > 0 } chip => chip.Bytes,
            AttachmentChip chip when !string.IsNullOrWhiteSpace(chip.LocalName)
                => TryLoadLocalAttachment(chip.LocalName),
            _ => null
        };

        if (bytes is not { Length: > 0 }) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            // Decode small for chip; the preview window uses a separate full-res load.
            image.DecodePixelWidth = 96;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryLoadLocalAttachment(string? localName)
    {
        try
        {
            return App.Services.GetRequiredService<AttachmentStore>().Load(localName);
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
