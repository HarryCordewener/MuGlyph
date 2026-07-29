using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Telnet;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What each connected world is told its terminal size is, over NAWS.
/// <para>
/// The answer used to be the whole client window, for the focused session only. In a client whose
/// main feature is splits that is wrong nearly whenever the feature is used: a world sharing the
/// screen with another gets perhaps half the columns, minus the rail and the divider, and its rows
/// are short by the header, the input area, the status line and its own tab strip — so a server told
/// 120x32 wrapped to a width that did not exist on screen. These drive the real thing headlessly: a
/// real window, a real frame, the pane rectangles the framework itself arranged, and a fake telnet
/// transport recording every NAWS write.
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

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>A world session over a transport that records what it is told, connected or not.</summary>
    private sealed record Reporter(WorldSession Session, RecordingTelnetSession Telnet)
    {
        public (int Width, int Height) Last => Telnet.Sizes[^1];

        public int Count => Telnet.Sizes.Count;
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

    private static SharpMUTermApp App()
    {
        // The window system reads the console for input even headless; a null reader returns EOF.
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(Width, Height));
    }

    private static ConsoleKeyInfo Key(char c) => new(c, ConsoleKey.NoName, false, false, false);

    /// <summary>The pane showing <paramref name="windowId"/>, and its output rectangle.</summary>
    private static PaneRect OutputRectOf(SharpMUTermApp app, string windowId)
    {
        var surface = app.PaneSnapshot();
        var paneId = surface.Rects.Keys.Single(id => surface.ActiveWindow(id) == windowId);
        return app.PaneOutputRects()[paneId];
    }

    /// <summary>The demo workspace's spawn window — the second tab, and after a split the second pane.</summary>
    private static string ChatWindowId => Workspace.SpawnWindowId("Chat");

    // --- the bug ---------------------------------------------------------------

    /// <summary>
    /// The one that would have caught it. Two connected worlds, one split, and each is told its own
    /// pane — not the window, and not each other's. Under the old code both numbers were 120x32.
    /// </summary>
    [Test]
    public async Task EachConnectedSessionIsToldItsOwnPane_NotTheWholeWindow()
    {
        var app = App();
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
        var app = App();
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
        var app = App();
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
    /// resize-only report cannot see.
    /// </summary>
    [Test]
    public async Task SplittingThePaneReportsAgain_WithNoTerminalResize()
    {
        var app = App();
        app.RenderSnapshot();

        var first = await ConnectedAsync("First");
        var second = await ConnectedAsync("Second");
        app.AttachSession(first.Session, "main");
        app.AttachSession(second.Session, ChatWindowId);
        app.RenderNextFrame();

        var shared = first.Last;
        await Assert.That(second.Last).IsEqualTo(shared);

        app.SimulatePrefixedKey(Key('|')); // ⌃B | — side by side, taking the Chat tab with it
        app.RenderNextFrame();

        await Assert.That(first.Count).IsEqualTo(2);
        await Assert.That(second.Count).IsEqualTo(2);
        await Assert.That(first.Last.Width).IsLessThan(shared.Width);
        await Assert.That(second.Last.Width).IsLessThan(shared.Width);
        await Assert.That(first.Last).IsEqualTo((OutputRectOf(app, "main").Width, OutputRectOf(app, "main").Height));
        await Assert.That(second.Last)
            .IsEqualTo((OutputRectOf(app, ChatWindowId).Width, OutputRectOf(app, ChatWindowId).Height));
    }

    /// <summary>An unchanged size is not re-announced, however many frames go by.</summary>
    [Test]
    public async Task AnUnchangedSizeIsNotAnnouncedAgain()
    {
        var app = App();
        app.RenderSnapshot();

        var world = await ConnectedAsync("Quiet");
        app.AttachSession(world.Session, "main");
        app.RenderNextFrame();
        app.RenderNextFrame();
        app.RenderNextFrame();

        await Assert.That(world.Count).IsEqualTo(1);
    }

    /// <summary>
    /// A reconnecting session is told again, at the same size: the server it is talking to now is a
    /// fresh negotiation that has never heard a NAWS from us.
    /// </summary>
    [Test]
    public async Task AReconnectingSessionIsToldAgain()
    {
        var app = App();
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
        var app = App();
        app.RenderSnapshot();

        var offline = Idle("Offline");
        var online = await ConnectedAsync("Online");
        app.AttachSession(offline.Session, "main");
        app.AttachSession(online.Session, ChatWindowId);
        app.RenderNextFrame();

        await Assert.That(offline.Count).IsEqualTo(0);
        await Assert.That(online.Count).IsEqualTo(1);
    }
}
