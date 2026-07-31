using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What each connected world is told its terminal size is, over NAWS — and how often.
/// <para>
/// The answer used to be the whole client window, for the focused session only. In a client whose
/// main feature is splits that is wrong nearly whenever the feature is used: a world sharing the
/// screen with another gets perhaps half the columns, minus the rail and the divider, and its rows
/// are short by the header, the input area, the status line and its own tab strip — so a server told
/// 120x32 wrapped to a width that did not exist on screen. These drive the real thing headlessly: a
/// real window, a real frame, the pane rectangles the framework itself arranged, and a fake telnet
/// transport recording every NAWS write.
/// </para>
/// <para>
/// The report rides the frame, so a drag-resize would otherwise write to every world sixty times a
/// second; the rate limit is the second half of these tests. Its clock is a
/// <see cref="ManualTimeProvider"/>, so "what arrives, and when" is asserted rather than slept on.
/// </para>
/// </summary>
/// <remarks>
/// Serialised for the same reason <see cref="PaneDragEndToEndTests"/> is: rendering a frame redirects
/// the process-global <c>Console.Out</c>, and the harness redirects <c>Console.In</c>.
/// </remarks>
[NotInParallel]
public class NawsPaneReportTests
{
    private const int Width = 120;
    private const int Height = 32;

    /// <summary>The rate limit's window — how long a held-back size waits, at most.</summary>
    private static readonly TimeSpan Interval = SharpMUTermApp.WindowSizeReportInterval;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>A world session over a transport that records what it is told, connected or not.</summary>
    private sealed record Reporter(WorldSession Session, RecordingTelnetSession Telnet)
    {
        public (int Width, int Height) Last => Telnet.Sizes[^1];

        public int Count => Telnet.Sizes.Count;

        public bool WasTold((int Width, int Height) size) => Telnet.Sizes.Contains(size);
    }

    private static async Task<Reporter> ConnectedAsync(string name)
    {
        var telnet = new RecordingTelnetSession();
        var world = new WorldDefinition { Name = name, Host = "h", Port = 1 };
        var session = new WorldSession(world, sessionFactory: _ => telnet);
        await session.ConnectAsync();
        return new Reporter(session, telnet);
    }

    private static Reporter Idle(string name)
    {
        var telnet = new RecordingTelnetSession();
        var world = new WorldDefinition { Name = name, Host = "h", Port = 1 };
        return new Reporter(new WorldSession(world, sessionFactory: _ => telnet), telnet);
    }

    /// <summary>The client under test and the clock its NAWS rate limit runs on.</summary>
    private sealed record Client(SharpMUTermApp App, ManualTimeProvider Clock);

