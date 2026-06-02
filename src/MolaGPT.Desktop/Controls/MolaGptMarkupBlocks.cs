using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace MolaGPT.Desktop.Controls;

/// <summary>
/// WPF visual representations of the MolaGPT custom markup units.
/// Built as plain UIElements so they slot into a FlowDocument via
/// BlockUIContainer alongside Markdig-produced Block elements.
///
/// The renderer keeps custom tool output compact: status chips, collapsible
/// analysis panels, search cards, and browser-step timeline rows are all built
/// as native WPF elements.
/// </summary>
internal static class MolaGptMarkupBlocks
{
    public static UIElement BuildMarkdownQuote(string markdown, FrameworkElement resourceHost)
    {
        var presenter = new MarkdownPresenter
        {
            Markdown = markdown,
            IsStreaming = false
        };

        var content = new Border
        {
            Background = GetBrush(resourceHost, "Brush.Primary.Blockquote", WithAlpha(GetBrush(resourceHost, "Brush.Primary", Brushes.HotPink), 0.05)),
            CornerRadius = new CornerRadius(0, 6, 6, 0),
            Padding = new Thickness(16, 8, 12, 8),
            Child = presenter
        };

        var grid = new Grid
        {
            Margin = new Thickness(0, 12, 0, 12)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border
        {
            Background = GetBrush(resourceHost, "Brush.Primary", Brushes.HotPink),
            CornerRadius = new CornerRadius(3, 0, 0, 3)
        });
        grid.Children.Add(content);
        Grid.SetColumn(content, 1);

        return grid;
    }

    public static UIElement BuildToolStatus(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement resourceHost)
    {
        return IsSearchStatus(unit)
            ? new ToolSearchStatusCard(unit, resourceHost)
            : new ToolStatusCard(unit, resourceHost);
    }

