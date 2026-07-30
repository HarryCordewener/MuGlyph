using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Tests.Workspaces;

/// <summary>
/// Making the focused pane wider/narrower and taller/shorter, in character cells.
/// <para>
/// The model has carried <see cref="SplitNode.Sizes"/> since splits existed and the renderer has always
/// read them, but nothing ever wrote them: <c>Even</c> at construction and the pruning that preserves
/// them on a close were the only two writes in the tree. So every split was permanently 50/50 and the
/// uneven case had never been exercised at all — which is why these go as far as re-solving the layout
/// and reading the resulting rectangles back, rather than asserting that a list of doubles changed.
/// </para>
/// <para>
/// Written in cells throughout. The rectangles are the input <em>and</em> the assertion, because "the
/// border moved two columns" is the claim; a fraction that moved by 0.025 is arithmetic that may or may
/// not survive rounding into the grid.
/// </para>
/// </summary>
public class PaneResizeTests
{
    private const int Divider = LayoutSolver.DividerThickness;

    /// <summary>A workspace of two panes side by side, and the area it is solved over.</summary>
    private static (WorkspaceLayout Layout, PaneRect Bounds) SideBySide(int width = 80, int height = 24)
    {
        var left = new PaneNode("p1", new[] { "w1" });
        var right = new PaneNode("p2", new[] { "w2" });
        return (new WorkspaceLayout(new SplitNode(SplitDirection.Row, new LayoutNode[] { left, right }), "p1"),
            new PaneRect(0, 0, width, height));
    }

    /// <summary>A workspace of two panes stacked.</summary>
    private static (WorkspaceLayout Layout, PaneRect Bounds) Stacked(int width = 80, int height = 24)
    {
        var top = new PaneNode("p1", new[] { "w1" });
        var bottom = new PaneNode("p2", new[] { "w2" });
        return (new WorkspaceLayout(new SplitNode(SplitDirection.Column, new LayoutNode[] { top, bottom }), "p1"),
            new PaneRect(0, 0, width, height));
    }

    private static IReadOnlyDictionary<string, PaneRect> Solve(WorkspaceLayout layout, PaneRect bounds) =>
        LayoutSolver.Solve(layout.Root, bounds, layout.ZoomedPaneId);

    /// <summary>Applies a resize against the freshly solved rectangles, the way the app does.</summary>
    private static PaneResizeResult Resize(WorkspaceLayout layout, PaneRect bounds, PaneDirection direction) =>
        PaneResize.Apply(layout, direction, Solve(layout, bounds));

    // --- the four directions -----------------------------------------------------------------------

    /// <summary>
    /// ⌥⇧→ widens the focused pane by exactly the step, and the columns come out of the pane beside it.
    /// The total is unchanged, which is the invariant the divider depends on.
    /// </summary>
    [Test]
    public async Task RightWidensTheFocusedPaneByTheStep()
    {
        var (layout, bounds) = SideBySide();
        var before = Solve(layout, bounds);

        var result = Resize(layout, bounds, PaneDirection.Right);
        var after = Solve(layout, bounds);

        await Assert.That(result.Outcome).IsEqualTo(PaneResizeOutcome.Resized);
        await Assert.That(result.Cells).IsEqualTo(PaneResize.StepCells);
        await Assert.That(after["p1"].Width - before["p1"].Width)
            .IsEqualTo(1)
            .Because("one press is one cell — the step is written out here because every other assertion "
                + "in this file is in terms of the constant and would follow it anywhere it went");
        await Assert.That(after["p1"].Width).IsEqualTo(before["p1"].Width + PaneResize.StepCells);
        await Assert.That(after["p2"].Width).IsEqualTo(before["p2"].Width - PaneResize.StepCells);
        await Assert.That(after["p1"].Width + after["p2"].Width + Divider).IsEqualTo(bounds.Width);
    }

    /// <summary>⌥⇧← narrows it again, and two presses either way return it to where it started.</summary>
    [Test]
    public async Task LeftNarrowsIt_AndTheTwoDirectionsUndoEachOther()
    {
        var (layout, bounds) = SideBySide();
        var before = Solve(layout, bounds);

        Resize(layout, bounds, PaneDirection.Left);
        var narrowed = Solve(layout, bounds);
        await Assert.That(narrowed["p1"].Width).IsEqualTo(before["p1"].Width - PaneResize.StepCells);

        Resize(layout, bounds, PaneDirection.Right);
        var back = Solve(layout, bounds);
        await Assert.That(back["p1"]).IsEqualTo(before["p1"]);
        await Assert.That(back["p2"]).IsEqualTo(before["p2"]);
    }

