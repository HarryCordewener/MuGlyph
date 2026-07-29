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

        await Assert.That(Panes(app).Single().Tabs).IsEquivalentTo(new[] { before[1], before[0] });
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
        await Assert.That(Panes(app).Single().Tabs).IsEquivalentTo(new[] { before[1], before[0] });

        app.SimulatePrefixedKey(Arrow(ConsoleKey.LeftArrow));
        await Assert.That(Panes(app).Single().Tabs).IsEquivalentTo(before);
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
