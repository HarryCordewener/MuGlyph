using System.Text.RegularExpressions;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// Which pane is selected and which command line ⏎ sends from, as the frame says it — and the keys that
/// move between them.
/// <para>
/// The reported defect was that neither was visible: the two input bands differed by thirteen points per
/// channel, and the focused pane was not rendered at all. So the assertions here are deliberately about
/// <em>painted cells</em>, not about a property having been assigned. A colour can be set on a control
/// that is arranged at zero rows, or set on a plane another control paints over, and every such bug is
/// invisible to a test that reads the property back. What cannot lie is the frame.
/// </para>
/// <para>
/// The one test to keep above all the others is
/// <see cref="MovingFocusDoesNotMoveAnyPaneRectangle"/>. Per-pane NAWS is derived from the pane
/// rectangle, so an indicator that cost a cell — a border, a gutter, a marker column — would announce a
/// new terminal size to every connected server on every focus change and reflow the game's own output.
/// That is why the indicator recolours what is already drawn, and that is the claim which stops someone
/// later "improving" it into a border.
/// </para>
/// </summary>
/// <remarks>
/// Serialised for the same reason <see cref="PaneDragEndToEndTests"/> is: rendering a frame redirects the
/// process-global <c>Console.Out</c>, and the harness redirects <c>Console.In</c>.
/// </remarks>
[NotInParallel]
public class FocusIndicationTests
{
    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    private static SharpMUTermApp App(int width = 120, int height = 34)
    {
        // The window system reads the console for input even headless; a null reader returns EOF.
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(width, height));
    }

    private static ConsoleKeyInfo Ctrl(ConsoleKey key) => new('\0', key, false, false, true);

    private static ConsoleKeyInfo Plain(char c, ConsoleKey key) => new(c, key, false, false, false);

    /// <summary>
    /// Empties the armed command line, then types <paramref name="text"/> into it. The demo scene seeds a
    /// per-window draft (that is the feature <c>draft</c> and <c>draft2</c> exist to render), so a test
    /// about what a key does to the text has to start from a known line rather than assume an empty one.
    /// Cleared through the app's own readline chords — ⌃E to the end, ⌃U back to the start — so the setup
    /// exercises the same path a user would and cannot pass by writing the buffer behind the bar's back.
    /// </summary>
    private static void Type(SharpMUTermApp app, string text)
    {
        app.SimulateKey(Ctrl(ConsoleKey.E));
        app.SimulateKey(Ctrl(ConsoleKey.U));
        foreach (var c in text)
        {
            app.SimulateKey(Plain(c, ConsoleKey.NoName));
        }
    }

    /// <summary>The demo workspace's spawn window — the second tab, and after a split the second pane.</summary>
    private static string ChatWindowId => DemoScene.ChatWindowId;


    // --- item 1: the input bands ------------------------------------------------------------------

    /// <summary>
    /// Both bands are on the screen, in quantity, and they are different colours. The pair is the point:
    /// "a colour was assigned to the armed bar" was already true when the bug was reported.
    /// </summary>
    [Test]
    [Arguments(120, 34)]
    [Arguments(100, 24)]
    public async Task BothInputBandsArePaintedAndAreDifferentColours(int width, int height)
    {
        var app = App(width, height);
        var ansi = app.RenderSnapshot("draft2"); // two bars, the second armed
        var (armed, idle) = app.InputBandColors;

        await Assert.That(FrameGrid.Sgr(armed)).IsNotEqualTo(FrameGrid.Sgr(idle));
        await Assert.That(FrameGrid.CellsPainted(ansi, armed)).IsGreaterThanOrEqualTo(width);
        await Assert.That(FrameGrid.CellsPainted(ansi, idle)).IsGreaterThanOrEqualTo(width);
    }

    /// <summary>
    /// The measured distance between the bands as <em>painted</em>, not as configured. This is the
    /// assertion in the language of the report: the two tones on the screen are this far apart, and the
    /// figure is more than double the step that was reported as almost invisible.
    /// </summary>
    [Test]
    public async Task ThePaintedBandsAreMoreThanTwiceAsFarApartAsTheReportedPair()
    {
        var app = App();
        var ansi = app.RenderSnapshot("draft2");
        var (armed, idle) = app.InputBandColors;

        // Both are genuinely on the frame — a distance between two colours nothing painted is arithmetic.
        await Assert.That(FrameGrid.CellsPainted(ansi, armed)).IsGreaterThan(0);
        await Assert.That(FrameGrid.CellsPainted(ansi, idle)).IsGreaterThan(0);

        var reported = Luma(0x33, 0x39, 0x4c) - Luma(0x26, 0x2b, 0x3a);
        var now = Luma(armed.R, armed.G, armed.B) - Luma(idle.R, idle.G, idle.B);

        await Assert.That(now).IsGreaterThan(reported * 2);
    }

    private static double Luma(int r, int g, int b) => ((r * 299) + (g * 587) + (b * 114)) / 1000.0;

    /// <summary>
    /// Arming the other bar swaps which band is which — so the indicator tracks the fact rather than the
    /// bar's position on screen. ⇥ is the gesture, because that is the one the chrome advertises.
    /// </summary>
    [Test]
    public async Task ArmingTheOtherBarSwapsTheBands()
    {
        var app = App();
        app.RenderSnapshot("focus"); // two bars, the primary armed
        await Assert.That(app.SecondBarArmed).IsFalse();

        var before = app.RenderWholeFrame();
        app.SimulateKey(Plain('\t', ConsoleKey.Tab));
        var after = app.RenderWholeFrame();

        await Assert.That(app.SecondBarArmed).IsTrue();

        // The armed tone still covers as many cells as before — it moved bar, it did not disappear.
        var (armed, idle) = app.InputBandColors;
        await Assert.That(FrameGrid.CellsPainted(after, armed)).IsEqualTo(FrameGrid.CellsPainted(before, armed));
        await Assert.That(FrameGrid.CellsPainted(after, idle)).IsEqualTo(FrameGrid.CellsPainted(before, idle));

        // And the frames differ, which is the whole claim: arming a bar changes what is on the screen.
        await Assert.That(after).IsNotEqualTo(before);
    }

    /// <summary>
    /// The prompt's weight is the cue that survives a terminal whose theme flattens the two backgrounds:
    /// the armed bar's prompt is bold and the idle one dim. Read off the markup the bars are actually
    /// given, because that is what the parser turns into cells.
    /// </summary>
    [Test]
    public async Task TheArmedPromptIsBoldAndTheIdleOneDim()
    {
        var app = App();
        app.RenderSnapshot("focus");

        await Assert.That(app.PrimaryPromptMarkup).Contains("bold");
        await Assert.That(app.SecondPromptMarkup).Contains("dim");

        app.SimulateKey(Plain('\t', ConsoleKey.Tab));

        await Assert.That(app.SecondPromptMarkup).Contains("bold");
        await Assert.That(app.PrimaryPromptMarkup).Contains("dim");
    }

    /// <summary>
    /// Both prompt spellings are the same width. The prompt's width is what the caret column and the wrap
    /// column are measured from, so a marker that appeared only on the armed bar would reflow the line
    /// being typed every time the other bar took over — which is a worse bug than the one being fixed.
    /// </summary>
    [Test]
    public async Task ArmingABarDoesNotChangeItsPromptWidth()
    {
        var app = App();
        app.RenderSnapshot("focus");

        var idleWidth = SharpMUTermApp.MarkupWidth(app.SecondPromptMarkup);
        app.SimulateKey(Plain('\t', ConsoleKey.Tab));
        var armedWidth = SharpMUTermApp.MarkupWidth(app.SecondPromptMarkup);

        await Assert.That(armedWidth).IsEqualTo(idleWidth);
    }

    // --- item 1: the focused pane -----------------------------------------------------------------

    /// <summary>
    /// The focused pane is painted on a different plane from an unfocused one, and both planes are really
    /// on the frame in quantity. Before this there was no rendering of pane focus at all: the one pane
    /// every workspace keystroke was aimed at looked exactly like the ones it was not.
    /// </summary>
    [Test]
    [Arguments(120, 34)]
    [Arguments(100, 24)]
    public async Task TheFocusedPaneIsPaintedOnItsOwnPlane(int width, int height)
    {
        var app = App(width, height);
        var ansi = app.RenderSnapshot("split");

        var (focused, unfocused) = app.PaneBandColors;
        await Assert.That(FrameGrid.Sgr(focused)).IsNotEqualTo(FrameGrid.Sgr(unfocused));

        var rects = app.PaneOutputRects();
        var focusedId = app.FocusedPaneId;
        var otherId = app.PaneIds.Single(id => id != focusedId);

        // Each plane dominates its own pane's output rectangle, and is not what the other is painted in.
        await Assert.That(FrameGrid.CellsPaintedIn(ansi, rects[focusedId], focused)).IsGreaterThan(0);
        await Assert.That(FrameGrid.CellsPaintedIn(ansi, rects[otherId], unfocused)).IsGreaterThan(0);
        await Assert.That(FrameGrid.CellsPaintedIn(ansi, rects[otherId], focused)).IsEqualTo(0);
        await Assert.That(FrameGrid.CellsPaintedIn(ansi, rects[focusedId], unfocused)).IsEqualTo(0);
    }

    /// <summary>
    /// The plane follows the focus. Two panes and the same frame twice over, once either side of a ⌃→ —
    /// which is the claim a test that only read the palette could not make.
    /// </summary>
    [Test]
    public async Task ThePlaneFollowsTheFocus()
    {
        var app = App();
        app.RenderSnapshot("split");

        var first = app.FocusedPaneId;
        var second = app.PaneIds.Single(id => id != first);
        var (focused, _) = app.PaneBandColors;
        var rects = app.PaneOutputRects();

        var before = app.RenderWholeFrame();
        await Assert.That(FrameGrid.CellsPaintedIn(before, rects[first], focused)).IsGreaterThan(0);
        await Assert.That(FrameGrid.CellsPaintedIn(before, rects[second], focused)).IsEqualTo(0);

        app.SimulateKey(Ctrl(ConsoleKey.RightArrow));
        var after = app.RenderWholeFrame();

        await Assert.That(app.FocusedPaneId).IsEqualTo(second);
        await Assert.That(FrameGrid.CellsPaintedIn(after, rects[second], focused)).IsGreaterThan(0);
        await Assert.That(FrameGrid.CellsPaintedIn(after, rects[first], focused)).IsEqualTo(0);
    }

    /// <summary>
    /// The shape cue: the focused pane's active tab carries a <c>▌</c> and no other tab does. It reads on
    /// a monochrome terminal, where a luminance step does not, and it is on the active tab only — so a
    /// pane never shows two of them.
    /// </summary>
    [Test]
    public async Task OnlyTheFocusedPanesActiveTabIsMarked()
    {
        var app = App();
        app.RenderSnapshot("split");

        var marked = app.PaneTabTitles
            .SelectMany(pane => pane.Value.Select(title => (pane.Key, Title: title)))
            .Where(t => t.Title.Contains(Glyphs.FocusedPane, StringComparison.Ordinal))
            .ToList();

        await Assert.That(marked.Count).IsEqualTo(1);
        await Assert.That(marked[0].Key).IsEqualTo(app.FocusedPaneId);
    }

    /// <summary>And the marker moves with the focus, rather than being baked in when the pane was built.</summary>
    [Test]
    public async Task TheMarkerMovesWithTheFocus()
    {
        var app = App();
        app.RenderSnapshot("split");
        var first = app.FocusedPaneId;

        app.SimulateKey(Ctrl(ConsoleKey.RightArrow));
        app.RenderNextFrame();

        var marked = app.PaneTabTitles
            .Where(p => p.Value.Any(t => t.Contains(Glyphs.FocusedPane, StringComparison.Ordinal)))
            .Select(p => p.Key)
            .ToList();

        await Assert.That(marked).IsEquivalentTo(new[] { app.FocusedPaneId });
        await Assert.That(app.FocusedPaneId).IsNotEqualTo(first);
    }

    // --- the NAWS trap ----------------------------------------------------------------------------

    /// <summary>
    /// <b>Moving focus moves no pane rectangle.</b> The indicator recolours what is already drawn, so
    /// every pane's rectangle — and every pane's <em>output</em> rectangle, which is what a server is told
    /// over NAWS — is identical before and after. An indicator that cost a cell (a border, a gutter, a
    /// marker column) would fail this, and the cost of failing it is that every focus change re-announces
    /// a different terminal size to the game and reflows its output. Checked narrow as well as roomy,
    /// because the roomy default is exactly where a one-cell change hides.
    /// </summary>
    [Test]
    [Arguments(160, 48)]
    [Arguments(120, 34)]
    [Arguments(100, 24)]
    [Arguments(80, 20)]
    public async Task MovingFocusDoesNotMoveAnyPaneRectangle(int width, int height)
    {
        var app = App(width, height);
        app.RenderSnapshot("split");

        var before = app.PaneOutputRects().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var first = app.FocusedPaneId;

        app.SimulateKey(Ctrl(ConsoleKey.RightArrow));
        app.RenderNextFrame();
        var after = app.PaneOutputRects();

        await Assert.That(app.FocusedPaneId).IsNotEqualTo(first); // the move really happened
        await Assert.That(after.Count).IsEqualTo(before.Count);
        foreach (var (paneId, rect) in before)
        {
            await Assert.That(after[paneId]).IsEqualTo(rect);
        }

        // And back again, so neither direction of the move is the one that shifts a rectangle.
        app.SimulateKey(Ctrl(ConsoleKey.LeftArrow));
        app.RenderNextFrame();
        var back = app.PaneOutputRects();
        foreach (var (paneId, rect) in before)
        {
            await Assert.That(back[paneId]).IsEqualTo(rect);
        }
    }

    /// <summary>
    /// <b>Typing moves no pane rectangle either.</b> The counterpart of
    /// <see cref="MovingFocusDoesNotMoveAnyPaneRectangle"/>, and the one that was missing: the rail's
    /// unsent-draft pen was a conditional one-to-two cells, the sidebar's width is its widest row's, and
    /// the pane area is what is left over — so the <em>first keystroke</em> of every line widened the
    /// sidebar, narrowed every pane and re-announced a new terminal size to every connected server. A
    /// reader watching a game reflow as they started to type was watching this.
    /// <para>
    /// Both edges are checked, because either one alone can hide a fix that only pads in one direction:
    /// the draft going out (⌃U on the demo's seeded line) and the draft coming back (the next character).
    /// </para>
    /// </summary>
    [Test]
    [Arguments(160, 48)]
    [Arguments(120, 34)]
    [Arguments(100, 24)]
    [Arguments(80, 20)]
    public async Task TypingDoesNotMoveAnyPaneRectangle(int width, int height)
    {
        var app = App(width, height);
        app.RenderSnapshot("split");
        SettleTheRail(app);

        Type(app, string.Empty); // ⌃E ⌃U — the seeded draft goes out and with it the ✎
        app.RenderNextFrame();
        await Assert.That(PenIsShowing(app)).IsFalse();

        var before = app.PaneOutputRects().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var railBefore = app.RailColumnWidth;

        app.SimulateKey(Plain('h', ConsoleKey.NoName)); // the first keystroke — the reported moment
        app.RenderNextFrame();
        await Assert.That(PenIsShowing(app)).IsTrue(); // the marker really did appear

        await Assert.That(app.RailColumnWidth).IsEqualTo(railBefore); // the direct cause
        foreach (var (paneId, rect) in before)
        {
            await Assert.That(app.PaneOutputRects()[paneId]).IsEqualTo(rect);
        }

        // And on out to a longer line, so it is the marker's presence being tested and not one glyph.
        foreach (var c in "ello there")
        {
            app.SimulateKey(Plain(c, ConsoleKey.NoName));
        }

        app.RenderNextFrame();
        await Assert.That(app.RailColumnWidth).IsEqualTo(railBefore);
        foreach (var (paneId, rect) in before)
        {
            await Assert.That(app.PaneOutputRects()[paneId]).IsEqualTo(rect);
        }
    }

    /// <summary>
    /// Reads the spawn window's unread badge away, so the rail's widest row is the one the pen lands on.
    /// <b>Without this the test passes on a broken client</b>: in the demo scene the Chat row's unread
    /// count happens to make it exactly as wide as the main row becomes once the pen appears, so the
    /// widest row — and therefore the sidebar's width — does not move even though the pen still costs two
    /// cells. That coincidence is why no snapshot ever caught this. Activating a window clears its unread,
    /// which is the ordinary way a reader gets there.
    /// </summary>
    private static void SettleTheRail(SharpMUTermApp app)
    {
        var spawn = app.WindowIds().Single(id => id.StartsWith("spawn:", StringComparison.Ordinal));
        var main = app.ActiveWindowId();
        app.SimulateWindowChange(spawn);
        app.SimulateWindowChange(main);
        app.RenderNextFrame();
    }

    /// <summary>
    /// The same claim on the bytes: a connected world is not told a new size because someone started
    /// typing. This is the consequence the rectangle test exists to prevent — a rail that widens on the
    /// first keystroke re-reports the terminal size to every server and reflows their output.
    /// <para>
    /// Driven on a <see cref="ManualTimeProvider"/> and wound past the report interval, because the NAWS
    /// throttle is four writes a second with a <em>trailing flush</em>: on a still clock a changed size is
    /// coalesced and never delivered inside the test, so this passes on a broken client without the wind.
    /// (It did. That is why the clock is here.)
    /// </para>
    /// </summary>
    [Test]
    public async Task TypingTellsNoServerANewSize()
    {
        Console.SetIn(TextReader.Null);
        var clock = new ManualTimeProvider();
        var app = new SharpMUTermApp(
            DemoScene.Build(), Headless, new HeadlessConsoleDriver(120, 34), clock);
        app.RenderSnapshot("split");

        // Before the session is attached, because activating a window whose session this becomes would
        // make it the active character — and the rail lists the *active* character's windows, so the very
        // rows the pen lands on would stop being drawn and the test would have nothing left to catch.
        SettleTheRail(app);
        Type(app, string.Empty);

        var telnet = new RecordingTelnetSession();
        var session = new WorldSession(
            new WorldDefinition { Name = "W", Host = "h", Port = 1 }, sessionFactory: _ => telnet);
        await session.ConnectAsync();
        app.AttachSession(session, "main");
        app.RenderNextFrame();
        clock.Advance(TimeSpan.FromSeconds(1));
        app.RenderNextFrame();

        var before = telnet.Sizes.Distinct().ToList();
        await Assert.That(before).IsNotEmpty();

        Type(app, "hello there");
        app.RenderNextFrame();
        clock.Advance(TimeSpan.FromSeconds(1));
        app.RenderNextFrame();

        await Assert.That(telnet.Sizes.Distinct().ToList()).IsEquivalentTo(before);
    }

    /// <summary>
    /// Whether the unsent-draft pen is on any tab of the frame — the visible half of the state the rail
    /// row carries, read off the strip the framework will draw rather than off a private field, so
    /// <see cref="TypingDoesNotMoveAnyPaneRectangle"/> cannot pass by never toggling anything.
    /// </summary>
    private static bool PenIsShowing(SharpMUTermApp app) =>
        app.PaneIds.Any(pane => app.PaneTabStrip(pane).Any(
            tab => tab.Title.Contains(Glyphs.Draft, StringComparison.Ordinal)));

    /// <summary>
    /// The same claim from the other end: a connected world is not told a new size because focus moved.
    /// This is the consequence the rectangle test exists to prevent, asserted on the bytes the server
    /// would actually receive.
    /// </summary>
    [Test]
    public async Task MovingFocusTellsNoServerANewSize()
    {
        var app = App();
        app.RenderSnapshot("split");

        var telnet = new RecordingTelnetSession();
        var session = new WorldSession(
            new WorldDefinition { Name = "W", Host = "h", Port = 1 }, sessionFactory: _ => telnet);
        await session.ConnectAsync();
        app.AttachSession(session, "main");
        app.RenderNextFrame();

        var sizesBefore = telnet.Sizes.ToList();
        await Assert.That(sizesBefore).IsNotEmpty(); // it was told something to begin with

        app.SimulateKey(Ctrl(ConsoleKey.RightArrow));
        app.RenderNextFrame();
        app.SimulateKey(Ctrl(ConsoleKey.LeftArrow));
        app.RenderNextFrame();

        // Whatever it was told again, it was told the same number — no size changed.
        await Assert.That(telnet.Sizes.Distinct().ToList()).IsEquivalentTo(sizesBefore.Distinct().ToList());
    }

    // --- item 2: the Ctrl+arrows ------------------------------------------------------------------

    /// <summary>A side-by-side split: ⌃→ and ⌃← walk between the panes and back.</summary>
    [Test]
    public async Task CtrlLeftAndRightWalkASideBySideSplit()
    {
        var app = App();
        app.RenderSnapshot("split");
        var left = app.FocusedPaneId;
        var right = app.PaneIds.Single(id => id != left);

        app.SimulateKey(Ctrl(ConsoleKey.RightArrow));
        await Assert.That(app.FocusedPaneId).IsEqualTo(right);

        app.SimulateKey(Ctrl(ConsoleKey.LeftArrow));
        await Assert.That(app.FocusedPaneId).IsEqualTo(left);
    }

    /// <summary>
    /// A stacked split: ⌃↓ and ⌃↑ walk it. Driven through ⌃B - so the geometry is the one the app really
    /// makes rather than a layout posed by a test.
    /// </summary>
    [Test]
    public async Task CtrlUpAndDownWalkAStackedSplit()
    {
        var app = App();
        app.RenderSnapshot();
        app.SimulatePrefixedKey(Plain('-', ConsoleKey.Subtract));
        app.RenderNextFrame();

        await Assert.That(app.PaneIds.Count).IsEqualTo(2);
        var top = app.FocusedPaneId;
        var bottom = app.PaneIds.Single(id => id != top);

        app.SimulateKey(Ctrl(ConsoleKey.DownArrow));
        await Assert.That(app.FocusedPaneId).IsEqualTo(bottom);

        app.SimulateKey(Ctrl(ConsoleKey.UpArrow));
        await Assert.That(app.FocusedPaneId).IsEqualTo(top);
    }

    /// <summary>
    /// The axis a split did not use has no neighbour, and the key <b>reports</b> rather than doing nothing.
    /// A navigation key that changes nothing and says nothing is indistinguishable from one that is not
    /// bound, which is exactly how the ⌃B prefix read before it learned to refuse out loud.
    /// </summary>
    [Test]
    [Arguments(ConsoleKey.UpArrow, "no pane that way")]
    [Arguments(ConsoleKey.DownArrow, "already has the keyboard")]
    public async Task TheNoNeighbourEdgeReportsInsteadOfDoingNothing(ConsoleKey key, string says)
    {
        var app = App();
        app.RenderSnapshot("split"); // side by side, so neither pane has one above or below
        var before = app.FocusedPaneId;

        app.SimulateKey(Ctrl(key));

        await Assert.That(app.FocusedPaneId).IsEqualTo(before);

        // ⌃↓ is the one direction whose refusal is worth wording differently: below the panes are the
        // command lines, and with only one of them the useful thing to say is that it already has the
        // keyboard — which teaches the focus pin rather than leaving the user wondering.
        await Assert.That(app.StatusMarkup).Contains(says);
    }

    /// <summary>
    /// On a single-pane workspace every one of these keys is a legitimate no-op, so all four say so — and
    /// what they say points at the keys that would make a second pane.
    /// </summary>
    [Test]
    [Arguments(ConsoleKey.LeftArrow)]
    [Arguments(ConsoleKey.RightArrow)]
    [Arguments(ConsoleKey.UpArrow)]
    [Arguments(ConsoleKey.DownArrow)]
    public async Task OnOnePaneEveryDirectionReportsAndPointsAtTheSplitKeys(ConsoleKey key)
    {
        var app = App();
        app.RenderSnapshot();

        await Assert.That(app.PaneIds.Count).IsEqualTo(1);
        app.SimulateKey(Ctrl(key));

        await Assert.That(app.StatusMarkup).Contains("one pane");
        await Assert.That(app.StatusMarkup).Contains("⌃B");
    }

    /// <summary>A zoomed pane hides the others, so there is nowhere to go and the refusal says why.</summary>
    [Test]
    public async Task AZoomedPaneRefusesAndSaysItIsZoomed()
    {
        var app = App();
        app.RenderSnapshot("split");
        app.SimulatePrefixedKey(Plain('z', ConsoleKey.Z));
        app.RenderNextFrame();

        app.SimulateKey(Ctrl(ConsoleKey.RightArrow));

        await Assert.That(app.StatusMarkup).Contains("zoomed");
    }

    /// <summary>
    /// The bars are the bottom of the same vertical ladder: ⌃↓ off the last pane arms the second command
    /// line, and ⌃↑ comes back up out of it. This is the "between input / panes" half of the request, and
    /// it needs no third piece of state — "which bar is armed" is a fact the app already keeps.
    /// </summary>
    [Test]
    public async Task CtrlDownReachesTheSecondBarAndCtrlUpLeavesIt()
    {
        var app = App();
        app.RenderSnapshot("focus"); // one pane per side, second bar up, primary armed

        await Assert.That(app.SecondBarShown).IsTrue();
        await Assert.That(app.SecondBarArmed).IsFalse();

        app.SimulateKey(Ctrl(ConsoleKey.DownArrow)); // no pane below this one
        await Assert.That(app.SecondBarArmed).IsTrue();

        app.SimulateKey(Ctrl(ConsoleKey.UpArrow));
        await Assert.That(app.SecondBarArmed).IsFalse();
    }

    /// <summary>
    /// With a single command line ⌃↓ at the bottom has nowhere to go, and what it says is the useful
    /// truth: the command line already has the keyboard. That is the honest answer given the focus pin,
    /// and it teaches it rather than leaving the user to wonder why nothing moved.
    /// </summary>
    [Test]
    public async Task WithOneBarCtrlDownSaysTheCommandLineAlreadyHasTheKeyboard()
    {
        var app = App();
        app.RenderSnapshot("split"); // two panes, so the refusal is about the bars and not about splitting

        await Assert.That(app.SecondBarShown).IsFalse();
        app.SimulateKey(Ctrl(ConsoleKey.DownArrow));

        await Assert.That(app.StatusMarkup).Contains("already has the keyboard");
    }

    /// <summary>
    /// <b>The pin is not weakened.</b> Moving focus anywhere leaves the keyboard on the armed command
    /// line, which is what makes paste, the caret and "which bar ⏎ sends from" one fact. That pin is
    /// today's fix for a real paste bug, and directional navigation must not be the thing that undoes it.
    /// </summary>
    [Test]
    public async Task MovingFocusLeavesTheKeyboardOnTheArmedCommandLine()
    {
        var app = App();
        app.RenderSnapshot("split");

        foreach (var key in new[]
        {
            ConsoleKey.RightArrow, ConsoleKey.LeftArrow, ConsoleKey.UpArrow, ConsoleKey.DownArrow,
        })
        {
            app.SimulateKey(Ctrl(key));
            app.RenderNextFrame();
            await Assert.That(app.ArmedBarHasFocus).IsTrue();
        }
    }

    /// <summary>
    /// And typing still lands in the command line after navigating — the point of not moving keyboard
    /// focus into a pane. A MU* client that lost a half-typed line to a navigation key would be worse for
    /// the feature.
    /// </summary>
    [Test]
    public async Task TypingAfterNavigatingStillReachesTheCommandLine()
    {
        var app = App();
        app.RenderSnapshot("split");

        app.SimulateKey(Ctrl(ConsoleKey.RightArrow));
        Type(app, "hail");

        await Assert.That(app.ArmedInputText).IsEqualTo("hail");
    }

    /// <summary>
    /// The command line no longer eats these. ⌃← used to be word movement there, and
    /// <c>TryRecallKey</c> ignores modifiers entirely — which is how Shift+↑ was once swallowed by history
    /// recall. Neither happens: the draft is untouched and the caret has not moved.
    /// </summary>
    [Test]
    public async Task TheCommandLineDoesNotSwallowTheFocusKeys()
    {
        // One pane on purpose: every focus key is a refusal here, so if the command line ever saw one the
        // damage would show. On a split the drafts are per window, and a legitimate draft swap on arrival
        // in the other pane would mask exactly the change this is looking for.
        var app = App();
        app.RenderSnapshot();
        Type(app, "say hello world");

        var before = app.ArmedInputText;
        foreach (var key in new[]
        {
            ConsoleKey.LeftArrow, ConsoleKey.RightArrow, ConsoleKey.UpArrow, ConsoleKey.DownArrow,
        })
        {
            app.SimulateKey(Ctrl(key));
        }

        // Nothing was typed, nothing recalled, and the line is the one that was there.
        await Assert.That(app.ArmedInputText).IsEqualTo(before);
    }

    /// <summary>
    /// ⌃O and a directional move end in the same place through the same path, so the two ways of changing
    /// pane focus cannot come to mean different things — including the repaint, which ⌃O did not do before.
    /// </summary>
    [Test]
    public async Task CyclingAndMovingAgreeAboutTheIndicator()
    {
        var app = App();
        app.RenderSnapshot("split");
        var first = app.FocusedPaneId;
        var (focused, _) = app.PaneBandColors;
        var rects = app.PaneOutputRects();

        app.DispatchCommand("layout:cycle");
        var cycled = app.RenderWholeFrame();
        await Assert.That(app.FocusedPaneId).IsNotEqualTo(first);
        await Assert.That(FrameGrid.CellsPaintedIn(cycled, rects[app.FocusedPaneId], focused)).IsGreaterThan(0);
        await Assert.That(FrameGrid.CellsPaintedIn(cycled, rects[first], focused)).IsEqualTo(0);
    }

    // --- item 2: word movement moved rather than vanished ------------------------------------------

    /// <summary>
    /// Word movement is Alt+←/→ now. Giving ⌃←/→ to pane navigation cost the command line a chord, and
    /// this is the assertion that it was respelt rather than dropped: Alt+← from the end of a line moves
    /// the caret back over one word, exactly as ⌃← used to.
    /// </summary>
    [Test]
    public async Task WordMovementIsOnAltArrowsNow()
    {
        var app = App();
        app.RenderSnapshot();
        Type(app, "say hello world");

        app.SimulateKey(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, true, false));
        app.SimulateKey(new ConsoleKeyInfo('!', ConsoleKey.NoName, false, false, false));

        // The caret landed at the start of "world", so the bang goes in front of it.
        await Assert.That(app.ArmedInputText).IsEqualTo("say hello !world");
    }

    // --- item 3: Alt+Enter ------------------------------------------------------------------------

    /// <summary>
    /// <b>Alt+⏎ inserts a newline.</b> The terminal splits it into Escape then Enter — the framework's
    /// parser emits ESC followed by a control byte as two key events — so both halves are driven here,
    /// in order, exactly as the reader dispatches them.
    /// </summary>
    [Test]
    public async Task AltEnterInsertsANewlineRatherThanSending()
    {
        var app = App();
        app.RenderSnapshot();
        Type(app, "one");

        app.SimulateKey(Plain('\x1b', ConsoleKey.Escape));
        app.SimulateKey(Plain('\r', ConsoleKey.Enter));
        foreach (var c in "two")
        {
            app.SimulateKey(Plain(c, ConsoleKey.NoName));
        }

        await Assert.That(app.ArmedInputText).IsEqualTo("one\ntwo");
    }

    /// <summary>
    /// A bare ⏎ still sends — the one binding a MU* client may not get wrong. Nothing about reassembling
    /// Alt+⏎ may put a condition in front of that.
    /// </summary>
    [Test]
    public async Task APlainEnterStillSends()
    {
        var app = App();
        app.RenderSnapshot();
        Type(app, "look");

        app.SimulateKey(Plain('\r', ConsoleKey.Enter));

        await Assert.That(app.ArmedInputText).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// A lone Escape is still nothing, and does not arm a newline for ever. Only the Enter that follows it
    /// <em>immediately</em> is the other half of an Alt+⏎; a later one sends, which is what makes "Esc did
    /// nothing, then I sent my line" behave as it always did.
    /// </summary>
    [Test]
    public async Task AnEscapeFollowedByAnotherKeyDoesNotArmANewline()
    {
        var app = App();
        app.RenderSnapshot();
        Type(app, "hi");

        app.SimulateKey(Plain('\x1b', ConsoleKey.Escape));
        app.SimulateKey(Plain('!', ConsoleKey.NoName)); // anything at all forgets the Escape
        app.SimulateKey(Plain('\r', ConsoleKey.Enter));

        // The Enter sent the line rather than adding a row to it.
        await Assert.That(app.ArmedInputText).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// ⌃L is the second spelling and still works — the chord that needs no reassembly, kept because it
    /// arrives whole on every host.
    /// </summary>
    [Test]
    public async Task CtrlLStillInsertsANewline()
    {
        var app = App();
        app.RenderSnapshot();
        Type(app, "a");
        app.SimulateKey(Ctrl(ConsoleKey.L));
        app.SimulateKey(Plain('b', ConsoleKey.NoName));

        await Assert.That(app.ArmedInputText).IsEqualTo("a\nb");
    }

    /// <summary>
    /// The ⌃P surface's newline entry makes the same edit the chord does, so the surface cannot advertise
    /// a key and then do something else when you pick the entry beside it.
    /// </summary>
    [Test]
    public async Task TheSurfacesNewlineEntryMakesTheSameEdit()
    {
        var app = App();
        app.RenderSnapshot();
        Type(app, "a");
        app.DispatchCommand("term:newline");
        app.SimulateKey(Plain('b', ConsoleKey.NoName));

        await Assert.That(app.ArmedInputText).IsEqualTo("a\nb");
    }
}