    /// <summary>⌥⇧↑ makes it taller and ⌥⇧↓ shorter, on a stacked split.</summary>
    [Test]
    public async Task UpAndDownChangeTheHeightOfAStackedSplit()
    {
        var (layout, bounds) = Stacked();
        var before = Solve(layout, bounds);

        await Assert.That(Resize(layout, bounds, PaneDirection.Up).Cells).IsEqualTo(PaneResize.StepCells);
        var taller = Solve(layout, bounds);
        await Assert.That(taller["p1"].Height).IsEqualTo(before["p1"].Height + PaneResize.StepCells);
        await Assert.That(taller["p2"].Height).IsEqualTo(before["p2"].Height - PaneResize.StepCells);

        Resize(layout, bounds, PaneDirection.Down);
        var back = Solve(layout, bounds);
        await Assert.That(back["p1"].Height).IsEqualTo(before["p1"].Height);
    }

    /// <summary>
    /// <b>The reported case: an arrow names what happens to <em>this</em> pane, from either end of the
    /// split.</b> Stacked panes, and ⌥⇧↑ pressed from each of them in turn — both get taller. The rule was
    /// once read off the divider instead, so the bottom pane's ↑ made it shorter and "what does ⌥⇧↑ do"
    /// had no answer that did not begin "it depends where you are standing". Asserting from one pane only
    /// is what let that ship: the top pane's behaviour was identical under both rules.
    /// </summary>
    [Test]
    [Arguments("p1")]
    [Arguments("p2")]
    public async Task UpMakesTheFocusedPaneTallerWhicheverEndOfTheSplitItIsAt(string focused)
    {
        var (layout, bounds) = Stacked();
        layout.Focus(focused);
        var other = focused == "p1" ? "p2" : "p1";
        var before = Solve(layout, bounds);

        await Assert.That(Resize(layout, bounds, PaneDirection.Up).Cells).IsEqualTo(PaneResize.StepCells);
        var taller = Solve(layout, bounds);
        await Assert.That(taller[focused].Height)
            .IsEqualTo(before[focused].Height + PaneResize.StepCells)
            .Because($"⌥⇧↑ with {focused} focused must make {focused} taller");
        await Assert.That(taller[other].Height).IsEqualTo(before[other].Height - PaneResize.StepCells);

        // And ↓ is its exact undo from the same place, rather than moving a different border back.
        await Assert.That(Resize(layout, bounds, PaneDirection.Down).Cells).IsEqualTo(-PaneResize.StepCells);
        await Assert.That(Solve(layout, bounds)).IsEquivalentTo(before);
    }

    /// <summary>The horizontal pair, held to the same rule from both ends — it has had no scrutiny at all.</summary>
    [Test]
    [Arguments("p1")]
    [Arguments("p2")]
    public async Task RightMakesTheFocusedPaneWiderWhicheverEndOfTheSplitItIsAt(string focused)
    {
        var (layout, bounds) = SideBySide();
        layout.Focus(focused);
        var other = focused == "p1" ? "p2" : "p1";
        var before = Solve(layout, bounds);

        Resize(layout, bounds, PaneDirection.Right);
        var wider = Solve(layout, bounds);
        await Assert.That(wider[focused].Width)
            .IsEqualTo(before[focused].Width + PaneResize.StepCells)
            .Because($"⌥⇧→ with {focused} focused must make {focused} wider");
        await Assert.That(wider[other].Width).IsEqualTo(before[other].Width - PaneResize.StepCells);

        Resize(layout, bounds, PaneDirection.Left);
        await Assert.That(Solve(layout, bounds)).IsEquivalentTo(before);
    }

