using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Tests.Workspaces;

/// <summary>
/// Directional pane focus: which pane is left of, right of, above and below another. The rule answers
/// from geometry, so these are written as geometry — hand-built rectangles for the shapes worth pinning,
/// and <see cref="LayoutSolver"/> for the ones that have to agree with the real solver.
/// <para>
/// The edge that matters most is the one with <em>no</em> neighbour. It returns null rather than wrapping,
/// because the caller reports it: a navigation key that silently changes nothing is indistinguishable from
/// one that is not bound, which is how the pane prefix read before it learned to refuse out loud.
/// </para>
/// </summary>
public class PaneNavigationTests
{
    /// <summary>Two panes side by side, as <c>⌃B |</c> leaves them.</summary>
    private static Dictionary<string, PaneRect> SideBySide() => new(StringComparer.Ordinal)
    {
        ["p1"] = new PaneRect(0, 0, 40, 20),
        ["p2"] = new PaneRect(41, 0, 40, 20),
    };

    /// <summary>Two panes stacked, as <c>⌃B -</c> leaves them.</summary>
    private static Dictionary<string, PaneRect> Stacked() => new(StringComparer.Ordinal)
    {
        ["p1"] = new PaneRect(0, 0, 80, 10),
        ["p2"] = new PaneRect(0, 11, 80, 10),
    };

    [Test]
    public async Task SideBySide_LeftAndRightFindEachOther()
    {
        var rects = SideBySide();

        await Assert.That(PaneNavigation.Neighbour(rects, "p1", PaneDirection.Right)).IsEqualTo("p2");
        await Assert.That(PaneNavigation.Neighbour(rects, "p2", PaneDirection.Left)).IsEqualTo("p1");
    }

    /// <summary>
    /// The axis a split did not use has no neighbours. Two panes side by side are not above or below one
    /// another, however tempting it is to let a direction fall back to "any other pane".
    /// </summary>
    [Test]
    public async Task SideBySide_HasNothingAboveOrBelow()
    {
        var rects = SideBySide();

        await Assert.That(PaneNavigation.Neighbour(rects, "p1", PaneDirection.Up)).IsNull();
        await Assert.That(PaneNavigation.Neighbour(rects, "p1", PaneDirection.Down)).IsNull();
        await Assert.That(PaneNavigation.Neighbour(rects, "p2", PaneDirection.Up)).IsNull();
        await Assert.That(PaneNavigation.Neighbour(rects, "p2", PaneDirection.Down)).IsNull();
    }

    [Test]
    public async Task Stacked_UpAndDownFindEachOther()
    {
        var rects = Stacked();

        await Assert.That(PaneNavigation.Neighbour(rects, "p1", PaneDirection.Down)).IsEqualTo("p2");
        await Assert.That(PaneNavigation.Neighbour(rects, "p2", PaneDirection.Up)).IsEqualTo("p1");
    }

    [Test]
    public async Task Stacked_HasNothingLeftOrRight()
    {
        var rects = Stacked();

        await Assert.That(PaneNavigation.Neighbour(rects, "p1", PaneDirection.Left)).IsNull();
        await Assert.That(PaneNavigation.Neighbour(rects, "p2", PaneDirection.Right)).IsNull();
    }

    /// <summary>
    /// The outer edges of a two-pane workspace, in both shapes. Nothing wraps: moving left from the
    /// leftmost pane is a refusal, not a jump to the far side.
    /// </summary>
    [Test]
    public async Task TheOuterEdgesDoNotWrapAround()
    {
        await Assert.That(PaneNavigation.Neighbour(SideBySide(), "p1", PaneDirection.Left)).IsNull();
        await Assert.That(PaneNavigation.Neighbour(SideBySide(), "p2", PaneDirection.Right)).IsNull();
        await Assert.That(PaneNavigation.Neighbour(Stacked(), "p1", PaneDirection.Up)).IsNull();
        await Assert.That(PaneNavigation.Neighbour(Stacked(), "p2", PaneDirection.Down)).IsNull();
    }

