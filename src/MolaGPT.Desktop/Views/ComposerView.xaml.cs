using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MolaGPT.Core.Models;
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
        if (e.Key != Key.Enter) return;

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
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
            Filter =
                "图片 (*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp" +
                "|文档 (*.pdf;*.docx;*.txt;*.md)|*.pdf;*.docx;*.txt;*.md" +
                "|代码 (*.py;*.c;*.cpp;*.js;*.ts;*.cs;*.java;*.go;*.rs;*.m;*.json)|" +
                  "*.py;*.c;*.cpp;*.js;*.ts;*.cs;*.java;*.go;*.rs;*.m;*.json" +
                "|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        foreach (var path in dlg.FileNames)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                var (mime, kind) = GuessKind(ext);
                if (kind == AttachmentKind.Image && !vm.CanAcceptImageAttachments)
                {
                    MessageBox.Show(
                        Window.GetWindow(this),
                        "当前模型不支持图片识别。请在模型配置中开启“视觉”，或切换到支持多模态的模型。",
                        "模型不支持图片",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    continue;
                }
                if (kind == AttachmentKind.File && !vm.CanAcceptFileAttachments)
                {
                    MessageBox.Show(
                        Window.GetWindow(this),
                        "自定义模型暂不支持上传文档。可登录 MolaGPT 账号使用沙箱上传，或仅上传图片（需模型支持视觉）。",
                        "暂不支持该附件",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    continue;
                }
                vm.Attachments.Add(new Attachment(
                    Kind: kind,
                    MimeType: mime,
                    Bytes: bytes,
                    FileName: Path.GetFileName(path)));
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    $"无法读取 {path}：{ex.Message}",
                    "附件错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
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

    private static (string mime, AttachmentKind kind) GuessKind(string ext) => ext switch
    {
        "png" => ("image/png", AttachmentKind.Image),
        "jpg" or "jpeg" => ("image/jpeg", AttachmentKind.Image),
        "gif" => ("image/gif", AttachmentKind.Image),
        "webp" => ("image/webp", AttachmentKind.Image),
        "bmp" => ("image/bmp", AttachmentKind.Image),
        "pdf" => ("application/pdf", AttachmentKind.File),
        "docx" => ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", AttachmentKind.File),
        "txt" or "md" => ("text/plain", AttachmentKind.File),
        "json" => ("application/json", AttachmentKind.File),
        _ => ("application/octet-stream", AttachmentKind.File)
    };

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
