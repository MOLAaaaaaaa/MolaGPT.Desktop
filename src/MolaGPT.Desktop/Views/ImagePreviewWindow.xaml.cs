using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// Lightweight fullscreen image preview overlay shown when the user clicks an
/// image attachment chip in the composer. Click anywhere, Esc, or Enter to close.
/// </summary>
public partial class ImagePreviewWindow : Window
{
    public ImagePreviewWindow()
    {
        InitializeComponent();
    }

    public static void Show(Window? owner, byte[] bytes, string? caption)
    {
        if (bytes is null || bytes.Length == 0) return;

        BitmapImage image;
        try
        {
            image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
        }
        catch
        {
            return;
        }

        ShowInternal(owner, image, caption);
    }

    /// <summary>
    /// Variant for MolaGPT-mode messages reloaded from SQLite: only the image
    /// host URL is available (raw bytes were dropped after send). WPF fetches
    /// the URL via <see cref="BitmapImage.UriSource"/>.
    /// </summary>
    public static void Show(Window? owner, string url, string? caption)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var source = url.Trim().Trim('"', '\'');
        if (File.Exists(source))
        {
            try
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = new MemoryStream();
                input.CopyTo(output);
                Show(owner, output.ToArray(), caption);
            }
            catch
            {
                return;
            }
            return;
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return;

        BitmapImage image;
        try
        {
            image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = uri;
            image.EndInit();
            image.Freeze();
        }
        catch
        {
            return;
        }

        ShowInternal(owner, image, caption);
    }

    private static void ShowInternal(Window? owner, BitmapImage image, string? caption)
    {
        var window = new ImagePreviewWindow
        {
            Owner = owner
        };
        window.PreviewImage.Source = image;
        window.CaptionText.Text = string.IsNullOrWhiteSpace(caption) ? string.Empty : caption;
        window.CaptionText.Visibility = string.IsNullOrWhiteSpace(caption) ? Visibility.Collapsed : Visibility.Visible;
        window.ShowDialog();
    }

    private void OnRootMouseDown(object sender, MouseButtonEventArgs e) => Close();

    private void OnRootKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter or Key.Space)
        {
            Close();
            e.Handled = true;
        }
    }
}
