using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Tests.Workspaces;

public class WorkspaceLayoutTests
{
    [Test]
    public async Task New_HasSingleFocusedPane_NoZoom()
    {
        var w = new WorkspaceLayout(new[] { "main" });

        await Assert.That(w.Root).IsTypeOf<PaneNode>();
        await Assert.That(w.Panes).HasSingleItem();
        await Assert.That(w.FocusedPane.Tabs).IsEquivalentTo(new[] { "main" });
        await Assert.That(w.FocusedPane.ActiveTab).IsEqualTo("main");
        await Assert.That(w.ZoomedPaneId).IsNull();
    }

    [Test]
    public async Task AddWindow_AppendsAndActivates_MovingFromAnyExistingPane()
    {
        var w = new WorkspaceLayout(new[] { "a" });
        w.AddWindow("b");

        await Assert.That(w.FocusedPane.Tabs).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(w.FocusedPane.ActiveTab).IsEqualTo("b");

        // Re-adding an existing window relocates it rather than duplicating.
        w.AddWindow("a");
        await Assert.That(w.FocusedPane.Tabs).IsEquivalentTo(new[] { "b", "a" });
        await Assert.That(w.FindWindow("a")).IsEqualTo(w.FocusedPane);
    }

    [Test]
    public async Task SplitFocused_WithOneTab_IsNoOp()
    {
        var w = new WorkspaceLayout(new[] { "only" });

        var split = w.SplitFocused(SplitDirection.Row);

        await Assert.That(split).IsFalse();
        await Assert.That(w.Root).IsTypeOf<PaneNode>();
    }

    [Test]
    public async Task SplitFocused_KeepsActiveTab_MovesOthersToNewPane()
    {
        var w = new WorkspaceLayout(new[] { "a", "b", "c" });
        var originalId = w.FocusedPaneId;

        var ok = w.SplitFocused(SplitDirection.Row);

        await Assert.That(ok).IsTrue();
        await Assert.That(w.Root).IsTypeOf<SplitNode>();
        var split = (SplitNode)w.Root;
        await Assert.That(split.Direction).IsEqualTo(SplitDirection.Row);
        await Assert.That(split.Children).Count().IsEqualTo(2);
        await Assert.That(split.Sizes).IsEquivalentTo(new[] { 0.5, 0.5 });

        // Focus and the active tab stay in the original pane; the rest move out.
        await Assert.That(w.FocusedPaneId).IsEqualTo(originalId);
        await Assert.That(w.FocusedPane.Tabs).IsEquivalentTo(new[] { "a" });
        var other = w.Panes.First(p => p.Id != originalId);
        await Assert.That(other.Tabs).IsEquivalentTo(new[] { "b", "c" });
    }

    [Test]
    public async Task SplitFocused_MovesTheNonActiveTab_WhenActiveIsNotFirst()
    {
        var w = new WorkspaceLayout(new[] { "a", "b" });
        w.SetActiveTab(w.FocusedPaneId, "b");

        w.SplitFocused(SplitDirection.Column);

        await Assert.That(w.FocusedPane.Tabs).IsEquivalentTo(new[] { "b" });
        await Assert.That(w.Panes.First(p => p.Id != w.FocusedPaneId).Tabs).IsEquivalentTo(new[] { "a" });
    }

    [Test]
    public async Task CloseFocused_CollapsesSplitIntoRemainingChild()
    {
        var w = new WorkspaceLayout(new[] { "a", "b" });
        w.SplitFocused(SplitDirection.Row); // focused pane [a], sibling [b]
        var survivorId = w.Panes.First(p => p.Id != w.FocusedPaneId).Id;

        w.CloseFocused();

        await Assert.That(w.Root).IsTypeOf<PaneNode>();
        await Assert.That(w.Panes).HasSingleItem();
        await Assert.That(w.FocusedPaneId).IsEqualTo(survivorId);
        await Assert.That(w.FocusedPane.Tabs).IsEquivalentTo(new[] { "b" });
    }

    [Test]
    public async Task CloseFocused_OnLonePane_LeavesAFreshEmptyPane()
    {
        var w = new WorkspaceLayout(new[] { "a" });

        w.CloseFocused();

        await Assert.That(w.Panes).HasSingleItem();
        await Assert.That(w.FocusedPane.Tabs).IsEmpty();
        await Assert.That(w.FindPane(w.FocusedPaneId)).IsNotNull();
    }

