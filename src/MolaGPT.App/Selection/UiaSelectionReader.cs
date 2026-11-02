using System.Runtime.InteropServices;
using static MolaGPT.App.Selection.SelectionNative;

namespace MolaGPT.App.Selection;

internal sealed record SelectionText(string Text, Point Anchor, Rect? Bounds = null);

internal sealed class UiaSelectionReader : IDisposable
{
    private readonly IUiaAutomation _automation;
    private readonly IUiaWalker _walker;
    private readonly IUiaCacheRequest _cache;

    public UiaSelectionReader()
    {
        // CUIAutomation8 exposes IUIAutomation2, including provider timeouts.
        _automation = (IUiaAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(
            new Guid("E22AD333-B25F-460C-83D0-0581107395C9"), true)!)!;
        _automation.put_AutoSetFocus(false);
        _automation.put_ConnectionTimeout(150);
        _automation.put_TransactionTimeout(150);
        _automation.get_RawViewWalker(out _walker);
        _automation.CreateCacheRequest(out _cache);
        _cache.AddProperty(30002); // ProcessId
        _cache.AddProperty(30019); // IsPassword
        _cache.AddProperty(30040); // IsTextPatternAvailable
        _cache.AddPattern(10014); // TextPattern
    }

    public SelectionText? Read(nint hwnd, Point point, Func<bool> current, out bool allowClipboard)
    {
        allowClipboard = false;
        GetWindowThreadProcessId(hwnd, out var pid);
        IUiaElement? focus = null;
        try
        {
            _automation.GetFocusedElementBuildCache(_cache, out focus);
            if (focus is not null)
            {
                focus.GetCachedPropertyValue(30002, out var process);
                if (process is int p && p == pid)
                {
                    focus.GetCachedPropertyValue(30019, out var password);
                    if (password is true) return null;
                    allowClipboard = password is false;
                    var selection = ReadAncestors(focus, pid, point, current, out var hasTextPattern, out var blocked);
                    if (blocked) { allowClipboard = false; return null; }
                    if (selection is not null) return selection;
                    // A TextPattern reporting an empty selection must not become "copy current line".
                    if (hasTextPattern) allowClipboard = false;
                }
            }
        }
        finally { Release(focus); }
        if (!current()) return null;
        IUiaElement? hit = null;
        try
        {
            _automation.ElementFromPointBuildCache(point, _cache, out hit);
            if (hit is null) return null;
            var selection = ReadAncestors(hit, pid, point, current, out var hasPattern, out var blocked);
            if (blocked || hasPattern) allowClipboard = false;
            return blocked ? null : selection;
        }
        finally { Release(hit); }
    }

    private SelectionText? ReadAncestors(IUiaElement first, uint pid, Point point, Func<bool> current,
        out bool hasPattern, out bool blocked)
    {
        hasPattern = false; blocked = false;
        IUiaElement? element = first;
        try
        {
            for (var depth = 0; element is not null && depth < 64 && current(); depth++)
            {
                element.GetCachedPropertyValue(30002, out var process);
                if (process is not int p || p != pid) break;
                element.GetCachedPropertyValue(30019, out var password);
                if (password is true) { blocked = true; return null; }
                object? pattern = null;
                try
                {
                    element.GetCachedPropertyValue(30040, out var available);
                    if (available is true) element.GetCachedPattern(10014, out pattern);
                    if (pattern is IUiaTextPattern text)
                    {
                        hasPattern = true;
                        text.GetSelection(out var ranges);
                        try
                        {
                            ranges.get_Length(out var count);
                            if (count > 0)
                            {
                                ranges.GetElement(0, out var range);
                                try
                                {
                                    range.GetText(16001, out var value);
                                    if (!string.IsNullOrWhiteSpace(value))
                                    {
                                        if (value.Length > 16000) throw new InvalidOperationException("选中文字过长，请缩小范围。");
                                        range.GetBoundingRectangles(out var rectangles);
                                        // Use the line nearest the gesture, rather than the union of a long selection.
                                        var anchor = point;
                                        var distance = double.MaxValue;
                                        Rect? bounds = null;
                                        for (var i = 0; i + 3 < rectangles.Length; i += 4)
                                        {
                                            if (rectangles[i + 2] <= 0 || rectangles[i + 3] <= 0) continue;
                                            var left = (int)Math.Floor(rectangles[i]);
                                            var top = (int)Math.Floor(rectangles[i + 1]);
                                            var right = (int)Math.Ceiling(rectangles[i] + rectangles[i + 2]);
                                            var bottom = (int)Math.Ceiling(rectangles[i + 1] + rectangles[i + 3]);
                                            bounds = bounds is { } b
                                                ? new Rect { Left = Math.Min(b.Left, left), Top = Math.Min(b.Top, top), Right = Math.Max(b.Right, right), Bottom = Math.Max(b.Bottom, bottom) }
                                                : new Rect { Left = left, Top = top, Right = right, Bottom = bottom };
                                            var d = Math.Abs(rectangles[i + 1] - point.Y);
                                            if (d >= distance) continue;
                                            distance = d;
                                            anchor = new Point
                                            {
                                                X = (int)Math.Clamp(point.X, rectangles[i], rectangles[i] + rectangles[i + 2]),
                                                Y = (int)(rectangles[i + 1] + rectangles[i + 3])
                                            };
                                        }
                                        return new(value, anchor, bounds);
                                    }
                                }
                                finally { Release(range); }
                            }
                        }
                        finally { Release(ranges); }
                    }
                }
                finally { Release(pattern); }
                _walker.GetParentElementBuildCache(element, _cache, out var parent);
                if (!ReferenceEquals(element, first)) Release(element);
                element = parent;
            }
            return null;
        }
        finally { if (!ReferenceEquals(element, first)) Release(element); }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    public void Dispose() { Release(_cache); Release(_walker); Release(_automation); }
}
