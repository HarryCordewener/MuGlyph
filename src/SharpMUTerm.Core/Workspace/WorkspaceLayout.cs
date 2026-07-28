namespace SharpMUTerm.Core.Workspaces;

/// <summary>
/// The pure, UI-agnostic model of a tmux-style pane workspace: a recursive split tree of panes,
/// each hosting a tab strip of window ids, plus focus and zoom state. All mutation goes through
/// this type so the invariants — no single-child splits, always at least one pane, a valid focus —
/// hold after every operation. Empty panes are pruned, with one exception: closing the final
/// pane leaves a single empty pane (an <c>ActiveTab</c> of <c>null</c>) rather than no pane at all,
/// so consumers such as the SharpConsoleUI renderer must not assume every pane has an active tab.
/// The SharpConsoleUI layer renders from this model.
/// </summary>
public sealed class WorkspaceLayout
{
    private readonly HashSet<LayoutNode> _removed = new();
    private int _paneCounter;

    /// <summary>Creates a workspace with a single pane, optionally seeded with window ids.</summary>
    public WorkspaceLayout(IEnumerable<string>? initialTabs = null)
    {
        var pane = NewPane(initialTabs);
        Root = pane;
        FocusedPaneId = pane.Id;
    }

    /// <summary>
    /// Rebuilds a layout from a pre-constructed tree (used when resuming a saved session). Focus and
    /// zoom fall back to a valid pane when the given ids are stale, and the internal pane counter is
    /// advanced past the highest <c>pN</c> id so future splits never collide with restored ids.
    /// </summary>
    public WorkspaceLayout(LayoutNode root, string focusedPaneId, string? zoomedPaneId = null)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        var panes = Root.Panes().ToList();
        if (panes.Count == 0)
        {
            throw new ArgumentException("A layout must contain at least one pane.", nameof(root));
        }