    /// <summary>A single pane has no neighbour in any direction.</summary>
    [Test]
    [Arguments(PaneDirection.Left)]
    [Arguments(PaneDirection.Right)]
    [Arguments(PaneDirection.Up)]
    [Arguments(PaneDirection.Down)]
    public async Task OnePane_HasNoNeighbourAnywhere(PaneDirection direction)
    {
        var rects = new Dictionary<string, PaneRect>(StringComparer.Ordinal)
        {
            ["p1"] = new PaneRect(0, 0, 80, 24),
        };

        await Assert.That(PaneNavigation.Neighbour(rects, "p1", direction)).IsNull();
    }

    /// <summary>
    /// A pane alongside a nested split: moving right from the tall left pane picks the nearer of the two
    /// panes stacked to its right by cross-axis alignment, and moving back left from either returns to it.
    /// This is the shape a second split produces, and the one a structural tree walk gets wrong.
    /// </summary>
    [Test]
    public async Task ANestedSplit_PicksTheAlignedPaneAndComesBack()
    {
        //  p1  |  p2
        //      |------
        //      |  p3
        var rects = new Dictionary<string, PaneRect>(StringComparer.Ordinal)
        {
            ["p1"] = new PaneRect(0, 0, 40, 21),
            ["p2"] = new PaneRect(41, 0, 39, 10),
            ["p3"] = new PaneRect(41, 11, 39, 10),
        };

        // p1 spans both, so its centre row decides: it lands on the upper pane, which owns that row.
        await Assert.That(PaneNavigation.Neighbour(rects, "p1", PaneDirection.Right)).IsEqualTo("p2");
        await Assert.That(PaneNavigation.Neighbour(rects, "p2", PaneDirection.Left)).IsEqualTo("p1");
        await Assert.That(PaneNavigation.Neighbour(rects, "p3", PaneDirection.Left)).IsEqualTo("p1");
        await Assert.That(PaneNavigation.Neighbour(rects, "p2", PaneDirection.Down)).IsEqualTo("p3");
        await Assert.That(PaneNavigation.Neighbour(rects, "p3", PaneDirection.Up)).IsEqualTo("p2");
    }

    /// <summary>
    /// A pane that overlaps on the cross axis always beats a nearer one that is only diagonal. Without
    /// that rule, moving right out of a tall pane in an L-shaped layout can land in a pane that is
    /// visibly <em>above</em> it, which is the class of answer that makes directional navigation feel
    /// random.
    /// </summary>
    [Test]
    public async Task AnAlongsidePaneBeatsANearerDiagonalOne()
    {
        //         p2 (higher up, closer horizontally)
        //  p1     ----
        //         p3 (further right, but the one actually beside p1)
        var rects = new Dictionary<string, PaneRect>(StringComparer.Ordinal)
        {
            ["p1"] = new PaneRect(0, 10, 20, 6),
            ["p2"] = new PaneRect(21, 0, 20, 6),
            ["p3"] = new PaneRect(30, 10, 20, 6),
        };

        await Assert.That(PaneNavigation.Neighbour(rects, "p1", PaneDirection.Right)).IsEqualTo("p3");
    }

    /// <summary>
    /// A zoomed workspace realises one pane, so there is correctly nowhere to go — and the app passes the
    /// rectangles the frame was built from, which is why this is expressed through the solver rather than
    /// as a special case in the rule.
    /// </summary>
    [Test]
    public async Task AZoomedWorkspaceHasNoNeighbours()
    {
        var layout = new WorkspaceLayout(new[] { "a", "b" });
        layout.SplitFocused(SplitDirection.Row);
        layout.ToggleZoom();

        var rects = LayoutSolver.Solve(layout.Root, new PaneRect(0, 0, 80, 24), layout.ZoomedPaneId);

        await Assert.That(rects.Count).IsEqualTo(1);
        await Assert.That(PaneNavigation.Neighbour(rects, layout.FocusedPaneId, PaneDirection.Right)).IsNull();
    }