    /// <summary>
    /// <b>Three children in one split, and "wider" still means wider from every one of them.</b> The payer
    /// is the nearest sibling on the side the growth comes from — the next child, or the previous one for
    /// the last — so the middle pane grows into the one on its right and the last into the one on its
    /// left, and in all three cases the pane the user is looking at is the one that got bigger.
    /// </summary>
    [Test]
    [Arguments("p1")]
    [Arguments("p2")]
    [Arguments("p3")]
    public async Task WiderMeansWiderFromEveryChildOfASplitOfThree(string focused)
    {
        var layout = new WorkspaceLayout(
            new SplitNode(
                SplitDirection.Row,
                new LayoutNode[]
                {
                    new PaneNode("p1", new[] { "w1" }),
                    new PaneNode("p2", new[] { "w2" }),
                    new PaneNode("p3", new[] { "w3" }),
                }),
            focused);
        var bounds = new PaneRect(0, 0, 150, 40);
        var before = Solve(layout, bounds);

        Resize(layout, bounds, PaneDirection.Right);
        var wider = Solve(layout, bounds);

        await Assert.That(wider[focused].Width).IsEqualTo(before[focused].Width + PaneResize.StepCells);

        // Exactly one sibling paid, and the third pane did not move at all.
        var payers = new[] { "p1", "p2", "p3" }
            .Where(id => id != focused && wider[id].Width != before[id].Width)
            .ToList();
        await Assert.That(payers.Count).IsEqualTo(1);
        await Assert.That(wider[payers[0]].Width).IsEqualTo(before[payers[0]].Width - PaneResize.StepCells);

        // ← is the undo of →, not a walk across the split: the same border comes back.
        Resize(layout, bounds, PaneDirection.Left);
        await Assert.That(Solve(layout, bounds)).IsEquivalentTo(before);
    }

    /// <summary>
    /// The pane-relative rule survives the walk up the tree. Two panes stacked inside the right half of a
    /// side-by-side split: ⌥⇧↑ from either of them is about the <em>inner</em> column split, and each
    /// makes itself taller — the ancestor walk is orthogonal to what the arrow means.
    /// </summary>
    [Test]
    [Arguments("p2")]
    [Arguments("p3")]
    public async Task UpMakesTheFocusedPaneTallerInsideANestedSubtree(string focused)
    {
        var stack = new SplitNode(
            SplitDirection.Column,
            new LayoutNode[] { new PaneNode("p2", new[] { "w2" }), new PaneNode("p3", new[] { "w3" }) });
        var layout = new WorkspaceLayout(
            new SplitNode(
                SplitDirection.Row,
                new LayoutNode[] { new PaneNode("p1", new[] { "w1" }), stack }),
            focused);
        var bounds = new PaneRect(0, 0, 120, 40);
        var before = Solve(layout, bounds);

        Resize(layout, bounds, PaneDirection.Up);
        var after = Solve(layout, bounds);

        await Assert.That(after[focused].Height).IsEqualTo(before[focused].Height + PaneResize.StepCells);
        await Assert.That(after["p1"]).IsEqualTo(before["p1"]); // the other branch is untouched
    }

    /// <summary>
    /// A pane at the far end of its split still grows when the arrow says wider — the border on its
    /// <em>other</em> side is the one that moves. tmux's rule, and the one that keeps "wider" meaning
    /// wider wherever the pane sits.
    /// </summary>
    [Test]
    public async Task TheLastChildGrowsFromTheBorderOnItsOtherSide()
    {
        var (layout, bounds) = SideBySide();
        layout.Focus("p2");
        var before = Solve(layout, bounds);

        Resize(layout, bounds, PaneDirection.Right);
        var after = Solve(layout, bounds);

        await Assert.That(after["p2"].Width).IsEqualTo(before["p2"].Width + PaneResize.StepCells);
        await Assert.That(after["p1"].Width).IsEqualTo(before["p1"].Width - PaneResize.StepCells);

        // And its left edge is what moved: the pane's right edge is the workspace's.
        await Assert.That(after["p2"].X).IsEqualTo(before["p2"].X - PaneResize.StepCells);
        await Assert.That(after["p2"].X + after["p2"].Width).IsEqualTo(before["p2"].X + before["p2"].Width);
    }

    /// <summary>
    /// A repeated press keeps moving the border a step at a time — the model round-trips through
    /// fractions without drifting, which a naive "add a fraction" implementation does not.
    /// </summary>
    [Test]
    public async Task RepeatedPressesEachMoveOneStep()
    {
        var (layout, bounds) = SideBySide(120);
        var start = Solve(layout, bounds)["p1"].Width;

        for (var press = 1; press <= 5; press++)
        {
            Resize(layout, bounds, PaneDirection.Right);
            await Assert.That(Solve(layout, bounds)["p1"].Width)
                .IsEqualTo(start + (press * PaneResize.StepCells))
                .Because($"press {press} must move the border one more step");
        }
    }

