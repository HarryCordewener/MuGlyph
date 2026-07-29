using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

public class PaneDragTrackerTests
{
    // p1 spans columns 10–39, p2 spans 41–69; both run rows 2–21, tab strips on row 2.
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

    private static List<MouseFlags> Press() => new() { MouseFlags.Button1Pressed };

    private static List<MouseFlags> Release() => new() { MouseFlags.Button1Released };

    // How a terminal in SGR mode reports motion with the primary button held.
    private static List<MouseFlags> Drag() => new()
    {
        MouseFlags.Button1Pressed,
        MouseFlags.Button1Dragged,
        MouseFlags.ReportMousePosition,
    };

    private static PaneDragTracker Started(out PaneDragSurface surface)
    {
        var tracker = new PaneDragTracker();
        var frozen = TwoPanes();
        surface = frozen;
        tracker.Handle(Press(), 15, 2, () => frozen);   // press p1's tab strip
        tracker.Handle(Drag(), 16, 3, () => frozen);    // move off the pressed cell
        return tracker;
    }

    // --- flag classification -------------------------------------------------

    [Test]
    public async Task Classify_ReadsAPlainPrimaryPressAsAPress()
    {
        await Assert.That(PaneDragTracker.Classify(Press())).IsEqualTo(PaneDragInput.Press);
    }

    [Test]
    public async Task Classify_ReadsSgrMotionWithTheButtonHeldAsADrag()
    {
        // SGR reports a drag as Button1Pressed + ReportMousePosition (+ Button1Dragged); the pressed
        // bit must not be mistaken for a fresh press or every drag frame would restart the gesture.
        await Assert.That(PaneDragTracker.Classify(Drag())).IsEqualTo(PaneDragInput.Drag);
        await Assert.That(PaneDragTracker.Classify(new List<MouseFlags>
        {
            MouseFlags.Button1Pressed,
            MouseFlags.ReportMousePosition,
        })).IsEqualTo(PaneDragInput.Drag);
        await Assert.That(PaneDragTracker.Classify(new List<MouseFlags> { MouseFlags.Button1Dragged }))
            .IsEqualTo(PaneDragInput.Drag);
    }

    [Test]
    public async Task Classify_ReadsAReleaseAsARelease_EvenAlongsideASynthesisedClick()
    {
        await Assert.That(PaneDragTracker.Classify(Release())).IsEqualTo(PaneDragInput.Release);
        await Assert.That(PaneDragTracker.Classify(new List<MouseFlags>
        {
            MouseFlags.Button1Released,
            MouseFlags.Button1Clicked,
        })).IsEqualTo(PaneDragInput.Release);
    }

    [Test]
    public async Task Classify_ReadsASecondaryPressAsAnAbort()
    {
        await Assert.That(PaneDragTracker.Classify(new List<MouseFlags> { MouseFlags.Button3Pressed }))
            .IsEqualTo(PaneDragInput.Abort);
    }

    [Test]
    public async Task Classify_IgnoresWheelPlainMotionAndEmptyFrames()
    {
        await Assert.That(PaneDragTracker.Classify(new List<MouseFlags> { MouseFlags.WheeledUp }))
            .IsEqualTo(PaneDragInput.Ignored);
        await Assert.That(PaneDragTracker.Classify(new List<MouseFlags> { MouseFlags.WheeledDown }))
            .IsEqualTo(PaneDragInput.Ignored);
        await Assert.That(PaneDragTracker.Classify(new List<MouseFlags> { MouseFlags.ReportMousePosition }))
            .IsEqualTo(PaneDragInput.Ignored);
        await Assert.That(PaneDragTracker.Classify(new List<MouseFlags>())).IsEqualTo(PaneDragInput.Ignored);
    }

    // --- arming --------------------------------------------------------------

    [Test]
    public async Task PressingATabStrip_ArmsButDoesNotYetDrag()
    {
        var tracker = new PaneDragTracker();
        var surface = TwoPanes();

        var result = tracker.Handle(Press(), 15, 2, () => surface);

        await Assert.That(result.Action).IsEqualTo(PaneDragAction.None);
        await Assert.That(tracker.IsDragging).IsFalse();
    }

    [Test]
    public async Task PressingAPanesBody_NeverStartsADrag()
    {
        var tracker = new PaneDragTracker();
        var surface = TwoPanes();

        tracker.Handle(Press(), 15, 8, () => surface); // row 8 is body, not the tab strip
        var result = tracker.Handle(Drag(), 50, 10, () => surface);

        await Assert.That(result.Action).IsEqualTo(PaneDragAction.None);
        await Assert.That(tracker.IsDragging).IsFalse();
    }

