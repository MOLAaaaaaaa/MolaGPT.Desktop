using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MolaGPT.Core.Models;
using MolaGPT.Desktop.Services;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Views;

public partial class ComposerView : UserControl
{
    public ComposerView() => InitializeComponent();

    /// <summary>
    /// Ctrl+Enter sends, plain Enter inserts a newline.
    /// </summary>
    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        // Ctrl+V: queue clipboard images / files as attachments. Plain text still
        // pastes normally (TryPasteAttachments leaves e.Handled false for text).
        if (ctrl && e.Key == Key.V && DataContext is ComposerViewModel pasteVm)
        {
            TryPasteAttachments(pasteVm, e);
            return;
        }

        if (e.Key != Key.Enter) return;

        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (DataContext is not ComposerViewModel vm) return;

        // Ctrl+Enter always sends.
        if (ctrl)
        {
            if (vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        // EnterToSend mode: bare Enter sends, Shift+Enter inserts newline.
        if (vm.EnterToSend && !shift)
        {
            if (vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
                e.Handled = true;
            }
        }
        // Default: bare Enter falls through to normal newline insertion.
    }

    /// <summary>
    /// Open the system file picker and queue the picked files into the composer's
    /// <see cref="ComposerViewModel.Attachments"/>. We don't upload anything
    /// here; the active provider's StreamChatAsync will marshal them into
    /// the wire format on Send.
    /// </summary>
    private void OnAttachClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ComposerViewModel vm) return;

        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "选择图片或文件",
            Filter = "所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        foreach (var path in dlg.FileNames)
            AddFileFromPath(vm, path);
    }

    /// <summary>
    /// Read one file from disk and queue it as an attachment. Shared by the
    /// attach button, clipboard paste and drag-and-drop so all three enforce the
    /// same limits. A refused file surfaces a dialog and is skipped; the caller
    /// keeps going with the rest.
    /// </summary>
    private void AddFileFromPath(ComposerViewModel vm, string path) =>
        Queue(vm, AttachmentIntake.FromFile(path, Capabilities(vm)));

    private void Queue(ComposerViewModel vm, AttachmentIntakeResult result)
    {
        if (result.Attachment is { } attachment)
        {
            vm.Attachments.Add(attachment);
            return;
        }

        MessageBox.Show(
            Window.GetWindow(this),
            result.Error ?? "无法添加该附件。",
            "附件",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static AttachmentIntakeCapabilities Capabilities(ComposerViewModel vm) =>
        new(vm.CanAcceptImageAttachments, vm.CanProcessOpaqueFiles);

    /// <summary>
    /// Ctrl+V handler: queue clipboard files (copied in Explorer) or a bitmap
    /// image (screenshot / copied image) as attachments and suppress the plain-
    /// text paste. Plain text leaves <paramref name="e"/> unhandled so the
    /// TextBox pastes it normally. Clipboard access is wrapped because it can be
    /// transiently locked by another process.
    /// </summary>
    private void TryPasteAttachments(ComposerViewModel vm, KeyEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var added = false;
                foreach (string? path in Clipboard.GetFileDropList())
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                    AddFileFromPath(vm, path!);
                    added = true;
                }
                if (added)
                {
                    e.Handled = true;
                    return;
                }
            }

            if (Clipboard.ContainsImage())
            {
                var png = TryGetClipboardImagePng();
                if (png is { Length: > 0 })
                {
                    AddPastedImage(vm, png);
                    e.Handled = true;
                }
            }
            // Otherwise: plain text / unsupported format → fall through to the
            // TextBox's own paste.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Paste-as-attachment failed: {ex.Message}");
        }
    }

    private void AddPastedImage(ComposerViewModel vm, byte[] pngBytes) =>
        Queue(vm, AttachmentIntake.FromBytes(
            pngBytes,
            $"粘贴图片_{DateTime.Now:HHmmss}.png",
            Capabilities(vm)));

    /// <summary>
    /// Drag-and-drop. Only file drops are taken; dragged text falls through to
    /// the TextBox, which handles it natively.
    /// </summary>
    private void OnComposerDragOver(object sender, DragEventArgs e)
    {
        var isFileDrop = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = isFileDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = isFileDrop;
    }

    private void OnComposerDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not ComposerViewModel vm) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        e.Handled = true;
        foreach (var path in paths)
        {
            // Dropping a folder is a common mis-drop; say so instead of failing
            // with a bare read error.
            if (Directory.Exists(path))
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    $"{Path.GetFileName(path)} 是文件夹，请拖入具体的文件。",
                    "附件",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                continue;
            }
            AddFileFromPath(vm, path);
        }
    }

    /// <summary>Extract the clipboard image as PNG bytes. Prefers a real "PNG"
    /// payload when the source app provides one (preserves alpha); otherwise
    /// encodes the bitmap from <see cref="Clipboard.GetImage"/>.</summary>
    private static byte[]? TryGetClipboardImagePng()
    {
        try
        {
            if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream pngStream)
                return pngStream.ToArray();
        }
        catch
        {
            // Fall back to encoding the bitmap below.
        }

        var source = Clipboard.GetImage();
        if (source is null) return null;
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Per-chip "remove" button handler. Bound from the ItemsControl item
    /// template (Row 0 of ComposerView.xaml).
    /// </summary>
    private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ComposerViewModel vm) return;
        if (sender is FrameworkElement fe && fe.DataContext is Attachment att)
        {
            vm.Attachments.Remove(att);
        }
        // Stop the click from bubbling to the card (which would open the
        // preview window on image attachments).
        e.Handled = true;
    }

    /// <summary>
    /// Whole-card click handler. For image attachments, opens the fullscreen
    /// preview overlay. Non-image cards do not react.
    /// </summary>
    private void OnAttachmentCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not Attachment att) return;
        if (!att.IsImage || att.Bytes is null || att.Bytes.Length == 0) return;

        ImagePreviewWindow.Show(Window.GetWindow(this), att.Bytes, att.FileName);
        e.Handled = true;
    }

    /// <summary>Reset the conversation persona to the built-in default. The popup auto-closes
    /// because the picker ToggleButton flips IsChecked on lost focus.</summary>
    private void OnClearPersonaClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ComposerViewModel vm) return;
        var defaultId = vm.Personas?.Find(PersonaListViewModel.BuiltinDefaultId) is not null
            ? PersonaListViewModel.BuiltinDefaultId
            : null;
        vm.Chat.SaveActivePersona(defaultId);
        ClosePersonaPopup();
    }

    /// <summary>Pick a persona from the popup list. The clicked Button.Tag
    /// carries the persona id.</summary>
    private void OnPickPersonaClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ComposerViewModel vm) return;
        if (sender is not FrameworkElement fe) return;
        var id = fe.Tag?.ToString();
        if (string.IsNullOrEmpty(id)) return;
        vm.Chat.SaveActivePersona(id);
        ClosePersonaPopup();
    }

    private void OnOpenImageWorkbenchClick(object sender, RoutedEventArgs e)
    {
        ClosePersonaPopup();
        if (Window.GetWindow(this)?.DataContext is MainViewModel mainVm)
            mainVm.OpenImageWorkbenchTask();
    }

    /// <summary>Open Settings directly into an unsaved new-persona editor.
    /// The persona is only added to the registry after the user saves it.</summary>
    private void OnNewPersonaClick(object sender, RoutedEventArgs e)
    {
        ClosePersonaPopup();
        RequestOpenPersonaManagement(startNewPersona: true);
    }

    private void OnManagePersonasClick(object sender, RoutedEventArgs e)
    {
        ClosePersonaPopup();
        RequestOpenPersonaManagement(startNewPersona: false);
    }

    private void ClosePersonaPopup()
    {
        if (FindName("PersonaToggle") is System.Windows.Controls.Primitives.ToggleButton tb)
            tb.IsChecked = false;
    }

    /// <summary>
    /// Surface a request to the host MainWindow to open the persona management
    /// surface. Implemented in Batch 4 by routing through
    /// <c>MainViewModel.OpenSettingsCommand</c> with a tab selector.
    /// In Batch 3 this just opens Settings — the user will see the personas
    /// tab once it's wired.
    /// </summary>
    private void RequestOpenPersonaManagement(bool startNewPersona)
    {
        var window = Window.GetWindow(this);
        if (window?.DataContext is MainViewModel mainVm)
        {
            mainVm.RequestPersonaSettings(startNewPersona);
            mainVm.OpenSettingsCommand.Execute(null);
        }
    }
}
