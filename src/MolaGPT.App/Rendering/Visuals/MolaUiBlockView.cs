using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MolaGPT.Presentation.Visuals;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Host for one <c>mola-ui</c> fence: a placeholder while the JSON streams, the
/// component once it parses, or the source with the reason when it cannot.
///
/// The placeholder takes the component's real height up front, so the answer
/// below does not jump when the plot appears. There is no fade: this app's
/// compositor can stop an opacity animation short and leave it there.
/// </summary>
public sealed class MolaUiBlockView : UserControl
{
    public static readonly StyledProperty<UiBlockRow?> RowProperty =
        AvaloniaProperty.Register<MolaUiBlockView, UiBlockRow?>(nameof(Row));

    // Compiling a plot is cheap but not free, and rows are realized again every
    // time they scroll back into view. Specs are immutable, so share them.
    private static readonly Dictionary<string, object> SpecCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> SpecCacheOrder = new();
    private const int SpecCacheLimit = 64;

    private UiBlockRow? _subscribed;
    private string? _lastRaw;
    private bool _lastFinal;
    private string? _shownKey;

    public UiBlockRow? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != RowProperty) return;
        Subscribe(Row);
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_subscribed is null && Row is not null)
        {
            Subscribe(Row);
            Refresh();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Subscribe(null);
    }

    private void Subscribe(UiBlockRow? row)
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnRowChanged;
        _subscribed = row;
        if (_subscribed is not null) _subscribed.PropertyChanged += OnRowChanged;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (Row is not { } row)
        {
            Content = null;
            return;
        }

        var raw = row.Block.Code;
        var final = row.IsFinal;
        if (string.Equals(raw, _lastRaw, StringComparison.Ordinal) && final == _lastFinal) return;
        _lastRaw = raw;
        _lastFinal = final;

        var result = MolaUiParser.Parse(raw, final);
        switch (result.Status)
        {
            case MolaUiStatus.Incomplete:
                _shownKey = null;
                Content = Placeholder(MolaUiParser.PeekComponent(raw));
                return;
            case MolaUiStatus.Invalid:
                _shownKey = null;
                Content = Fallback(result.Error ?? "格式错误", raw);
                return;
        }

        var envelope = result.Envelope!;
        var key = envelope.Component + "\n" + envelope.Props.GetRawText();
        if (string.Equals(key, _shownKey, StringComparison.Ordinal)) return;
        _shownKey = key;

        Content = envelope.Component switch
        {
            "function-plot" or "plot" or "function" => Build<FunctionPlotSpec>(key, envelope, raw, FunctionPlotSpec.TryParse, s => new FunctionPlotView(s)),
            "chart" => Build<ChartSpec>(key, envelope, raw, ChartSpec.TryParse, s => new ChartView(s)),
            "data-table" or "table" => Build<DataTableSpec>(key, envelope, raw, DataTableSpec.TryParse, s => new DataTableView(s)),
            "stat-grid" or "stats" or "metrics" => Build<StatGridSpec>(key, envelope, raw, StatGridSpec.TryParse, s => new StatGridView(s)),
            "card-grid" or "cards" => Build<CardGridSpec>(key, envelope, raw, CardGridSpec.TryParse, s => new CardGridView(s)),
            _ => Fallback($"不认识的组件 {envelope.Component}（可用 function-plot、chart、data-table、stat-grid、card-grid）", raw),
        };
    }

    private delegate bool SpecParser<T>(JsonElement props, out T? spec, out string? error);

    private static Control Build<T>(string key, MolaUiEnvelope envelope, string raw, SpecParser<T> parse, Func<T, Control> view)
        where T : class
    {
        if (!TryGetCached(key, out T? spec))
        {
            if (!parse(envelope.Props, out spec, out var error))
                return Fallback(error ?? "props 无效", raw);
            Remember(key, spec!);
        }

        return view(spec!);
    }

    private static bool TryGetCached<T>(string key, out T? spec) where T : class
    {
        lock (SpecCache)
        {
            if (SpecCache.TryGetValue(key, out var value) && value is T typed)
            {
                spec = typed;
                return true;
            }
        }

        spec = null;
        return false;
    }

    private static void Remember(string key, object spec)
    {
        lock (SpecCache)
        {
            if (SpecCache.ContainsKey(key)) return;
            SpecCache[key] = spec;
            SpecCacheOrder.Enqueue(key);
            while (SpecCacheOrder.Count > SpecCacheLimit)
                SpecCache.Remove(SpecCacheOrder.Dequeue());
        }
    }

    private static Control Placeholder(string? component)
    {
        var (height, label) = component switch
        {
            "function-plot" or "plot" or "function" => (FunctionPlotView.EstimatedHeight, "正在绘制函数图像…"),
            "chart" => (ChartView.EstimatedHeight, "正在生成图表…"),
            "data-table" or "table" => (DataTableView.EstimatedHeight, "正在生成表格…"),
            "stat-grid" or "stats" or "metrics" => (StatGridView.EstimatedHeight, "正在生成指标…"),
            "card-grid" or "cards" => (CardGridView.EstimatedHeight, "正在生成卡片…"),
            _ => (96d, "正在生成组件…"),
        };

        return new Border
        {
            Classes = { "uiblockplaceholder" },
            Height = height,
            Child = new TextBlock
            {
                Text = label,
                Classes = { "muted" },
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>What went wrong, in one line, with the source one click away —
    /// never a blank gap and never a silent substitute.</summary>
    private static Control Fallback(string reason, string raw)
    {
        var source = new SelectableTextBlock
        {
            Text = raw.Trim(),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.NoWrap,
        };
        var sourceHost = new ScrollViewer
        {
            Content = source,
            MaxHeight = 220,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(12, 0, 12, 10),
            IsVisible = false,
        };

        var toggle = new ToggleButton
        {
            Classes = { "uilegend" },
            Content = new TextBlock { Text = "源码", FontSize = 11.5 },
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.IsCheckedChanged += (_, _) => sourceHost.IsVisible = toggle.IsChecked == true;

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 8, 8, 8) };
        header.Children.Add(new TextBlock
        {
            Classes = { "icon", "muted" },
            Text = "",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        var text = new TextBlock
        {
            Text = "组件无法显示：" + reason,
            Classes = { "muted" },
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        Grid.SetColumn(toggle, 2);
        header.Children.Add(toggle);

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(sourceHost);
        return new Border { Classes = { "uiblockplaceholder" }, Child = panel };
    }
}

/// <summary>Theme lookups shared by the inline components.</summary>
internal static class VisualTheme
{
    private static readonly Color[] FallbackSeries =
    [
        Color.Parse("#BE727F"), Color.Parse("#3B82A0"), Color.Parse("#C9832F"),
        Color.Parse("#4E9A6B"), Color.Parse("#7A6AB8"), Color.Parse("#5B6B7A"),
    ];

    public static Color Series(Control owner, int index)
    {
        var slot = index % FallbackSeries.Length;
        return owner.TryFindResource($"Color.Chart.{slot + 1}", owner.ActualThemeVariant, out var value) && value is Color color
            ? color
            : FallbackSeries[slot];
    }

    public static IBrush Brush(Control owner, string key, IBrush fallback) =>
        owner.TryFindResource(key, owner.ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    public static string Format(double value, double step)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "–";
        if (Math.Abs(value) < step * 1e-6) return "0";
        var abs = Math.Abs(value);
        if (abs >= 1e6 || (abs < 1e-3 && abs > 0)) return value.ToString("0.##e+0", CultureInfo.InvariantCulture);
        var decimals = Math.Clamp((int)Math.Ceiling(-Math.Log10(step)), 0, 6);
        return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    public static string Readout(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "–";
        var abs = Math.Abs(value);
        if (abs >= 1e5 || (abs < 1e-3 && abs > 0)) return value.ToString("0.###e+0", CultureInfo.InvariantCulture);
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public static double NiceStep(double raw) => FunctionPlotSpec.NiceStep(raw);
}
