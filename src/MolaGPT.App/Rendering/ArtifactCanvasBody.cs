using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.VisualBasic.FileIO;
using MolaGPT.App.Rendering.Canvas;
using MolaGPT.Presentation;
using MolaGPT.Presentation.Artifacts;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Rendering;

/// <summary>
/// What the canvas shows for the selected artifact.
///
/// One web view for the lifetime of the canvas, re-navigated rather than
/// recreated: creating a WebView2 costs a few hundred milliseconds and a
/// flash, and the old canvas made a new one on every selection change and
/// every streaming delta. Native renderers (SVG, images, tables, Markdown,
/// source) take over by hiding it — nothing can be layered over a native
/// window, so hiding is the only way to show anything else in its place.
/// </summary>
public sealed class ArtifactCanvasBody : UserControl
{
    private const int MaxTextBytes = 2 * 1024 * 1024;
    // A refresh re-lays out only the lines that changed, so it can come often
    // enough to read as live. It was 250 ms when each one rebuilt the whole file.
    private static readonly TimeSpan StreamingInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RevealTimeout = TimeSpan.FromSeconds(2);

    // Created on first use: the browser process behind it costs ~100 MB,
    // which a user who never opens a page should not pay.
    private CanvasWebView? _web;
    private readonly Grid _stage = new();
    private readonly CanvasSandbox _sandbox = new();
    private readonly ContentControl _native = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
    };
    private readonly TextBlock _traffic = new() { Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _errorText = new()
    {
        FontSize = 11.5,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
    };
    private readonly Border _errorBar;
    private readonly Border _trafficBar;
    private readonly DispatcherTimer _streamingTimer;
    private CodeView? _sourceView;
    private ScrollViewer? _sourceScroller;
    private (string Id, int Version)? _sourceDocument;

    private ArtifactItemViewModel? _item;
    private bool _sourceMode;
    private string? _conversationId;
    private string? _token;
    private string? _pendingError;
    private int _generation;
    private bool _webMode;
    private DateTime _lastRender;

    /// <summary>「发给模型修复」 on a page that threw.</summary>
    public event EventHandler<string>? FixErrorRequested;

    public ArtifactCanvasBody()
    {
        var fix = new Button
        {
            Classes = { "inlineaction" },
            // inlineaction is an icon button with a fixed width; this one has words.
            Width = double.NaN,
            MinWidth = 88,
            Padding = new Thickness(8, 0),
            Height = 24,
            Content = new TextBlock { Text = "发送修复请求", FontSize = 11.5 },
        };
        fix.Click += (_, _) =>
        {
            if (_pendingError is { } error) FixErrorRequested?.Invoke(this, error);
        };
        var errorRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        errorRow.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_errorText, 1);
        errorRow.Children.Add(_errorText);
        Grid.SetColumn(fix, 2);
        errorRow.Children.Add(fix);
        _errorBar = new Border { Classes = { "canvaserror" }, Child = errorRow, IsVisible = false };

        _trafficBar = new Border { Classes = { "canvasfooter" }, Child = _traffic, IsVisible = false };

        _stage.Children.Add(_native);

        var root = new DockPanel();
        DockPanel.SetDock(_trafficBar, Dock.Bottom);
        DockPanel.SetDock(_errorBar, Dock.Bottom);
        root.Children.Add(_trafficBar);
        root.Children.Add(_errorBar);
        root.Children.Add(_stage);
        Content = root;

        _sandbox.PageError += (_, message) =>
        {
            _pendingError = message;
            var firstLine = message.Split('\n', 2)[0].Trim();
            _errorText.Text = "页面报错：" + firstLine;
            ToolTip.SetTip(_errorText, message.Length > 1200 ? message[..1200] + "…" : message);
            _errorBar.IsVisible = true;
        };
        _sandbox.TrafficChanged += (_, _) => UpdateTraffic();
        _errorText.Bind(TextBlock.ForegroundProperty, _errorText.GetResourceObservable("Brush.Danger.Foreground"));

        _streamingTimer = new DispatcherTimer { Interval = StreamingInterval };
        _streamingTimer.Tick += (_, _) =>
        {
            _streamingTimer.Stop();
            Render();
        };

        ActualThemeVariantChanged += (_, _) => OnThemeChanged();
    }

    // ---- entry -----------------------------------------------------------------

    public void Show(ArtifactItemViewModel? item, bool sourceMode, string? conversationId)
    {
        var token = item is null ? "(none)" : $"{item.RenderToken}|{sourceMode}";
        if (string.Equals(token, _token, StringComparison.Ordinal)) return;

        var sameItem = ReferenceEquals(item, _item);
        _item = item;
        _sourceMode = sourceMode;
        _conversationId = conversationId;
        _token = token;

        // A page still being written changes every frame; its source view is
        // redrawn a few times a second, not on every delta.
        if (sameItem && item is { IsComplete: false } && DateTime.UtcNow - _lastRender < StreamingInterval)
        {
            if (!_streamingTimer.IsEnabled) _streamingTimer.Start();
            return;
        }

        _streamingTimer.Stop();
        Render();
    }

    public void Clear() => Show(null, false, _conversationId);

    private void Render()
    {
        _lastRender = DateTime.UtcNow;
        var generation = ++_generation;
        var item = _item;
        _errorBar.IsVisible = false;
        _pendingError = null;

        if (item is null)
        {
            ShowNative(null);
            return;
        }

        if (_sourceMode || !item.IsComplete)
        {
            var source = LoadText(item);
            ShowNative(source is null
                ? Empty("暂不支持预览此格式", item.CanReveal ? "可在资源管理器中打开" : null)
                : LiveSource(item, source, item.FenceLanguage ?? item.Language ?? string.Empty, follow: !item.IsComplete));
            return;
        }

        switch (item.RenderKind)
        {
            case ArtifactRenderKind.Html:
            case ArtifactRenderKind.Mermaid:
            {
                var source = LoadText(item);
                if (source is null)
                {
                    ShowNative(Empty("无法读取内容"));
                    return;
                }

                var theme = BuildTheme();
                var html = item.RenderKind == ArtifactRenderKind.Html
                    ? CanvasSandbox.BuildHtml(source, theme)
                    : CanvasSandbox.BuildMermaid(source, theme);
                _ = ShowWebAsync(OriginKey(item), html, generation);
                return;
            }
            case ArtifactRenderKind.Svg:
                ShowNative(BuildSvg(item));
                return;
            case ArtifactRenderKind.Image:
                ShowNative(BuildImage(item));
                return;
            case ArtifactRenderKind.Table:
                ShowNative(BuildTable(item));
                return;
            case ArtifactRenderKind.Markdown:
                ShowNative(BuildMarkdown(item));
                return;
            default:
            {
                var source = LoadText(item);
                ShowNative(source is null
                    ? Empty("暂不支持预览此格式", item.CanReveal ? "可在资源管理器中打开" : null)
                    : LiveSource(item, source, item.Language ?? string.Empty, follow: false));
                return;
            }
        }
    }

    private void ShowNative(Control? content)
    {
        if (_webMode)
        {
            _webMode = false;
            if (_web is not null)
            {
                _web.IsVisible = false;
                _web.Unpark();
            }

            _sandbox.Blank();
        }

        _trafficBar.IsVisible = false;
        _native.Content = content ?? Empty("请选择交付物");
    }

    private async Task ShowWebAsync(string originKey, string html, int generation)
    {
        _webMode = true;
        if (_web is null)
        {
            _web = new CanvasWebView();
            // Parked before it is attached, so its window is never on screen
            // before the browser has drawn in it.
            _web.Park();
            _stage.Children.Add(_web);
        }

        _web.SetBackground(ThemeColor("Color.Bg.Primary", Colors.White));

        // A page already on screen stays there until its successor has loaded.
        // A web view that is only now appearing — the first time, which also
        // pays for starting the browser, or back from a native preview — loads
        // offstage while the placeholder says what is happening.
        var onScreen = _web.IsVisible && !_web.IsParked;
        if (!onScreen)
        {
            _native.Content = Empty("正在准备预览…");
            _web.Park();
        }

        _trafficBar.IsVisible = true;
        UpdateTraffic();

        try
        {
            var core = await _web.Ready;
            if (generation != _generation) return;
            _sandbox.Attach(core);
            var loaded = _sandbox.Show(originKey, html);
            // A page stuck on a slow library still appears; it can say so
            // itself once it is visible.
            if (!onScreen) await Task.WhenAny(loaded, Task.Delay(RevealTimeout));
            if (generation != _generation) return;
            _web.Unpark();
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            // The web view is a native window: the message has to replace it,
            // not sit under it.
            _web.Unpark();
            _web.IsVisible = false;
            _webMode = false;
            _trafficBar.IsVisible = false;
            _native.Content = Empty(
                "网页预览不可用",
                "需要 Microsoft Edge WebView2 运行时。请从微软官网安装「WebView2 Runtime」后重新打开画布；或单击「源码」查看内容。\n" + ex.Message);
        }
    }

    private void OnThemeChanged()
    {
        // A page restyles in place through its CSS variables. Mermaid bakes its
        // theme into the SVG when it draws, so a diagram is drawn again.
        if (_webMode && _item?.RenderKind != ArtifactRenderKind.Mermaid)
        {
            _web?.SetBackground(ThemeColor("Color.Bg.Primary", Colors.White));
            _sandbox.ApplyTheme(BuildTheme());
            return;
        }

        if (_item is not null) Render();
    }

    private void UpdateTraffic()
    {
        var t = _sandbox.Traffic;
        if (t.Loaded == 0 && t.Blocked == 0 && t.Failed == 0)
        {
            _traffic.Text = "MolaGPT 画布";
            return;
        }

        var parts = new List<string>();
        if (t.Loaded > 0) parts.Add($"从 CDN 加载 {t.Loaded} 个资源");
        if (t.Failed > 0) parts.Add($"{t.Failed} 个加载失败");
        if (t.Blocked > 0) parts.Add($"已拦截 {t.Blocked} 个请求");
        _traffic.Text = string.Join(" · ", parts);

        var tip = new StringBuilder();
        if (t.LoadedHosts.Count > 0) tip.Append("加载自：").AppendJoin("、", t.LoadedHosts);
        if (t.BlockedHosts.Count > 0)
        {
            if (tip.Length > 0) tip.AppendLine();
            tip.Append("已拦截：").AppendJoin("、", t.BlockedHosts);
        }

        ToolTip.SetTip(_traffic, tip.Length > 0 ? tip.ToString() : null);
    }

    // ---- theme -------------------------------------------------------------------

    private CanvasTheme BuildTheme()
    {
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        var variables = new List<KeyValuePair<string, string>>
        {
            new("--mola-bg", Css(ThemeColor("Color.Bg.Primary", dark ? Color.Parse("#1A1A1A") : Colors.White))),
            new("--mola-surface", Css(ThemeColor("Color.Bg.Secondary", dark ? Color.Parse("#2D2D2D") : Color.Parse("#F8F9FA")))),
            new("--mola-text", Css(ThemeColor("Color.Text.Primary", dark ? Color.Parse("#E0E0E0") : Color.Parse("#212529")))),
            new("--mola-muted", Css(ThemeColor("Color.Text.Secondary", dark ? Color.Parse("#B4B4B4") : Color.Parse("#6C757D")))),
            new("--mola-border", Css(ThemeColor("Color.Border", dark ? Color.Parse("#404040") : Color.Parse("#DEE2E6")))),
            new("--mola-accent", Css(ThemeColor("Color.Primary", Color.Parse("#BE727F")))),
        };
        for (var i = 1; i <= 6; i++)
            variables.Add(new($"--mola-chart-{i}", Css(VisualTheme.Series(this, i - 1))));
        return new CanvasTheme(dark, variables);
    }

    private Color ThemeColor(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color color ? color : fallback;

    private static string Css(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";

    /// <summary>Stable per conversation and logical artifact: a page's
    /// localStorage survives its next version and an app restart, and never
    /// leaks into another conversation's page of the same name.</summary>
    private string OriginKey(ArtifactItemViewModel item)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes((_conversationId ?? string.Empty) + "|" + item.Id));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    // ---- native renderers ------------------------------------------------------------

    /// <summary>
    /// The source view, kept across refreshes and updated in place: while a
    /// page streams in, only the lines that changed are laid out again. Follows
    /// the newest line while the reader is at the bottom; a reader who scrolled
    /// up to look at something is left there.
    /// </summary>
    private Control LiveSource(ArtifactItemViewModel item, string source, string language, bool follow)
    {
        if (_sourceView is null || _sourceScroller is null)
        {
            _sourceView = new CodeView();
            _sourceScroller = new ScrollViewer
            {
                Content = _sourceView,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
        }

        var scroller = _sourceScroller;
        var document = (item.Id, item.CurrentVersionIndex);
        var sameDocument = _sourceDocument == document && ReferenceEquals(scroller.Parent, _native);
        var atBottom = scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - LineSlack;
        _sourceDocument = document;
        _sourceView.SetCode(source, language);

        if (!sameDocument) scroller.Offset = default;
        if (follow && (!sameDocument || atBottom))
            Dispatcher.UIThread.Post(() => scroller.ScrollToEnd(), DispatcherPriority.Background);
        return scroller;
    }

    // Within a line and a bit of the end still counts as reading the end.
    private const double LineSlack = 28;

    private static Control SourceView(string source, string language)
    {
        var view = new CodeView();
        view.SetCode(source, language);
        return new ScrollViewer
        {
            Content = view,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private static Control BuildImage(ArtifactItemViewModel item)
    {
        try
        {
            if (string.IsNullOrEmpty(item.FullPath) || !File.Exists(item.FullPath))
                return Empty("图片文件不存在");

            using var stream = File.OpenRead(item.FullPath);
            return new OwnedBitmapView(new Bitmap(stream), paper: false);
        }
        catch (Exception ex)
        {
            return Empty("无法显示图片", ex.Message);
        }
    }

    /// <summary>SVGs are drawn on white paper in both themes: models write them
    /// for a white page, and black strokes on the dark canvas vanish.</summary>
    private static Control BuildSvg(ArtifactItemViewModel item)
    {
        var source = LoadText(item);
        if (source is null) return Empty("无法读取 SVG");

        try
        {
            using var svg = new Svg.Skia.SKSvg();
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
            svg.Load(stream);
            var picture = svg.Picture;
            if (picture is null) return SourceView(source, "svg");

            var sourceWidth = Math.Max(1f, picture.CullRect.Width);
            var sourceHeight = Math.Max(1f, picture.CullRect.Height);
            var scale = Math.Min(3d, 2400d / Math.Max(sourceWidth, sourceHeight));
            var width = Math.Max(1, (int)Math.Ceiling(sourceWidth * scale));
            var height = Math.Max(1, (int)Math.Ceiling(sourceHeight * scale));

            var image = new WriteableBitmap(new PixelSize(width, height), new Vector(96 * scale, 96 * scale), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var frame = image.Lock())
            {
                var info = new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                using var surface = SkiaSharp.SKSurface.Create(info, frame.Address, frame.RowBytes);
                var canvas = surface.Canvas;
                canvas.Clear(SkiaSharp.SKColors.Transparent);
                canvas.Scale(width / sourceWidth, height / sourceHeight);
                canvas.Translate(-picture.CullRect.Left, -picture.CullRect.Top);
                canvas.DrawPicture(picture);
                canvas.Flush();
            }

            return new OwnedBitmapView(image, paper: true);
        }
        catch
        {
            return SourceView(source, "svg");
        }
    }

    private static Control BuildTable(ArtifactItemViewModel item)
    {
        var source = LoadText(item);
        if (source is null) return Empty("无法读取表格");

        // A leading "# data.csv" is the file-name comment, not a row.
        var lines = source.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && lines[0].TrimStart().StartsWith('#')) lines.RemoveAt(0);
        var body = string.Join('\n', lines);

        try
        {
            var rows = new List<string[]>();
            using var parser = new TextFieldParser(new StringReader(body))
            {
                HasFieldsEnclosedInQuotes = true,
                TrimWhiteSpace = false,
            };
            parser.SetDelimiters(body.Contains('\t') && !body.Contains(',') ? "\t" : ",");
            while (!parser.EndOfData && rows.Count < 501)
                rows.Add(parser.ReadFields() ?? []);
            if (rows.Count == 0) return Empty("表格没有数据");

            var columnCount = Math.Min(30, rows.Max(row => row.Length));
            var grid = new Grid();
            for (var column = 0; column < columnCount; column++)
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { MinWidth = 72 });

            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (var column = 0; column < columnCount; column++)
                {
                    var cell = new Border
                    {
                        Classes = { rowIndex == 0 ? "artifacttableheader" : "artifacttablecell" },
                        Child = new SelectableTextBlock
                        {
                            Text = column < rows[rowIndex].Length ? rows[rowIndex][column] : string.Empty,
                            FontSize = 12,
                            FontWeight = rowIndex == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 320,
                        },
                    };
                    Grid.SetRow(cell, rowIndex);
                    Grid.SetColumn(cell, column);
                    grid.Children.Add(cell);
                }
            }

            var content = new StackPanel { Margin = new Thickness(12) };
            content.Children.Add(grid);
            if (!parser.EndOfData || rows.Any(row => row.Length > columnCount))
            {
                content.Children.Add(new TextBlock
                {
                    Text = "预览了部分内容；完整内容可复制源码查看",
                    Classes = { "muted" },
                    FontSize = 11,
                    Margin = new Thickness(0, 10, 0, 0),
                });
            }

            return new ScrollViewer
            {
                Content = content,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
        }
        catch
        {
            return SourceView(source, "csv");
        }
    }

    private static Control BuildMarkdown(ArtifactItemViewModel item)
    {
        var source = LoadText(item);
        if (source is null) return Empty("无法读取 Markdown");

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(18, 16) };
        foreach (var block in MessageDocumentParser.Parse(source).Blocks)
        {
            Control? control = block switch
            {
                ParagraphBlock paragraph => new MarkdownTextBlock { Markdown = paragraph.Markdown, Classes = { "prose" } },
                HeadingBlock heading => new MarkdownTextBlock
                {
                    Markdown = heading.Markdown,
                    Classes = { "heading" },
                    FontSize = heading.Level switch { 1 => 22, 2 => 19, 3 => 16.5, _ => 15 },
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, heading.Level <= 2 ? 12 : 6, 0, 2),
                },
                CodeBlock code => new Border
                {
                    Classes = { "codepanel" },
                    Child = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = new CodeTextBlock { Language = code.Language, Code = code.Code, Classes = { "code" }, Margin = new Thickness(12) },
                    },
                },
                QuoteBlock quote => new Border
                {
                    Classes = { "artifactquote" },
                    Child = new MarkdownTextBlock { Markdown = quote.Markdown, Classes = { "prose" } },
                },
                ListBlock list => new MarkdownListView { Block = list },
                TableBlock table => new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = new MarkdownTableView { Block = table },
                },
                MathBlock math => new MathView { Latex = math.Latex, Margin = new Thickness(8) },
                ImageBlock image => new MarkdownImageView { Url = image.Url, Alt = image.Alt },
                ThematicBreakBlock => new Border { Height = 1, Classes = { "artifactdivider" }, Margin = new Thickness(0, 8) },
                RawTextBlock raw => new SelectableTextBlock { Text = raw.Text, TextWrapping = TextWrapping.Wrap },
                _ => null,
            };
            if (control is not null) panel.Children.Add(control);
        }

        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static string? LoadText(ArtifactItemViewModel item)
    {
        if (item.Content is not null) return item.Content;
        if (!item.IsText || string.IsNullOrEmpty(item.FullPath) || !File.Exists(item.FullPath)) return null;
        try
        {
            return new FileInfo(item.FullPath).Length <= MaxTextBytes ? File.ReadAllText(item.FullPath) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Control Empty(string title, string? detail = null)
    {
        var panel = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
            MaxWidth = 380,
        };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        if (!string.IsNullOrWhiteSpace(detail))
        {
            panel.Children.Add(new TextBlock
            {
                Text = detail,
                Classes = { "muted" },
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
            });
        }

        return panel;
    }

    /// <summary>A bitmap the canvas owns: fit to width, scroll for the rest,
    /// released when it leaves the screen.</summary>
    private sealed class OwnedBitmapView : UserControl
    {
        public OwnedBitmapView(Bitmap bitmap, bool paper)
        {
            Control image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            if (paper)
            {
                image = new Border
                {
                    Background = Brushes.White,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = image,
                };
            }

            Content = new ScrollViewer
            {
                Content = new Border { Padding = new Thickness(12), Child = image },
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            DetachedFromVisualTree += (_, _) => bitmap.Dispose();
        }
    }
}
