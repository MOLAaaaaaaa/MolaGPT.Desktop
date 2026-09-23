using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Utilities;
using Avalonia.VisualTree;

namespace MolaGPT.App.Rendering;

/// <summary>
/// A read-only code viewer that lays out only the lines on screen, for the
/// canvas's source view.
///
/// The canvas used a <see cref="CodeTextBlock"/> here and rebuilt it on every
/// refresh while a page streamed in. On a real 297-line page that block was
/// 5,066 coloured runs, and laying it out took 415 ms — every 250 ms, so the
/// UI thread never came up for air and the transcript beside it could not
/// scroll. Here each line is its own small layout, built when it first scrolls
/// into view and kept until its text or colour changes; a streaming update
/// touches the lines that changed and nothing else. Line height is fixed, so
/// the extent is arithmetic rather than a layout of the whole file.
///
/// Text can be selected with the pointer, and copied with Ctrl+C or the context
/// menu; Ctrl+A selects everything.
/// </summary>
internal sealed class CodeView : Control
{
    public static readonly Thickness DefaultPadding = new(14);
    private const double LineHeight = 20;
    private const double CodeFontSize = 13;
    // Lines drawn above and below the viewport, so a fast scroll does not show
    // an edge before the next render.
    private const int Overscan = 12;
    // Cached layouts beyond this are dropped for lines far from the viewport.
    private const int KeepLayouts = 600;

    private sealed class Line(string text)
    {
        public readonly string Text = text;
        public CodeToken[]? Tokens;
        public TextLayout? Layout;
        public double Width = -1;
    }

    private readonly List<Line> _lines = new();
    private string _code = string.Empty;
    private string? _language;
    private Typeface _typeface = Typeface.Default;
    private IBrush _foreground = Brushes.Black;
    private IBrush _selection = Brushes.LightBlue;
    private double _cell = 7.2;
    private double _maxWidth;
    private Rect _viewport;
    private int _layouts;

    private bool _highlighting;
    private bool _highlightStale;
    private bool _deferred;
    private int _generation;
    private bool? _dark;

    private (int Line, int Col) _anchor;
    private (int Line, int Col) _caret;
    private bool _dragging;

    public CodeView()
    {
        Focusable = true;
        FocusAdorner = null;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            InvalidateVisual();
        };
        ActualThemeVariantChanged += (_, _) => ResolveTheme();

