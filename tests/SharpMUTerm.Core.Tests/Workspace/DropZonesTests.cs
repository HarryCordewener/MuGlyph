using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Tests.Workspaces;

public class DropZonesTests
{
    // A 100×100 pane at the origin keeps the fractions easy to reason about.
    private static Edge? At(int x, int y) => DropZones.Resolve(0, 0, 100, 100, x, y);

    [Test]
    public async Task Centre_AddsAsATab()
    {
        await Assert.That(At(50, 50)).IsNull();
        await Assert.That(At(40, 60)).IsNull(); // still outside every 25% margin
    }

    [Test]
    public async Task NearLeftEdge_SplitsLeft()
    {
        await Assert.That(At(5, 50)).IsEqualTo(Edge.Left);
    }

    [Test]
    public async Task NearRightEdge_SplitsRight()
    {
        await Assert.That(At(95, 50)).IsEqualTo(Edge.Right);
    }

    [Test]
    public async Task NearTopEdge_SplitsTop()
    {
        await Assert.That(At(50, 5)).IsEqualTo(Edge.Top);
    }

    [Test]
    public async Task NearBottomEdge_SplitsBottom()
    {
        await Assert.That(At(50, 95)).IsEqualTo(Edge.Bottom);
    }

    [Test]
    public async Task Corner_PicksTheNearestEdge()
    {
        // Point (2, 10): left margin 0.02 < top margin 0.10 → left wins.
        await Assert.That(At(2, 10)).IsEqualTo(Edge.Left);
        // Point (10, 2): top margin 0.02 < left margin 0.10 → top wins.
        await Assert.That(At(10, 2)).IsEqualTo(Edge.Top);
    }

    [Test]
    public async Task PointOutsideRect_IsClampedToTheNearestEdge()
    {
        await Assert.That(DropZones.Resolve(0, 0, 100, 100, -20, 50)).IsEqualTo(Edge.Left);
        await Assert.That(DropZones.Resolve(0, 0, 100, 100, 120, 50)).IsEqualTo(Edge.Right);
    }

    [Test]
    public async Task DegeneratePane_IsATabDrop()
    {
        await Assert.That(DropZones.Resolve(0, 0, 0, 0, 0, 0)).IsNull();
    }

    [Test]
    public async Task RespectsANonOriginPaneRectangle()
    {
        // Pane at (200,100) sized 80×40; a point 4 cols in from its left edge splits left.
        await Assert.That(DropZones.Resolve(200, 100, 80, 40, 204, 120)).IsEqualTo(Edge.Left);
        // Dead centre of that pane is a tab drop.
        await Assert.That(DropZones.Resolve(200, 100, 80, 40, 240, 120)).IsNull();
    }
}