    [Test]
    public async Task PressingOutsideEveryPane_NeverStartsADrag()
    {
        var tracker = new PaneDragTracker();
        var surface = TwoPanes();

        tracker.Handle(Press(), 2, 2, () => surface); // over the rail
        var result = tracker.Handle(Drag(), 50, 10, () => surface);

        await Assert.That(result.Action).IsEqualTo(PaneDragAction.None);
        await Assert.That(tracker.IsDragging).IsFalse();
    }

    [Test]
    public async Task PressingAPaneWithNoWindow_NeverStartsADrag()
    {
        var tracker = new PaneDragTracker();
        var surface = new PaneDragSurface(
            new Dictionary<string, PaneRect> { ["p1"] = new(10, 2, 30, 20) },
            new Dictionary<string, string>()); // realised, but showing nothing

        tracker.Handle(Press(), 15, 2, () => surface);

        await Assert.That(tracker.Handle(Drag(), 20, 10, () => surface).Action).IsEqualTo(PaneDragAction.None);
    }

    [Test]
    public async Task TheGeometryIsSnapshotOnlyOnPress()
    {
        var calls = 0;
        var surface = TwoPanes();
        var tracker = new PaneDragTracker();

        PaneDragSurface Snapshot()
        {
            calls++;
            return surface;
        }

        tracker.Handle(Press(), 15, 2, Snapshot);
        tracker.Handle(Drag(), 50, 10, Snapshot);
        tracker.Handle(Drag(), 55, 12, Snapshot);
        tracker.Handle(Release(), 55, 12, Snapshot);

        await Assert.That(calls).IsEqualTo(1);
    }

    // --- dragging ------------------------------------------------------------

    [Test]
    public async Task StayingOnThePressedCell_IsStillAClick()
    {
        var tracker = new PaneDragTracker();
        var surface = TwoPanes();

        tracker.Handle(Press(), 15, 2, () => surface);
        var result = tracker.Handle(Drag(), 15, 2, () => surface);

        await Assert.That(result.Action).IsEqualTo(PaneDragAction.None);
        await Assert.That(tracker.IsDragging).IsFalse();
    }

    [Test]
    public async Task MovingOffThePressedCell_BeginsTheDrag()
    {
        var tracker = new PaneDragTracker();
        var surface = TwoPanes();

        tracker.Handle(Press(), 15, 2, () => surface);
        var result = tracker.Handle(Drag(), 16, 2, () => surface);

        await Assert.That(result.Action).IsEqualTo(PaneDragAction.Begin);
        await Assert.That(result.WindowId).IsEqualTo("main");
        await Assert.That(tracker.IsDragging).IsTrue();
        await Assert.That(tracker.SourcePaneId).IsEqualTo("p1");
    }

    [Test]
    public async Task HoveringAnotherPanesEdge_ReportsThatEdge()
    {
        var tracker = Started(out var surface);

        // p2 spans columns 41–69; two cells in from its left edge is inside the 25% margin.
        var result = tracker.Handle(Drag(), 43, 10, () => surface);

        await Assert.That(result.Action).IsEqualTo(PaneDragAction.Update);
        await Assert.That(result.TargetPaneId).IsEqualTo("p2");
        await Assert.That(result.Edge).IsEqualTo(Edge.Left);
    }

    [Test]
    public async Task HoveringAnotherPanesCentre_ReportsATabDrop()
    {
        var tracker = Started(out var surface);

        var result = tracker.Handle(Drag(), 55, 11, () => surface);

        await Assert.That(result.Action).IsEqualTo(PaneDragAction.Update);
        await Assert.That(result.TargetPaneId).IsEqualTo("p2");
        await Assert.That(result.Edge).IsNull();
    }

    [Test]
    public async Task MovingWithinTheSameZone_ReportsNothingNew()
    {
        var tracker = Started(out var surface);

        tracker.Handle(Drag(), 55, 11, () => surface);
        var result = tracker.Handle(Drag(), 56, 12, () => surface);

        // The preview only needs repainting when the target or the edge changes, not every cell.
        await Assert.That(result.Action).IsEqualTo(PaneDragAction.None);
        await Assert.That(tracker.TargetPaneId).IsEqualTo("p2");
    }

    [Test]
    public async Task LeavingEveryPane_ClearsTheTarget()
    {
        var tracker = Started(out var surface);

        tracker.Handle(Drag(), 55, 11, () => surface);
        var result = tracker.Handle(Drag(), 40, 11, () => surface); // onto the divider

        await Assert.That(result.Action).IsEqualTo(PaneDragAction.Update);
        await Assert.That(result.TargetPaneId).IsNull();
        await Assert.That(result.Edge).IsNull();
    }

    // --- finishing -----------------------------------------------------------

