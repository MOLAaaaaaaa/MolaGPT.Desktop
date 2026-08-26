using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace MolaGPT.App.Rendering;

/// <summary>
/// A vertical virtualizing panel whose unknown rows keep independent height
/// estimates. Avalonia's VirtualizingStackPanel derives one global estimate
/// from the currently realized rows; expanding a tall tool card therefore
/// changes the estimated position of every unrealized row at once. This panel
/// updates only the measured row, so an inline expansion cannot move the rows
/// that precede it.
/// </summary>
public sealed class StableVirtualizingStackPanel : VirtualizingPanel
{
    private const double CacheScreens = 1;
    private static readonly object OwnContainerKey = new();

    private readonly Dictionary<object, double> _heights = new(ItemIdentityComparer.Instance);
    private readonly Dictionary<object, RealizedItem> _realized = new(ItemIdentityComparer.Instance);
    private readonly Dictionary<object, Stack<Control>> _recyclePool = new();
    private readonly HashSet<Control> _registeredAnchors = [];

    private Rect _viewport;
    private double[] _positions = [0];
    private IScrollAnchorProvider? _anchorProvider;
    private int _scrollToIndex = -1;

    public StableVirtualizingStackPanel()
    {
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Items.Count == 0)
        {
            RecycleAll();
            _positions = [0];
            return default;
        }

        BuildPositions();
        var viewport = MeasureViewport(availableSize);
        var maxWidth = 0d;

        // A measured height can change the end of the realization range. Two
        // passes are sufficient: the second uses the exact sizes learned by the
        // first and fills any newly exposed space.
        for (var pass = 0; pass < 2; pass++)
        {
            var (first, last) = RangeFor(viewport);
            if (_scrollToIndex >= 0)
            {
                first = Math.Min(first, _scrollToIndex);
                last = Math.Max(last, _scrollToIndex);
            }

            var width = double.IsInfinity(availableSize.Width)
                ? Math.Max(Bounds.Width, 1)
                : availableSize.Width;
            var constraint = new Size(width, double.PositiveInfinity);

            for (var index = first; index <= last; index++)
            {
                var realized = GetOrCreate(index);
                realized.Control.Measure(constraint);
                maxWidth = Math.Max(maxWidth, realized.Control.DesiredSize.Width);

                var measured = Math.Max(1, realized.Control.DesiredSize.Height);
                if (!_heights.TryGetValue(realized.Item, out var previous)
                    || Math.Abs(previous - measured) > 0.25)
                {
                    _heights[realized.Item] = measured;
                }
            }

            BuildPositions();
        }

        var finalRange = RangeFor(viewport);
        RecycleOutside(finalRange.First, finalRange.Last);

        var desiredWidth = double.IsInfinity(availableSize.Width) ? maxWidth : availableSize.Width;
        return new Size(desiredWidth, _positions[^1]);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        BuildPositions();

        Control? anchorCandidate = null;
        foreach (var realized in _realized.Values.OrderBy(x => x.Index))
        {
            if (realized.Index < 0 || realized.Index >= Items.Count) continue;

            var top = _positions[realized.Index];
            var height = _positions[realized.Index + 1] - top;
            var bounds = new Rect(0, top, finalSize.Width, height);
            realized.Control.Arrange(bounds);

            if (anchorCandidate is null
                && realized.Control.IsVisible
                && _viewport.Intersects(bounds))
            {
                anchorCandidate = realized.Control;
            }
        }

        if (anchorCandidate is not null && _registeredAnchors.Add(anchorCandidate))
            _anchorProvider?.RegisterAnchorCandidate(anchorCandidate);