    private static Client App()
    {
        // The window system reads the console for input even headless; a null reader returns EOF.
        Console.SetIn(TextReader.Null);
        var clock = new ManualTimeProvider();
        return new Client(
            new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(Width, Height), clock),
            clock);
    }

    private static ConsoleKeyInfo Key(char c) => new(c, ConsoleKey.NoName, false, false, false);

    /// <summary>The pane showing <paramref name="windowId"/>, and its output rectangle.</summary>
    private static (int Width, int Height) OutputSizeOf(SharpMUTermApp app, string windowId)
    {
        var rect = OutputRectOf(app, windowId);
        return (rect.Width, rect.Height);
    }

    private static PaneRect OutputRectOf(SharpMUTermApp app, string windowId)
    {
        var surface = app.PaneSnapshot();
        var paneId = surface.Rects.Keys.Single(id => surface.ActiveWindow(id) == windowId);
        return app.PaneOutputRects()[paneId];
    }

    /// <summary>The demo workspace's spawn window — the second tab, and after a split the second pane.</summary>
    private static string ChatWindowId => DemoScene.ChatWindowId;

    // --- the bug ---------------------------------------------------------------

    /// <summary>
    /// The one that would have caught it. Two connected worlds, one split, and each is told its own
    /// pane — not the window, and not each other's. Under the old code both numbers were 120x32.
    /// </summary>
    [Test]
    public async Task EachConnectedSessionIsToldItsOwnPane_NotTheWholeWindow()
    {
        var (app, _) = App();
        app.RenderSnapshot("split"); // main in the left pane, the Chat window moved to the right

        var left = await ConnectedAsync("Left");
        var right = await ConnectedAsync("Right");
        app.AttachSession(left.Session, "main");
        app.AttachSession(right.Session, ChatWindowId);
        app.RenderNextFrame();

        var leftRect = OutputRectOf(app, "main");
        var rightRect = OutputRectOf(app, ChatWindowId);

        await Assert.That(left.Last).IsEqualTo((leftRect.Width, leftRect.Height));
        await Assert.That(right.Last).IsEqualTo((rightRect.Width, rightRect.Height));

        // Neither is the window, on either axis — which is what was reported before.
        await Assert.That(left.Last.Width).IsLessThan(Width);
        await Assert.That(left.Last.Height).IsLessThan(Height);
        await Assert.That(right.Last.Width).IsLessThan(Width);
        await Assert.That(right.Last.Height).IsLessThan(Height);

        // The two panes share the row, so a split world gets well under half the client's columns.
        await Assert.That(left.Last.Width + right.Last.Width).IsLessThan(Width);
    }

    // --- what the rectangle is ------------------------------------------------

    /// <summary>
    /// The rows reported are the output area's, not the pane's: a pane's top row is its tab strip,
    /// which no server output is ever drawn on.
    /// </summary>
    [Test]
    public async Task TheReportedHeightExcludesTheTabStrip()
    {
        var (app, _) = App();
        app.RenderSnapshot();

        var world = await ConnectedAsync("Solo");
        app.AttachSession(world.Session, "main");
        app.RenderNextFrame();

        var surface = app.PaneSnapshot();
        var paneId = surface.Rects.Keys.Single();
        var pane = surface.RectOf(paneId)!.Value;
        var output = app.PaneOutputRects()[paneId];

        await Assert.That(output.Height).IsEqualTo(pane.Height - 1); // the classic one-row tab header
        await Assert.That(output.Y).IsEqualTo(pane.Y + 1);
        await Assert.That(output.Width).IsEqualTo(pane.Width);
        await Assert.That(world.Last).IsEqualTo((output.Width, output.Height));

        // And the pane itself is inside the workspace row, so the header, both input bars and the
        // status line are already out of the count.
        await Assert.That(world.Last.Height).IsLessThan(app.LaidOutRows.Workspace);
    }

    /// <summary>
    /// A background tab is still told where it lives. It is the size that window will be shown at the
    /// moment its tab is picked, and the alternative — saying nothing — leaves the server wrapping to
    /// whatever it last heard, which is the fault this whole change is about.
    /// </summary>
    [Test]
    public async Task AWindowThatIsNotTheVisibleTabIsToldItsPanesSizeAnyway()
    {
        var (app, _) = App();
        app.RenderSnapshot(); // one pane, two tabs: main is active, Chat is not

        var visible = await ConnectedAsync("Visible");
        var hidden = await ConnectedAsync("Hidden");
        app.AttachSession(visible.Session, "main");
        app.AttachSession(hidden.Session, ChatWindowId);
        app.RenderNextFrame();

        var rect = OutputRectOf(app, "main"); // the pane both windows are tabs of

        await Assert.That(hidden.Count).IsEqualTo(1);
        await Assert.That(hidden.Last).IsEqualTo((rect.Width, rect.Height));
        await Assert.That(hidden.Last).IsEqualTo(visible.Last);
    }

    // --- when it is reported ---------------------------------------------------

    /// <summary>
    /// A split resizes both worlds without the terminal changing size at all — the case a
    /// resize-only report cannot see. The clock is moved past the rate limit's interval first, so
    /// what is being asserted here is the report and not the timing (which the throttle tests below
    /// own).
    /// </summary>
    [Test]
    public async Task SplittingThePaneReportsAgain_WithNoTerminalResize()
    {
        var (app, clock) = App();
        app.RenderSnapshot();

        var first = await ConnectedAsync("First");
        var second = await ConnectedAsync("Second");
        app.AttachSession(first.Session, "main");
        app.AttachSession(second.Session, ChatWindowId);
        app.RenderNextFrame();

        var shared = first.Last;
        await Assert.That(second.Last).IsEqualTo(shared);

        clock.Advance(Interval);
        app.SimulatePrefixedKey(Key('|')); // ⌃B | — side by side, taking the Chat tab with it
        app.RenderNextFrame();

        await Assert.That(first.Count).IsEqualTo(2);
        await Assert.That(second.Count).IsEqualTo(2);
        await Assert.That(first.Last.Width).IsLessThan(shared.Width);
        await Assert.That(second.Last.Width).IsLessThan(shared.Width);
        await Assert.That(first.Last).IsEqualTo(OutputSizeOf(app, "main"));
        await Assert.That(second.Last).IsEqualTo(OutputSizeOf(app, ChatWindowId));
    }

    /// <summary>
    /// An unchanged size is not re-announced, however many frames go by — and the clock is wound past
    /// the interval between them, so it is change detection suppressing them and not the rate limit.
    /// </summary>
    [Test]
    public async Task AnUnchangedSizeIsNotAnnouncedAgain()
    {
        var (app, clock) = App();
        app.RenderSnapshot();

        var world = await ConnectedAsync("Quiet");
        app.AttachSession(world.Session, "main");

        for (var frame = 0; frame < 3; frame++)
        {
            app.RenderNextFrame();
            clock.Advance(Interval);
        }

        await Assert.That(world.Count).IsEqualTo(1);
    }

    /// <summary>
    /// A reconnecting session is told again, at the same size, <em>without</em> the clock moving: the
    /// server it is talking to now is a fresh negotiation that has never heard a NAWS from us, so it
    /// must not be made to serve out an interval belonging to the connection before it.
    /// </summary>
    [Test]
    public async Task AReconnectingSessionIsToldAgainImmediately()
    {
        var (app, _) = App();
        app.RenderSnapshot();

        var world = await ConnectedAsync("Flappy");
        app.AttachSession(world.Session, "main");
        app.RenderNextFrame();
        var announced = world.Last;

        await world.Session.DisconnectAsync();
        app.RenderNextFrame(); // nothing to tell — and the record of what it was told is dropped
        await Assert.That(world.Count).IsEqualTo(1);

        await world.Session.ConnectAsync();
        app.RenderNextFrame();

        await Assert.That(world.Count).IsEqualTo(2);
        await Assert.That(world.Last).IsEqualTo(announced);
    }

    /// <summary>A session that never connected is never written to; its neighbour still is.</summary>
    [Test]
    public async Task ASessionThatIsNotConnectedIsSkipped()
    {
        var (app, _) = App();
        app.RenderSnapshot();

        var offline = Idle("Offline");
        var online = await ConnectedAsync("Online");
        app.AttachSession(offline.Session, "main");
        app.AttachSession(online.Session, ChatWindowId);
        app.RenderNextFrame();

        await Assert.That(offline.Count).IsEqualTo(0);
        await Assert.That(online.Count).IsEqualTo(1);
    }

    // --- the rate limit --------------------------------------------------------

    /// <summary>
    /// The one that matters most. A size changes, the limit holds it back, and then the frames stop —
    /// which is exactly what the end of a drag-resize looks like. Nothing renders again, so a limiter
    /// that only dropped would leave the server wrapping to the width the drag passed through rather
    /// than the one it ended on. Note there is no frame after the change: only time passes.
    /// </summary>
    [Test]
    public async Task TheSettledSizeArrivesAfterTheFramesStop()
    {
        var (app, clock) = App();
        app.RenderSnapshot();

        var world = await ConnectedAsync("Dragged");
        app.AttachSession(world.Session, "main");
        app.RenderNextFrame();
        var opening = world.Last;

        app.SimulatePrefixedKey(Key('|')); // a new size, well inside the interval
        app.RenderNextFrame();
        await Assert.That(world.Count).IsEqualTo(1); // held back

        var settled = OutputSizeOf(app, "main");
        await Assert.That(settled).IsNotEqualTo(opening);

        clock.Advance(Interval); // no further frame — the trailing flush is on its own

        await Assert.That(world.Count).IsEqualTo(2);
        await Assert.That(world.Last).IsEqualTo(settled);
    }

    /// <summary>
    /// Sizes arriving faster than the interval are coalesced to the newest one: the server is told
    /// where the drag ended, and never the widths it swept through on the way.
    /// </summary>
    [Test]
    public async Task SizesInsideTheIntervalAreCoalescedToTheLatest()
    {
        var (app, clock) = App();
        app.RenderSnapshot();

        var world = await ConnectedAsync("Sweep");
        app.AttachSession(world.Session, "main");
        app.RenderNextFrame();

        app.SimulatePrefixedKey(Key('|')); // split: a narrower pane
        app.RenderNextFrame();
        var swept = OutputSizeOf(app, "main");

        app.SimulatePrefixedKey(Key('b')); // collapse the rail: narrower still, same interval
        app.RenderNextFrame();
        var settled = OutputSizeOf(app, "main");

        await Assert.That(settled).IsNotEqualTo(swept);
        await Assert.That(world.Count).IsEqualTo(1); // both held back, the second replacing the first

        clock.Advance(Interval);

        await Assert.That(world.Count).IsEqualTo(2);
        await Assert.That(world.Last).IsEqualTo(settled);
        await Assert.That(world.WasTold(swept)).IsFalse(); // the intermediate never went out
    }

    /// <summary>
    /// The leading edge is not delayed: a change that arrives after a quiet interval goes out on the
    /// frame that produced it. A split, a zoom or a closed tab is one discrete event, and adding a
    /// quarter second to it would buy nothing.
    /// </summary>
    [Test]
    public async Task AChangeAfterAQuietIntervalGoesOutAtOnce()
    {
        var (app, clock) = App();
        app.RenderSnapshot();

        var world = await ConnectedAsync("Prompt");
        app.AttachSession(world.Session, "main");
        app.RenderNextFrame();

        clock.Advance(Interval);
        app.SimulatePrefixedKey(Key('|'));
        app.RenderNextFrame(); // no clock movement afterwards

        await Assert.That(world.Count).IsEqualTo(2);
        await Assert.That(world.Last).IsEqualTo(OutputSizeOf(app, "main"));
    }

    /// <summary>
    /// A size that comes back to what the server already has is not sent at all: a drag that ends
    /// where it started, or a rail collapsed and re-opened inside one interval, owes it nothing.
    /// </summary>
    [Test]
    public async Task ASizeThatReturnsToWhatTheServerKnowsIsNeverSent()
    {
        var (app, clock) = App();
        app.RenderSnapshot();

        var world = await ConnectedAsync("Wobble");
        app.AttachSession(world.Session, "main");
        app.RenderNextFrame();
        var announced = world.Last;

        app.SimulatePrefixedKey(Key('b')); // collapse the rail — a new size, held back
        app.RenderNextFrame();
        app.SimulatePrefixedKey(Key('b')); // and open it again, inside the same interval
        app.RenderNextFrame();

        await Assert.That(OutputSizeOf(app, "main")).IsEqualTo(announced);

        clock.Advance(Interval);

        await Assert.That(world.Count).IsEqualTo(1);
    }

    /// <summary>
    /// A drag-resize's worth of frames costs a bounded number of writes, not one per frame. Sixty
    /// alternating sizes inside one interval is what a second of dragging looks like from here.
    /// </summary>
    [Test]
    public async Task ADragsWorthOfFramesCostsOneWritePerInterval()
    {
        var (app, clock) = App();
        app.RenderSnapshot();

        var world = await ConnectedAsync("Storm");
        app.AttachSession(world.Session, "main");
        app.RenderNextFrame();

        // 60 frames at 60fps: a second of dragging, spanning four of the limit's windows.
        for (var frame = 0; frame < 60; frame++)
        {
            app.SimulatePrefixedKey(Key('b')); // toggles the rail, so every frame is a new size
            app.RenderNextFrame();
            clock.Advance(TimeSpan.FromMilliseconds(1000.0 / 60));
        }

        clock.Advance(Interval); // the drag ends; the settled size still lands

        // One second of frames, one opening report plus at most one per 250 ms window, and the last
        // thing the server heard is what is on screen.
        await Assert.That(world.Count).IsLessThanOrEqualTo(6);
        await Assert.That(world.Last).IsEqualTo(OutputSizeOf(app, "main"));
    }
}