    // --- which split an arrow acts on --------------------------------------------------------------

    /// <summary>
    /// <b>The walk up the tree.</b> Two panes stacked inside the right half of a side-by-side split: the
    /// focused pane's own parent is a <em>column</em>, so ⌥⇧→ has to climb past it to the row split above
    /// and move that border. The whole stacked subtree widens together, and the left pane pays.
    /// </summary>
    [Test]
    public async Task AnArrowClimbsPastAParentThatDoesNotRunInItsAxis()
    {
        var left = new PaneNode("p1", new[] { "w1" });
        var stack = new SplitNode(
            SplitDirection.Column,
            new LayoutNode[] { new PaneNode("p2", new[] { "w2" }), new PaneNode("p3", new[] { "w3" }) });
        var layout = new WorkspaceLayout(
            new SplitNode(SplitDirection.Row, new LayoutNode[] { left, stack }), "p2");
        var bounds = new PaneRect(0, 0, 120, 40);
        var before = Solve(layout, bounds);

        var result = Resize(layout, bounds, PaneDirection.Right);
        var after = Solve(layout, bounds);

        await Assert.That(result.Outcome).IsEqualTo(PaneResizeOutcome.Resized);
        await Assert.That(after["p1"].Width).IsEqualTo(before["p1"].Width - PaneResize.StepCells);
        await Assert.That(after["p2"].Width).IsEqualTo(before["p2"].Width + PaneResize.StepCells);

        // Its stacked neighbour moved with it — the border belongs to the subtree, not to the pane.
        await Assert.That(after["p3"].Width).IsEqualTo(before["p3"].Width + PaneResize.StepCells);
        await Assert.That(after["p2"].Height).IsEqualTo(before["p2"].Height);
        await Assert.That(after["p3"].Height).IsEqualTo(before["p3"].Height);
    }

    /// <summary>
    /// The nearest ancestor in the axis wins, not the outermost. In a row inside a row, ⌥⇧→ moves the
    /// inner border — the one the user is looking at — and leaves the outer split alone.
    /// </summary>
    [Test]
    public async Task TheNearestAncestorInTheAxisIsTheOneThatMoves()
    {
        var inner = new SplitNode(
            SplitDirection.Row,
            new LayoutNode[] { new PaneNode("p2", new[] { "w2" }), new PaneNode("p3", new[] { "w3" }) });
        var layout = new WorkspaceLayout(
            new SplitNode(
                SplitDirection.Row,
                new LayoutNode[] { new PaneNode("p1", new[] { "w1" }), inner }),
            "p2");
        var bounds = new PaneRect(0, 0, 160, 40);
        var before = Solve(layout, bounds);

        Resize(layout, bounds, PaneDirection.Right);
        var after = Solve(layout, bounds);

        await Assert.That(after["p1"].Width).IsEqualTo(before["p1"].Width); // the outer split did not move
        await Assert.That(after["p2"].Width).IsEqualTo(before["p2"].Width + PaneResize.StepCells);
        await Assert.That(after["p3"].Width).IsEqualTo(before["p3"].Width - PaneResize.StepCells);
    }

    // --- reporting rather than no-op'ing -----------------------------------------------------------

    /// <summary>
    /// <b>No ancestor in that axis reports, and reports in words.</b> Two panes side by side have no
    /// border between their rows, so ⌥⇧↓ has nothing to move — and a key that changes nothing and says
    /// nothing is the most-repeated defect in this client's history. Every non-<c>Resized</c> outcome
    /// carries a sentence.
    /// </summary>
    [Test]
    public async Task AnAxisWithNoSplitReportsInsteadOfDoingNothingQuietly()
    {
        var (layout, bounds) = SideBySide();
        var before = Solve(layout, bounds);

        foreach (var direction in new[] { PaneDirection.Down, PaneDirection.Up })
        {
            var result = Resize(layout, bounds, direction);

            await Assert.That(result.Outcome).IsEqualTo(PaneResizeOutcome.NoSplitInThatAxis);
            await Assert.That(result.Cells).IsEqualTo(0);
            await Assert.That(PaneResize.Describe(result.Outcome, direction)).IsNotEmpty();
            await Assert.That(Solve(layout, bounds)).IsEquivalentTo(before);
        }
    }