        FocusedPaneId = panes.Any(p => p.Id == focusedPaneId) ? focusedPaneId : panes[0].Id;
        ZoomedPaneId = zoomedPaneId is not null && panes.Any(p => p.Id == zoomedPaneId) ? zoomedPaneId : null;
        _paneCounter = panes.Select(p => ParsePaneCounter(p.Id)).DefaultIfEmpty(0).Max();
    }

    /// <summary>Extracts the numeric suffix of a <c>pN</c> pane id, or 0 for an unrecognised shape.</summary>
    private static int ParsePaneCounter(string id) =>
        id.Length > 1 && id[0] == 'p' && int.TryParse(id.AsSpan(1), out var n) ? n : 0;

    /// <summary>The root of the split tree (a <see cref="PaneNode"/> or <see cref="SplitNode"/>).</summary>
    public LayoutNode Root { get; private set; }

    /// <summary>The id of the focused pane. Always references a live pane.</summary>
    public string FocusedPaneId { get; private set; }

    /// <summary>The id of the zoomed pane (rendered full-area), or null when nothing is zoomed.</summary>
    public string? ZoomedPaneId { get; private set; }

    /// <summary>Every pane, in left-to-right / top-to-bottom order.</summary>
    public IReadOnlyList<PaneNode> Panes => Root.Panes().ToList();

    /// <summary>The focused pane.</summary>
    public PaneNode FocusedPane => FindPane(FocusedPaneId)!;

    /// <summary>Finds a pane by id, or null.</summary>
    public PaneNode? FindPane(string id) => Root.Panes().FirstOrDefault(p => p.Id == id);

    /// <summary>Finds the pane currently hosting a window, or null.</summary>
    public PaneNode? FindWindow(string windowId) =>
        Root.Panes().FirstOrDefault(p => p.Tabs.Contains(windowId));

    /// <summary>Moves focus to a pane by id. Returns false if the pane does not exist.</summary>
    public bool Focus(string paneId)
    {
        if (FindPane(paneId) is null)
        {
            return false;
        }

        FocusedPaneId = paneId;
        return true;
    }

    /// <summary>Cycles focus to the next pane in tree order (tmux <c>o</c>).</summary>
    public void CycleFocus()
    {
        var panes = Panes;
        if (panes.Count <= 1)
        {
            return;
        }

        var index = 0;
        for (var i = 0; i < panes.Count; i++)
        {
            if (panes[i].Id == FocusedPaneId)
            {
                index = i;
                break;
            }
        }

        FocusedPaneId = panes[(index + 1) % panes.Count].Id;
    }

    /// <summary>
    /// Splits the focused pane along <paramref name="direction"/>, moving its non-active tabs into a
    /// new sibling pane (tmux <c>|</c> / <c>-</c>). The active tab and focus stay put. A no-op when
    /// the pane has fewer than two tabs (nothing to pull out). Returns true if a split happened.
    /// </summary>
    public bool SplitFocused(SplitDirection direction)
    {
        var pane = FocusedPane;
        if (pane.Tabs.Count <= 1)
        {
            return false;
        }

        var active = pane.ActiveTab!;
        var others = pane.Tabs.Where((_, i) => i != pane.ActiveIndex).ToList();

        pane.Tabs.Clear();
        pane.Tabs.Add(active);
        pane.ActiveIndex = 0;

        var newPane = NewPane(others);
        var split = new SplitNode(direction, new LayoutNode[] { pane, newPane });
        ReplaceNode(pane, split);
        return true;
    }

    /// <summary>Closes the focused pane and its tabs (tmux <c>x</c>), pruning and refocusing.</summary>
    public void CloseFocused()
    {
        _removed.Add(FocusedPane);
        PruneAndFix();
    }

    /// <summary>Toggles zoom on the focused pane (tmux <c>z</c>).</summary>
    public void ToggleZoom() =>
        ZoomedPaneId = ZoomedPaneId == FocusedPaneId ? null : FocusedPaneId;

    /// <summary>Toggles the frozen (split-scrollback) state of the focused pane.</summary>
    public void ToggleFreezeFocused()
    {
        var pane = FocusedPane;
        pane.Frozen = !pane.Frozen;
    }

    /// <summary>
    /// Adds a window as a tab in a pane (the focused pane by default). When
    /// <paramref name="activate"/> is true it becomes the pane's active tab; otherwise it is added in
    /// the background (but an empty pane always activates its first tab). Returns false without
    /// changing anything if a non-null <paramref name="paneId"/> does not resolve, matching
    /// <see cref="MoveWindowToPane"/> / <see cref="SplitWithWindow"/>.
    /// </summary>
    public bool AddWindow(string windowId, string? paneId = null, bool activate = true)
    {
        ArgumentNullException.ThrowIfNull(windowId);
        var pane = paneId is null ? FocusedPane : FindPane(paneId);
        if (pane is null)
        {
            return false;
        }

        DetachWindow(windowId);
        pane.Tabs.Add(windowId);
        if (activate || pane.ActiveIndex < 0)
        {
            pane.ActiveIndex = pane.Tabs.Count - 1;
        }

        PruneAndFix();
        return true;
    }

    /// <summary>Removes a window wherever it lives, pruning any pane it emptied. Returns false if absent.</summary>
    public bool RemoveWindow(string windowId)
    {
        if (!DetachWindow(windowId))
        {
            return false;
        }

        PruneAndFix();
        return true;
    }

    /// <summary>Moves a window into an existing pane as a tab, activating it. Returns false if the pane is gone.</summary>
    public bool MoveWindowToPane(string windowId, string targetPaneId)
    {
        var target = FindPane(targetPaneId);
        if (target is null)
        {
            return false;
        }

        DetachWindow(windowId);
        target.Tabs.Add(windowId);
        target.ActiveIndex = target.Tabs.Count - 1;
        PruneAndFix();
        return true;
    }

    /// <summary>
    /// Moves a window out into a new pane created by splitting <paramref name="targetPaneId"/> along
    /// the given edge, and focuses the new pane. Returns false if the target pane is gone.
    /// </summary>
    public bool SplitWithWindow(string windowId, string targetPaneId, Edge edge)
    {
        var target = FindPane(targetPaneId);
        if (target is null)
        {
            return false;
        }

        DetachWindow(windowId);
        var newPane = NewPane(new[] { windowId });
        var direction = edge is Edge.Left or Edge.Right ? SplitDirection.Row : SplitDirection.Column;
        var before = edge is Edge.Left or Edge.Top;
        var children = before
            ? new LayoutNode[] { newPane, target }
            : new LayoutNode[] { target, newPane };
        ReplaceNode(target, new SplitNode(direction, children));
        FocusedPaneId = newPane.Id;
        PruneAndFix();
        return true;
    }

    /// <summary>Reorders the focused pane's active tab by one slot (tmux <c>&lt;</c> / <c>&gt;</c>).</summary>
    public bool ReorderActiveTab(int delta)
    {
        var pane = FocusedPane;
        if (pane.Tabs.Count < 2 || pane.ActiveIndex < 0 || delta == 0)
        {
            return false;
        }

        var target = pane.ActiveIndex + Math.Sign(delta);
        if (target < 0 || target >= pane.Tabs.Count)
        {
            return false;
        }

        (pane.Tabs[pane.ActiveIndex], pane.Tabs[target]) = (pane.Tabs[target], pane.Tabs[pane.ActiveIndex]);
        pane.ActiveIndex = target;
        return true;
    }

    /// <summary>Sets the active tab of a pane to a hosted window. Returns false if not found.</summary>
    public bool SetActiveTab(string paneId, string windowId)
    {
        var pane = FindPane(paneId);
        var index = pane?.Tabs.IndexOf(windowId) ?? -1;
        if (pane is null || index < 0)
        {
            return false;
        }

        pane.ActiveIndex = index;
        return true;
    }

    private PaneNode NewPane(IEnumerable<string>? tabs = null) => new($"p{++_paneCounter}", tabs);

    /// <summary>Removes a window from whichever pane hosts it, fixing that pane's active index.</summary>
    private bool DetachWindow(string windowId)
    {
        foreach (var pane in Root.Panes())
        {
            var index = pane.Tabs.IndexOf(windowId);
            if (index < 0)
            {
                continue;
            }

            pane.Tabs.RemoveAt(index);

            // Only a tab strictly before the active one shifts the active index down. Removing the
            // active tab itself leaves the index pointing at what slid into its slot (the next tab);
            // ClampActive handles removing the active tab at the tail.
            if (pane.ActiveIndex > index)
            {
                pane.ActiveIndex--;
            }

            pane.ClampActive();
            return true;
        }

        return false;
    }

    /// <summary>Swaps <paramref name="target"/> for <paramref name="replacement"/> in the tree.</summary>
    private void ReplaceNode(LayoutNode target, LayoutNode replacement)
    {
        if (ReferenceEquals(Root, target))
        {
            Root = replacement;
            return;
        }

        if (FindParent(target) is (SplitNode parent, int index))
        {
            parent.Children[index] = replacement;
        }
    }

    private (SplitNode parent, int index)? FindParent(LayoutNode target)
    {
        return Search(Root);

        (SplitNode, int)? Search(LayoutNode node)
        {
            if (node is not SplitNode split)
            {
                return null;
            }

            for (var i = 0; i < split.Children.Count; i++)
            {
                if (ReferenceEquals(split.Children[i], target))
                {
                    return (split, i);
                }

                if (Search(split.Children[i]) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Rebuilds the tree, dropping panes marked for removal or left empty and collapsing splits with
    /// a single surviving child. Guarantees at least one pane and a valid focus/zoom afterward.
    /// </summary>
    private void PruneAndFix()
    {
        Root = Prune(Root) ?? NewPane();
        _removed.Clear();

        if (FindPane(FocusedPaneId) is null)
        {
            FocusedPaneId = Root.Panes().First().Id;
        }

        if (ZoomedPaneId is not null && FindPane(ZoomedPaneId) is null)
        {
            ZoomedPaneId = null;
        }
    }

    private LayoutNode? Prune(LayoutNode node)
    {
        if (_removed.Contains(node))
        {
            return null;
        }

        if (node is PaneNode pane)
        {
            return pane.Tabs.Count > 0 ? pane : null;
        }

        var split = (SplitNode)node;
        var keptNodes = new List<LayoutNode>();
        var keptSizes = new List<double>();
        for (var i = 0; i < split.Children.Count; i++)
        {
            if (Prune(split.Children[i]) is { } survivor)
            {
                keptNodes.Add(survivor);
                keptSizes.Add(split.Sizes[i]);
            }
        }

        return keptNodes.Count switch
        {
            0 => null,
            1 => keptNodes[0],
            _ => new SplitNode(split.Direction, keptNodes, keptSizes),
        };
    }
}
