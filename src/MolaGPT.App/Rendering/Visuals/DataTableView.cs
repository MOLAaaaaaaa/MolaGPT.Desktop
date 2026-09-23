using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MolaGPT.Presentation.Visuals;

namespace MolaGPT.App.Rendering;

/// <summary>
/// An inline data table: sort by any column, search, page, copy to a
/// spreadsheet or save as CSV.
///
/// Pages rather than an inner scroll: a table that captured the wheel would
/// trap the reader scrolling past it, the same reason the plot wants Ctrl to
/// zoom. Only one page of cells exists at a time, so a thousand-row table costs
/// what ten rows cost.
/// </summary>
public sealed class DataTableView : UserControl
{
    public const double EstimatedHeight = 380;

    // A column wider than this wraps: a long description should not push every
    // other column out of view.
    private const double CellMaxWidth = 320;

    private readonly DataTableSpec _spec;
    private readonly Grid _table = new();
    private readonly Border _body;
    private readonly TextBlock _count;
    private readonly Control _footer;
    private readonly TextBlock _pageLabel;
    private readonly Button _previous;
    private readonly Button _next;

    private string _query = string.Empty;
    private int? _sortColumn;
    private bool _descending;
    private int _page;
    private List<IReadOnlyList<TableCell>> _view = new();

