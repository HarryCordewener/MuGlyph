using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Tests.Workspaces;

public class PaneDropTests
{
    // Two panes side by side: p1 holds "a" and "b", p2 holds "c".
    private static WorkspaceLayout Split()
    {
        var layout = new WorkspaceLayout(new[] { "a", "b", "c" });
        layout.SplitWithWindow("c", layout.FocusedPaneId, Edge.Right);
        return layout;
    }

    [Test]
    public async Task NullEdge_AddsTheWindowToTheTargetPaneAsATab()
    {
        var layout = Split();
        var target = layout.FindWindow("c")!.Id;

        await Assert.That(PaneDrop.Apply(layout, "b", target, edge: null)).IsTrue();

        var pane = layout.FindWindow("b")!;
        await Assert.That(pane.Id).IsEqualTo(target);
        await Assert.That(pane.Tabs).IsEquivalentTo(new[] { "c", "b" });
        await Assert.That(pane.ActiveTab).IsEqualTo("b"); // a dropped tab becomes the visible one
    }

    [Test]
    public async Task AnEdge_SplitsTheTargetPaneAndPutsTheWindowInTheNewOne()
    {
        var layout = Split();
        var target = layout.FindWindow("c")!.Id;

        await Assert.That(PaneDrop.Apply(layout, "b", target, Edge.Bottom)).IsTrue();

        await Assert.That(layout.Panes.Count).IsEqualTo(3);
        var pane = layout.FindWindow("b")!;
        await Assert.That(pane.Id).IsNotEqualTo(target);
        await Assert.That(pane.Tabs).IsEquivalentTo(new[] { "b" });
        await Assert.That(layout.FocusedPaneId).IsEqualTo(pane.Id); // the new pane takes focus
    }

    [Test]
    [Arguments(Edge.Left)]
    [Arguments(Edge.Right)]
    [Arguments(Edge.Top)]
    [Arguments(Edge.Bottom)]
    public async Task EveryEdgeIsAccepted(Edge edge)
    {
        var layout = Split();
        var target = layout.FindWindow("c")!.Id;

        await Assert.That(PaneDrop.Apply(layout, "b", target, edge)).IsTrue();
        await Assert.That(layout.Panes.Count).IsEqualTo(3);
    }

    [Test]
    public async Task DroppingAWindowOnItsOwnPaneAsATab_ChangesNothing()
    {
        var layout = Split();
        var home = layout.FindWindow("b")!;

        await Assert.That(PaneDrop.Apply(layout, "b", home.Id, edge: null)).IsFalse();
        await Assert.That(layout.Panes.Count).IsEqualTo(2);
        await Assert.That(home.Tabs).IsEquivalentTo(new[] { "a", "b" }); // no reordering either
    }

    [Test]
    public async Task SplittingAPaneWithItsOnlyWindow_ChangesNothing()
    {
        var layout = Split();
        var lonely = layout.FindWindow("c")!;

        // Detaching "c" would empty its pane, and pruning would collapse the split straight back —
        // a no-op that nevertheless churns the pane id and focus. It must not be attempted.
        await Assert.That(PaneDrop.Apply(layout, "c", lonely.Id, Edge.Left)).IsFalse();
        await Assert.That(layout.Panes.Count).IsEqualTo(2);
        await Assert.That(layout.FindWindow("c")!.Id).IsEqualTo(lonely.Id);
    }

    [Test]
    public async Task SplittingAPaneWithOneOfItsSeveralWindows_IsAllowed()
    {
        var layout = Split();
        var home = layout.FindWindow("b")!;

        // "b" shares its pane with "a", so pulling it out into a new pane is a real change.
        await Assert.That(PaneDrop.Apply(layout, "b", home.Id, Edge.Top)).IsTrue();
        await Assert.That(layout.Panes.Count).IsEqualTo(3);
    }

    [Test]
    public async Task AMissingPaneOrWindow_IsRejected()
    {
        var layout = Split();

        await Assert.That(PaneDrop.Apply(layout, "b", "nope", edge: null)).IsFalse();
        await Assert.That(PaneDrop.Apply(layout, "nope", layout.FocusedPaneId, edge: null)).IsFalse();
        await Assert.That(layout.Panes.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ResolvingOverload_PicksTheEdgeFromTheDropPoint()
    {
        var layout = Split();
        var target = layout.FindWindow("c")!.Id;
        var rect = new PaneRect(200, 100, 80, 40);

        // Four cells in from the left of the rectangle is inside the 25% margin → split left.
        await Assert.That(PaneDrop.Apply(layout, "b", target, rect, 204, 120)).IsTrue();
        await Assert.That(layout.Panes.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ResolvingOverload_TreatsTheCentreAsATabDrop()
    {
        var layout = Split();
        var target = layout.FindWindow("c")!.Id;
        var rect = new PaneRect(200, 100, 80, 40);

        await Assert.That(PaneDrop.Apply(layout, "b", target, rect, 240, 120)).IsTrue();
        await Assert.That(layout.Panes.Count).IsEqualTo(2); // no new pane — it landed as a tab
        await Assert.That(layout.FindWindow("b")!.Id).IsEqualTo(target);
    }
}