    [Test]
    public async Task ReleasingOverAPane_Commits()
    {
        var tracker = Started(out var surface);

        tracker.Handle(Drag(), 55, 11, () => surface);
        var result = tracker.Handle(Release(), 43, 10, () => surface);

        // The release position wins, not the last drag frame — the button may come up after a move.
        await Assert.That(result.Action).IsEqualTo(PaneDragAction.Commit);
        await Assert.That(result.WindowId).IsEqualTo("main");
        await Assert.That(result.TargetPaneId).IsEqualTo("p2");
        await Assert.That(result.Edge).IsEqualTo(Edge.Left);
        await Assert.That(tracker.IsDragging).IsFalse();
    }

    [Test]
    public async Task ReleasingOffEveryPane_Cancels()
    {
        var tracker = Started(out var surface);

        var result = tracker.Handle(Release(), 2, 30, () => surface);

        await Assert.That(result.Action).IsEqualTo(PaneDragAction.Cancel);
        await Assert.That(tracker.IsDragging).IsFalse();
    }

    [Test]
    public async Task ReleasingWithoutHavingMoved_ReportsNothing()
    {
        var tracker = new PaneDragTracker();
        var surface = TwoPanes();

        tracker.Handle(Press(), 15, 2, () => surface);
        var result = tracker.Handle(Release(), 15, 2, () => surface);

        // A plain click on a tab: the framework's own TabControl handling owns it.
        await Assert.That(result.Action).IsEqualTo(PaneDragAction.None);
    }

    [Test]
    public async Task ASecondaryPressMidDrag_Cancels()
    {
        var tracker = Started(out var surface);

        var result = tracker.Handle(new List<MouseFlags> { MouseFlags.Button3Pressed }, 50, 10, () => surface);

        await Assert.That(result.Action).IsEqualTo(PaneDragAction.Cancel);
        await Assert.That(tracker.IsDragging).IsFalse();
    }

    [Test]
    public async Task AFreshPressMidDrag_CancelsTheOldGesture()
    {
        var tracker = Started(out var surface);

        // A swallowed button-up would otherwise leave the preview stuck over the panes.
        var result = tracker.Handle(Press(), 50, 2, () => surface);

        await Assert.That(result.Action).IsEqualTo(PaneDragAction.Cancel);
        await Assert.That(tracker.IsDragging).IsFalse();
    }

    [Test]
    public async Task AFreshPressMidDrag_AlsoArmsTheNextGesture()
    {
        var tracker = Started(out var surface);

        tracker.Handle(Press(), 50, 2, () => surface); // over p2's tab strip
        var result = tracker.Handle(Drag(), 51, 3, () => surface);

        // Cancelling the stale drag must not cost the user the press they just made.
        await Assert.That(result.Action).IsEqualTo(PaneDragAction.Begin);
        await Assert.That(result.WindowId).IsEqualTo("chat");
        await Assert.That(tracker.SourcePaneId).IsEqualTo("p2");
    }

    /// <summary>
    /// The one that made drag-to-split look broken in a real terminal. SharpConsoleUI's Unix reader
    /// auto-repeats the held button: a bare <c>Button1Pressed</c> at the pointer's current cell every
    /// 100 ms until it comes up. Read as a fresh press it reset the gesture a tenth of a second into
    /// every drag — the preview flashed on and vanished, and the drop landed only if the release beat
    /// the next repeat.
    /// </summary>
    [Test]
    public async Task AnAutoRepeatedPressAtTheSameCell_DoesNotDisturbALiveDrag()
    {
        var tracker = Started(out var surface);
        tracker.Handle(Drag(), 43, 10, () => surface);

        var result = tracker.Handle(Press(), 43, 10, () => surface); // the 100 ms repeat

        await Assert.That(result.Action).IsEqualTo(PaneDragAction.None);
        await Assert.That(tracker.IsDragging).IsTrue();
        await Assert.That(tracker.TargetPaneId).IsEqualTo("p2");
        await Assert.That(tracker.TargetEdge).IsEqualTo(Edge.Left);
    }

    /// <summary>And the gesture still finishes on the button-up that follows the repeats.</summary>
    [Test]
    public async Task ADragPunctuatedByAutoRepeats_StillCommits()
    {
        var surface = TwoPanes();
        var tracker = new PaneDragTracker();
        var actions = new List<PaneDragAction>();

        void Feed(List<MouseFlags> flags, int x, int y) =>
            actions.Add(tracker.Handle(flags, x, y, () => surface).Action);

        Feed(Press(), 15, 2);
        Feed(Press(), 15, 2);   // repeat before the pointer has moved
        Feed(Drag(), 16, 3);
        Feed(Press(), 16, 3);   // repeat
        Feed(Drag(), 43, 10);
        Feed(Press(), 43, 10);  // repeat
        Feed(Press(), 43, 10);  // and another
        Feed(Release(), 43, 10);

        await Assert.That(actions.Count(a => a == PaneDragAction.Begin)).IsEqualTo(1);
        await Assert.That(actions.Count(a => a == PaneDragAction.Cancel)).IsEqualTo(0);
        await Assert.That(actions[^1]).IsEqualTo(PaneDragAction.Commit);
    }

