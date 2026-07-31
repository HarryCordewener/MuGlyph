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

    /// <summary>
    /// The last <see cref="PaneNode.Sequence"/> handed out. Kept apart from <see cref="_paneCounter"/>
    /// (which only feeds the <c>pN</c> ids) because the two can start out of step: a workspace restored
    /// from a configuration written before panes carried a sequence has its numbering assigned here from
    /// tree order, and its ids may be anything a previous version wrote.
    /// </summary>
    private int _sequenceCounter;

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
    /// <para>
    /// <b>A pane restored without a creation sequence is given one here, from tree order.</b> Panes are
    /// numbered by <see cref="PaneNode.Sequence"/> and a configuration written before that field existed
    /// carries none, so without this every restored pane would sort equal and the numbering would be
    /// whatever the sort happened to do. Tree order is the numbering those workspaces were saved under,
    /// which is why it is the right seed: an existing configuration comes back reading exactly as it
    /// read when it was closed, and is stable from then on. Any pane that <em>does</em> carry a sequence
    /// keeps it, and unsequenced ones are numbered after the highest that is already taken, so a
    /// half-migrated tree cannot produce two panes with one number.
    /// </para>
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

        _sequenceCounter = panes.Select(p => p.Sequence).DefaultIfEmpty(PaneNode.Unsequenced).Max();
        foreach (var pane in panes.Where(p => p.Sequence <= PaneNode.Unsequenced))
        {
            pane.Sequence = ++_sequenceCounter;
        }
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

    /// <summary>
    /// <b>Every pane in creation order — the one order this client numbers panes in.</b> The Nth entry is
    /// <c>pane N</c> in the ⌃P <c>Go to pane N</c> entry, the move/drag overlays' label and badge, and the
    /// ⌃B N chord; those are three spellings of this index and there is deliberately no second ordering
    /// for any of them to drift onto. It is <em>not</em> what ⌥N counts — that chord names a window
    /// (<see cref="Workspace.PlacedWindows"/>), which is a different set of things in a different order,
    /// and the two are kept apart in the vocabulary as well: a pane is <c>pane N</c>, a window's chord is
    /// <c>⌥N</c>, and the sidebar prints only the second.
    /// <para>
    /// <b>Why not tree order.</b> It used to be <c>Root.Panes()</c>, which is geometry: a pane's position
    /// left-to-right / top-to-bottom. Creating a pane therefore renumbered every pane after the insertion
    /// point — dropping a window on the left edge of pane 2 made that pane into pane 3 — so the digit that
    /// meant it silently stopped meaning it, on a workspace the user had not otherwise rearranged.
    /// Creation order cannot do that: a pane's number is fixed for as long as it is open, and a new pane
    /// always appears at the end.
    /// </para>
    /// <para>
    /// <b>The number is the index, not the sequence.</b> Sequences are never reused, so reading them
    /// directly would leave holes — close pane 2 of three and the survivors would be 1 and 3, with the
    /// digit 2 doing nothing while two panes sat on the screen. Taking the position in this list instead
    /// compacts the numbering on every close, which is what keeps 1–N contiguous.
    /// </para>
    /// <para>
    /// Geometry does not read this. The renderer (<c>LayoutSolver</c>), the resizer (<c>PaneResize</c>)
    /// and directional movement (<c>PaneNavigation</c>, which works on arranged rectangles) all go
    /// through <see cref="LayoutNode.Panes"/> or the tree itself, so ordering here changes what panes are
    /// <em>called</em> and never where they are drawn.
    /// </para>
    /// </summary>
    public IReadOnlyList<PaneNode> Panes => Root.Panes().OrderBy(p => p.Sequence).ToList();

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

    /// <summary>
    /// Cycles focus to the next pane by number (tmux <c>o</c>) — ⌃O from <c>pane 2</c> goes to
    /// <c>pane 3</c>, and wraps from the last back to <c>pane 1</c>.
    /// <para>
    /// It counts in <see cref="Panes"/> order, because these are the two <em>ordinal</em> pane movers and
    /// a user pressing them alternately must not be counting two different sequences: three presses of
    /// ⌃O from pane 1 land where ⌃B 4 does, and where the move overlay's badge <c>4</c> is drawn. It used
    /// to be tree order, which was the numbering at the time and stopped being it.
    /// </para>
    /// <para>
    /// <b>The partner it has to agree with changed, and the order did not.</b> That sentence used to name
    /// ⌥N, which counted panes; ⌥N counts <em>windows</em> now and the numbered pane jump moved to ⌃B N.
    /// Cycling still counts panes in this order because every surface that still says <c>pane N</c> does
    /// — the move and drag overlays, the split and resize refusals, the ⌃P entry and ⌃B N. Nothing about
    /// the window chord gives this a reason to count differently; the window movers are ⌥N and ⌃N, and
    /// they are a separate ladder.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Re-points an existing zoom at whichever pane is now focused, and says whether it moved. Returns
    /// false when nothing is zoomed — this never <em>starts</em> a zoom, it only carries one.
    /// <para>
    /// Focus and zoom are separate fields, and a mover that changes one without the other leaves the
    /// focused pane <em>off screen</em>: a zoomed workspace realises exactly one pane, so the selection,
    /// the session the command line talks to and the caret would all be on a pane the user cannot see —
    /// attention on one pane and keystrokes to another, which is the defect the per-window work exists to
    /// eliminate. The ordinal movers (cycle, and jump-to-number) therefore carry the zoom with them, and
    /// so the pane you asked for is the pane that fills the screen. The <em>directional</em> movers do
    /// not, and cannot: while one pane is realised there is no pane "to the left" to ask for, which is
    /// why ⌃← refuses out loud instead.
    /// </para>
    /// </summary>
    public bool CarryZoomToFocused()
    {
        if (ZoomedPaneId is null || ZoomedPaneId == FocusedPaneId)
        {
            return false;
        }

        ZoomedPaneId = FocusedPaneId;
        return true;
    }

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

    /// <summary>
    /// Mints a pane: a fresh id, and the next creation sequence, which is what puts it last in
    /// <see cref="Panes"/> and so gives it the highest pane number rather than displacing anyone's.
    /// </summary>
    private PaneNode NewPane(IEnumerable<string>? tabs = null) =>
        new($"p{++_paneCounter}", tabs, sequence: ++_sequenceCounter);

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
            // The lowest-numbered survivor — pane 1 — rather than the topmost-leftmost, so closing the
            // focused pane lands you where the label says it did.
            FocusedPaneId = Panes[0].Id;
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
