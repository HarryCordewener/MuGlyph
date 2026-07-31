namespace SharpMUTerm.Core.Workspaces;

/// <summary>How a <see cref="SplitNode"/> arranges its children.</summary>
public enum SplitDirection
{
    /// <summary>Children sit side by side, left to right (a vertical divider between them).</summary>
    Row,

    /// <summary>Children stack top to bottom (a horizontal divider between them).</summary>
    Column,
}

/// <summary>An edge of a pane, used when splitting a pane by dropping a window against a side.</summary>
public enum Edge
{
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>A node in the workspace split tree: either a <see cref="PaneNode"/> or a <see cref="SplitNode"/>.</summary>
public abstract class LayoutNode
{
    /// <summary>
    /// Enumerates every pane at or under this node in <em>tree</em> order — left-to-right /
    /// top-to-bottom, which is geometry and nothing else.
    /// <para>
    /// This is the order the renderer and the resizer want, and it is <b>not</b> the order panes are
    /// numbered in. Tree order is a function of where a pane sits, so creating one renumbers whatever
    /// it was inserted before: splitting the second of two panes on its left edge makes the old pane 2
    /// into pane 3, and the digit that meant pane 2 stops meaning it. The numbering users see and
    /// press comes from <see cref="WorkspaceLayout.Panes"/>, which is creation order.
    /// </para>
    /// </summary>
    public abstract IEnumerable<PaneNode> Panes();
}

/// <summary>
/// A leaf pane: a bordered box hosting a tab strip of window ids and one active tab. Panes are the
/// only nodes that hold content; interior nodes only divide space.
/// </summary>
public sealed class PaneNode : LayoutNode
{
    public PaneNode(
        string id,
        IEnumerable<string>? tabs = null,
        int activeIndex = 0,
        bool frozen = false,
        int sequence = Unsequenced)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Tabs = tabs is null ? new List<string>() : new List<string>(tabs);
        ActiveIndex = Tabs.Count == 0 ? -1 : Math.Clamp(activeIndex, 0, Tabs.Count - 1);
        Frozen = frozen;
        Sequence = sequence;
    }

    /// <summary>
    /// The <see cref="Sequence"/> of a pane nobody has numbered yet — a pane restored from a workspace
    /// persisted before panes carried a creation order. <see cref="WorkspaceLayout"/> replaces it on
    /// load, from tree order, so an old configuration comes back numbered the way it was left rather
    /// than scrambled.
    /// </summary>
    public const int Unsequenced = 0;

    /// <summary>Stable pane identity, unique within a workspace.</summary>
    public string Id { get; }

    /// <summary>
    /// When this pane was created, as a per-workspace counter that only ever goes up. Panes are
    /// <em>numbered</em> by this (see <see cref="WorkspaceLayout.Panes"/>) rather than by where they
    /// sit, so a pane's number is fixed for as long as it is open: creating one somewhere to its left
    /// no longer pushes it from pane 2 to pane 3.
    /// <para>
    /// It is a sort key and not the number itself. The number is the pane's position in the sorted
    /// list, which is what keeps the pane numbering contiguous when a pane in the middle closes — see
    /// <see cref="WorkspaceLayout.Panes"/>.
    /// </para>
    /// </summary>
    public int Sequence { get; internal set; }

    /// <summary>The window ids hosted here, in tab order.</summary>
    public List<string> Tabs { get; }

    /// <summary>Index of the active tab, or -1 when the pane is empty.</summary>
    public int ActiveIndex { get; set; }

    /// <summary>Whether this pane is frozen (scrollback split from live output).</summary>
    public bool Frozen { get; set; }

    /// <summary>The active window id, or null when the pane is empty.</summary>
    public string? ActiveTab => ActiveIndex >= 0 && ActiveIndex < Tabs.Count ? Tabs[ActiveIndex] : null;

    public override IEnumerable<PaneNode> Panes()
    {
        yield return this;
    }

    /// <summary>Re-clamps <see cref="ActiveIndex"/> into range after the tab list changes.</summary>
    internal void ClampActive() => ActiveIndex = Tabs.Count == 0 ? -1 : Math.Clamp(ActiveIndex, 0, Tabs.Count - 1);
}

/// <summary>
/// An interior split dividing its area among two or more children along one axis. Sizes are
/// fractional weights that always sum to 1.
/// </summary>
public sealed class SplitNode : LayoutNode
{
    public SplitNode(SplitDirection direction, IReadOnlyList<LayoutNode> children, IReadOnlyList<double>? sizes = null)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Count < 2)
        {
            throw new ArgumentException("A split needs at least two children.", nameof(children));
        }

        Direction = direction;
        Children = new List<LayoutNode>(children);
        Sizes = sizes is { Count: > 0 } && sizes.Count == children.Count
            ? Normalize(sizes)
            : Even(children.Count);
    }

    public SplitDirection Direction { get; }

    public List<LayoutNode> Children { get; }

    /// <summary>Fractional sizes parallel to <see cref="Children"/>, summing to 1.</summary>
    public List<double> Sizes { get; private set; }

    public override IEnumerable<PaneNode> Panes()
    {
        foreach (var child in Children)
        {
            foreach (var pane in child.Panes())
            {
                yield return pane;
            }
        }
    }

    /// <summary>Resets sizes to an even split across the current children.</summary>
    internal void ResetSizes() => Sizes = Even(Children.Count);

    /// <summary>
    /// Replaces the sizes, re-normalised so they still sum to 1 — the seam <see cref="PaneResize"/>
    /// writes a moved border back through. Internal, and normalising rather than trusting, because the
    /// "sums to 1" claim on <see cref="Sizes"/> is what the renderer's star weights and
    /// <see cref="LayoutSolver"/> both rely on; a caller handing in cell counts must not be able to break
    /// it by arithmetic.
    /// </summary>
    internal void SetSizes(IReadOnlyList<double> sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        if (sizes.Count != Children.Count)
        {
            throw new ArgumentException("One size per child.", nameof(sizes));
        }

        Sizes = Normalize(sizes);
    }

    private static List<double> Even(int count)
    {
        var each = 1.0 / count;
        return Enumerable.Repeat(each, count).ToList();
    }

    private static List<double> Normalize(IReadOnlyList<double> sizes)
    {
        var total = sizes.Sum(s => Math.Max(0, s));
        if (total <= 0)
        {
            return Even(sizes.Count);
        }

        return sizes.Select(s => Math.Max(0, s) / total).ToList();
    }
}
