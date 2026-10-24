using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace VideoPlayer;

/// <summary>
/// Positions a window's panes and dividers from its <see cref="PaneLayout"/>.
/// Panes stay children of this panel for as long as they live in the window: moving a
/// native video window between WPF parents can leave libVLC drawing to a dead surface.
/// </summary>
public sealed class PaneHost : Panel
{
    readonly Dictionary<SplitNode, Thumb> _dividers = new();
    readonly Dictionary<UIElement, Rect> _slots = new();
    readonly Dictionary<SplitNode, double> _shared = new();
    PaneLayout? _layout;

    public PaneLayout? Layout
    {
        get => _layout;
        set
        {
            _layout = value;
            Sync();
        }
    }

    /// <summary>Adds new panes, drops departed ones and refreshes dividers after a layout change.</summary>
    public void Sync()
    {
        var panes = _layout?.Panes.ToHashSet() ?? new HashSet<PlayerPane>();
        foreach (var gone in Children.OfType<PlayerPane>().Where(p => !panes.Contains(p)).ToList())
            Children.Remove(gone);
        foreach (var pane in panes.Where(p => !Children.Contains(p)))
        {
            if (pane.Parent is Panel previous) previous.Children.Remove(pane);
            Children.Add(pane);
        }

        var splits = _layout?.Splits().ToHashSet() ?? new HashSet<SplitNode>();
        foreach (var stale in _dividers.Keys.Where(s => !splits.Contains(s)).ToList())
        {
            Children.Remove(_dividers[stale]);
            _dividers.Remove(stale);
        }
        foreach (var split in splits.Where(s => !_dividers.ContainsKey(s)))
        {
            var divider = new Thumb { Style = (Style)FindResource("PaneDivider"), ToolTip = "끌어서 크기 조절" };
            divider.DragDelta += (_, e) => OnDividerDrag(split, e);
            _dividers[split] = divider;
            Children.Add(divider);
        }
        InvalidateMeasure();
    }

    void OnDividerDrag(SplitNode split, DragDeltaEventArgs e)
    {
        // Size of the space shared by both sides of this divider, from the last arrange pass.
        if (!_shared.TryGetValue(split, out var total) || total <= 0) return;
        double delta = split.Orientation == Orientation.Horizontal ? e.HorizontalChange : e.VerticalChange;
        split.Ratio = Math.Clamp(split.Ratio + delta / total, 0.05, 0.95);
        InvalidateArrange();
    }

    void ComputeSlots(Size size)
    {
        _slots.Clear();
        _shared.Clear();
        _layout?.Arrange(new Rect(size),
            (pane, rect) => _slots[pane] = rect,
            (split, rect, total) =>
            {
                _shared[split] = total;
                if (!_dividers.TryGetValue(split, out var divider)) return;
                divider.Cursor = split.Orientation == Orientation.Horizontal ? Cursors.SizeWE : Cursors.SizeNS;
                _slots[divider] = rect;
            });
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
        ComputeSlots(size);
        foreach (UIElement child in Children)
            child.Measure(_slots.TryGetValue(child, out var r) ? r.Size : new Size(0, 0));
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ComputeSlots(finalSize);
        foreach (UIElement child in Children)
            child.Arrange(_slots.TryGetValue(child, out var r) ? r : new Rect(0, 0, 0, 0));
        return finalSize;
    }
}
