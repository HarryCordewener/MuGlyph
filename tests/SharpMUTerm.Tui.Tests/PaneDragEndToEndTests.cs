using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// Drives the whole pane-drag path headlessly: real mouse frames into the console driver, out the
/// other side as a changed split tree. Everything between — the tracker, the geometry read back from
/// the framework's arranged layout, <see cref="DropZones"/> and <see cref="PaneDrop"/> — runs for
/// real. The only link these tests cannot cover is the terminal's own mouse reporting, which is
/// SharpConsoleUI's <c>NetConsoleDriver</c> turning escape sequences into the very frames fed here.
/// </summary>
/// <remarks>
/// Serialised: <see cref="SharpMUTermApp.RenderSnapshot"/> redirects <c>Console.Out</c> to capture the
/// frame, and <see cref="Split"/> redirects <c>Console.In</c>. Both are process-global, so two of these
/// running at once swap each other's streams and one gets a truncated frame — which arranges no layout,
/// which yields pane rects that don't match the screen. That surfaced as roughly a one-in-six failure.
/// </remarks>
[NotInParallel]
public class PaneDragEndToEndTests
{
    private const int Width = 120;
    private const int Height = 32;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    private sealed record Harness(SharpMUTermApp App, HeadlessConsoleDriver Driver, PaneDragSurface Surface);

    /// <summary>A rendered two-pane workspace, with the pane geometry the app itself would drag against.</summary>
    private static Harness Split()
    {
        // The window system reads the console for input even headless; a null reader returns EOF.
        Console.SetIn(TextReader.Null);

        var driver = new HeadlessConsoleDriver(Width, Height);
        var app = new SharpMUTermApp(DemoScene.Build(), Headless, driver);

        // Rendering a frame is what arranges the layout, so control bounds only exist afterwards.
        app.RenderSnapshot("split");

        return new Harness(app, driver, app.PaneSnapshot());
    }

    private static void Press(HeadlessConsoleDriver driver, int x, int y) =>
        driver.SimulateMouseEvent(new List<MouseFlags> { MouseFlags.Button1Pressed }, new System.Drawing.Point(x, y));

    private static void Drag(HeadlessConsoleDriver driver, int x, int y) =>
        driver.SimulateMouseEvent(
            new List<MouseFlags>
            {
                MouseFlags.Button1Pressed,
                MouseFlags.Button1Dragged,
                MouseFlags.ReportMousePosition,
            },
            new System.Drawing.Point(x, y));

    private static void Release(HeadlessConsoleDriver driver, int x, int y) =>
        driver.SimulateMouseEvent(new List<MouseFlags> { MouseFlags.Button1Released }, new System.Drawing.Point(x, y));

    private static (string Id, PaneRect Rect) Pane(Harness harness, int index)
    {
        var id = PaneIds(harness)[index];
        return (id, harness.Surface.RectOf(id)!.Value);
    }

    /// <summary>The snapshot's pane ids in left-to-right screen order.</summary>
    private static List<string> PaneIds(Harness harness) =>
        harness.Surface.Rects.Keys.OrderBy(id => harness.Surface.RectOf(id)!.Value.X).ToList();

    private static int PaneCount(SharpMUTermApp app) => Flatten(app.CaptureSession().Root).Count;

    /// <summary>The layout's panes in tree order — which, for a split, is screen order.</summary>
    private static List<LayoutNodeState> Flatten(LayoutNodeState node) =>
        node.Children is { Count: > 0 } children
            ? children.SelectMany(Flatten).ToList()
            : new List<LayoutNodeState> { node };

    [Test]
    public async Task TheRenderedFrameGivesTwoNonOverlappingPanes()
    {
        var harness = Split();

        await Assert.That(harness.Surface.Rects.Count).IsEqualTo(2);

        var panes = PaneIds(harness).Select(id => harness.Surface.RectOf(id)!.Value).ToList();
        foreach (var rect in panes)
        {
            await Assert.That(rect.IsEmpty).IsFalse();
            await Assert.That(rect.X).IsGreaterThanOrEqualTo(0);
            await Assert.That(rect.X + rect.Width).IsLessThanOrEqualTo(Width);
            await Assert.That(rect.Y + rect.Height).IsLessThanOrEqualTo(Height);
        }

        // Left pane, one-cell divider, right pane — exactly what LayoutSolver models.
        await Assert.That(panes[0].X + panes[0].Width).IsLessThanOrEqualTo(panes[1].X);
    }