    [Test]
    public async Task CycleFocus_WalksPanesAndWraps()
    {
        var w = new WorkspaceLayout(new[] { "a", "b" });
        w.SplitFocused(SplitDirection.Row);
        var first = w.FocusedPaneId;

        w.CycleFocus();
        var second = w.FocusedPaneId;
        await Assert.That(second).IsNotEqualTo(first);

        w.CycleFocus();
        await Assert.That(w.FocusedPaneId).IsEqualTo(first);
    }

    [Test]
    public async Task ToggleZoom_SetsAndClears_AndClosingZoomedPaneResetsIt()
    {
        var w = new WorkspaceLayout(new[] { "a", "b" });
        w.SplitFocused(SplitDirection.Row);

        w.ToggleZoom();
        await Assert.That(w.ZoomedPaneId).IsEqualTo(w.FocusedPaneId);

        w.ToggleZoom();
        await Assert.That(w.ZoomedPaneId).IsNull();

        w.ToggleZoom();
        w.CloseFocused();
        await Assert.That(w.ZoomedPaneId).IsNull();
    }

    /// <summary>
    /// A zoom carried onto whichever pane is now focused — what the ordinal movers (⌃O, ⌥N) do, so the
    /// selection never lands on a pane a zoom is hiding. It never starts a zoom and never ends one.
    /// </summary>
    [Test]
    public async Task CarryZoomToFocused_FollowsTheSelection_AndOnlyWhenSomethingIsZoomed()
    {
        var w = new WorkspaceLayout(new[] { "a", "b" });
        w.SplitFocused(SplitDirection.Row);
        var first = w.FocusedPaneId;

        // Nothing zoomed: nothing to carry, and it must not invent one.
        await Assert.That(w.CarryZoomToFocused()).IsFalse();
        await Assert.That(w.ZoomedPaneId).IsNull();

        w.ToggleZoom();
        w.CycleFocus();
        await Assert.That(w.FocusedPaneId).IsNotEqualTo(first);
        await Assert.That(w.ZoomedPaneId).IsEqualTo(first); // the state the carry exists to fix

        await Assert.That(w.CarryZoomToFocused()).IsTrue();
        await Assert.That(w.ZoomedPaneId).IsEqualTo(w.FocusedPaneId);

        // Already where it belongs: no change, and it says so.
        await Assert.That(w.CarryZoomToFocused()).IsFalse();
        await Assert.That(w.ZoomedPaneId).IsEqualTo(w.FocusedPaneId);
    }

    [Test]
    public async Task ReorderActiveTab_MovesWithinPane_AndClampsAtEdges()
    {
        var w = new WorkspaceLayout(new[] { "a", "b", "c" });

        await Assert.That(w.ReorderActiveTab(-1)).IsFalse(); // active "a" already leftmost

        w.SetActiveTab(w.FocusedPaneId, "b");
        await Assert.That(w.ReorderActiveTab(-1)).IsTrue();
        await Assert.That(w.FocusedPane.Tabs).IsEquivalentTo(new[] { "b", "a", "c" });
        await Assert.That(w.FocusedPane.ActiveTab).IsEqualTo("b");
    }

    [Test]
    public async Task MoveWindowToPane_MovesTab_AndPrunesEmptiedSource()
    {
        var w = new WorkspaceLayout(new[] { "a", "b" });
        w.SplitFocused(SplitDirection.Row); // [a] focused, [b] sibling
        var focusedId = w.FocusedPaneId;

        var moved = w.MoveWindowToPane("b", focusedId);

        await Assert.That(moved).IsTrue();
        await Assert.That(w.Root).IsTypeOf<PaneNode>(); // sibling emptied → collapsed
        await Assert.That(w.FindPane(focusedId)!.Tabs).IsEquivalentTo(new[] { "a", "b" });
    }

    [Test]
    public async Task SplitWithWindow_CreatesEdgePane_AndFocusesIt()
    {
        var w = new WorkspaceLayout(new[] { "a", "b", "c" });
        var targetId = w.FocusedPaneId;

        var ok = w.SplitWithWindow("c", targetId, Edge.Right);

        await Assert.That(ok).IsTrue();
        var split = (SplitNode)w.Root;
        await Assert.That(split.Direction).IsEqualTo(SplitDirection.Row);
        await Assert.That(((PaneNode)split.Children[0]).Id).IsEqualTo(targetId);
        var newPane = (PaneNode)split.Children[1];
        await Assert.That(newPane.Tabs).IsEquivalentTo(new[] { "c" });
        await Assert.That(w.FocusedPaneId).IsEqualTo(newPane.Id);
        await Assert.That(w.FindPane(targetId)!.Tabs).IsEquivalentTo(new[] { "a", "b" });
    }