    /// <summary>
    /// A repeat before any motion must not turn the press into a drag either — holding the button still
    /// on a tab is a click, and a click selects that tab.
    /// </summary>
    [Test]
    public async Task AnAutoRepeatBeforeAnyMotion_LeavesItAClick()
    {
        var tracker = new PaneDragTracker();
        var surface = TwoPanes();

        tracker.Handle(Press(), 15, 2, () => surface);
        var repeat = tracker.Handle(Press(), 15, 2, () => surface);
        var release = tracker.Handle(Release(), 15, 2, () => surface);

        await Assert.That(repeat.Action).IsEqualTo(PaneDragAction.None);
        await Assert.That(release.Action).IsEqualTo(PaneDragAction.None);
    }

    /// <summary>
    /// The repeat only ever carries the cell of the frame before it, so the geometry is still read back
    /// exactly once per gesture — a second snapshot mid-drag would read the preview's own controls.
    /// </summary>
    [Test]
    public async Task AutoRepeatsDoNotResnapshotTheGeometry()
    {
        var calls = 0;
        var surface = TwoPanes();
        var tracker = new PaneDragTracker();

        PaneDragSurface Snapshot()
        {
            calls++;
            return surface;
        }

        tracker.Handle(Press(), 15, 2, Snapshot);
        tracker.Handle(Drag(), 43, 10, Snapshot);
        tracker.Handle(Press(), 43, 10, Snapshot);
        tracker.Handle(Press(), 43, 10, Snapshot);
        tracker.Handle(Release(), 43, 10, Snapshot);

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task WheelAndIdleMotionDoNotDisturbALiveDrag()
    {
        var tracker = Started(out var surface);
        tracker.Handle(Drag(), 43, 10, () => surface);

        await Assert.That(tracker.Handle(new List<MouseFlags> { MouseFlags.WheeledUp }, 43, 10, () => surface).Action)
            .IsEqualTo(PaneDragAction.None);

        await Assert.That(tracker.IsDragging).IsTrue();
        await Assert.That(tracker.TargetPaneId).IsEqualTo("p2");
        await Assert.That(tracker.TargetEdge).IsEqualTo(Edge.Left);
    }

    [Test]
    public async Task Reset_AbandonsTheGestureSilently()
    {
        var tracker = Started(out _);

        tracker.Reset();

        await Assert.That(tracker.IsDragging).IsFalse();
        await Assert.That(tracker.WindowId).IsNull();
        await Assert.That(tracker.TargetPaneId).IsNull();
    }

    [Test]
    public async Task DraggingBackOntoItsOwnPanesEdge_IsReported()
    {
        var tracker = Started(out var surface);

        // Resolution is the tracker's job; whether it is a no-op is PaneDrop's (see PaneDropTests).
        var result = tracker.Handle(Drag(), 11, 10, () => surface);

        await Assert.That(result.TargetPaneId).IsEqualTo("p1");
        await Assert.That(result.Edge).IsEqualTo(Edge.Left);
    }

    [Test]
    public async Task AnEmptySurface_NeverArms()
    {
        var tracker = new PaneDragTracker();
        var surface = new PaneDragSurface(
            new Dictionary<string, PaneRect>(),
            new Dictionary<string, string>());

        tracker.Handle(Press(), 15, 2, () => surface);

        await Assert.That(tracker.Handle(Drag(), 20, 10, () => surface).Action).IsEqualTo(PaneDragAction.None);
    }

    [Test]
    public async Task AFullGestureAcrossPanes_ProducesExactlyOneBeginAndOneCommit()
    {
        var surface = TwoPanes();
        var tracker = new PaneDragTracker();
        var actions = new List<PaneDragAction>();

        void Feed(List<MouseFlags> flags, int x, int y) =>
            actions.Add(tracker.Handle(flags, x, y, () => surface).Action);

        Feed(Press(), 15, 2);
        Feed(Drag(), 16, 3);
        Feed(Drag(), 30, 8);
        Feed(Drag(), 45, 10);
        Feed(Drag(), 46, 11);
        Feed(Release(), 46, 11);

        await Assert.That(actions.Count(a => a == PaneDragAction.Begin)).IsEqualTo(1);
        await Assert.That(actions.Count(a => a == PaneDragAction.Commit)).IsEqualTo(1);
        await Assert.That(actions[^1]).IsEqualTo(PaneDragAction.Commit);
    }
}
