using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;      // ClipboardExtensions.SetTextAsync
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MolaGPT.App.Views;
using MolaGPT.Presentation.Artifacts;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Rendering;

/// <summary>
/// A canvas fence in the answer, in one of two forms the user switches
/// between: a chip (what it is, how big, and the things one does with it), or
/// the ordinary code block it would otherwise have been, with a way onto the
/// canvas in its header.
///
/// Built once per row and updated in place — the row is position-keyed, so a
/// page still being written keeps the same control (and the same button under
/// the pointer) while its line count climbs. The code form is only built the
/// first time it is shown: highlighting a page on every delta is not a cost
/// the chip form should pay.
/// </summary>
public sealed class ArtifactFenceView : UserControl
{
    public static readonly StyledProperty<ArtifactFenceRow?> RowProperty =
        AvaloniaProperty.Register<ArtifactFenceView, ArtifactFenceRow?>(nameof(Row));

    private readonly Border _chip;
    private readonly TextBlock _title;
    private readonly TextBlock _meta;
    private readonly TextBlock _glyph;
    private readonly Button _copy;
    private readonly Button _revise;

    private Border? _codePanel;
    private TextBlock? _codeLanguage;
    private CodeBody? _code;

    private ArtifactFenceRow? _subscribed;

    public ArtifactFenceView()
    {
        _glyph = new TextBlock
        {
            Classes = { "icon" },
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.Gray,
        };
        _glyph.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Brush.Text.Secondary"));

        _title = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _meta = new TextBlock
        {
            Classes = { "muted" },
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var open = new Button
        {
            Classes = { "artifactprimaryaction" },
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "在画布打开", FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        Avalonia.Automation.AutomationProperties.SetName(open, "在画布打开");
        open.Click += (_, _) => Request(ArtifactAction.Open);
        open.PointerEntered += (_, _) => Canvas.CanvasEnvironment.Prewarm();

        _copy = IconButton("", "复制源码", 30, 30);
        _copy.Click += OnCopy;
        _revise = IconButton("", "基于此修改", 30, 30);
        _revise.Click += (_, _) => Request(ArtifactAction.Revise);
        var showCode = IconButton("", "查看代码", 30, 30);
        showCode.Click += (_, _) => Row?.Choose(showCode: true);

        var text = new StackPanel { Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title);
        text.Children.Add(_meta);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(_copy);
        actions.Children.Add(_revise);
        actions.Children.Add(showCode);
        actions.Children.Add(open);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var icon = new Border { Classes = { "artifactchipicon" }, Child = _glyph };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        _chip = new Border { Classes = { "artifactchip" }, Child = grid };
        Content = _chip;
    }

    public ArtifactFenceRow? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != RowProperty) return;

        if (_subscribed is not null) _subscribed.PropertyChanged -= OnRowChanged;
        _subscribed = Row;
        if (_subscribed is not null) _subscribed.PropertyChanged += OnRowChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnRowChanged;
        _subscribed = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_subscribed is null && Row is { } row)
        {
            _subscribed = row;
            row.PropertyChanged += OnRowChanged;
            Refresh();
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (Row is not { } row) return;
        var block = row.Block;

        if (row.ShowCode)
        {
            EnsureCodePanel();
            _codeLanguage!.Text = block.Language;
            _code!.Language = block.Language;
            _code.IsLive = row.IsGenerating;
            _code.Code = block.Code;
            Content = _codePanel;
            return;
        }

        Content = _chip;
        _glyph.Text = row.Kind switch
        {
            ArtifactRenderKind.Html => "",
            ArtifactRenderKind.Svg => "",
            ArtifactRenderKind.Mermaid => "",
            ArtifactRenderKind.Table => "",
            _ => "",
        };
        _title.Text = FenceArtifactCapture.DisplayTitle(block.Code, row.Kind);
        _meta.Text = row.IsGenerating
            ? $"正在生成 · {block.LineCount} 行"
            : $"{FenceArtifactCapture.KindLabel(row.Kind)} · {ArtifactItemViewModel.FormatSize(Encoding.UTF8.GetByteCount(block.Code))} · {block.LineCount} 行";
        // Revising or copying half a page is not something anyone wants.
        _copy.IsEnabled = !row.IsGenerating;
        _revise.IsEnabled = !row.IsGenerating;
    }

    /// <summary>The transcript's code block, as its template draws it, plus
    /// the two ways back out: onto the canvas, or into a chip.</summary>
    private void EnsureCodePanel()
    {
        if (_codePanel is not null) return;

        _codeLanguage = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        _codeLanguage.Bind(TextBlock.FontSizeProperty, this.GetResourceObservable("Font.Size.XSmall"));
        _codeLanguage.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Brush.Text.Muted"));

        var open = IconButton("", "在画布打开", 26, 24);
        open.Click += (_, _) => Request(ArtifactAction.Open);
        open.PointerEntered += (_, _) => Canvas.CanvasEnvironment.Prewarm();
        var collapse = IconButton("", "收起为卡片", 26, 24);
        collapse.Click += (_, _) => Row?.Choose(showCode: false);
        var copy = IconButton("", "复制代码", 26, 24);
        copy.Click += OnCopy;

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(open);
        tools.Children.Add(collapse);
        tools.Children.Add(copy);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 7, 6, 0) };
        header.Children.Add(_codeLanguage);
        Grid.SetColumn(tools, 1);
        header.Children.Add(tools);

        _code = new CodeBody();
        var scroller = new ScrollViewer
        {
            Classes = { "codescroll" },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _code,
        };

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(header);
        Grid.SetRow(scroller, 1);
        body.Children.Add(scroller);

        _codePanel = new Border { Classes = { "codepanel" }, Child = body };
    }

    private void Request(ArtifactAction action)
    {
        if (Row is not { } row) return;
        if (this.FindAncestorOfType<TranscriptView>()?.DataContext is ChatViewModel chat)
            chat.RequestArtifactAction(row.Message, row.Ordinal, action);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Row is not { } row) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(row.Block.Code);
        }
        catch
        {
            // Clipboard contention with another app; nothing useful to say.
        }
    }

    private static Button IconButton(string glyph, string tip, double width, double height)
    {
        var button = new Button
        {
            Classes = { "inlineaction" },
            Width = width,
            Height = height,
            Padding = new Thickness(0),
            Content = new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 13 },
        };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }
}