    public DataTableView(DataTableSpec spec)
    {
        _spec = spec;
        // Title and row count as one line, so a narrow column trims the line
        // instead of running it under the search box.
        _count = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

        var root = new StackPanel();
        root.Children.Add(BuildHeader());

        _body = new Border
        {
            Child = new ScrollViewer
            {
                Content = _table,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        };
        root.Children.Add(_body);

        _pageLabel = new TextBlock { Classes = { "muted" }, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        _previous = PageButton("上一页", -1);
        _next = PageButton("下一页", 1);
        _footer = BuildFooter();
        root.Children.Add(_footer);

        Content = new Border { Classes = { "uiblockframe" }, Child = root };
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Rebuild();
    }

    // The frame spans the answer column like the other components, whatever the
    // table's own width; a narrow table would otherwise shrink the frame with it.
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        return double.IsInfinity(availableSize.Width) ? size : size.WithWidth(availableSize.Width);
    }

    private Control BuildHeader()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14, 6, 6, 6), MinHeight = 32 };
        header.Children.Add(_count);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        // Search earns its place only once there is more than a page to look through.
        if (_spec.Rows.Count > _spec.PageSize)
        {
            var search = new TextBox
            {
                Classes = { "field", "uisearch" },
                PlaceholderText = "搜索",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            };
            Avalonia.Automation.AutomationProperties.SetName(search, "搜索表格");
            search.TextChanged += (_, _) =>
            {
                _query = search.Text ?? string.Empty;
                _page = 0;
                _body.MinHeight = 0;
                Rebuild();
            };
            tools.Children.Add(search);
        }

        tools.Children.Add(Tool("\uE8C8", "复制表格（可直接粘贴到 Excel）", CopyAsync));
        tools.Children.Add(Tool("\uE74E", "导出 CSV", ExportAsync));
        Grid.SetColumn(tools, 1);
        header.Children.Add(tools);

        return new Border { Classes = { "uitablehead" }, Child = header };
    }

    private Control BuildFooter()
    {
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14, 4, 6, 4) };
        footer.Children.Add(_pageLabel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        buttons.Children.Add(_previous);
        buttons.Children.Add(_next);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        return new Border { Classes = { "uitablefoot" }, Child = footer };
    }

    private Button PageButton(string text, int step)
    {
        var button = new Button { Classes = { "uilegend" }, Content = new TextBlock { Text = text, FontSize = 12 } };
        button.Click += (_, _) =>
        {
            // The last page is usually short. Holding the height keeps the page
            // buttons under the pointer, so paging through is click, click, click
            // rather than chasing the button up the screen.
            _body.MinHeight = Math.Max(_body.MinHeight, _body.Bounds.Height);
            _page += step;
            Rebuild();
        };
        return button;
    }

    private void Rebuild()
    {
        var needle = _query.Trim();
        IEnumerable<IReadOnlyList<TableCell>> rows = _spec.Rows;
        if (needle.Length > 0)
            rows = rows.Where(row => row.Any(cell => !cell.IsEmpty && cell.Text.Contains(needle, StringComparison.CurrentCultureIgnoreCase)));
        if (_sortColumn is { } sorted)
        {
            var type = _spec.Columns[sorted].Type;
            var descending = _descending;
            rows = rows.OrderBy(row => row[sorted], Comparer<TableCell>.Create((a, b) => DataTableSpec.Compare(a, b, type, descending)));
        }

        _view = rows.ToList();
        var pageCount = Math.Max(1, (_view.Count + _spec.PageSize - 1) / _spec.PageSize);
        _page = Math.Clamp(_page, 0, pageCount - 1);

        ShowCount(needle.Length > 0 ? $"{_view.Count} / {_spec.Rows.Count} 行" : $"{_spec.Rows.Count} 行");
        _footer.IsVisible = pageCount > 1;
        _pageLabel.Text = $"第 {_page + 1} / {pageCount} 页";
        _previous.IsEnabled = _page > 0;
        _next.IsEnabled = _page < pageCount - 1;

        BuildGrid(needle);
    }

    private void ShowCount(string count)
    {
        var inlines = new InlineCollection();
        if (!string.IsNullOrWhiteSpace(_spec.Title))
            inlines.Add(new Run(_spec.Title + "   ") { FontSize = 13, FontWeight = FontWeight.SemiBold });
        var muted = new Run(count) { FontSize = 11.5 };
        muted.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("Brush.Text.Muted"));
        inlines.Add(muted);
        _count.Inlines = inlines;
    }

    private void BuildGrid(string needle)
    {
        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        _table.ColumnDefinitions.Clear();

        var columns = _spec.Columns;
        for (var c = 0; c < columns.Count; c++)
            _table.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        // An empty column takes the slack, so the rules run the full width of the
        // frame while the columns stay together. Giving the slack to the last
        // real column instead flung it to the far edge, a hand-span from the rest.
        _table.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        var filler = columns.Count;

        _table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < columns.Count; c++)
        {
            var head = HeaderCell(c);
            Grid.SetColumn(head, c);
            _table.Children.Add(head);
        }

        var page = _view.Skip(_page * _spec.PageSize).Take(_spec.PageSize).ToList();
        if (page.Count == 0)
        {
            _table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var empty = new Border
            {
                Classes = { "uitablecell" },
                Padding = new Thickness(14, 16),
                Child = new TextBlock { Text = "没有匹配的行", Classes = { "muted" }, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center },
            };
            Grid.SetRow(empty, 1);
            Grid.SetColumnSpan(empty, columns.Count + 1);
            _table.Children.Add(empty);
            return;
        }

        for (var r = 0; r < page.Count; r++)
        {
            _table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns.Count; c++)
            {
                var cell = BodyCell(page[r][c], columns[c], needle);
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c);
                _table.Children.Add(cell);
            }

            // Carries the row's rule on across the empty column.
            var rule = new Border { Classes = { "uitablecell" } };
            Grid.SetRow(rule, r + 1);
            Grid.SetColumn(rule, filler);
            _table.Children.Add(rule);
        }
    }

    private Control HeaderCell(int column)
    {
        var spec = _spec.Columns[column];
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(new TextBlock
        {
            Text = spec.Label,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            MaxWidth = CellMaxWidth,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (_sortColumn == column)
        {
            content.Children.Add(new TextBlock
            {
                Classes = { "icon" },
                Text = _descending ? "\uE74B" : "\uE74A",
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var button = new Button
        {
            Classes = { "uisort" },
            Content = content,
            HorizontalContentAlignment = Horizontal(spec.Align),
        };
        ToolTip.SetTip(button, "点击排序");
        Avalonia.Automation.AutomationProperties.SetName(button, $"按{spec.Label}排序");
        button.Click += (_, _) =>
        {
            // Ascending, descending, then back to the order the model wrote.
            if (_sortColumn != column) (_sortColumn, _descending) = (column, false);
            else if (!_descending) _descending = true;
            else _sortColumn = null;
            Rebuild();
        };
        return button;
    }

    private Control BodyCell(TableCell cell, TableColumn column, string needle)
    {
        var text = new SelectableTextBlock
        {
            FontSize = 13,
            LineHeight = 19,
            MaxWidth = CellMaxWidth,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = column.Align switch
            {
                TableAlign.End => TextAlignment.Right,
                TableAlign.Center => TextAlignment.Center,
                _ => TextAlignment.Left,
            },
            // Explicit, not Stretch: capped by MaxWidth inside the wider last
            // column, a stretched block would be centred rather than aligned.
            HorizontalAlignment = Horizontal(column.Align),
        };

        if (cell.IsEmpty)
        {
            text.Text = cell.Text;
            text.Classes.Add("muted");
        }
        else if (needle.Length > 0 && cell.Text.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
        {
            Highlight(text, cell.Text, needle);
        }
        else
        {
            text.Text = cell.Text;
        }

        return new Border { Classes = { "uitablecell" }, Child = text };
    }

    private void Highlight(SelectableTextBlock owner, string text, string needle)
    {
        var inlines = new InlineCollection();
        var cursor = 0;
        while (cursor < text.Length)
        {
            var at = text.IndexOf(needle, cursor, StringComparison.CurrentCultureIgnoreCase);
            if (at < 0) break;
            if (at > cursor) inlines.Add(new Run(text[cursor..at]));
            var match = new Run(text.Substring(at, needle.Length));
            // Primary.Tint (8%) was measured invisible behind CJK text; this is
            // ~22%, still short of the selection colour so a hit is not taken
            // for selected text.
            match.Bind(TextElement.BackgroundProperty, this.GetResourceObservable("Brush.Primary.Border"));
            inlines.Add(match);
            cursor = at + needle.Length;
        }

        if (cursor < text.Length) inlines.Add(new Run(text[cursor..]));
        owner.Inlines = inlines;
    }

    private static HorizontalAlignment Horizontal(TableAlign align) => align switch
    {
        TableAlign.End => HorizontalAlignment.Right,
        TableAlign.Center => HorizontalAlignment.Center,
        _ => HorizontalAlignment.Left,
    };

    /// <summary>Tab-separated, which is what a spreadsheet splits into cells on
    /// paste. Rows in the order shown — filtered and sorted — across all pages.</summary>
    private async Task CopyAsync()
    {
        var builder = new StringBuilder();
        builder.AppendJoin('\t', _spec.Columns.Select(c => Flatten(c.Label))).Append('\n');
        foreach (var row in _view)
            builder.AppendJoin('\t', row.Select(c => c.IsEmpty ? string.Empty : Flatten(c.Text))).Append('\n');

        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(builder.ToString());
        }
        catch
        {
            // Clipboard contention with another app; nothing useful to say.
        }

        static string Flatten(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    private async Task ExportAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage) return;

        var name = string.Concat((_spec.Title ?? "表格").Where(ch => !Path.GetInvalidFileNameChars().Contains(ch))).Trim();
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 CSV",
            SuggestedFileName = (name.Length > 0 ? name : "表格") + ".csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV 表格") { Patterns = ["*.csv"] }],
        });
        if (file is null) return;

        var builder = new StringBuilder();
        builder.AppendJoin(',', _spec.Columns.Select(c => Escape(c.Label))).Append("\r\n");
        foreach (var row in _view)
            builder.AppendJoin(',', row.Select(c => c.IsEmpty ? string.Empty : Escape(c.Text))).Append("\r\n");

        try
        {
            await using var stream = await file.OpenWriteAsync();
            // With the BOM, Excel opens it as UTF-8; without, Chinese comes out garbled.
            await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
            await writer.WriteAsync(builder.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The usual cause is the previous export still open in Excel, which
            // locks the file. Saying nothing would leave the old one looking saved.
            (TopLevel.GetTopLevel(this) as Views.MainWindow)?.NotificationCenter
                .Error("导出 CSV 失败", ex.Message, key: "table-export");
        }

        static string Escape(string value) =>
            value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }

    private static Button Tool(string glyph, string tip, Func<Task> action)
    {
        var button = new Button
        {
            Classes = { "inlineaction" },
            Width = 28,
            Height = 26,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 12 },
        };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        button.Click += async (_, _) => await action();
        return button;
    }
}