    [Test]
    public async Task ThePaneRectanglesAgreeWithTheFrameworksOwnHitTesting()
    {
        // This is the assumption the whole mouse path rests on: that our desktop-cell rectangles name
        // the same regions SharpConsoleUI would resolve a click in. Sample each pane and ask it.
        var harness = Split();

        foreach (var id in PaneIds(harness))
        {
            var rect = harness.Surface.RectOf(id)!.Value;
            foreach (var (x, y) in new[]
            {
                (rect.X, rect.Y),
                (rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2)),
                (rect.X + rect.Width - 1, rect.Y + rect.Height - 1),
            })
            {
                await Assert.That(harness.Surface.PaneAt(x, y)).IsEqualTo(id);
            }
        }
    }

    [Test]
    [Arguments(Edge.Left)]
    [Arguments(Edge.Right)]
    [Arguments(Edge.Top)]
    [Arguments(Edge.Bottom)]
    public async Task DroppingOnAnEdgeSplitsTheTargetPaneTowardThatEdge(Edge edge)
    {
        var harness = Split();
        var source = Pane(harness, 0);
        var target = Pane(harness, 1);
        var dragged = harness.Surface.ActiveWindow(source.Id)!;

        // A cell one in from the chosen edge, centred along the other axis — inside the 25% margin.
        var (x, y) = edge switch
        {
            Edge.Left => (target.Rect.X + 1, target.Rect.Y + (target.Rect.Height / 2)),
            Edge.Right => (target.Rect.X + target.Rect.Width - 2, target.Rect.Y + (target.Rect.Height / 2)),
            Edge.Top => (target.Rect.X + (target.Rect.Width / 2), target.Rect.Y + 1),
            _ => (target.Rect.X + (target.Rect.Width / 2), target.Rect.Y + target.Rect.Height - 2),
        };

        Press(harness.Driver, source.Rect.X + 2, source.Rect.Y);
        Drag(harness.Driver, source.Rect.X + 3, source.Rect.Y + 1);
        Drag(harness.Driver, x, y);
        Release(harness.Driver, x, y);

        var root = harness.App.CaptureSession().Root;
        var panes = Flatten(root);

        // The source pane held only this window, so handing it over empties and prunes it: the tree
        // is the target pane split against a new pane holding the dragged window.
        await Assert.That(root.Type).IsEqualTo("split");
        await Assert.That(root.Direction)
            .IsEqualTo(edge is Edge.Left or Edge.Right ? SplitDirection.Row : SplitDirection.Column);
        await Assert.That(panes.Count).IsEqualTo(2);

        var newPaneIndex = edge is Edge.Left or Edge.Top ? 0 : 1;
        await Assert.That(panes[newPaneIndex].Tabs).IsEquivalentTo(new[] { dragged });
    }

    [Test]
    public async Task DroppingATabOnItsOwnPanesEdge_PullsItOutIntoANewPane()
    {
        // The default workspace resumes with both windows sharing one pane, so the window has
        // somewhere to leave from — and the source pane survives, giving a genuine second pane.
        Console.SetIn(TextReader.Null);
        var driver = new HeadlessConsoleDriver(Width, Height);
        var app = new SharpMUTermApp(DemoScene.Build(), Headless, driver);
        app.RenderSnapshot();
        var surface = app.PaneSnapshot();

        var paneId = surface.Rects.Keys.Single();
        var rect = surface.RectOf(paneId)!.Value;
        var dragged = surface.ActiveWindow(paneId)!;

        Press(driver, rect.X + 2, rect.Y);
        Drag(driver, rect.X + 3, rect.Y + 1);
        Drag(driver, rect.X + 1, rect.Y + (rect.Height / 2));
        Release(driver, rect.X + 1, rect.Y + (rect.Height / 2));

        var root = app.CaptureSession().Root;
        var panes = Flatten(root);

        await Assert.That(panes.Count).IsEqualTo(2);
        await Assert.That(root.Direction).IsEqualTo(SplitDirection.Row);
        await Assert.That(panes[0].Tabs).IsEquivalentTo(new[] { dragged });
        await Assert.That(panes[1].Tabs).DoesNotContain(dragged);
    }

    [Test]
    public async Task DroppingInTheMiddleOfTheOtherPane_MovesTheTabInsteadOfSplitting()
    {
        var harness = Split();
        var source = Pane(harness, 0);
        var target = Pane(harness, 1);
        var centreX = target.Rect.X + (target.Rect.Width / 2);
        var centreY = target.Rect.Y + (target.Rect.Height / 2);

        Press(harness.Driver, source.Rect.X + 2, source.Rect.Y);
        Drag(harness.Driver, source.Rect.X + 3, source.Rect.Y + 1);
        Drag(harness.Driver, centreX, centreY);
        Release(harness.Driver, centreX, centreY);

        // The source pane held a single window, so handing it over empties and prunes it: one pane.
        await Assert.That(PaneCount(harness.App)).IsEqualTo(1);
    }

    [Test]
    public async Task AClickOnATabWithoutMoving_LeavesTheLayoutAlone()
    {
        var harness = Split();
        var source = Pane(harness, 0);

        Press(harness.Driver, source.Rect.X + 2, source.Rect.Y);
        Release(harness.Driver, source.Rect.X + 2, source.Rect.Y);

        await Assert.That(PaneCount(harness.App)).IsEqualTo(2);
    }

    [Test]
    public async Task ADragThatStartsInAPanesBody_LeavesTheLayoutAlone()
    {
        var harness = Split();
        var source = Pane(harness, 0);
        var target = Pane(harness, 1);

        // Body presses belong to the content (text selection, links), not to pane dragging.
        Press(harness.Driver, source.Rect.X + 2, source.Rect.Y + 5);
        Drag(harness.Driver, target.Rect.X + 1, target.Rect.Y + 5);
        Release(harness.Driver, target.Rect.X + 1, target.Rect.Y + 5);

        await Assert.That(PaneCount(harness.App)).IsEqualTo(2);
    }

    [Test]
    public async Task ReleasingOutsideEveryPane_LeavesTheLayoutAlone()
    {
        var harness = Split();
        var source = Pane(harness, 0);

        Press(harness.Driver, source.Rect.X + 2, source.Rect.Y);
        Drag(harness.Driver, source.Rect.X + 3, source.Rect.Y + 1);
        Release(harness.Driver, 0, Height - 1); // the status line

        await Assert.That(PaneCount(harness.App)).IsEqualTo(2);
    }
}
