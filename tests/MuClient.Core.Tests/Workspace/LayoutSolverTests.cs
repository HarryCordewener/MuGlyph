using MuClient.Core.Workspaces;

namespace MuClient.Core.Tests.Workspaces;

public class LayoutSolverTests
{
    private static readonly PaneRect Screen = new(0, 0, 100, 40);

    [Test]
    public async Task SinglePane_FillsBounds()
    {
        var w = new WorkspaceLayout(new[] { "a" });

        var rects = LayoutSolver.Solve(w.Root, Screen);

        await Assert.That(rects).HasSingleItem();
        await Assert.That(rects[w.FocusedPaneId]).IsEqualTo(Screen);
    }

    [Test]
    public async Task RowSplit_SplitsWidth_ReservingADivider()
    {
        var w = new WorkspaceLayout(new[] { "a", "b" });
        w.SplitFocused(SplitDirection.Row);
        var ids = w.Panes.Select(p => p.Id).ToArray();

        var rects = LayoutSolver.Solve(w.Root, Screen);

        var left = rects[ids[0]];
        var right = rects[ids[1]];
        // 100 cells − 1 divider = 99 split 50/50 → 50 and 49 (cumulative rounding).
        await Assert.That(left).IsEqualTo(new PaneRect(0, 0, 50, 40));
        await Assert.That(right).IsEqualTo(new PaneRect(51, 0, 49, 40));
        // Full-height, and the gap between them is exactly the divider.
        await Assert.That(left.X + left.Width).IsEqualTo(right.X - LayoutSolver.DividerThickness);
    }

    [Test]
    public async Task ColumnSplit_SplitsHeight_ReservingADivider()
    {
        var w = new WorkspaceLayout(new[] { "a", "b" });
        w.SplitFocused(SplitDirection.Column);
        var ids = w.Panes.Select(p => p.Id).ToArray();

        var rects = LayoutSolver.Solve(w.Root, Screen);

        await Assert.That(rects[ids[0]]).IsEqualTo(new PaneRect(0, 0, 100, 20));
        await Assert.That(rects[ids[1]]).IsEqualTo(new PaneRect(0, 21, 100, 19));
    }

    [Test]
    public async Task NestedSplit_TilesWithoutOverlap_AndCoversTheArea()
    {
        var w = new WorkspaceLayout(new[] { "a", "b", "c", "d" });
        w.SplitFocused(SplitDirection.Row);    // [a] | [b,c,d]
        w.CycleFocus();
        w.SplitFocused(SplitDirection.Column); // [b] / [c,d]

        var rects = LayoutSolver.Solve(w.Root, Screen);

        await Assert.That(rects).Count().IsEqualTo(3);
        // No two pane rects overlap.
        var list = rects.Values.ToArray();
        for (var i = 0; i < list.Length; i++)
        {
            for (var j = i + 1; j < list.Length; j++)
            {
                await Assert.That(Overlaps(list[i], list[j])).IsFalse();
            }
        }
    }

    [Test]
    public async Task Zoom_FillsWholeArea_WithOnlyTheZoomedPane()
    {
        var w = new WorkspaceLayout(new[] { "a", "b" });
        w.SplitFocused(SplitDirection.Row);
        var zoomed = w.FocusedPaneId;

        var rects = LayoutSolver.Solve(w.Root, Screen, zoomed);

        await Assert.That(rects).HasSingleItem();
        await Assert.That(rects[zoomed]).IsEqualTo(Screen);
    }

    [Test]
    public async Task Distribute_SumsExactlyToTotal_EvenWithUnevenFractions()
    {
        var parts = LayoutSolver.Distribute(100, new[] { 0.3333, 0.3333, 0.3334 });

        await Assert.That(parts.Sum()).IsEqualTo(100);
        await Assert.That(parts.Length).IsEqualTo(3);
    }

    [Test]
    public async Task Distribute_ZeroOrNegativeTotal_YieldsZeros()
    {
        await Assert.That(LayoutSolver.Distribute(0, new[] { 0.5, 0.5 })).IsEquivalentTo(new[] { 0, 0 });
        await Assert.That(LayoutSolver.Distribute(-5, new[] { 0.5, 0.5 })).IsEquivalentTo(new[] { 0, 0 });
    }

    private static bool Overlaps(PaneRect a, PaneRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width &&
        a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
}
