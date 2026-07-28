using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

public class PaneDragSurfaceTests
{
    // Two panes side by side with a one-cell divider between them, as LayoutSolver would place them:
    // p1 spans columns 10–39, the divider sits at 40, p2 spans 41–69. Both run rows 2–21.
    private static PaneDragSurface TwoPanes() => new(
        new Dictionary<string, PaneRect>
        {
            ["p1"] = new(10, 2, 30, 20),
            ["p2"] = new(41, 2, 29, 20),
        },
        new Dictionary<string, string>
        {
            ["p1"] = "main",
            ["p2"] = "chat",
        });

    [Test]
    public async Task PaneAt_FindsThePaneContainingTheCell()
    {
        var surface = TwoPanes();

        await Assert.That(surface.PaneAt(10, 2)).IsEqualTo("p1");   // top-left corner is inside
        await Assert.That(surface.PaneAt(39, 21)).IsEqualTo("p1");  // bottom-right corner is inside
        await Assert.That(surface.PaneAt(41, 2)).IsEqualTo("p2");
        await Assert.That(surface.PaneAt(69, 21)).IsEqualTo("p2");
    }

    [Test]
    public async Task PaneAt_ReturnsNullOutsideEveryPane()
    {
        var surface = TwoPanes();

        await Assert.That(surface.PaneAt(40, 10)).IsNull(); // the divider between the panes
        await Assert.That(surface.PaneAt(9, 10)).IsNull();  // the rail, left of the pane area
        await Assert.That(surface.PaneAt(30, 1)).IsNull();  // the header row above
        await Assert.That(surface.PaneAt(30, 22)).IsNull(); // the input band below
        await Assert.That(surface.PaneAt(70, 10)).IsNull(); // past the right edge
    }

    [Test]
    public async Task PaneAt_SkipsCollapsedPanes()
    {
        var surface = new PaneDragSurface(
            new Dictionary<string, PaneRect> { ["p1"] = new(5, 5, 0, 20) },
            new Dictionary<string, string> { ["p1"] = "main" });

        await Assert.That(surface.PaneAt(5, 10)).IsNull();
    }

    [Test]
    public async Task IsTabStrip_IsOnlyThePanesTopRow()
    {
        var surface = TwoPanes();

        await Assert.That(surface.IsTabStrip("p1", 2)).IsTrue();
        await Assert.That(surface.IsTabStrip("p1", 3)).IsFalse();
        await Assert.That(surface.IsTabStrip("p1", 1)).IsFalse();
        await Assert.That(surface.IsTabStrip("nope", 2)).IsFalse();
    }

    [Test]
    public async Task RectAndActiveWindow_RoundTripPerPane()
    {
        var surface = TwoPanes();

        await Assert.That(surface.RectOf("p2")).IsEqualTo(new PaneRect(41, 2, 29, 20));
        await Assert.That(surface.RectOf("nope")).IsNull();
        await Assert.That(surface.ActiveWindow("p2")).IsEqualTo("chat");
        await Assert.That(surface.ActiveWindow("nope")).IsNull();
    }

    [Test]
    public async Task ItCopiesTheRectsSoALaterRebuildCannotMoveThem()
    {
        var rects = new Dictionary<string, PaneRect> { ["p1"] = new(0, 0, 10, 10) };
        var surface = new PaneDragSurface(rects, new Dictionary<string, string> { ["p1"] = "main" });

        rects["p1"] = new PaneRect(500, 500, 10, 10);

        await Assert.That(surface.RectOf("p1")).IsEqualTo(new PaneRect(0, 0, 10, 10));
    }

    [Test]
    public async Task AnEmptySurfaceReportsItself()
    {
        var surface = new PaneDragSurface(
            new Dictionary<string, PaneRect>(),
            new Dictionary<string, string>());

        await Assert.That(surface.IsEmpty).IsTrue();
        await Assert.That(TwoPanes().IsEmpty).IsFalse();
    }
}
