using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// Lightweight fullscreen image preview overlay shown when the user clicks an
/// image attachment chip in the composer. Click the backdrop, Esc, or Enter to
/// close; the top-right toolbar copies or saves the image.
/// </summary>
public partial class ImagePreviewWindow : Window
{
    private string? _suggestedFileName;

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
        window._suggestedFileName = caption;
        window.ShowDialog();
    }

    private void OnRootMouseDown(object sender, MouseButtonEventArgs e) => Close();

    // Clicks inside the toolbar must not bubble to the window's close-on-click.
    private void OnToolbarMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source is not BitmapSource bitmap)
            return;
        try
        {
            Clipboard.SetImage(bitmap);
            FlashCopied();
        }
        catch
        {
            // Clipboard can transiently fail if another app holds it; ignore.
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source is not BitmapSource bitmap)
            return;

        var dialog = new SaveFileDialog
        {
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|所有文件|*.*",
            FileName = BuildSuggestedFileName(),
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            BitmapEncoder encoder = dialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || dialog.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                ? new JpegBitmapEncoder { QualityLevel = 95 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write);
            encoder.Save(stream);
        }
        catch
        {
            MessageBox.Show(this, "保存失败，请重试或更换保存位置。", "保存图片",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string BuildSuggestedFileName()
    {
        var name = _suggestedFileName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileName(name.Trim());
            if (!string.IsNullOrWhiteSpace(name)
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && Path.HasExtension(name))
            {
                return name;
            }
        }

        return "image.png";
    }

    private void FlashCopied()
    {
        var original = CopyButton.Content;
        CopyButton.Content = "已复制";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CopyButton.Content = original;
        };
        timer.Start();
    }

    private void OnRootKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter or Key.Space)
        {
            Close();
            e.Handled = true;
        }
    }
}