    public static bool TryUpdateToolStatus(UIElement element, MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement resourceHost)
    {
        if (element is ToolStatusCard status)
        {
            if (IsSearchStatus(unit)) return false;
            status.Update(unit, resourceHost);
            return true;
        }

        if (element is ToolSearchStatusCard search)
        {
            if (!IsSearchStatus(unit)) return false;
            search.Update(unit, resourceHost);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Renders a DSanalysis block as a collapsible container whose body is
    /// markdown text rendered through a nested <see cref="MarkdownPresenter"/>.
    /// While the block is still streaming (<see cref="MolaGptMarkupSplitter.MarkupUnit.IsClosed"/>
    /// is false) the header reads "工具运行中…" so the user knows it is live.
    /// </summary>
    public static UIElement BuildDsAnalysis(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement resourceHost, bool hasFollowingContent = false)
    {
        return new DsAnalysisCard(unit, resourceHost, hasFollowingContent);
    }

    public static bool TryUpdateDsAnalysis(UIElement element, MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement resourceHost, bool hasFollowingContent = false)
    {
        if (element is not DsAnalysisCard card) return false;
        card.Update(unit, resourceHost, hasFollowingContent);
        return true;
    }

    public static UIElement BuildSteelStep(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement resourceHost)
    {
        return new SteelStepCard(unit, resourceHost);
    }

    public static bool TryUpdateSteelStep(UIElement element, MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement resourceHost)
    {
        if (element is not SteelStepCard card) return false;
        card.Update(unit, resourceHost);
        return true;
    }

    public static UIElement BuildImagePendingSkeleton(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement resourceHost)
    {
        return new ImagePendingSkeletonCard(unit, resourceHost);
    }

    public static bool TryUpdateImagePendingSkeleton(UIElement element, MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement resourceHost)
    {
        if (element is not ImagePendingSkeletonCard card) return false;
        card.Update(unit, resourceHost);
        return true;
    }

    public static UIElement BuildImageErrorCard(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement resourceHost)
    {
        return new AiImageErrorCard(unit, resourceHost);
    }

    public static bool TryUpdateImageErrorCard(UIElement element, MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement resourceHost)
    {
        if (element is not AiImageErrorCard card) return false;
        card.Update(unit, resourceHost);
        return true;
    }

    private static string MapHeaderForType(string? type, bool isClosed)
    {
        return type?.ToLowerInvariant() switch
        {
            "python" => "分析过程",
            "mcp" => "连接器调用",
            "image-gen" => "图片生成",
            "image-analyze" => "图片分析",
            "image-action" => "图片处理",
            _ => "工具过程"
        };
    }

    private static string MapIconForVariant(MolaGptMarkupSplitter.Variant variant) => variant switch
    {
        MolaGptMarkupSplitter.Variant.Completed or MolaGptMarkupSplitter.Variant.Success => "\uE73E",
        MolaGptMarkupSplitter.Variant.Error => "\uE7BA",
        MolaGptMarkupSplitter.Variant.Info => "\uE946",
        _ => "\uE90F"
    };

    private static string MapIconForTool(string? type) => type?.ToLowerInvariant() switch
    {
        "python" => "\uE943",
        "mcp" => "\uE8F1",
        "image-gen" => "\uEB9F",
        "image-analyze" => "\uEB9F",
        "image-action" => "\uEB9F",
        _ => "\uE90F"
    };

    private static string MapIconForFontAwesome(string? className) =>
        (className ?? string.Empty).ToLowerInvariant() switch
        {
            var c when c.Contains("fa-eye") => "\uE890",
            var c when c.Contains("fa-list") => "\uE8FD",
            var c when c.Contains("fa-link") => "\uE71B",
            var c when c.Contains("fa-search") => "\uE721",
            var c when c.Contains("fa-mouse-pointer") || c.Contains("fa-hand-pointer") => "\uE7C9",
            var c when c.Contains("fa-crosshairs") => "\uE7C9",
            var c when c.Contains("fa-route") => "\uE8B7",
            var c when c.Contains("fa-keyboard") => "\uE765",
            var c when c.Contains("fa-check") => "\uE73E",
            var c when c.Contains("fa-times") || c.Contains("fa-exclamation") => "\uE711",
            var c when c.Contains("fa-globe") => "\uE774",
            var c when c.Contains("fa-file") => "\uE7C3",
            var c when c.Contains("fa-code") => "\uE943",
            _ => "\uE890"
        };

    public static bool ShouldRenderDsAnalysis(MolaGptMarkupSplitter.MarkupUnit unit)
    {
        var type = unit.Tag?.ToLowerInvariant();
        return type is "python" or "mcp" or "image-action";
    }

    private static bool IsSearchStatus(MolaGptMarkupSplitter.MarkupUnit unit)
    {
        return unit.Tag?.IndexOf("tool-search-blockquote", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static Brush VariantForeground(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement host)
    {
        return unit.ToolVariant switch
        {
            MolaGptMarkupSplitter.Variant.Completed or MolaGptMarkupSplitter.Variant.Success
                => GetBrush(host, "Brush.Success", Brushes.Green),
            MolaGptMarkupSplitter.Variant.Error
                => GetBrush(host, "Brush.Error", Brushes.Red),
            MolaGptMarkupSplitter.Variant.Info
                => GetBrush(host, "Brush.Info", Brushes.SteelBlue),
            _ => GetBrush(host, "Brush.Primary", Brushes.HotPink)
        };
    }

    private static Brush VariantBackground(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement host)
    {
        return unit.ToolVariant switch
        {
            MolaGptMarkupSplitter.Variant.Completed or MolaGptMarkupSplitter.Variant.Success
                => WithAlpha(VariantForeground(unit, host), 0.06),
            MolaGptMarkupSplitter.Variant.Error
                => WithAlpha(VariantForeground(unit, host), 0.08),
            _ => GetBrush(host, "Brush.Bg.Tertiary", Brushes.WhiteSmoke)
        };
    }

    private static Brush WithAlpha(Brush brush, double alpha)
    {
        if (brush is SolidColorBrush solid)
        {
            var c = solid.Color;
            return new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B));
        }

        return brush;
    }

    private static void ApplyPulse(UIElement element, bool enabled)
    {
        if (!enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            return;
        }

        var pulse = new DoubleAnimation(0.48, 1.0, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        element.BeginAnimation(UIElement.OpacityProperty, pulse);
    }

    private static Brush GetBrush(FrameworkElement host, string key, Brush fallback)
    {
        try { return host.TryFindResource(key) as Brush ?? fallback; }
        catch { return fallback; }
    }

    private static CornerRadius GetCornerRadius(FrameworkElement host, string key, CornerRadius fallback)
    {
        try
        {
            if (host.TryFindResource(key) is CornerRadius cr) return cr;
        }
        catch { }
        return fallback;
    }

    private sealed class ToolStatusCard : Border
    {
        private readonly TextBlock _icon;
        private readonly TextBlock _label;

        public ToolStatusCard(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement host)
        {
            CornerRadius = GetCornerRadius(host, "Radius.Web.Sm", new CornerRadius(6));
            Padding = new Thickness(10, 5, 10, 5);
            Margin = new Thickness(0, 4, 0, 4);
            HorizontalAlignment = HorizontalAlignment.Left;

            _icon = new TextBlock
            {
                FontFamily = GetFont(host, "Font.Icon"),
                FontSize = 11,
                Width = 16,
                Height = 16,
                LineHeight = 16,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Center,
                Opacity = 0.78,
                VerticalAlignment = VerticalAlignment.Center
            };

            _label = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                LineHeight = 18,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };

            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    _icon,
                    new Border { Width = 6 },
                    _label
                }
            };

            Update(unit, host);
        }

        public void Update(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement host)
        {
            Background = VariantBackground(unit, host);
            _icon.Text = MapIconForVariant(unit.ToolVariant);
            _icon.Foreground = VariantForeground(unit, host);
            _label.Text = ResolveToolStatusLabel(unit);
            _label.Foreground = unit.ToolVariant == MolaGptMarkupSplitter.Variant.Error
                ? VariantForeground(unit, host)
                : GetBrush(host, "Brush.Text.Secondary", Brushes.DimGray);

            ApplyPulse(_icon, unit.ToolVariant is MolaGptMarkupSplitter.Variant.Analyzing or MolaGptMarkupSplitter.Variant.None);
        }

        private static string ResolveToolStatusLabel(MolaGptMarkupSplitter.MarkupUnit unit)
        {
            if (unit.Tag?.IndexOf("tool-image-blockquote", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return unit.ToolVariant switch
                {
                    MolaGptMarkupSplitter.Variant.Completed or MolaGptMarkupSplitter.Variant.Success => "绘制完成",
                    MolaGptMarkupSplitter.Variant.Error => "绘制失败",
                    _ => string.IsNullOrWhiteSpace(unit.Inner) ? "正在绘制" : unit.Inner
                };
            }

            return string.IsNullOrWhiteSpace(unit.Inner) ? "正在处理..." : unit.Inner;
        }
    }

    private sealed class ImagePendingSkeletonCard : Border
    {
        private readonly Border _glow;

        public ImagePendingSkeletonCard(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement host)
        {
            Width = 480;
            Height = 480;
            MaxWidth = 480;
            MaxHeight = 480;
            CornerRadius = new CornerRadius(12);
            Margin = new Thickness(0, 10, 0, 10);
            ClipToBounds = true;
            HorizontalAlignment = HorizontalAlignment.Left;
            BorderBrush = WithAlpha(GetBrush(host, "Brush.Text.Secondary", Brushes.SteelBlue), 0.15);
            BorderThickness = new Thickness(1);
            Background = BuildImageSkeletonBrush();

            _glow = new Border
            {
                Background = WithAlpha(Brushes.White, 0.20),
                Width = 120,
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = new TranslateTransform(-140, 0)
            };
            Child = new Grid
            {
                Children = { _glow }
            };

            BeginShimmer();
            Update(unit, host);
        }

        public void Update(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement host)
        {
            Visibility = unit.AnalysisPhase is "completed" or "success" or "error"
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void BeginShimmer()
        {
            if (_glow.RenderTransform is not TranslateTransform transform) return;
            transform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(-140, 520, TimeSpan.FromSeconds(1.8))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                });
        }

        private static Brush BuildImageSkeletonBrush()
        {
            var group = new DrawingGroup();
            void Add(Color color, Point center, double radiusX, double radiusY)
            {
                var brush = new RadialGradientBrush
                {
                    Center = center,
                    GradientOrigin = center,
                    RadiusX = radiusX,
                    RadiusY = radiusY,
                    MappingMode = BrushMappingMode.RelativeToBoundingBox
                };
                brush.GradientStops.Add(new GradientStop(color, 0));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
                group.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
            }

            Add(Color.FromArgb(190, 220, 20, 60), new Point(0.12, 0.25), 0.55, 0.45);
            Add(Color.FromArgb(180, 255, 200, 0), new Point(0.88, 0.12), 0.42, 0.55);
            Add(Color.FromArgb(166, 0, 200, 130), new Point(0.75, 0.88), 0.55, 0.42);
            Add(Color.FromArgb(174, 0, 140, 255), new Point(0.08, 0.75), 0.45, 0.52);
            Add(Color.FromArgb(128, 180, 0, 255), new Point(0.55, 0.45), 0.36, 0.36);

            return new DrawingBrush(group)
            {
                Stretch = Stretch.Fill
            };
        }
    }