        return finalSize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _anchorProvider = this.FindAncestorOfType<IScrollAnchorProvider>();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_anchorProvider is not null)
        {
            foreach (var anchor in _registeredAnchors)
                _anchorProvider.UnregisterAnchorCandidate(anchor);
        }

        _registeredAnchors.Clear();
        _anchorProvider = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnItemsChanged(
        IReadOnlyList<object?> items,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            RecycleAll();
            _heights.Clear();
        }
        else
        {
            var current = new HashSet<object>(
                items.Where(x => x is not null).Cast<object>(),
                ItemIdentityComparer.Instance);

            foreach (var realized in _realized.Values
                         .Where(x => !current.Contains(x.Item))
                         .ToList())
            {
                Recycle(realized);
            }

            foreach (var item in _heights.Keys.Where(x => !current.Contains(x)).ToList())
                _heights.Remove(item);
        }

        UpdateRealizedIndices();
        InvalidateMeasure();
    }

    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count) return null;
        if (ContainerFromIndex(index) is { } existing)
        {
            existing.BringIntoView();
            return existing;
        }

        _scrollToIndex = index;
        InvalidateMeasure();
        UpdateLayout();
        _scrollToIndex = -1;

        var control = ContainerFromIndex(index);
        control?.BringIntoView();
        return control;
    }

    protected override Control? ContainerFromIndex(int index)
    {
        if (index < 0 || index >= Items.Count || Items[index] is not { } item) return null;
        return _realized.TryGetValue(item, out var realized) ? realized.Control : null;
    }

    protected override int IndexFromContainer(Control container)
    {
        foreach (var realized in _realized.Values)
        {
            if (ReferenceEquals(realized.Control, container)) return realized.Index;
        }

        return -1;
    }

    protected override IEnumerable<Control>? GetRealizedContainers() =>
        _realized.Values.OrderBy(x => x.Index).Select(x => x.Control);

    protected override IInputElement? GetControl(
        NavigationDirection direction,
        IInputElement? from,
        bool wrap)
    {
        var index = from is Control control ? IndexFromContainer(control) : -1;
        index = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => Items.Count - 1,
            NavigationDirection.Up or NavigationDirection.Previous => index - 1,
            NavigationDirection.Down or NavigationDirection.Next => index + 1,
            _ => index
        };

        if (wrap && index < 0) index = Items.Count - 1;
        if (wrap && index >= Items.Count) index = 0;
        return ScrollIntoView(index);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var next = e.EffectiveViewport.Intersect(new Rect(Bounds.Size));
        if (next == _viewport) return;
        _viewport = next;
        InvalidateMeasure();
    }

    private Rect MeasureViewport(Size availableSize)
    {
        var viewport = _viewport;
        if (viewport.Height <= 0)
        {
            var initialHeight = double.IsInfinity(availableSize.Height)
                ? Math.Max(Bounds.Height > 0 ? Math.Min(Bounds.Height, 600) : 600, 1)
                : Math.Max(availableSize.Height, 1);
            viewport = new Rect(0, 0, Math.Max(availableSize.Width, 1), initialHeight);
        }

        var cache = viewport.Height * CacheScreens;
        var top = Math.Max(0, viewport.Top - cache);
        var bottom = Math.Min(_positions[^1], viewport.Bottom + cache);
        return new Rect(viewport.X, top, viewport.Width, Math.Max(0, bottom - top));
    }

    private (int First, int Last) RangeFor(Rect viewport)
    {
        var count = Items.Count;
        if (count == 0) return (0, -1);

        var first = FindIndex(viewport.Top);
        var last = FindIndex(Math.Max(viewport.Top, viewport.Bottom - 0.01));
        return (Math.Clamp(first, 0, count - 1), Math.Clamp(last, 0, count - 1));
    }

    private int FindIndex(double position)
    {
        var index = Array.BinarySearch(_positions, position);
        if (index >= 0) return Math.Min(index, Items.Count - 1);

        var insertion = ~index;
        return Math.Clamp(insertion - 1, 0, Items.Count - 1);
    }

    private void BuildPositions()
    {
        if (_positions.Length != Items.Count + 1)
            _positions = new double[Items.Count + 1];

        _positions[0] = 0;
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            var height = item is not null && _heights.TryGetValue(item, out var measured)
                ? measured
                : EstimateHeight(item);
            _positions[i + 1] = _positions[i] + height;
        }
    }

    private static double EstimateHeight(object? item) => item switch
    {
        HeaderRow => 60,
        UserMessageRow => 96,
        ToolRow => 56,
        ToolGroupRow => 56,
        ThinkingRow => 88,
        PendingRow => 48,
        ActionRow => 40,
        ProseRow => 72,
        _ => 64
    };

    private RealizedItem GetOrCreate(int index)
    {
        var item = Items[index] ?? throw new InvalidOperationException("Transcript rows cannot be null.");
        if (_realized.TryGetValue(item, out var existing))
        {
            if (existing.Index != index)
            {
                ItemContainerGenerator!.ItemContainerIndexChanged(
                    existing.Control, existing.Index, index);
                existing.Index = index;
            }

            return existing;
        }

        var generator = ItemContainerGenerator
            ?? throw new InvalidOperationException("The panel is not attached to an ItemsControl.");
        var needsContainer = generator.NeedsContainer(item, index, out var recycleKey);
        Control control;

        if (!needsContainer)
        {
            control = (Control)item;
            recycleKey = OwnContainerKey;
        }
        else if (recycleKey is not null
                 && _recyclePool.TryGetValue(recycleKey, out var pool)
                 && pool.Count > 0)
        {
            control = pool.Pop();
            control.IsVisible = true;
        }
        else
        {
            control = generator.CreateContainer(item, index, recycleKey);
        }

        generator.PrepareItemContainer(control, item, index);
        AddInternalChild(control);
        generator.ItemContainerPrepared(control, item, index);

        var realized = new RealizedItem(item, index, control, recycleKey);
        _realized.Add(item, realized);
        return realized;
    }

    private void UpdateRealizedIndices()
    {
        var indexByItem = new Dictionary<object, int>(ItemIdentityComparer.Instance);
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i] is { } item) indexByItem[item] = i;
        }

        foreach (var realized in _realized.Values)
        {
            if (!indexByItem.TryGetValue(realized.Item, out var index)) continue;
            if (realized.Index == index) continue;

            ItemContainerGenerator!.ItemContainerIndexChanged(
                realized.Control, realized.Index, index);
            realized.Index = index;
        }
    }

    private void RecycleOutside(int first, int last)
    {
        var currentAnchor = _anchorProvider?.CurrentAnchor;
        foreach (var realized in _realized.Values
                     .Where(x => (x.Index < first || x.Index > last)
                                  && !x.Control.IsKeyboardFocusWithin
                                  && !x.Control.IsPointerOver
                                  && !ReferenceEquals(x.Control, currentAnchor))
                     .ToList())
        {
            Recycle(realized);
        }
    }

    private void RecycleAll()
    {
        foreach (var realized in _realized.Values.ToList()) Recycle(realized);
    }

    private void Recycle(RealizedItem realized)
    {
        if (_registeredAnchors.Remove(realized.Control))
            _anchorProvider?.UnregisterAnchorCandidate(realized.Control);
        _realized.Remove(realized.Item);

        var generator = ItemContainerGenerator;
        if (generator is null) return;

        generator.ClearItemContainer(realized.Control);
        RemoveInternalChild(realized.Control);

        if (realized.RecycleKey is not null
            && !ReferenceEquals(realized.RecycleKey, OwnContainerKey))
        {
            if (!_recyclePool.TryGetValue(realized.RecycleKey, out var pool))
            {
                pool = new Stack<Control>();
                _recyclePool.Add(realized.RecycleKey, pool);
            }

            realized.Control.IsVisible = false;
            pool.Push(realized.Control);
        }
    }

    private sealed class RealizedItem(
        object item,
        int index,
        Control control,
        object? recycleKey)
    {
        public object Item { get; } = item;
        public int Index { get; set; } = index;
        public Control Control { get; } = control;
        public object? RecycleKey { get; } = recycleKey;
    }

    private sealed class ItemIdentityComparer : IEqualityComparer<object>
    {
        public static ItemIdentityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null || x.GetType() != y.GetType()) return false;
            return x.GetType().IsValueType && x.Equals(y);
        }

        public int GetHashCode(object obj) => obj.GetType().IsValueType
            ? obj.GetHashCode()
            : RuntimeHelpers.GetHashCode(obj);
    }
}