    /// <summary>The mirror: a stacked split has nothing to move sideways.</summary>
    [Test]
    public async Task AStackedSplitHasNoHorizontalBorder()
    {
        var (layout, bounds) = Stacked();

        await Assert.That(Resize(layout, bounds, PaneDirection.Right).Outcome)
            .IsEqualTo(PaneResizeOutcome.NoSplitInThatAxis);
        await Assert.That(Resize(layout, bounds, PaneDirection.Left).Outcome)
            .IsEqualTo(PaneResizeOutcome.NoSplitInThatAxis);
    }

    /// <summary>A one-pane workspace has no border in any axis, and says so four times over.</summary>
    [Test]
    public async Task AOnePaneWorkspaceRefusesEveryDirection()
    {
        var layout = new WorkspaceLayout(new[] { "w1" });
        var bounds = new PaneRect(0, 0, 80, 24);

        foreach (var direction in Enum.GetValues<PaneDirection>())
        {
            var result = Resize(layout, bounds, direction);
            await Assert.That(result.Outcome).IsEqualTo(PaneResizeOutcome.NoSplitInThatAxis);
            await Assert.That(PaneResize.Describe(result.Outcome, direction)).IsNotEmpty();
        }
    }

    /// <summary>A zoomed pane is the whole area, so there is no split on screen to move — reported too.</summary>
    [Test]
    public async Task AZoomedPaneReportsRatherThanResizingTheTreeBehindIt()
    {
        var (layout, bounds) = SideBySide();
        layout.ToggleZoom();
        var sizes = ((SplitNode)layout.Root).Sizes.ToList();

        var result = Resize(layout, bounds, PaneDirection.Right);

        await Assert.That(result.Outcome).IsEqualTo(PaneResizeOutcome.Zoomed);
        await Assert.That(PaneResize.Describe(result.Outcome, PaneDirection.Right)).IsNotEmpty();
        await Assert.That(((SplitNode)layout.Root).Sizes).IsEquivalentTo(sizes);
    }

    /// <summary>Every outcome has words. A refusal with an empty sentence is a silent no-op with extra steps.</summary>
    [Test]
    public async Task EveryRefusalHasSomethingToSay()
    {
        foreach (var outcome in Enum.GetValues<PaneResizeOutcome>())
        {
            foreach (var direction in Enum.GetValues<PaneDirection>())
            {
                var text = PaneResize.Describe(outcome, direction);
                if (outcome == PaneResizeOutcome.Resized)
                {
                    await Assert.That(text).IsEmpty();
                    continue;
                }

                await Assert.That(text).IsNotEmpty().Because($"{outcome} must be explainable");
            }
        }
    }

    // --- the minimum -------------------------------------------------------------------------------

    /// <summary>
    /// <b>A pane cannot be squeezed below the floor.</b> Pressed until it refuses, the pane paying for the
    /// growth stops at <see cref="PaneResize.MinimumPaneColumns"/> and never goes under — and the last
    /// press before the floor moves a partial step rather than being refused, so the floor is reachable
    /// exactly.
    /// </summary>
    [Test]
    public async Task TheDonorStopsAtTheMinimumWidth()
    {
        var (layout, bounds) = SideBySide();

        PaneResizeResult result;
        var presses = 0;
        do
        {
            result = Resize(layout, bounds, PaneDirection.Right);
            presses++;
            await Assert.That(Solve(layout, bounds)["p2"].Width)
                .IsGreaterThanOrEqualTo(PaneResize.MinimumPaneColumns);
        }
        while (result.Changed && presses < 200);

        await Assert.That(result.Outcome).IsEqualTo(PaneResizeOutcome.AtMinimum);
        await Assert.That(Solve(layout, bounds)["p2"].Width).IsEqualTo(PaneResize.MinimumPaneColumns);
    }

    /// <summary>And the focused pane itself stops at the floor when it is the one shrinking.</summary>
    [Test]
    public async Task TheFocusedPaneStopsAtTheMinimumHeight()
    {
        var (layout, bounds) = Stacked();

        PaneResizeResult result;
        var presses = 0;
        do
        {
            result = Resize(layout, bounds, PaneDirection.Down);
            presses++;
            await Assert.That(Solve(layout, bounds)["p1"].Height)
                .IsGreaterThanOrEqualTo(PaneResize.MinimumPaneRows);
        }
        while (result.Changed && presses < 200);

        await Assert.That(result.Outcome).IsEqualTo(PaneResizeOutcome.AtMinimum);
        await Assert.That(Solve(layout, bounds)["p1"].Height).IsEqualTo(PaneResize.MinimumPaneRows);
    }

