using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Tests.Workspaces;

/// <summary>
/// <b>A pane's number is when it was made, not where it sits.</b> The request was that pane numbering be
/// global and in order of creation, so ⌥1–⌥9 double as a way to reach another character; global it always
/// was (a workspace has one split tree whoever is connected in it), and this is the half that had to be
/// built.
/// <para>
/// <b>What was wrong.</b> <c>WorkspaceLayout.Panes</c> was <c>Root.Panes()</c> — tree order, left-to-right
/// then top-to-bottom, which is a function of where a pane <em>is</em>. So creating a pane renumbered every
/// pane after the insertion point: drop a window on the left edge of pane 2 and the pane that had been
/// pane 2 became pane 3, while the user was doing something else entirely and had no reason to look. ⌥2
/// then went somewhere new without any surface having said so, which is the same defect as a label and a
/// chord disagreeing — the thing this repository has already paid for twice.
/// </para>
/// <para>
/// <b>Number versus sequence.</b> <see cref="PaneNode.Sequence"/> is a sort key that is never reused; the
/// number is the pane's <em>position</em> in the sorted list. That distinction is the whole of the
/// compaction rule: reading sequences directly would leave holes after a close (1 and 3, with ⌥2 doing
/// nothing while two panes sat on the screen), and reading positions closes them up.
/// </para>
/// </summary>
public class PaneNumberingTests
{
    // --- creation order, and what it is not ---------------------------------------------------------

    /// <summary>
    /// <b>The defect, pinned.</b> Two panes; a third is created to the <em>left</em> of the second. Under
    /// tree order the second pane's number went 2 → 3. It must not move: it is the same pane, in the same
    /// place, and nobody asked for it to be renamed.
    /// </summary>
    [Test]
    public async Task CreatingAPaneToTheLeftOfAnotherDoesNotRenumberIt()
    {
        var layout = new WorkspaceLayout(new[] { "a", "b" });
        layout.SplitFocused(SplitDirection.Row);
        var second = layout.Panes[1].Id;

        layout.SplitWithWindow("c", second, Edge.Left);

        await Assert.That(Number(layout, second))
            .IsEqualTo(2)
            .Because("a pane keeps its number for as long as it is open");
        await Assert.That(layout.Panes.Count).IsEqualTo(3);
    }

    /// <summary>And the new pane takes the number after the last one, wherever on the screen it landed.</summary>
    [Test]
    public async Task ANewPaneIsNumberedLastEvenWhenItIsDrawnFirst()
    {
        var layout = new WorkspaceLayout(new[] { "a", "b" });
        layout.SplitFocused(SplitDirection.Row);
        var first = layout.Panes[0].Id;

        layout.SplitWithWindow("c", first, Edge.Left);
        var newest = layout.FocusedPaneId; // SplitWithWindow focuses the pane it made

        // Drawn leftmost — tree order puts it first — and numbered third.
        await Assert.That(layout.Root.Panes().First().Id).IsEqualTo(newest);
        await Assert.That(Number(layout, newest)).IsEqualTo(3);
        await Assert.That(Number(layout, first)).IsEqualTo(1);
    }

    /// <summary>
    /// Tree order still exists and still means geometry — this is the assertion that the two orders are
    /// genuinely different, so the tests above are not passing by coincidence on a layout where they agree.
    /// </summary>
    [Test]
    public async Task TreeOrderAndTheNumberingAreAllowedToDisagree()
    {
        var layout = new WorkspaceLayout(new[] { "a", "b" });
        layout.SplitFocused(SplitDirection.Row);
        layout.SplitWithWindow("c", layout.Panes[0].Id, Edge.Left);

        var numbered = Order(layout.Panes);
        var drawn = Order(layout.Root.Panes());

        await Assert.That(numbered).IsNotEqualTo(drawn);
        await Assert.That(numbered.Split(',').Order()).IsEquivalentTo(drawn.Split(',').Order()); // same panes
    }

    // --- compaction ---------------------------------------------------------------------------------

    /// <summary>
    /// <b>Closing pane 2 of three leaves panes 1 and 2, not 1 and 3.</b> The numbering has to stay
    /// contiguous or ⌥2 is a chord that does nothing while two panes are on the screen — a silent no-op,
    /// which is this codebase's most-repeated defect. Sequences are never reused, so it is the position in
    /// the list and not the sequence that is the number.
    /// </summary>
    [Test]
    public async Task ClosingAPaneInTheMiddleCompactsTheNumbering()
    {
        var layout = ThreePanes();
        var (first, second, third) = (layout.Panes[0].Id, layout.Panes[1].Id, layout.Panes[2].Id);

        layout.Focus(second);
        layout.CloseFocused();

        await Assert.That(layout.Panes.Count).IsEqualTo(2);
        await Assert.That(Number(layout, first)).IsEqualTo(1);
        await Assert.That(Number(layout, third))
            .IsEqualTo(2)
            .Because("⌥2 must reach a pane, not a hole where one used to be");
    }

