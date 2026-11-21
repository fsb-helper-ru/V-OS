using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VOS.UI;

/// <summary>
/// Virtualized list or grid built in code: an ItemsRepeater with a recycling element factory, so only the rows on
/// screen exist (thousands of files, songs or processes stay fast). Rows are created once by <c>create</c> and
/// re-bound with <c>bind</c> as they scroll into view.
/// </summary>
public sealed class VirtualList<T> : Grid where T : class
{
    private sealed class Factory(Func<FrameworkElement> create, Action<FrameworkElement, T, int> bind, VirtualList<T> owner) : IElementFactory
    {
        private readonly Stack<FrameworkElement> _pool = new();

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var e = _pool.Count > 0 ? _pool.Pop() : create();
            if (args.Data is T item) bind(e, item, owner.IndexOf(item));
            return e;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is FrameworkElement fe) _pool.Push(fe);
        }
    }

    private readonly ItemsRepeater _repeater;
    public ScrollViewer Scroller { get; }
    private IReadOnlyList<T> _items = [];
    private Dictionary<T, int>? _index;

    public VirtualList(Func<FrameworkElement> create, Action<FrameworkElement, T, int> bind, Layout? layout = null)
    {
        _repeater = new ItemsRepeater
        {
            Layout = layout ?? new StackLayout { Spacing = 1 },
            ItemTemplate = new Factory(create, bind, this),
        };
        Scroller = new ScrollViewer { Content = _repeater, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, IsTabStop = false };
        Children.Add(Scroller);
    }

    /// <summary>Grid layout with fixed cell size (icons view, album covers, game library).</summary>
    public static UniformGridLayout GridLayout(double itemWidth, double itemHeight, double spacing = 8) => new()
    {
        MinItemWidth = itemWidth, MinItemHeight = itemHeight, MinColumnSpacing = spacing, MinRowSpacing = spacing,
        ItemsJustification = UniformGridLayoutItemsJustification.Start, ItemsStretch = UniformGridLayoutItemsStretch.Uniform,
    };

    public IReadOnlyList<T> Items => _items;

    public void SetItems(IReadOnlyList<T> items)
    {
        _items = items;
        _index = null;
        _repeater.ItemsSource = items;
    }

    public void SetLayout(Layout layout) => _repeater.Layout = layout;

    internal int IndexOf(T item)
    {
        if (_index == null)
        {
            _index = new Dictionary<T, int>(ReferenceEqualityComparer.Instance as IEqualityComparer<T> ?? EqualityComparer<T>.Default);
            for (int i = 0; i < _items.Count; i++) _index.TryAdd(_items[i], i);
        }
        return _index.TryGetValue(item, out var i2) ? i2 : -1;
    }

    /// <summary>Re-binds the rows currently on screen (after their data changed in place).</summary>
    public void RefreshVisible()
    {
        var copy = _items;
        _repeater.ItemsSource = null;
        _repeater.ItemsSource = copy;
    }

    public void ScrollIntoView(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var e = _repeater.GetOrCreateElement(index);
        e.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.4 });
    }
}
