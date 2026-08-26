using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MolaGPT.Presentation;

namespace MolaGPT.App.Rendering;

/// <summary>Native Avalonia view for the tool markup embedded in MolaGPT deltas.</summary>
public sealed class MolaGptMarkupBlockView : ContentControl
{
    public static readonly StyledProperty<MarkupUnitBlock?> BlockProperty =
        AvaloniaProperty.Register<MolaGptMarkupBlockView, MarkupUnitBlock?>(nameof(Block));

    public MarkupUnitBlock? Block
    {
        get => GetValue(BlockProperty);
        set => SetValue(BlockProperty, value);
    }

    private bool _attached;

    static MolaGptMarkupBlockView()
    {
        BlockProperty.Changed.AddClassHandler<MolaGptMarkupBlockView>((view, _) => view.Rebuild());
    }

    public MolaGptMarkupBlockView()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ActualThemeVariantChanged += OnThemeChanged;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        _attached = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        if (!_attached) return;

        Content = Block is { } block ? Build(block) : null;
        IsVisible = Content is not null;
    }

    private Control? Build(MarkupUnitBlock block) => block.Unit.Kind switch
    {
        MarkupUnitKind.ToolStatus => BuildToolStatus(block.Unit),
        MarkupUnitKind.DsAnalysis when ShouldRenderAnalysis(block.Unit) => BuildAnalysis(block.Unit),
        MarkupUnitKind.DsAnalysis => BuildAbsorbedStatus(block.Unit),
        MarkupUnitKind.SteelStep => BuildSteelStep(block.Unit),
        MarkupUnitKind.ImagePendingSkeleton => BuildSimpleStatus("正在生成图片", "\uEB9F", false),
        MarkupUnitKind.ImageErrorCard => BuildSimpleStatus(
            string.IsNullOrWhiteSpace(block.Unit.Inner) ? "图片生成失败" : block.Unit.Inner!,
            "\uE7BA", true),
        _ => null
    };

    private Control BuildToolStatus(MolaGptMarkupSplitter.MarkupUnit unit)
    {
        var isSearch = unit.Tag?.Contains("tool-search-blockquote", StringComparison.OrdinalIgnoreCase) == true;
        var title = string.IsNullOrWhiteSpace(unit.Inner)
            ? isSearch ? "正在联网搜索" : "工具运行中"
            : unit.Inner!;

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(BuildHeader(
            title,
            isSearch ? "\uE721" : "\uE90F",
            StatusText(unit.ToolVariant),
            unit.ToolVariant == MolaGptMarkupSplitter.Variant.Error));

        if (isSearch && unit.SearchChips is { Count: > 0 } chips)
        {
            var wrap = new WrapPanel();
            foreach (var chip in chips)
                wrap.Children.Add(BuildSearchChip(chip));
            stack.Children.Add(wrap);
        }

        return Card(stack);
    }

    /// <summary>
    /// The call's own card: the status line the splitter folded in
    /// (<see cref="MolaGptMarkupSplitter.MarkupUnit.StatusText"/>) becomes the
    /// subtitle, the analysis body the fold — so 输入 and 输出 live in the same
    /// card the header describes, instead of a card plus a separate rule-and-fold
    /// underneath it.
    /// </summary>
    private Control BuildAnalysis(MolaGptMarkupSplitter.MarkupUnit unit)
    {
        var expander = new Expander
        {
            IsExpanded = !unit.IsClosed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Header = BuildHeader(
                AnalysisTitle(unit.Tag),
                AnalysisIcon(unit.Tag),
                unit.IsClosed ? "已完成" : "运行中",
                string.Equals(unit.AnalysisPhase, "error", StringComparison.OrdinalIgnoreCase),
                unit.StatusText)
        };
        expander.Classes.Add("toolbody");

        if (!string.IsNullOrWhiteSpace(unit.Inner))
        {
            var body = new ItemsControl
            {
                ItemsSource = MessageDocumentParser.Parse(unit.Inner).Blocks
            };
            expander.Content = new ScrollViewer
            {
                Content = body,
                MaxHeight = 620,
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        return Card(expander, maxWidth: 900);
    }

    /// <summary>
    /// An analysis type this view does not draw (image-gen, image-analyze, …)
    /// still carries the status line the splitter folded into it, so the status
    /// card is drawn on its own rather than disappearing with the body.
    /// </summary>
    private Control? BuildAbsorbedStatus(MolaGptMarkupSplitter.MarkupUnit unit) =>
        string.IsNullOrWhiteSpace(unit.StatusText)
            ? null
            : Card(BuildHeader(
                unit.StatusText!,
                AnalysisIcon(unit.Tag),
                unit.IsClosed ? "已完成" : "运行中",
                string.Equals(unit.AnalysisPhase, "error", StringComparison.OrdinalIgnoreCase)));

    private Control BuildSteelStep(MolaGptMarkupSplitter.MarkupUnit unit)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(BuildHeader(
            string.IsNullOrWhiteSpace(unit.Inner) ? "执行步骤" : unit.Inner!,
            "\uE90F",
            StatusText(unit.ToolVariant),
            unit.ToolVariant == MolaGptMarkupSplitter.Variant.Error));

        if (unit.SteelMetaItems is { Count: > 0 } items)
        {
            var wrap = new WrapPanel();
            foreach (var item in items)
            {
                wrap.Children.Add(Chip(new TextBlock
                {
                    Text = item.Text,
                    FontSize = 12,
                    Foreground = Brush("Brush.Text.Secondary")
                }));
            }
            stack.Children.Add(wrap);
        }

        return Card(stack);
    }

    private Control BuildSimpleStatus(string title, string icon, bool error) =>
        Card(BuildHeader(title, icon, error ? "出错" : "运行中", error));

    private Grid BuildHeader(string title, string icon, string status, bool error, string? subtitle = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("32,10,*,Auto") };

        var iconTile = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Top,
            Background = error ? Brush("Brush.Bg.Tertiary") : Brush("Brush.Primary.Tint"),
            Child = new TextBlock
            {
                Text = icon,
                FontFamily = Font("Font.Icon"),
                FontSize = 14,
                Foreground = Brush(error ? "Brush.Error" : "Brush.Primary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        grid.Children.Add(iconTile);

        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Brush.Text.Primary")
        });

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            titles.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = Brush("Brush.Text.Muted")
            });
        }
        else
        {
            // One line of text next to a 32px tile centres better than it aligns.
            iconTile.VerticalAlignment = VerticalAlignment.Center;
        }

        Grid.SetColumn(titles, 2);
        grid.Children.Add(titles);

        var statusText = new TextBlock
        {
            Text = status,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(error ? "Brush.Error" : "Brush.Text.Muted")
        };
        var statusChip = new Border
        {
            Background = Brush(error ? "Brush.Bg.Tertiary" : "Brush.Primary.Tint"),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = statusText
        };
        Grid.SetColumn(statusChip, 3);
        grid.Children.Add(statusChip);

        return grid;
    }

    private Control BuildSearchChip(MolaGptMarkupSplitter.ToolSearchChip chip)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(new TextBlock
        {
            Text = "\uE721",
            FontFamily = Font("Font.Icon"),
            FontSize = 10,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = chip.Text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });

        foreach (var badge in chip.Badges)
        {
            row.Children.Add(new TextBlock
            {
                Text = badge,
                FontSize = 10,
                Foreground = Brush("Brush.Text.Muted"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        return Chip(row);
    }

    private Border Chip(Control child) => new()
    {
        Background = Brush("Brush.Bg.Elevated"),
        BorderBrush = Brush("Brush.Border.Subtle"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8, 4),
        Margin = new Thickness(0, 0, 6, 6),
        Child = child
    };

    private Border Card(Control child, double maxWidth = 720) => new()
    {
        Background = Brush("Brush.Bg.Primary"),
        BorderBrush = Brush("Brush.Border"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16, 14),
        Margin = new Thickness(0, 8),
        MaxWidth = maxWidth,
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = child
    };

    private static bool ShouldRenderAnalysis(MolaGptMarkupSplitter.MarkupUnit unit) =>
        unit.Tag?.ToLowerInvariant() is "python" or "mcp" or "image-action";

    private static string AnalysisTitle(string? type) => type?.ToLowerInvariant() switch
    {
        "python" => "分析过程",
        "mcp" => "连接器调用",
        "image-action" => "图片处理",
        _ => "工具过程"
    };

    private static string AnalysisIcon(string? type) => type?.ToLowerInvariant() switch
    {
        "python" => "\uE943",
        "mcp" => "\uE8F1",
        "image-action" => "\uEB9F",
        _ => "\uE90F"
    };

    private static string StatusText(MolaGptMarkupSplitter.Variant variant) => variant switch
    {
        MolaGptMarkupSplitter.Variant.Completed or MolaGptMarkupSplitter.Variant.Success => "已完成",
        MolaGptMarkupSplitter.Variant.Error => "出错",
        MolaGptMarkupSplitter.Variant.Info => "信息",
        _ => "运行中"
    };

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    private FontFamily Font(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is FontFamily family
            ? family
            : FontFamily.Default;
}