    /// <summary>Closing the first one compacts too: what was 2 and 3 becomes 1 and 2, in the same order.</summary>
    [Test]
    public async Task ClosingTheFirstPaneShiftsTheRestDownRatherThanLeavingAGap()
    {
        var layout = ThreePanes();
        var (first, second, third) = (layout.Panes[0].Id, layout.Panes[1].Id, layout.Panes[2].Id);

        layout.Focus(first);
        layout.CloseFocused();

        await Assert.That(Order(layout.Panes)).IsEqualTo($"{second},{third}");
        await Assert.That(Number(layout, second)).IsEqualTo(1);
        await Assert.That(Number(layout, third)).IsEqualTo(2);
    }

    /// <summary>
    /// And a pane created after a close takes the free number at the end rather than reusing the closed
    /// pane's — the surviving panes' numbers are what must not move, and reusing would mean the new pane
    /// appeared in the middle of an ordering that is supposed to be chronological.
    /// </summary>
    [Test]
    public async Task APaneMadeAfterACloseGoesOnTheEnd()
    {
        var layout = ThreePanes();
        var (first, third) = (layout.Panes[0].Id, layout.Panes[2].Id);

        layout.Focus(layout.Panes[1].Id);
        layout.CloseFocused();
        layout.SplitWithWindow("d", first, Edge.Left);
        var newest = layout.FocusedPaneId;

        await Assert.That(Number(layout, first)).IsEqualTo(1);
        await Assert.That(Number(layout, third)).IsEqualTo(2);
        await Assert.That(Number(layout, newest)).IsEqualTo(3);
    }

    // --- the other ordinal mover --------------------------------------------------------------------

    /// <summary>
    /// <b>⌃O counts the way ⌥N counts.</b> These are the two ordinal movers, and a user pressing them
    /// alternately must not be counting two sequences: three presses of cycle from pane 1 land where the
    /// digit 4 would. Cycle read tree order before, which agreed with the numbering then and would not now.
    /// </summary>
    [Test]
    public async Task CyclingWalksTheNumberingInOrderAndWraps()
    {
        var layout = ThreePanes();
        layout.SplitWithWindow("d", layout.Panes[0].Id, Edge.Left); // a fourth, drawn first, numbered last
        var order = layout.Panes.Select(p => p.Id).ToList();

        layout.Focus(order[0]);
        foreach (var expected in order.Skip(1).Append(order[0]))
        {
            layout.CycleFocus();
            await Assert.That(layout.FocusedPaneId).IsEqualTo(expected);
        }
    }

    /// <summary>Closing the focused pane falls back to pane 1, which is what the surviving labels say.</summary>
    [Test]
    public async Task ClosingTheFocusedPaneFallsBackToPaneOne()
    {
        var layout = ThreePanes();
        layout.SplitWithWindow("d", layout.Panes[0].Id, Edge.Left);
        var firstNumbered = layout.Panes[0].Id;

        layout.Focus(layout.Panes[3].Id);
        layout.CloseFocused();

        await Assert.That(layout.FocusedPaneId).IsEqualTo(firstNumbered);
        await Assert.That(Number(layout, layout.FocusedPaneId)).IsEqualTo(1);
    }

    // --- persistence --------------------------------------------------------------------------------

    /// <summary>
    /// <b>A resumed workspace comes back numbered the way it was left.</b> The numbering is only worth
    /// learning if it survives a restart, so the sequence is persisted rather than re-derived — re-deriving
    /// it from the saved tree would hand back exactly the tree-order numbering this change removes.
    /// </summary>
    [Test]
    public async Task TheNumberingSurvivesACaptureAndRestore()
    {
        var layout = new WorkspaceLayout(new[] { "a", "b" });
        layout.SplitFocused(SplitDirection.Row);
        layout.SplitWithWindow("c", layout.Panes[0].Id, Edge.Left);
        var before = Order(layout.Panes);

        var restored = WorkspaceState.Capture(new Workspace(
            new[]
            {
                new WorkspaceWindow("a", "a"), new WorkspaceWindow("b", "b"), new WorkspaceWindow("c", "c"),
            },
            layout)).Restore();

        await Assert.That(Order(restored.Layout.Panes)).IsEqualTo(before);
        await Assert.That(Order(restored.Layout.Panes))
            .IsNotEqualTo(Order(restored.Layout.Root.Panes()))
            .Because("the fixture must still be one where tree order would give a different answer");
    }

