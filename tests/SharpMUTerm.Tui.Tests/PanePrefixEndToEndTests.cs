using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The ⌃B prefix, driven key by key through the app's own handler. It was reported dead in a real
/// terminal — the armed strip appeared and none of <c>| - z o x b m &lt; &gt;</c> appeared to do
/// anything — and the dispatch turned out to be fine: on a fresh workspace, one pane holding one
/// window, nearly every command on that strip is a legitimate no-op. A keystroke that changes nothing
/// and says nothing is indistinguishable from a prefix that never fired, so these pin both halves:
/// the commands run, and the ones that can't say why.
/// </summary>
/// <remarks>
/// Serialised for the same reason <see cref="MacroDispatchEndToEndTests"/> is: constructing the app
/// touches the process-global console streams.
/// </remarks>
[NotInParallel]
public class PanePrefixEndToEndTests
{
    private const int Width = 120;
    private const int Height = 34;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>The demo workspace: one pane holding the main window and a Chat spawn as two tabs.</summary>
    private static SharpMUTermApp TwoTabs()
    {
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(Width, Height));
    }

    /// <summary>A fresh client: one pane, one window, nothing configured. What the report was made on.</summary>
    private static SharpMUTermApp OneTab()
    {
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(new AppConfiguration(), Headless, new HeadlessConsoleDriver(Width, Height));
    }

    private static readonly string ChatId = Workspace.SpawnWindowId(null, "Chat");

    private static readonly string OocId = Workspace.SpawnWindowId(null, "OOC");

    /// <summary>
    /// One pane holding three tabs — <c>main</c>, a Chat spawn and an OOC spawn — with the tab at
    /// <paramref name="activeIndex"/> the visible one. Three is the smallest strip that has a
    /// <em>middle</em>, and a middle is the only place a reorder can move a tab without it also
    /// arriving at an end; with two tabs every legal move lands at one, which is what let the strip
    /// and the model disagree for as long as they did.
    /// </summary>
    private static SharpMUTermApp ThreeTabs(int activeIndex)
    {
        Console.SetIn(TextReader.Null);
        var config = new AppConfiguration
        {
            LastSession = new WorkspaceState
            {
                Windows =
                {
                    new WorkspaceWindowState { Id = "main", Title = "Main", Kind = WindowKind.Main },
                    new WorkspaceWindowState { Id = ChatId, Title = "Chat", Kind = WindowKind.Spawn },
                    new WorkspaceWindowState { Id = OocId, Title = "OOC", Kind = WindowKind.Spawn },
                },
                Root = new LayoutNodeState
                {
                    Type = "pane",
                    Id = "p1",
                    Tabs = { "main", ChatId, OocId },
                    ActiveIndex = activeIndex,
                },
                FocusedPaneId = "p1",
            },
        };

        return new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
    }

    /// <summary>The window ids of <see cref="ThreeTabs"/>, by the short names the test cases name them.</summary>
    private static string WindowId(string shortName) => shortName switch
    {
        "chat" => ChatId,
        "ooc" => OocId,
        _ => "main",
    };

    /// <summary>The inverse: a window id as the short name a test case spells it.</summary>
    private static string ShortName(string windowId) =>
        windowId == ChatId ? "chat" : windowId == OocId ? "ooc" : windowId;

    /// <summary>
    /// A tab order as one comparable string, which is the whole reason it is a string: TUnit's
    /// <c>IsEquivalentTo</c> defaults to order-<em>insensitive</em> collection equivalence, so
    /// <c>[a,b]</c> and <c>[b,a]</c> satisfy it — and a reorder assertion that cannot see order is not
    /// an assertion at all.
    /// </summary>
    private static string Order(IEnumerable<string> windowIds) => string.Join(",", windowIds.Select(ShortName));

    private static ConsoleKeyInfo Key(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static ConsoleKeyInfo Arrow(ConsoleKey key) => new('\0', key, false, false, false);

    /// <summary>The layout's panes in tree order.</summary>
    private static List<LayoutNodeState> Panes(SharpMUTermApp app) => Flatten(app.CaptureSession().Root);

    private static List<LayoutNodeState> Flatten(LayoutNodeState node) =>
        node.Children is { Count: > 0 } children
            ? children.SelectMany(Flatten).ToList()
            : new List<LayoutNodeState> { node };

    // --- the commands do run ---------------------------------------------------

    [Test]
    public async Task ThePrefixThenPipe_SplitsThePaneSideBySide()
    {
        var app = TwoTabs();

        app.SimulatePrefixedKey(Key('|'));

        var root = app.CaptureSession().Root;
        await Assert.That(root.Type).IsEqualTo("split");
        await Assert.That(root.Direction).IsEqualTo(SplitDirection.Row);
        await Assert.That(Panes(app).Count).IsEqualTo(2);
    }

    [Test]
    public async Task ThePrefixThenDash_SplitsThePaneStacked()
    {
        var app = TwoTabs();

        app.SimulatePrefixedKey(Key('-'));

        await Assert.That(app.CaptureSession().Root.Direction).IsEqualTo(SplitDirection.Column);
    }

    [Test]
    public async Task ThePrefixThenAngleBracket_ReordersTheActiveTab()
    {
        var app = TwoTabs();
        var before = Panes(app).Single().Tabs.ToList();

        app.SimulatePrefixedKey(Key('>'));

        await Assert.That(Order(Panes(app).Single().Tabs)).IsEqualTo(Order(new[] { before[1], before[0] }));
        await Assert.That(Order(app.PaneTabStrip("p1").Select(t => t.WindowId)))
            .IsEqualTo(Order(new[] { before[1], before[0] }));
    }

    /// <summary>
    /// The label invites the arrows: <c>&lt;</c> and <c>&gt;</c> on the armed strip read as a direction,
    /// and the first thing the maintainer reached for was ← and →. They are the same two commands.
    /// </summary>
    [Test]
    public async Task ThePrefixThenAnArrow_ReordersTheActiveTabJustLikeTheAngleBracket()
    {
        var app = TwoTabs();
        var before = Panes(app).Single().Tabs.ToList();

        app.SimulatePrefixedKey(Arrow(ConsoleKey.RightArrow));
        await Assert.That(Order(Panes(app).Single().Tabs)).IsEqualTo(Order(new[] { before[1], before[0] }));
        await Assert.That(Order(app.PaneTabStrip("p1").Select(t => t.WindowId)))
            .IsEqualTo(Order(new[] { before[1], before[0] }));

        app.SimulatePrefixedKey(Arrow(ConsoleKey.LeftArrow));
        await Assert.That(Order(Panes(app).Single().Tabs)).IsEqualTo(Order(before));
        await Assert.That(Order(app.PaneTabStrip("p1").Select(t => t.WindowId))).IsEqualTo(Order(before));
    }

    /// <summary>
    /// <b>The reorder has to move the strip the user is looking at, not only the model behind it.</b>
    /// Three tabs, both directions, from all three positions — the grid the report only named one cell
    /// of ("3 tabs, move the middle one right, it says it is already at the end").
    /// <para>
    /// The refusal was reading a model the screen had stopped agreeing with. <c>TabControl</c> cannot
    /// move a page — <c>TabPages</c> is a copy and the only mutators add — so the old
    /// <c>RefreshTabTitles</c>, which repaints each page by its own <c>Tag</c>, left the strip in its
    /// original order after every reorder. The move looked like a no-op, and the press after it refused
    /// truthfully about a model that had the tab at the end and falsely about a strip that showed it in
    /// the middle. Asserting the *strip* is the point of this test: the model half passed throughout.
    /// </para>
    /// </summary>
    [Test]
    [Arguments(0, '<', "main,chat,ooc", "main", true)]
    [Arguments(0, '>', "chat,main,ooc", "main", false)]
    [Arguments(1, '<', "chat,main,ooc", "chat", false)]
    [Arguments(1, '>', "main,ooc,chat", "chat", false)]
    [Arguments(2, '<', "main,ooc,chat", "ooc", false)]
    [Arguments(2, '>', "main,chat,ooc", "ooc", true)]
    public async Task ReorderingATabMovesItOnTheStripAndRefusesOnlyAtARealEnd(
        int activeIndex, char key, string expected, string moved, bool refused)
    {
        var app = ThreeTabs(activeIndex);

        app.SimulatePrefixedKey(Key(key));

        // The model, the strip on screen, and which tab is still the active one — all three, because the
        // bug was exactly the model moving while the other two stayed where they were.
        await Assert.That(Order(Panes(app).Single().Tabs)).IsEqualTo(expected);
        await Assert.That(Order(app.PaneTabStrip("p1").Select(t => t.WindowId))).IsEqualTo(expected);
        await Assert.That(app.PaneActiveTab("p1")).IsEqualTo(WindowId(moved));

        // Loud either way: a tab genuinely at the end still says so, and one that had somewhere to go
        // must not be told it did not.
        if (refused)
        {
            await Assert.That(app.StatusMarkup).Contains("already at that end");
        }
        else
        {
            await Assert.That(app.StatusMarkup).DoesNotContain("already at that end");
        }
    }

    /// <summary>Any other key spends the prefix and does nothing — the next key is typing again.</summary>
    [Test]
    public async Task AnUnboundKeyDisarmsThePrefixWithoutRunningTheNextOne()
    {
        var app = TwoTabs();

        app.SimulatePrefixedKey(Key('q'));
        app.SimulateKey(Key('|')); // no longer prefixed: this is a character, not a command

        await Assert.That(app.CaptureSession().Root.Type).IsEqualTo("pane");
    }

    // --- and the ones that can't, say so ---------------------------------------

    [Test]
    public async Task SplittingAPaneWithOneWindow_SaysSoInsteadOfDoingNothing()
    {
        var app = OneTab();

        app.SimulatePrefixedKey(Key('|'));

        await Assert.That(app.CaptureSession().Root.Type).IsEqualTo("pane"); // genuinely refused
        await Assert.That(app.StatusMarkup).Contains("nothing to split");
    }

    [Test]
    public async Task ReorderingASingleTab_SaysThereIsNothingToReorder()
    {
        var app = OneTab();

        app.SimulatePrefixedKey(Key('<'));

        await Assert.That(app.StatusMarkup).Contains("nothing to reorder");
    }

    /// <summary>With two tabs the refusal is a different one: the tab is already at that end.</summary>
    [Test]
    public async Task ReorderingPastTheEndOfTheStrip_SaysTheTabIsAlreadyThere()
    {
        var app = TwoTabs();

        app.SimulatePrefixedKey(Key('<')); // the active tab is already the first

        await Assert.That(app.StatusMarkup).Contains("already at that end");
    }

    [Test]
    [Arguments('z', "nothing to zoom")]
    [Arguments('o', "nowhere to cycle to")]
    public async Task ZoomAndCycleOnALoneP1_SayThereIsOnlyOnePane(char key, string reason)
    {
        var app = OneTab();

        app.SimulatePrefixedKey(Key(key));

        await Assert.That(app.StatusMarkup).Contains(reason);
    }

    /// <summary>Closing is refused for the main window, which is the session rather than a closable tab.</summary>
    [Test]
    public async Task ClosingTheMainWindow_SaysItStaysOpen()
    {
        var app = OneTab();

        app.SimulatePrefixedKey(Key('x'));

        await Assert.That(app.StatusMarkup).Contains("main window stays open");
        await Assert.That(Panes(app).Single().Tabs.Count).IsEqualTo(1);
    }

    /// <summary>
    /// The palette's split entries went through the same silent refusal, so they route through the same
    /// report — a menu item that appears to do nothing is the same defect with a different door.
    /// </summary>
    [Test]
    public async Task TheCommandSurfacesSplitEntry_ReportsTheSameRefusal()
    {
        var app = OneTab();

        app.DispatchCommand("layout:split-right");

        await Assert.That(app.StatusMarkup).Contains("nothing to split");
    }
}