    /// <summary>
    /// The floor reaches <em>inside</em> a subtree. Squeezing a side-by-side pane whose neighbour is
    /// itself two panes side by side must leave both of those above the minimum, not just the subtree as
    /// a whole — which a single flat floor on the outermost child would allow.
    /// </summary>
    [Test]
    public async Task TheFloorProtectsPanesNestedInsideTheSubtreeThatPays()
    {
        var inner = new SplitNode(
            SplitDirection.Row,
            new LayoutNode[] { new PaneNode("p2", new[] { "w2" }), new PaneNode("p3", new[] { "w3" }) });
        var layout = new WorkspaceLayout(
            new SplitNode(
                SplitDirection.Row,
                new LayoutNode[] { new PaneNode("p1", new[] { "w1" }), inner }),
            "p1");
        var bounds = new PaneRect(0, 0, 120, 40);

        PaneResizeResult result;
        var presses = 0;
        do
        {
            result = Resize(layout, bounds, PaneDirection.Right);
            presses++;
            var rects = Solve(layout, bounds);
            await Assert.That(rects["p2"].Width).IsGreaterThanOrEqualTo(PaneResize.MinimumPaneColumns);
            await Assert.That(rects["p3"].Width).IsGreaterThanOrEqualTo(PaneResize.MinimumPaneColumns);
        }
        while (result.Changed && presses < 200);

        await Assert.That(result.Outcome).IsEqualTo(PaneResizeOutcome.AtMinimum);

        // The subtree stopped at two minimum panes plus the divider between them — the recursive floor,
        // not the flat one, which would have let it fall to a single pane's worth.
        var final = Solve(layout, bounds);
        await Assert.That(final["p2"].Width + final["p3"].Width + Divider)
            .IsEqualTo((PaneResize.MinimumPaneColumns * 2) + Divider);
    }

    /// <summary>
    /// <see cref="PaneResize.Minimum"/> as a rule: along the axis a split runs in, floors add up (plus the
    /// dividers); across it, the largest wins.
    /// </summary>
    [Test]
    public async Task TheSubtreeFloorAddsAlongTheAxisAndTakesTheLargestAcrossIt()
    {
        var row = new SplitNode(
            SplitDirection.Row,
            new LayoutNode[] { new PaneNode("a"), new PaneNode("b"), new PaneNode("c") });

        await Assert.That(PaneResize.Minimum(row, horizontal: true))
            .IsEqualTo((PaneResize.MinimumPaneColumns * 3) + (Divider * 2));
        await Assert.That(PaneResize.Minimum(row, horizontal: false))
            .IsEqualTo(PaneResize.MinimumPaneRows);
        await Assert.That(PaneResize.Minimum(new PaneNode("a"), horizontal: true))
            .IsEqualTo(PaneResize.MinimumPaneColumns);
    }

    /// <summary>
    /// A workspace already too small for its own split refuses outright rather than making it worse. The
    /// bounds here cannot hold two minimum panes at all, so both sides are under the floor and every
    /// direction is a refusal — including the one that would have "helped" one pane at the other's cost.
    /// </summary>
    [Test]
    public async Task AWorkspaceAlreadyUnderTheFloorRefusesBothWays()
    {
        var (layout, bounds) = SideBySide(width: 16);

        await Assert.That(Resize(layout, bounds, PaneDirection.Right).Outcome)
            .IsEqualTo(PaneResizeOutcome.AtMinimum);
        await Assert.That(Resize(layout, bounds, PaneDirection.Left).Outcome)
            .IsEqualTo(PaneResizeOutcome.AtMinimum);
    }

    // --- what a resize must not touch ---------------------------------------------------------------