    /// <summary>
    /// The rule agrees with the real solver, at the real split the app makes. <c>SplitFocused(Row)</c>
    /// puts the new pane to the <em>right</em>, so ⌃→ from the original has to reach it — a claim about
    /// two components together, which neither hand-built rectangles nor the solver alone can make.
    /// </summary>
    [Test]
    public async Task ARowSplitPutsTheNewPaneToTheRightOfTheOldOne()
    {
        var layout = new WorkspaceLayout(new[] { "a", "b" });
        var original = layout.FocusedPaneId;
        layout.SplitFocused(SplitDirection.Row);
        var added = layout.Panes.Single(p => p.Id != original).Id;

        var rects = LayoutSolver.Solve(layout.Root, new PaneRect(0, 0, 80, 24));

        await Assert.That(PaneNavigation.Neighbour(rects, original, PaneDirection.Right)).IsEqualTo(added);
        await Assert.That(PaneNavigation.Neighbour(rects, added, PaneDirection.Left)).IsEqualTo(original);
    }

    /// <summary>And a column split puts it below, which is the other half of the same claim.</summary>
    [Test]
    public async Task AColumnSplitPutsTheNewPaneBelowTheOldOne()
    {
        var layout = new WorkspaceLayout(new[] { "a", "b" });
        var original = layout.FocusedPaneId;
        layout.SplitFocused(SplitDirection.Column);
        var added = layout.Panes.Single(p => p.Id != original).Id;

        var rects = LayoutSolver.Solve(layout.Root, new PaneRect(0, 0, 80, 24));

        await Assert.That(PaneNavigation.Neighbour(rects, original, PaneDirection.Down)).IsEqualTo(added);
        await Assert.That(PaneNavigation.Neighbour(rects, added, PaneDirection.Up)).IsEqualTo(original);
    }

    /// <summary>An unknown pane id is a refusal, not a throw — the layout can be rebuilt under a keystroke.</summary>
    [Test]
    public async Task AnUnknownPaneIsARefusal()
    {
        await Assert.That(PaneNavigation.Neighbour(SideBySide(), "gone", PaneDirection.Left)).IsNull();
    }

    /// <summary>
    /// A pane collapsed to no area is not a destination. <see cref="LayoutSolver"/> deliberately keeps
    /// zero-area rectangles in its answer so callers can decide, and this is the deciding.
    /// </summary>
    [Test]
    public async Task ACollapsedPaneIsNotADestination()
    {
        var rects = new Dictionary<string, PaneRect>(StringComparer.Ordinal)
        {
            ["p1"] = new PaneRect(0, 0, 40, 20),
            ["p2"] = new PaneRect(41, 0, 0, 20),
        };

        await Assert.That(PaneNavigation.Neighbour(rects, "p1", PaneDirection.Right)).IsNull();
    }

    /// <summary>
    /// The answer is stable when two candidates tie: the walk over a dictionary has no order of its own,
    /// so ties break on the pane id and repeated calls agree. A focus key that moved somewhere different
    /// on the second press for no visible reason would be the worst kind of bug to chase.
    /// </summary>
    [Test]
    public async Task TiesBreakDeterministically()
    {
        var rects = new Dictionary<string, PaneRect>(StringComparer.Ordinal)
        {
            ["p1"] = new PaneRect(0, 0, 20, 20),
            ["p2"] = new PaneRect(21, 0, 20, 10),
            ["p3"] = new PaneRect(21, 10, 20, 10),
        };

        var answers = Enumerable.Range(0, 20)
            .Select(_ => PaneNavigation.Neighbour(
                new Dictionary<string, PaneRect>(rects, StringComparer.Ordinal), "p1", PaneDirection.Right))
            .Distinct()
            .ToList();

        await Assert.That(answers.Count).IsEqualTo(1);
    }
}