        var copy = new MenuItem { Header = "复制" };
        copy.Click += (_, _) => _ = CopyAsync();
        var all = new MenuItem { Header = "全选" };
        all.Click += (_, _) => SelectAll();
        ContextMenu = new ContextMenu { Items = { copy, all } };
    }

    public string Code => _code;

    private Thickness _padding = DefaultPadding;

    public Thickness Padding
    {
        get => _padding;
        set
        {
            _padding = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Shows <paramref name="code"/>. Lines it shares with what is on screen —
    /// all but the last few, while a page streams — keep their layout and
    /// colour; only the changed tail is replaced.
    /// </summary>
    public void SetCode(string code, string? language)
    {
        code = code.Replace("\r\n", "\n", StringComparison.Ordinal);
        var languageChanged = !string.Equals(language, _language, StringComparison.OrdinalIgnoreCase);
        if (!languageChanged && string.Equals(code, _code, StringComparison.Ordinal)) return;

        _code = code;
        _language = language;
        var lines = code.Split('\n');

        var shared = 0;
        if (languageChanged)
        {
            _generation++;
        }
        else
        {
            var limit = Math.Min(_lines.Count, lines.Length);
            while (shared < limit && string.Equals(_lines[shared].Text, lines[shared], StringComparison.Ordinal)) shared++;
        }

        for (var i = shared; i < _lines.Count; i++)
        {
            if (_lines[i].Layout is not null) _layouts--;
        }

        _lines.RemoveRange(shared, _lines.Count - shared);
        for (var i = shared; i < lines.Length; i++) _lines.Add(new Line(lines[i]));
        if (languageChanged)
        {
            foreach (var line in _lines) Forget(line, tokens: true);
        }

        _maxWidth = 0;
        foreach (var line in _lines) _maxWidth = Math.Max(_maxWidth, line.Width >= 0 ? line.Width : Estimate(line.Text));

        _anchor = Clamp(_anchor);
        _caret = Clamp(_caret);
        InvalidateMeasure();
        InvalidateVisual();
        RequestHighlight();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ResolveTheme();
        if (_deferred)
        {
            _deferred = false;
            RequestHighlight();
        }
    }

    private void ResolveTheme()
    {
        var family = this.TryFindResource("Font.Mono", ActualThemeVariant, out var f) && f is FontFamily mono
            ? mono
            : new FontFamily("Cascadia Mono, Consolas, monospace");
        _typeface = new Typeface(family);
        _foreground = this.TryFindResource("Brush.Text.Primary", ActualThemeVariant, out var fg) && fg is IBrush text ? text : Brushes.Black;
        _selection = this.TryFindResource("Brush.Selection", ActualThemeVariant, out var sel) && sel is IBrush s ? s : Brushes.LightBlue;
        _cell = new TextLayout("0000000000", _typeface, CodeFontSize).WidthIncludingTrailingWhitespace / 10;

        // Colours come from a light or dark TextMate theme, so a variant switch
        // means tokenizing again; the old colours would be wrong on the new background.
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var retokenize = _dark is not null && _dark != dark;
        _dark = dark;
        if (retokenize) _generation++;
        foreach (var line in _lines) Forget(line, tokens: retokenize);

        _maxWidth = 0;
        foreach (var line in _lines) _maxWidth = Math.Max(_maxWidth, Estimate(line.Text));
        InvalidateMeasure();
        InvalidateVisual();
        if (retokenize) RequestHighlight();
    }

    private void Forget(Line line, bool tokens)
    {
        if (line.Layout is not null) _layouts--;
        line.Layout = null;
        line.Width = -1;
        if (tokens) line.Tokens = null;
    }

    // ---- highlighting ----------------------------------------------------------

    /// <summary>One request in flight at a time. Updates that arrive meanwhile
    /// are folded into a single follow-up for whatever the code is by then.</summary>
    private async void RequestHighlight()
    {
        if (_highlighting)
        {
            _highlightStale = true;
            return;
        }

        if (_code.Length == 0 || !CodeHighlighter.Supports(_language)) return;

        // Before it is in the tree the view does not know its theme yet, and
        // colours from the wrong one would stay until the next edit.
        if (TopLevel.GetTopLevel(this) is null)
        {
            _deferred = true;
            return;
        }

        _highlighting = true;
        var generation = _generation;
        var result = await CodeHighlighter.HighlightAsync(_code, _language, ActualThemeVariant == ThemeVariant.Dark, CodeHighlighter.MaxViewLength, remember: false);
        _highlighting = false;
        if (result is not null && generation == _generation) ApplyTokens(result);

        if (_highlightStale)
        {
            _highlightStale = false;
            RequestHighlight();
        }
    }

    private void ApplyTokens(HighlightedCode result)
    {
        var changed = false;
        var count = Math.Min(result.Lines.Length, _lines.Count);
        for (var i = 0; i < count; i++)
        {
            var line = _lines[i];
            // A result for an older snapshot still colours every line it shares
            // with the current text; the line being written waits for the next.
            if (ReferenceEquals(line.Tokens, result.Tokens[i]) || !string.Equals(line.Text, result.Lines[i], StringComparison.Ordinal)) continue;
            line.Tokens = result.Tokens[i];
            if (line.Layout is not null)
            {
                line.Layout = null;
                _layouts--;
            }

            changed = true;
        }

        if (changed) InvalidateVisual();
    }

    // ---- layout ------------------------------------------------------------------

    protected override Size MeasureOverride(Size availableSize) =>
        new(Padding.Left + Padding.Right + _maxWidth, Padding.Top + Padding.Bottom + _lines.Count * LineHeight);

    /// <summary>Width before a line is laid out: character cells, wide for CJK.
    /// Corrected to the real width once the line is drawn.</summary>
    private double Estimate(string text)
    {
        var cells = 0;
        foreach (var ch in text)
        {
            if (ch == '\t') cells += 4;
            else if (char.IsLowSurrogate(ch)) continue;
            else if (ch >= 0x1100 && IsWide(ch)) cells += 2;
            else cells++;
        }

        return cells * _cell;
    }

    private static bool IsWide(char ch) =>
        ch is (>= 'ᄀ' and <= 'ᅟ') or (>= '⺀' and <= '꓏') or (>= '가' and <= '힣')
            or (>= '豈' and <= '﫿') or (>= '︰' and <= '﹏') or (>= '＀' and <= '｠')
            or (>= '￠' and <= '￦') or (>= '\uD800' and <= '\uDBFF');

    private TextLayout GetLayout(int index)
    {
        var line = _lines[index];
        if (line.Layout is { } cached) return cached;

        List<ValueSpan<TextRunProperties>>? overrides = null;
        if (line.Tokens is { Length: > 0 } tokens)
        {
            overrides = new List<ValueSpan<TextRunProperties>>(tokens.Length);
            foreach (var token in tokens)
            {
                if (token.Brush is null && token.Style == FontStyle.Normal && token.Weight == FontWeight.Normal) continue;
                if (token.Start + token.Length > line.Text.Length) continue;
                var typeface = token.Style == FontStyle.Normal && token.Weight == FontWeight.Normal
                    ? _typeface
                    : new Typeface(_typeface.FontFamily, token.Style, token.Weight);
                overrides.Add(new ValueSpan<TextRunProperties>(token.Start, token.Length,
                    new GenericTextRunProperties(typeface, CodeFontSize, foregroundBrush: token.Brush ?? _foreground)));
            }
        }

        var layout = new TextLayout(line.Text, _typeface, CodeFontSize, _foreground, lineHeight: LineHeight, textStyleOverrides: overrides);
        line.Layout = layout;
        _layouts++;

        var width = layout.WidthIncludingTrailingWhitespace;
        line.Width = width;
        if (width > _maxWidth + 0.5)
        {
            _maxWidth = width;
            InvalidateMeasure();
        }

        return layout;
    }

    private (int First, int Last) VisibleRange()
    {
        var top = _viewport.Height > 0 ? _viewport.Top : 0;
        var bottom = _viewport.Height > 0 ? _viewport.Bottom : Math.Min(Bounds.Height, 2000);
        var first = Math.Clamp((int)Math.Floor((top - Padding.Top) / LineHeight) - Overscan, 0, _lines.Count - 1);
        var last = Math.Clamp((int)Math.Ceiling((bottom - Padding.Top) / LineHeight) + Overscan, 0, _lines.Count - 1);
        return (first, last);
    }

    public override void Render(DrawingContext context)
    {
        // Hit-testable everywhere, not just on glyphs.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_lines.Count == 0) return;

        var (first, last) = VisibleRange();
        var (start, end) = Ordered();
        var selecting = start != end;
        for (var i = first; i <= last; i++)
        {
            var layout = GetLayout(i);
            var y = Padding.Top + i * LineHeight;
            if (selecting && i >= start.Line && i <= end.Line) DrawSelection(context, layout, i, y, start, end);
            layout.Draw(context, new Point(Padding.Left, y));
        }

        if (_layouts > KeepLayouts) TrimLayouts(first, last);
    }

    private void DrawSelection(DrawingContext context, TextLayout layout, int index, double y, (int Line, int Col) start, (int Line, int Col) end)
    {
        var text = _lines[index].Text;
        var from = index == start.Line ? start.Col : 0;
        var to = index == end.Line ? end.Col : text.Length;
        if (to > from)
        {
            foreach (var rect in layout.HitTestTextRange(from, to - from))
                context.FillRectangle(_selection, new Rect(Padding.Left + rect.X, y, rect.Width, LineHeight));
        }

        // The line break is part of the selection too; show it as one cell.
        if (index < end.Line)
            context.FillRectangle(_selection, new Rect(Padding.Left + layout.WidthIncludingTrailingWhitespace, y, _cell, LineHeight));
    }

    private void TrimLayouts(int first, int last)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (i >= first - 200 && i <= last + 200) continue;
            if (_lines[i].Layout is null) continue;
            _lines[i].Layout = null;
            _layouts--;
        }
    }

    // ---- selection ------------------------------------------------------------------

    private (int Line, int Col) Clamp((int Line, int Col) at)
    {
        if (_lines.Count == 0) return (0, 0);
        var line = Math.Clamp(at.Line, 0, _lines.Count - 1);
        return (line, Math.Clamp(at.Col, 0, _lines[line].Text.Length));
    }

    private ((int Line, int Col) Start, (int Line, int Col) End) Ordered() =>
        _anchor.Line < _caret.Line || (_anchor.Line == _caret.Line && _anchor.Col <= _caret.Col)
            ? (_anchor, _caret)
            : (_caret, _anchor);

    private (int Line, int Col) HitTest(Point point)
    {
        var line = Math.Clamp((int)Math.Floor((point.Y - Padding.Top) / LineHeight), 0, _lines.Count - 1);
        var hit = GetLayout(line).HitTestPoint(new Point(Math.Max(0, point.X - Padding.Left), LineHeight / 2));
        return (line, Math.Clamp(hit.TextPosition, 0, _lines[line].Text.Length));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (_lines.Count == 0 || !point.Properties.IsLeftButtonPressed) return;

        Focus(NavigationMethod.Pointer);
        var hit = HitTest(point.Position);
        if (e.ClickCount == 2)
        {
            var text = _lines[hit.Line].Text;
            int from = hit.Col, to = hit.Col;
            while (from > 0 && IsWordChar(text[from - 1])) from--;
            while (to < text.Length && IsWordChar(text[to])) to++;
            (_anchor, _caret) = ((hit.Line, from), (hit.Line, to));
        }
        else if (e.ClickCount >= 3)
        {
            (_anchor, _caret) = ((hit.Line, 0), hit.Line + 1 < _lines.Count ? (hit.Line + 1, 0) : (hit.Line, _lines[hit.Line].Text.Length));
        }
        else
        {
            if ((e.KeyModifiers & KeyModifiers.Shift) == 0) _anchor = hit;
            _caret = hit;
            _dragging = true;
            e.Pointer.Capture(this);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var position = e.GetPosition(this);
        _caret = HitTest(position);
        AutoScroll(position);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    /// <summary>Dragging past the top or bottom edge keeps selecting.</summary>
    private void AutoScroll(Point position)
    {
        if (this.FindAncestorOfType<ScrollViewer>() is not { } scroller || _viewport.Height <= 0) return;
        var dy = position.Y < _viewport.Top ? position.Y - _viewport.Top : position.Y > _viewport.Bottom ? position.Y - _viewport.Bottom : 0;
        if (dy != 0) scroller.Offset = scroller.Offset.WithY(Math.Max(0, scroller.Offset.Y + dy));
    }

    private static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_' || ch == '$' || ch == '-';

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && e.Key == Key.C)
        {
            _ = CopyAsync();
            e.Handled = true;
        }
        else if ((e.KeyModifiers & KeyModifiers.Control) != 0 && e.Key == Key.A)
        {
            SelectAll();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private void SelectAll()
    {
        if (_lines.Count == 0) return;
        _anchor = (0, 0);
        _caret = (_lines.Count - 1, _lines[^1].Text.Length);
        InvalidateVisual();
    }

    private string SelectedText()
    {
        var (start, end) = Ordered();
        if (start == end) return string.Empty;
        if (start.Line == end.Line) return _lines[start.Line].Text[start.Col..end.Col];

        var builder = new StringBuilder();
        builder.Append(_lines[start.Line].Text, start.Col, _lines[start.Line].Text.Length - start.Col);
        for (var i = start.Line + 1; i < end.Line; i++) builder.Append('\n').Append(_lines[i].Text);
        builder.Append('\n').Append(_lines[end.Line].Text, 0, end.Col);
        return builder.ToString();
    }

    private async Task CopyAsync()
    {
        var text = SelectedText();
        if (text.Length == 0) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        }
        catch
        {
            // Clipboard contention with another app; nothing useful to say.
        }
    }
}