    /// <summary>
    /// <b>Only the affected panes move.</b> A resize inside one branch of the tree leaves every pane in
    /// the other branch at exactly the rectangle it had. Per-pane NAWS is derived from these rectangles,
    /// so a resize that nudged the whole workspace would re-announce a terminal size to every connected
    /// server — including the ones nothing happened to.
    /// </summary>
    [Test]
    public async Task PanesOutsideTheResizedSplitKeepTheirRectanglesExactly()
    {
        // A column of two rows; the top row is two panes side by side, the bottom row is one pane.
        var topRow = new SplitNode(
            SplitDirection.Row,
            new LayoutNode[] { new PaneNode("p1", new[] { "w1" }), new PaneNode("p2", new[] { "w2" }) });
        var layout = new WorkspaceLayout(
            new SplitNode(
                SplitDirection.Column,
                new LayoutNode[] { topRow, new PaneNode("p3", new[] { "w3" }) }),
            "p1");
        var bounds = new PaneRect(0, 0, 120, 40);
        var before = Solve(layout, bounds);

        await Assert.That(Resize(layout, bounds, PaneDirection.Right).Changed).IsTrue();
        var after = Solve(layout, bounds);

        await Assert.That(after["p3"]).IsEqualTo(before["p3"]); // untouched, to the cell
        await Assert.That(after["p1"].Height).IsEqualTo(before["p1"].Height);
        await Assert.That(after["p2"].Height).IsEqualTo(before["p2"].Height);
        await Assert.That(after["p1"].Width).IsNotEqualTo(before["p1"].Width);
    }

    // --- persistence --------------------------------------------------------------------------------

    /// <summary>
    /// <b>A resized layout survives capture and restore.</b> <see cref="WorkspaceState"/> already carried
    /// <c>Sizes</c> — but nothing had ever written an uneven one, so the round trip had only ever been
    /// exercised on halves, where a bug that dropped them would be invisible. A feature that resets on
    /// restart reads as broken.
    /// </summary>
    [Test]
    public async Task ResizedSizesSurviveCaptureAndRestore()
    {
        var (layout, bounds) = SideBySide(120);
        var workspace = new Workspace(
            new[]
            {
                new WorkspaceWindow("w1", "One"),
                new WorkspaceWindow("w2", "Two"),
            },
            layout);

        for (var press = 0; press < 4; press++)
        {
            await Assert.That(Resize(workspace.Layout, bounds, PaneDirection.Right).Changed).IsTrue();
        }

        var expected = Solve(workspace.Layout, bounds);
        await Assert.That(expected["p1"].Width).IsNotEqualTo(expected["p2"].Width); // genuinely uneven

        var restored = WorkspaceState.Capture(workspace).Restore();
        var got = Solve(restored.Layout, bounds);

        await Assert.That(got).IsEquivalentTo(expected);
        await Assert.That(((SplitNode)restored.Layout.Root).Sizes)
            .IsEquivalentTo(((SplitNode)workspace.Layout.Root).Sizes);
    }

    /// <summary>
    /// And it survives the JSON, not only the object graph — the state is persisted as a file, and a
    /// <c>Sizes</c> list that round-tripped in memory but was dropped by the serializer would read the
    /// same way to a user.
    /// </summary>
    [Test]
    public async Task ResizedSizesSurviveTheJsonRoundTrip()
    {
        var (layout, bounds) = SideBySide(120);
        var workspace = new Workspace(
            new[] { new WorkspaceWindow("w1", "One"), new WorkspaceWindow("w2", "Two") }, layout);
        Resize(workspace.Layout, bounds, PaneDirection.Right);
        var expected = Solve(workspace.Layout, bounds);

        var json = System.Text.Json.JsonSerializer.Serialize(WorkspaceState.Capture(workspace));
        var restored = System.Text.Json.JsonSerializer.Deserialize<WorkspaceState>(json)!.Restore();

        await Assert.That(Solve(restored.Layout, bounds)).IsEquivalentTo(expected);
    }

    // --- the step ------------------------------------------------------------------------------------

    /// <summary>
    /// The step is honoured as given, so the constant is a policy and not a hard-coded assumption: a
    /// single-cell resize really does move the border one column.
    /// </summary>
    [Test]
    public async Task TheStepIsWhateverTheCallerAsksFor()
    {
        var (layout, bounds) = SideBySide(120);
        var before = Solve(layout, bounds)["p1"].Width;

        var result = PaneResize.Apply(layout, PaneDirection.Right, Solve(layout, bounds), step: 1);

        await Assert.That(result.Cells).IsEqualTo(1);
        await Assert.That(Solve(layout, bounds)["p1"].Width).IsEqualTo(before + 1);
    }
}
