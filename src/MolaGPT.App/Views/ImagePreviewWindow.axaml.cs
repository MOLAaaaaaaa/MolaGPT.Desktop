using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;      // ClipboardExtensions.SetBitmapAsync
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;          // FindAncestorOfType
using MolaGPT.App.Rendering;

namespace MolaGPT.App.Views;

/// <summary>
/// Full-size image preview. Click anywhere, or press Esc/Enter/Space, to close;
/// the floating toolbar copies or saves.
///
/// Replaces three near-identical throwaway <c>new Window { Content = new Image }</c>
/// blocks (markdown image, composer attachment chip, image workbench result),
/// each of which came up with the host OS title bar and no way to get the
/// picture back out again.
/// </summary>
public partial class ImagePreviewWindow : MolaWindow
{
    private Bitmap? _bitmap;
    private byte[]? _bytes;
    private string? _caption;
    private bool _ownsBitmap;

    public ImagePreviewWindow()
    {
        InitializeComponent();

        PART_Copy.Click += async (_, _) => await CopyAsync();
        PART_Save.Click += async (_, _) => await SaveAsync();
        PART_Close.Click += (_, _) => Close();

        // Press, not release: a release-based close fires when a drag that
        // started on the toolbar happens to end over the image.
        AddHandler(PointerPressedEvent, OnPointerPressed, handledEventsToo: false);
        KeyDown += OnKeyDown;
        Closed += (_, _) =>
        {
            if (_ownsBitmap) _bitmap?.Dispose();
        };
    }

    /// <summary>Shows encoded image bytes — an attachment, or a tool result.</summary>
    public static Task ShowAsync(Window? owner, byte[]? bytes, string? caption)
    {
        if (owner is null || bytes is null || bytes.Length == 0) return Task.CompletedTask;

        Bitmap bitmap;
        try
        {
            using var stream = new MemoryStream(bytes);
            bitmap = new Bitmap(stream);
        }
        catch
        {
            // A chip whose bytes are not a decodable image is not worth an error
            // dialog; the click simply does nothing, as it did before.
            return Task.CompletedTask;
        }

        return ShowCoreAsync(owner, bitmap, bytes, caption, ownsBitmap: true);
    }

    /// <summary>
    /// Shows an image named by URL. Goes through <see cref="ImageSourceLoader"/>
    /// so the same http / file:// / bare-Windows-path / data: handling the
    /// transcript already does applies here too.
    /// </summary>
    public static async Task ShowAsync(Window? owner, string? url, string? caption)
    {
        if (owner is null || string.IsNullOrWhiteSpace(url)) return;

        // Full width: this is the "see it properly" path, so decoding to the
        // thumbnail size the transcript used would defeat the point.
        var bitmap = await ImageSourceLoader.LoadAsync(url, decodeWidth: 0).ConfigureAwait(true);
        if (bitmap is null) return;

        var bytes = ImageSourceLoader.TryResolveLocalPath(url, out var path)
            ? TryReadAllBytes(path)
            : null;

        // Never owned: the loader hands back a cached, shared instance, and the
        // transcript row that first decoded it is still drawing with it.
        await ShowCoreAsync(owner, bitmap, bytes, caption, ownsBitmap: false).ConfigureAwait(true);
    }

    /// <summary>
    /// Shows a bitmap the caller already holds. The caller keeps ownership —
    /// workbench results stay in their list after the preview closes.
    /// </summary>
    public static Task ShowAsync(Window? owner, Bitmap? bitmap, string? caption, byte[]? bytes = null)
    {
        if (owner is null || bitmap is null) return Task.CompletedTask;
        return ShowCoreAsync(owner, bitmap, bytes, caption, ownsBitmap: false);
    }

    private static Task ShowCoreAsync(
        Window owner, Bitmap bitmap, byte[]? bytes, string? caption, bool ownsBitmap)
    {
        var window = new ImagePreviewWindow
        {
            _bitmap = bitmap,
            _bytes = bytes,
            _caption = caption,
            _ownsBitmap = ownsBitmap
        };

        window.PART_Image.Source = bitmap;
        window.PART_Caption.Text = caption ?? string.Empty;
        window.PART_CaptionHost.IsVisible = !string.IsNullOrWhiteSpace(caption);
        window.Title = string.IsNullOrWhiteSpace(caption) ? "图片预览" : caption;
        window.SizeToImage(owner, bitmap);

        // Modal: the preview is a detour, and leaving one open behind the main
        // window is how you end up with a stack of them.
        return window.ShowDialog(owner);
    }

