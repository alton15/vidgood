using System.Windows;
using System.Windows.Controls;

namespace VideoPlayer;

public enum DockSide { Left, Right, Top, Bottom, Center }

public abstract class LayoutNode
{
    public SplitNode? Parent { get; set; }
}

public sealed class PaneNode : LayoutNode
{
    public PaneNode(PlayerPane pane) => Pane = pane;

    public PlayerPane Pane { get; set; }
}

/// <summary>Two children side by side (Horizontal) or stacked (Vertical).</summary>
public sealed class SplitNode : LayoutNode
{
    public SplitNode(Orientation orientation, LayoutNode first, LayoutNode second)
    {
        Orientation = orientation;
        First = first;
        Second = second;
        first.Parent = this;
        second.Parent = this;
    }

    public Orientation Orientation { get; set; }
    public LayoutNode First { get; set; }
    public LayoutNode Second { get; set; }
    /// <summary>Share of the space given to <see cref="First"/>.</summary>
    public double Ratio { get; set; } = 0.5;
}

/// <summary>
/// Binary split tree of panes for one window. Panes can be inserted next to any pane,
/// removed (the sibling takes over the freed space), swapped or re-oriented.
/// The tree only computes rectangles; <see cref="PaneHost"/> positions the panes,
/// so rearranging never re-parents the native video windows.
/// </summary>
public sealed class PaneLayout
{
    public const double MinPaneSize = 140;
    public const double SplitterSize = 5;

    public LayoutNode? Root { get; private set; }

    public IEnumerable<PlayerPane> Panes => Leaves(Root).Select(n => n.Pane);

    public int Count => Leaves(Root).Count();

    public bool Contains(PlayerPane pane) => Find(pane) != null;

    public bool HasSibling(PlayerPane pane) => Find(pane)?.Parent != null;

    public void Add(PlayerPane pane, PlayerPane? nextTo, DockSide side)
    {
        var leaf = new PaneNode(pane);
        var target = nextTo == null ? null : Find(nextTo);
        if (Root == null || target == null || side == DockSide.Center)
        {
            if (Root == null) Root = leaf;
            else Wrap(Root, leaf, side == DockSide.Center ? DockSide.Right : side);
            return;
        }
        Wrap(target, leaf, side);
    }

    public void Remove(PlayerPane pane)
    {
        var node = Find(pane);
        if (node == null) return;
        var parent = node.Parent;
        if (parent == null)
        {
            Root = null;
            return;
        }
        Replace(parent, parent.First == node ? parent.Second : parent.First);
    }

    /// <summary>Swaps a pane with its neighbour in the same split (a|b → b|a).</summary>
    public bool SwapWithSibling(PlayerPane pane)
    {
        var parent = Find(pane)?.Parent;
        if (parent == null) return false;
        (parent.First, parent.Second) = (parent.Second, parent.First);
        parent.Ratio = 1 - parent.Ratio;
        return true;
    }

    /// <summary>Side by side ↔ stacked for the split containing the pane.</summary>
    public bool ToggleOrientation(PlayerPane pane)
    {
        var parent = Find(pane)?.Parent;
        if (parent == null) return false;
        parent.Orientation = parent.Orientation == Orientation.Horizontal ? Orientation.Vertical : Orientation.Horizontal;
        return true;
    }

    /// <summary>Puts <paramref name="replacement"/> where <paramref name="pane"/> is (used for cross-window swaps).</summary>
    public void ReplacePane(PlayerPane pane, PlayerPane replacement)
    {
        var node = Find(pane);
        if (node != null) node.Pane = replacement;
    }

    public void SwapPanes(PlayerPane a, PlayerPane b)
    {
        var nodeA = Find(a);
        var nodeB = Find(b);
        if (nodeA == null || nodeB == null) return;
        (nodeA.Pane, nodeB.Pane) = (nodeB.Pane, nodeA.Pane);
    }

    /// <summary>Computes where every pane and every divider goes inside <paramref name="area"/>.</summary>
    public void Arrange(Rect area, Action<PlayerPane, Rect> placePane, Action<SplitNode, Rect, double> placeSplitter)
    {
        if (Root != null) Arrange(Root, area, placePane, placeSplitter);
    }

    static void Arrange(LayoutNode node, Rect area, Action<PlayerPane, Rect> placePane, Action<SplitNode, Rect, double> placeSplitter)
    {
        if (node is PaneNode leaf)
        {
            placePane(leaf.Pane, area);
            return;
        }

        var split = (SplitNode)node;
        bool horizontal = split.Orientation == Orientation.Horizontal;
        double total = Math.Max(0, (horizontal ? area.Width : area.Height) - SplitterSize);
        double first = total * split.Ratio;
        // Keep both sides usable when there is room for it.
        if (total >= MinPaneSize * 2) first = Math.Clamp(first, MinPaneSize, total - MinPaneSize);

        Rect a, divider, b;
        if (horizontal)
        {
            a = new Rect(area.X, area.Y, first, area.Height);
            divider = new Rect(area.X + first, area.Y, SplitterSize, area.Height);
            b = new Rect(area.X + first + SplitterSize, area.Y, total - first, area.Height);
        }
        else
        {
            a = new Rect(area.X, area.Y, area.Width, first);
            divider = new Rect(area.X, area.Y + first, area.Width, SplitterSize);
            b = new Rect(area.X, area.Y + first + SplitterSize, area.Width, total - first);
        }

        placeSplitter(split, divider, total);
        Arrange(split.First, a, placePane, placeSplitter);
        Arrange(split.Second, b, placePane, placeSplitter);
    }

    public IEnumerable<SplitNode> Splits() => Splits(Root);

    static IEnumerable<SplitNode> Splits(LayoutNode? node)
    {
        if (node is not SplitNode split) yield break;
        yield return split;
        foreach (var s in Splits(split.First)) yield return s;
        foreach (var s in Splits(split.Second)) yield return s;
    }

    void Wrap(LayoutNode target, PaneNode leaf, DockSide side)
    {
        var parent = target.Parent;
        bool before = side is DockSide.Left or DockSide.Top;
        var orientation = side is DockSide.Left or DockSide.Right ? Orientation.Horizontal : Orientation.Vertical;
        var split = before ? new SplitNode(orientation, leaf, target) : new SplitNode(orientation, target, leaf);
        split.Parent = parent;
        AttachTo(parent, target, split);
    }

    void Replace(LayoutNode old, LayoutNode replacement)
    {
        var parent = old.Parent;
        replacement.Parent = parent;
        AttachTo(parent, old, replacement);
    }

    void AttachTo(SplitNode? parent, LayoutNode old, LayoutNode replacement)
    {
        if (parent == null) Root = replacement;
        else if (parent.First == old) parent.First = replacement;
        else parent.Second = replacement;
    }

    PaneNode? Find(PlayerPane pane) => Leaves(Root).FirstOrDefault(n => n.Pane == pane);

    static IEnumerable<PaneNode> Leaves(LayoutNode? node)
    {
        switch (node)
        {
            case PaneNode leaf:
                yield return leaf;
                break;
            case SplitNode split:
                foreach (var n in Leaves(split.First)) yield return n;
                foreach (var n in Leaves(split.Second)) yield return n;
                break;
        }
    }
}
