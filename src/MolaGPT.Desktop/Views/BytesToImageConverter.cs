using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

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
        if (value is not byte[] bytes || bytes.Length == 0) return null;

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

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
