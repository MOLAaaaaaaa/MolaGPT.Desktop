using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MolaGPT.App.Rendering;

namespace MolaGPT.App.Views;

public partial class ImageAttachmentEditorWindow : MolaContentWindow
{
    private readonly ImageMarkupView _editor;

    public ImageAttachmentEditorWindow(byte[] bytes)
    {
        InitializeComponent();

        using var stream = new MemoryStream(bytes);
        var bitmap = new Bitmap(stream);
        _editor = new ImageMarkupView(bitmap, ownsBitmap: true, paper: false, allowCrop: true, allowZoom: false);
        PART_EditorHost.Content = _editor;

        PART_Pen.Click += (_, _) => _editor.Tool = _editor.Tool == MarkupTool.Pen ? MarkupTool.None : MarkupTool.Pen;
        PART_Crop.Click += (_, _) => _editor.Tool = _editor.Tool == MarkupTool.Crop ? MarkupTool.None : MarkupTool.Crop;
        PART_Undo.Click += (_, _) => _editor.Undo();
        PART_Cancel.Click += (_, _) => Close(null);
        PART_Save.Click += (_, _) => Close(_editor.CanUndo ? _editor.ExportPng() : null);

        _editor.StateChanged += (_, _) => UpdateEditorState();
        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        _editor.Tool = MarkupTool.Pen;
        UpdateEditorState();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => _editor.Focus(), DispatcherPriority.Input);
    }

    private void UpdateEditorState()
    {
        PART_Pen.IsChecked = _editor.Tool == MarkupTool.Pen;
        PART_Crop.IsChecked = _editor.Tool == MarkupTool.Crop;
        PART_Undo.IsEnabled = _editor.CanUndo;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _editor.IsDragging) return;

        Close(null);
        e.Handled = true;
    }
}