    /// <summary>
    /// Fits the frame to the picture instead of using one fixed size, then keeps
    /// it inside the screen the owner is on. A fixed frame either crops large
    /// images or strands small ones in the middle of empty space.
    /// </summary>
    private void SizeToImage(Window owner, Bitmap bitmap)
    {
        // Toolbar, caption and the surface's own padding all sit outside the
        // picture, so the frame has to be larger than the pixels it shows.
        const double chromeWidth = 14 * 2;
        const double chromeHeight = 14 * 2 + 34;

        var scaling = owner.RenderScaling <= 0 ? 1.0 : owner.RenderScaling;
        var maxWidth = 1280.0;
        var maxHeight = 860.0;

        if (owner.Screens.ScreenFromWindow(owner) is { } screen)
        {
            var area = screen.WorkingArea;
            maxWidth = Math.Min(maxWidth, area.Width / scaling - 80);
            maxHeight = Math.Min(maxHeight, area.Height / scaling - 80);
        }

        var natural = bitmap.Size;
        Width = Math.Clamp(natural.Width + chromeWidth, 420, Math.Max(420, maxWidth));
        Height = Math.Clamp(natural.Height + chromeHeight, 320, Math.Max(320, maxHeight));
    }

    private static byte[]? TryReadAllBytes(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch { return null; }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Anything with its own meaning — the buttons — keeps its click.
        if (e.Source is Control source
            && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        Close();
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Enter or Key.Space)) return;
        Close();
        e.Handled = true;
    }

    private async Task CopyAsync()
    {
        if (_bitmap is null || Clipboard is null) return;

        try
        {
            await Clipboard.SetBitmapAsync(_bitmap);
            FlashCopied();
        }
        catch
        {
            // The clipboard is shared state and another process can hold it.
        }
    }

    /// <summary>
    /// Confirms the copy on the button that was clicked. A transient label is
    /// enough here — a toast would outlive the window it belongs to.
    /// </summary>
    private void FlashCopied()
    {
        PART_Copy.Content = "已复制";
        DispatcherTimer.RunOnce(
            () => PART_Copy.Content = "复制",
            TimeSpan.FromMilliseconds(1100));
    }

    private async Task SaveAsync()
    {
        if (StorageProvider is not { } storage) return;

        var suggested = BuildSuggestedFileName();
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存图片",
            SuggestedFileName = suggested,
            DefaultExtension = Path.GetExtension(suggested).TrimStart('.'),
            FileTypeChoices =
            [
                new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] },
                new FilePickerFileType("JPEG 图片") { Patterns = ["*.jpg", "*.jpeg"] },
                FilePickerFileTypes.All
            ]
        });
        if (file is null) return;

        var wantsJpeg = (file.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || file.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

        try
        {
            await using var stream = await file.OpenWriteAsync();

            // Straight through when the bytes we hold are already the format the
            // filename promises: decoding and re-encoding a JPEG to write the
            // same picture back out only loses quality.
            if (_bytes is { Length: > 0 } bytes && wantsJpeg == IsJpeg(bytes))
            {
                await stream.WriteAsync(bytes);
                return;
            }

            _bitmap?.Save(stream, wantsJpeg
                ? new JpegBitmapEncoderOptions { Quality = 95 }
                : PngBitmapEncoderOptions.Default);
        }
        catch
        {
            // Read-only target, vanished drive. Nothing was written, and the
            // picker already told the user where they aimed.
        }
    }

    private static bool IsJpeg(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private string BuildSuggestedFileName()
    {
        if (string.IsNullOrWhiteSpace(_caption)) return "image.png";

        var name = Path.GetFileName(_caption.Trim());
        return !string.IsNullOrWhiteSpace(name)
               && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
               && Path.HasExtension(name)
            ? name
            : "image.png";
    }
}