    [Test]
    public async Task SplitWithWindow_LeftEdge_PlacesNewPaneFirst_AsColumnForTopBottom()
    {
        var w = new WorkspaceLayout(new[] { "a", "b" });
        var targetId = w.FocusedPaneId;

        w.SplitWithWindow("b", targetId, Edge.Top);

        var split = (SplitNode)w.Root;
        await Assert.That(split.Direction).IsEqualTo(SplitDirection.Column);
        await Assert.That(((PaneNode)split.Children[0]).Tabs).IsEquivalentTo(new[] { "b" }); // Top → first
        await Assert.That(((PaneNode)split.Children[1]).Id).IsEqualTo(targetId);
    }

    [Test]
    public async Task RemoveWindow_RemovesTab_AndFixesActiveIndex()
    {
        var w = new WorkspaceLayout(new[] { "a", "b", "c" });
        w.SetActiveTab(w.FocusedPaneId, "c");

        var removed = w.RemoveWindow("a");

        await Assert.That(removed).IsTrue();
        await Assert.That(w.FocusedPane.Tabs).IsEquivalentTo(new[] { "b", "c" });
        await Assert.That(w.FocusedPane.ActiveTab).IsEqualTo("c");
    }

    [Test]
    public async Task RemoveWindow_Absent_ReturnsFalse()
    {
        var w = new WorkspaceLayout(new[] { "a" });
        await Assert.That(w.RemoveWindow("nope")).IsFalse();
    }

    [Test]
    public async Task RemoveWindow_ActiveTabFromMiddle_KeepsFocusOnNextTab()
    {
        var w = new WorkspaceLayout(new[] { "a", "b", "c" });
        w.SetActiveTab(w.FocusedPaneId, "b"); // active is the middle tab

        w.RemoveWindow("b");

        await Assert.That(w.FocusedPane.Tabs).IsEquivalentTo(new[] { "a", "c" });
        await Assert.That(w.FocusedPane.ActiveTab).IsEqualTo("c"); // the tab that slid into b's slot
    }

    [Test]
    public async Task RemoveWindow_ActiveTabAtTail_FallsBackToPrevious()
    {
        var w = new WorkspaceLayout(new[] { "a", "b", "c" });
        w.SetActiveTab(w.FocusedPaneId, "c"); // active is the last tab

        w.RemoveWindow("c");

        await Assert.That(w.FocusedPane.Tabs).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(w.FocusedPane.ActiveTab).IsEqualTo("b"); // clamped back to the new tail
    }

    [Test]
    public async Task AddWindow_UnresolvedPaneId_ReturnsFalse_AndDoesNotAdd()
    {
        var w = new WorkspaceLayout(new[] { "a" });

        var ok = w.AddWindow("b", "no-such-pane");

        await Assert.That(ok).IsFalse();
        await Assert.That(w.FindWindow("b")).IsNull();
        await Assert.That(w.FocusedPane.Tabs).IsEquivalentTo(new[] { "a" });
    }

    [Test]
    public async Task ToggleFreezeFocused_TogglesPaneFlag()
    {
        var w = new WorkspaceLayout(new[] { "a" });

        w.ToggleFreezeFocused();
        await Assert.That(w.FocusedPane.Frozen).IsTrue();

        w.ToggleFreezeFocused();
        await Assert.That(w.FocusedPane.Frozen).IsFalse();
    }

    [Test]
    public async Task NestedSplits_PaneOrderIsStable()
    {
        var w = new WorkspaceLayout(new[] { "a", "b", "c", "d" });
        w.SplitFocused(SplitDirection.Row);   // [a] | [b,c,d]
        w.CycleFocus();                        // focus the [b,c,d] pane
        w.SplitFocused(SplitDirection.Column); // [b] / [c,d]

        await Assert.That(w.Panes).Count().IsEqualTo(3);
        var tabs = w.Panes.Select(p => string.Join(",", p.Tabs)).ToArray();
        await Assert.That(tabs).IsEquivalentTo(new[] { "a", "b", "c,d" });
    }
}