    /// <summary>
    /// <b>A configuration written before panes carried a sequence does not come back scrambled.</b> Such a
    /// tree deserialises with every sequence at <see cref="PaneNode.Unsequenced"/>, and every pane sorting
    /// equal would leave the numbering to whatever the sort did. Tree order is the numbering that
    /// configuration was saved under, so that is what it is seeded with: the workspace reads exactly as it
    /// read when it was closed, and is stable from then on.
    /// </summary>
    [Test]
    public async Task ALayoutRestoredWithoutSequencesIsNumberedFromTreeOrder()
    {
        var state = new WorkspaceState
        {
            Windows =
            {
                new WorkspaceWindowState { Id = "a", Title = "a" },
                new WorkspaceWindowState { Id = "b", Title = "b" },
                new WorkspaceWindowState { Id = "c", Title = "c" },
            },
            Root = new LayoutNodeState
            {
                Type = "split",
                Direction = SplitDirection.Row,
                Children =
                {
                    // Ids deliberately out of order, so an implementation reading the number off the
                    // `pN` id would disagree with one reading tree order.
                    new LayoutNodeState { Type = "pane", Id = "p7", Tabs = { "a" } },
                    new LayoutNodeState { Type = "pane", Id = "p2", Tabs = { "b" } },
                    new LayoutNodeState { Type = "pane", Id = "p5", Tabs = { "c" } },
                },
            },
            FocusedPaneId = "p2",
        };

        var layout = state.Restore().Layout;

        await Assert.That(Order(layout.Panes)).IsEqualTo("p7,p2,p5");
        await Assert.That(string.Join(",", layout.Panes.Select(p => p.Sequence))).IsEqualTo("1,2,3");
    }

    /// <summary>
    /// And a pane created afterwards is numbered after them rather than colliding — the counter picks up
    /// from the highest sequence in the tree, not from the ids, which a legacy tree gives no guarantees
    /// about.
    /// </summary>
    [Test]
    public async Task APaneAddedToARestoredLegacyLayoutTakesTheNextNumber()
    {
        var state = new WorkspaceState
        {
            Windows = { new WorkspaceWindowState { Id = "a", Title = "a" } },
            Root = new LayoutNodeState { Type = "pane", Id = "legacy-pane", Tabs = { "a" } },
            FocusedPaneId = "legacy-pane",
        };

        var layout = state.Restore().Layout;
        layout.SplitWithWindow("b", "legacy-pane", Edge.Left);

        await Assert.That(layout.Panes.Select(p => p.Sequence).Distinct().Count())
            .IsEqualTo(2)
            .Because("two panes may never share a number");
        await Assert.That(Number(layout, "legacy-pane")).IsEqualTo(1);
    }

    /// <summary>
    /// A half-migrated tree — some panes numbered, some not — puts the unnumbered ones after the numbered
    /// ones rather than on top of them. There is no path that writes such a tree today; the guard is here
    /// because "assign 1..n from tree order" is the obvious implementation and it would silently give two
    /// panes the same number the first time one arrives.
    /// </summary>
    [Test]
    public async Task UnsequencedPanesAreNumberedAfterSequencedOnes()
    {
        var root = new SplitNode(
            SplitDirection.Row,
            new LayoutNode[]
            {
                new PaneNode("fresh", new[] { "a" }),                   // Unsequenced
                new PaneNode("known", new[] { "b" }, sequence: 4),
            });

        var layout = new WorkspaceLayout(root, "known");

        await Assert.That(Order(layout.Panes)).IsEqualTo("known,fresh");
        await Assert.That(layout.FindPane("fresh")!.Sequence).IsEqualTo(5);
    }

    // --- harness ------------------------------------------------------------------------------------

    /// <summary>
    /// Pane ids in the order given, as one string. Ordered comparison is the point of most of this
    /// suite, and TUnit's <c>IsEquivalentTo</c> compares collections as <em>sets</em> — it passed happily
    /// on <c>[p1,p2,p3]</c> against <c>[p3,p1,p2]</c>, which is exactly the difference being asserted.
    /// </summary>
    private static string Order(IEnumerable<PaneNode> panes) => string.Join(",", panes.Select(p => p.Id));

    /// <summary>The number a pane wears — its 1-based position in the numbering.</summary>
    private static int Number(WorkspaceLayout layout, string paneId)
    {
        var panes = layout.Panes;
        for (var i = 0; i < panes.Count; i++)
        {
            if (panes[i].Id == paneId)
            {
                return i + 1;
            }
        }

        return -1;
    }

    /// <summary>Three panes side by side, made in that order.</summary>
    private static WorkspaceLayout ThreePanes()
    {
        var layout = new WorkspaceLayout(new[] { "a", "b", "c" });
        layout.SplitFocused(SplitDirection.Row);
        layout.Focus(layout.Panes[1].Id);
        layout.SplitFocused(SplitDirection.Row);
        layout.Focus(layout.Panes[0].Id);
        return layout;
    }
}