    private sealed class AiImageErrorCard : Border
    {
        private readonly TextBlock _title;
        private readonly TextBlock _message;

        public AiImageErrorCard(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement host)
        {
            MaxWidth = 480;
            Padding = new Thickness(14, 12, 14, 12);
            Margin = new Thickness(0, 8, 0, 12);
            HorizontalAlignment = HorizontalAlignment.Left;
            CornerRadius = GetCornerRadius(host, "Radius.Web.Sm", new CornerRadius(8));
            BorderThickness = new Thickness(1);

            _title = new TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                LineHeight = 20,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.Wrap
            };
            _message = new TextBlock
            {
                FontSize = 13,
                LineHeight = 20,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(22, 5, 0, 0)
            };

            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Border
                    {
                        Width = 16,
                        Height = 16,
                        CornerRadius = new CornerRadius(999),
                        Margin = new Thickness(0, 2, 6, 0),
                        Child = new TextBlock
                        {
                            Text = "!",
                            FontSize = 11,
                            FontWeight = FontWeights.Bold,
                            TextAlignment = TextAlignment.Center,
                            LineHeight = 16,
                            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
                        }
                    },
                    _title
                }
            };

            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children = { titleRow, _message }
            };

            Update(unit, host);
        }

        public void Update(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement host)
        {
            var error = GetBrush(host, "Brush.Error", Brushes.Red);
            Background = WithAlpha(error, 0.08);
            BorderBrush = WithAlpha(error, 0.24);
            _title.Text = string.IsNullOrWhiteSpace(unit.Tag) ? "图片绘制失败" : unit.Tag;
            _title.Foreground = error;
            _message.Text = string.IsNullOrWhiteSpace(unit.Inner) ? "未知错误" : unit.Inner;
            _message.Foreground = GetBrush(host, "Brush.Text.Secondary", Brushes.DimGray);
        }
    }

    private sealed class ToolSearchStatusCard : Border
    {
        private readonly TextBlock _icon;
        private readonly TextBlock _title;
        private readonly WrapPanel _chips;

        public ToolSearchStatusCard(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement host)
        {
            CornerRadius = GetCornerRadius(host, "Radius.Web.Sm", new CornerRadius(6));
            Padding = new Thickness(14, 12, 14, 12);
            Margin = new Thickness(0, 4, 0, 4);
            HorizontalAlignment = HorizontalAlignment.Left;
            MaxWidth = 760;

            _icon = new TextBlock
            {
                Text = "\uE721",
                FontFamily = GetFont(host, "Font.Icon"),
                FontSize = 12,
                Width = 16,
                Height = 16,
                LineHeight = 16,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _title = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                LineHeight = 18,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };

            _chips = new WrapPanel
            {
                Margin = new Thickness(0, 10, 0, 0)
            };

            var titleRow = new Grid
            {
                MinHeight = 20,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.Children.Add(_icon);
            Grid.SetColumn(_icon, 0);
            titleRow.Children.Add(_title);
            Grid.SetColumn(_title, 2);

            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children = { titleRow, _chips }
            };

            Update(unit, host);
        }

        public void Update(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement host)
        {
            Background = GetBrush(host, "Brush.Bg.Tertiary", Brushes.WhiteSmoke);
            _icon.Foreground = VariantForeground(unit, host);
            _title.Foreground = GetBrush(host, "Brush.Text.Secondary", Brushes.DimGray);
            _title.Text = string.IsNullOrWhiteSpace(unit.Inner) ? "正在联网搜索" : unit.Inner;
            ApplyPulse(_title, unit.ToolVariant is MolaGptMarkupSplitter.Variant.Analyzing or MolaGptMarkupSplitter.Variant.None);

            _chips.Children.Clear();
            foreach (var chip in unit.SearchChips ?? Array.Empty<MolaGptMarkupSplitter.ToolSearchChip>())
            {
                _chips.Children.Add(BuildSearchChip(chip, host));
            }
            _chips.Visibility = _chips.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static UIElement BuildSearchChip(MolaGptMarkupSplitter.ToolSearchChip chip, FrameworkElement host)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(new TextBlock
            {
                Text = "\uE721",
                FontFamily = GetFont(host, "Font.Icon"),
                FontSize = 12,
                Width = 16,
                Height = 18,
                LineHeight = 18,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Center,
                Opacity = 0.85,
                Foreground = GetBrush(host, "Brush.Text.Secondary", Brushes.DimGray),
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = chip.Text,
                FontSize = 13,
                Margin = new Thickness(6, 0, 0, 0),
                LineHeight = 18,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = GetBrush(host, "Brush.Text.Primary", Brushes.Black),
                VerticalAlignment = VerticalAlignment.Center
            });

            foreach (var badge in chip.Badges)
            {
                row.Children.Add(new Border
                {
                    Background = GetBrush(host, "Brush.Bg.Tertiary", Brushes.WhiteSmoke),
                    CornerRadius = GetCornerRadius(host, "Radius.Sm", new CornerRadius(6)),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(7, 0, 0, 0),
                    Child = new TextBlock
                    {
                        Text = badge,
                        FontSize = 11,
                        LineHeight = 16,
                        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                        Foreground = GetBrush(host, "Brush.Text.Secondary", Brushes.DimGray)
                    }
                });
            }

            return new Border
            {
                Background = GetBrush(host, "Brush.Bg.Primary", Brushes.White),
                BorderBrush = GetBrush(host, "Brush.Primary.Border", Brushes.Pink),
                BorderThickness = new Thickness(1),
                CornerRadius = GetCornerRadius(host, "Radius.Md", new CornerRadius(10)),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Child = row
            };
        }
    }

    private sealed class SteelStepCard : Grid
    {
        private readonly Border _line;
        private readonly Border _dot;
        private readonly SteelGlyph _dotIcon;
        private readonly Border _card;
        private readonly TextBlock _title;
        private readonly WrapPanel _meta;
        private MolaGptMarkupSplitter.Variant _phase = MolaGptMarkupSplitter.Variant.None;
        private bool _isPulsing;
        private string? _lastIconClass;
        private string? _lastMetaKey;
        private string? _lastTitle;

        public SteelStepCard(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement host)
        {
            Margin = new Thickness(0, 8, 0, 8);
            HorizontalAlignment = HorizontalAlignment.Left;
            MaxWidth = 820;
            Opacity = 0;

            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _line = new Border
            {
                Width = 2,
                Margin = new Thickness(0, 22, 0, -12),
                HorizontalAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(2),
                IsHitTestVisible = false
            };
            Children.Add(_line);
            Grid.SetColumn(_line, 0);

            _dotIcon = new SteelGlyph
            {
                Width = 28,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _dot = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(999),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Children = { _dotIcon }
                }
            };
            Children.Add(_dot);
            Grid.SetColumn(_dot, 0);

            _title = new TextBlock
            {
                FontFamily = GetFont(host, "Font.Cjk"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                LineHeight = 18,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 7)
            };
            _meta = new WrapPanel();

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children = { _title, _meta }
            };
            _card = new Border
            {
                CornerRadius = GetCornerRadius(host, "Radius.Web.Sm", new CornerRadius(6)),
                Padding = new Thickness(12, 9, 12, 9),
                Child = stack
            };
            Children.Add(_card);
            Grid.SetColumn(_card, 2);

            Update(unit, host);
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        public void Update(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement host)
        {
            var incomingPhase = unit.ToolVariant == MolaGptMarkupSplitter.Variant.None
                ? MolaGptMarkupSplitter.Variant.Analyzing
                : unit.ToolVariant;
            var phase = (_phase is MolaGptMarkupSplitter.Variant.Completed or MolaGptMarkupSplitter.Variant.Error
                    && incomingPhase == MolaGptMarkupSplitter.Variant.Analyzing)
                ? _phase
                : incomingPhase;
            var title = string.IsNullOrWhiteSpace(unit.Inner) ? "正在查看网页" : unit.Inner;
            var phaseChanged = _phase != phase;
            _phase = phase;

            if (!string.Equals(_lastTitle, title, StringComparison.Ordinal))
            {
                _title.Text = title;
                _lastTitle = title;
            }
            _title.Foreground = GetBrush(host, "Brush.Text.Primary", Brushes.Black);
            if (!string.Equals(_lastIconClass, unit.SteelIconClass, StringComparison.Ordinal))
            {
                _dotIcon.IconClass = unit.SteelIconClass;
                _lastIconClass = unit.SteelIconClass;
            }

            var textSecondary = GetBrush(host, "Brush.Text.Secondary", Brushes.DimGray);
            var primary = GetBrush(host, "Brush.Primary", Brushes.HotPink);
            var error = GetBrush(host, "Brush.Error", Brushes.Red);
            _card.Background = phase == MolaGptMarkupSplitter.Variant.Error
                ? WithAlpha(error, 0.08)
                : GetBrush(host, "Brush.Bg.Tertiary", Brushes.WhiteSmoke);
            _dot.Background = GetBrush(host, "Brush.Bg.Primary", Brushes.White);
            _dot.BorderBrush = phase switch
            {
                MolaGptMarkupSplitter.Variant.Error => WithAlpha(error, 0.35),
                MolaGptMarkupSplitter.Variant.Analyzing => WithAlpha(primary, 0.35),
                _ => GetBrush(host, "Brush.Border", Brushes.LightGray)
            };
            _dotIcon.Foreground = phase switch
            {
                MolaGptMarkupSplitter.Variant.Error => error,
                MolaGptMarkupSplitter.Variant.Analyzing => primary,
                _ => textSecondary
            };
            _line.Background = phase == MolaGptMarkupSplitter.Variant.Error
                ? WithAlpha(error, 0.24)
                : WithAlpha(textSecondary, phase == MolaGptMarkupSplitter.Variant.Completed ? 0.28 : 0.18);

            var shouldPulse = phase == MolaGptMarkupSplitter.Variant.Analyzing;
            if (phaseChanged || _isPulsing != shouldPulse)
            {
                ApplyPulse(_title, shouldPulse);
                _isPulsing = shouldPulse;
            }

            var metaItems = unit.SteelMetaItems ?? Array.Empty<MolaGptMarkupSplitter.SteelMetaItem>();
            var metaKey = BuildSteelMetaKey(metaItems);
            if (!string.Equals(_lastMetaKey, metaKey, StringComparison.Ordinal))
            {
                _meta.Children.Clear();
                foreach (var item in metaItems)
                    _meta.Children.Add(BuildSteelMetaChip(item, host));
                _lastMetaKey = metaKey;
            }
            _meta.Visibility = _meta.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string BuildSteelMetaKey(IReadOnlyList<MolaGptMarkupSplitter.SteelMetaItem> items)
        {
            if (items.Count == 0) return string.Empty;
            var builder = new StringBuilder();
            foreach (var item in items)
            {
                builder.Append(item.IconClass);
                builder.Append('\u001f');
                builder.Append(item.Text);
                builder.Append('\u001e');
            }
            return builder.ToString();
        }

        private static UIElement BuildSteelMetaChip(MolaGptMarkupSplitter.SteelMetaItem item, FrameworkElement host)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            row.Children.Add(new TextBlock
            {
                Text = MapIconForFontAwesome(item.IconClass),
                FontFamily = GetFont(host, "Font.Icon"),
                FontSize = 11,
                Width = 14,
                Height = 16,
                LineHeight = 16,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Center,
                Foreground = GetBrush(host, "Brush.Text.Secondary", Brushes.DimGray),
                VerticalAlignment = VerticalAlignment.Center
            });

            row.Children.Add(new TextBlock
            {
                Text = item.Text,
                FontFamily = GetFont(host, "Font.Cjk"),
                FontSize = 12,
                LineHeight = 16,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 520,
                Margin = new Thickness(5, 0, 0, 0),
                Foreground = GetBrush(host, "Brush.Text.Secondary", Brushes.DimGray),
                VerticalAlignment = VerticalAlignment.Center
            });

            return new Border
            {
                Background = GetBrush(host, "Brush.Bg.Secondary", Brushes.WhiteSmoke),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Child = row
            };
        }
    }

    private sealed class SteelGlyph : FrameworkElement
    {
        private string? _iconClass;
        private Brush _foreground = Brushes.Gray;

        public string? IconClass
        {
            get => _iconClass;
            set
            {
                _iconClass = value;
                InvalidateVisual();
            }
        }

        public Brush Foreground
        {
            get => _foreground;
            set
            {
                _foreground = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            var size = Math.Min(14, Math.Min(ActualWidth, ActualHeight));
            var x = (ActualWidth - size) / 2.0;
            var y = (ActualHeight - size) / 2.0;
            dc.PushTransform(new TranslateTransform(x, y));
            dc.PushTransform(new ScaleTransform(size / 14.0, size / 14.0));

            var icon = (IconClass ?? string.Empty).ToLowerInvariant();
            if (icon.Contains("fa-list"))
                DrawList(dc);
            else if (icon.Contains("fa-hand-pointer") || icon.Contains("fa-mouse-pointer"))
                DrawPointer(dc);
            else
                DrawEye(dc);

            dc.Pop();
            dc.Pop();
        }

        private void DrawList(DrawingContext dc)
        {
            var pen = CreatePen(1.35);
            for (var i = 0; i < 3; i++)
            {
                var y = 3.5 + i * 3.5;
                dc.DrawEllipse(Foreground, null, new Point(2.8, y), 0.75, 0.75);
                dc.DrawLine(pen, new Point(5, y), new Point(11.8, y));
            }
        }

        private void DrawPointer(DrawingContext dc)
        {
            var geometry = Geometry.Parse("M3.2,2.1 L3.2,9.4 L5.1,7.8 L6.7,11.7 L8.4,11 L6.8,7.2 L9.3,7.2 Z");
            dc.DrawGeometry(null, CreatePen(1.25), geometry);
        }

        private void DrawEye(DrawingContext dc)
        {
            var pen = CreatePen(1.25);
            var geometry = Geometry.Parse("M1.6,7 C3.2,4.4 5.1,3.3 7,3.3 C8.9,3.3 10.8,4.4 12.4,7 C10.8,9.6 8.9,10.7 7,10.7 C5.1,10.7 3.2,9.6 1.6,7 Z");
            dc.DrawGeometry(null, pen, geometry);
            dc.DrawEllipse(null, pen, new Point(7, 7), 1.45, 1.45);
        }

        private Pen CreatePen(double thickness) => new(Foreground, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
    }

    private sealed class DsAnalysisCard : Border
    {
        private readonly TextBlock _icon;
        private readonly TextBlock _title;
        private readonly TextBlock _chevron;
        private readonly Border _headerBorder;
        private readonly Border _bodyFrame;
        private readonly ScrollViewer _bodyScroll;
        private readonly MarkdownPresenter _presenter;
        private string? _toolType;
        private bool _expanded;
        private bool _userToggled;
        private bool _hasBody;
        private bool _wasClosed;
        private bool _autoCollapsed;

        public DsAnalysisCard(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement host, bool hasFollowingContent = false)
        {
            Margin = new Thickness(4, 8, 0, 8);
            HorizontalAlignment = HorizontalAlignment.Left;
            Background = Brushes.Transparent;
            BorderBrush = GetBrush(host, "Brush.Border", Brushes.LightGray);
            BorderThickness = new Thickness(2, 0, 0, 0);
            CornerRadius = new CornerRadius(0);
            ClipToBounds = true;
            MaxWidth = 900;

            _icon = new TextBlock
            {
                FontFamily = GetFont(host, "Font.Icon"),
                FontSize = 11,
                Width = 16,
                Height = 18,
                LineHeight = 18,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Center,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center
            };
            _title = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                LineHeight = 18,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            _chevron = new TextBlock
            {
                Text = "\uE70D",
                FontFamily = GetFont(host, "Font.Icon"),
                FontSize = 10,
                Opacity = 0.75,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var header = new Grid
            {
                Background = Brushes.Transparent,
                MinHeight = 36,
                Margin = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(_icon);
            Grid.SetColumn(_icon, 0);
            var spacer = new Border { Width = 6 };
            header.Children.Add(spacer);
            Grid.SetColumn(spacer, 1);
            header.Children.Add(_title);
            Grid.SetColumn(_title, 2);
            header.Children.Add(_chevron);
            Grid.SetColumn(_chevron, 3);

            _presenter = new MarkdownPresenter
            {
                ThrottleMs = 32,
                CodeBlockMaxHeight = 460
            };

            _bodyScroll = new ScrollViewer
            {
                Content = _presenter,
                MaxHeight = 620,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0),
                CanContentScroll = false
            };
            _bodyScroll.PreviewMouseWheel += (_, e) =>
            {
                if (FindNestedScrollable(e.OriginalSource as DependencyObject, _bodyScroll, e.Delta) is not null)
                    return;
                if (CanScrollVertically(_bodyScroll, e.Delta))
                {
                    e.Handled = true;
                    _bodyScroll.ScrollToVerticalOffset(_bodyScroll.VerticalOffset - e.Delta);
                }
            };

            _bodyFrame = new Border
            {
                Padding = new Thickness(10, 0, 10, 8),
                BorderThickness = new Thickness(0),
                Child = _bodyScroll
            };

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _headerBorder = new Border
            {
                Padding = new Thickness(10, 6, 10, 6),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = header
            };
            _headerBorder.AddHandler(
                UIElement.MouseLeftButtonDownEvent,
                new System.Windows.Input.MouseButtonEventHandler(OnHeaderClicked),
                handledEventsToo: true);
            stack.Children.Add(_headerBorder);
            stack.Children.Add(_bodyFrame);
            Child = stack;

            Update(unit, host, hasFollowingContent);
        }

        private void OnHeaderClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
            _expanded = !_expanded;
            _userToggled = true;
            ApplyExpandedState();
            e.Handled = true;
        }

        public void Update(MolaGptMarkupSplitter.MarkupUnit unit, FrameworkElement host, bool hasFollowingContent = false)
        {
            Visibility = ShouldRenderDsAnalysis(unit) ? Visibility.Visible : Visibility.Collapsed;

            if (!string.Equals(_toolType, unit.Tag, StringComparison.OrdinalIgnoreCase))
            {
                _toolType = unit.Tag;
                _expanded = DefaultExpanded(unit);
                _userToggled = false;
                _wasClosed = unit.IsClosed;
                _autoCollapsed = false;
            }
            else if (!_userToggled)
            {
                // Mirror the web client (core241002.js:4783-4800): keep the card
                // EXPANDED while streaming and even right after it closes, so the
                // execution output stays visible. Only auto-collapse once the
                // model's following answer has appeared — and only a single time,
                // so a later streaming refresh can't re-collapse a card the user
                // reopened.
                if (!unit.IsClosed)
                {
                    _expanded = true;
                }
                else if (hasFollowingContent && !_autoCollapsed)
                {
                    _expanded = false;
                    _autoCollapsed = true;
                }
            }
            _wasClosed = unit.IsClosed;

            _icon.Text = MapIconForTool(unit.Tag);
            _icon.Foreground = unit.AnalysisPhase == "error"
                ? GetBrush(host, "Brush.Error", Brushes.Red)
                : unit.IsClosed
                    ? GetBrush(host, "Brush.Success", Brushes.Green)
                    : GetBrush(host, "Brush.Primary", Brushes.HotPink);
            _title.Text = MapHeaderForType(unit.Tag, unit.IsClosed);
            _title.Foreground = GetBrush(host, "Brush.Text.Secondary", Brushes.DimGray);
            _chevron.Foreground = GetBrush(host, "Brush.Text.Secondary", Brushes.DimGray);
            _headerBorder.Background = Brushes.Transparent;
            _bodyFrame.Background = Brushes.Transparent;
            _presenter.Markdown = unit.Inner ?? string.Empty;
            _presenter.IsStreaming = !unit.IsClosed;
            _hasBody = !string.IsNullOrWhiteSpace(unit.Inner);
            _chevron.Visibility = _hasBody ? Visibility.Visible : Visibility.Hidden;
            ApplyPulse(_icon, !unit.IsClosed || unit.AnalysisPhase == "analyzing");
            ApplyExpandedState();
        }

        private void ApplyExpandedState()
        {
            _bodyFrame.Visibility = _expanded && _hasBody ? Visibility.Visible : Visibility.Collapsed;
            _chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            _chevron.RenderTransform = new RotateTransform(_expanded ? 180 : 0);
        }

        private static bool DefaultExpanded(MolaGptMarkupSplitter.MarkupUnit unit)
        {
            return !unit.IsClosed;
        }

        private static bool CanScrollVertically(ScrollViewer viewer, int wheelDelta)
        {
            if (viewer.ScrollableHeight <= 0) return false;
            return wheelDelta < 0
                ? viewer.VerticalOffset < viewer.ScrollableHeight
                : viewer.VerticalOffset > 0;
        }

        private static ScrollViewer? FindNestedScrollable(DependencyObject? source, ScrollViewer boundary, int wheelDelta)
        {
            var node = source;
            while (node is not null && !ReferenceEquals(node, boundary))
            {
                if (node is ScrollViewer sv && CanScrollVertically(sv, wheelDelta))
                    return sv;
                node = GetTreeParent(node);
            }
            return null;
        }

        private static DependencyObject? GetTreeParent(DependencyObject node)
        {
            if (node is Visual or Visual3D)
                return VisualTreeHelper.GetParent(node);
            if (node is FrameworkContentElement fce)
                return fce.Parent;
            return null;
        }
    }

    private static FontFamily GetFont(FrameworkElement host, string key)
    {
        try
        {
            if (host.TryFindResource(key) is FontFamily ff) return ff;
        }
        catch { }

        return new FontFamily("Segoe UI");
    }
}
