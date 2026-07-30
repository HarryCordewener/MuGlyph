using System.Text.RegularExpressions;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Events;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// Whether an output pane can be scrolled — and whether it shows its newest line at all.
/// <para>
/// The absence of this file is what let the defect ship. Every output pane was a bare
/// <c>MarkupControl</c>, which paints its rows from index 0 downwards until it runs out of box and has
/// no scroll offset and no bottom anchoring of its own (<c>MarkupControl.PaintDOM</c>). So the instant a
/// session produced more lines than the pane had rows, the client showed its <em>oldest</em> screenful
/// for the rest of the run and every arriving line landed off the bottom, invisible — and nothing
/// noticed, because the demo scene is six lines and every existing snapshot view fits in a pane.
/// </para>
/// <para>
/// These therefore assert two things and never the arithmetic between them: what the framework
/// <em>arranged</em> (a viewport's child at its full content height, pushed up out of the box) and what
/// the frame <em>painted</em> (which numbered lines are on screen). A pane that cannot scroll passes any
/// amount of offset arithmetic while showing line one.
/// </para>
/// </summary>
/// <remarks>
/// Serialised for the same reason <see cref="PaneDragEndToEndTests"/> is: rendering a frame redirects the
/// process-global <c>Console.Out</c>, and the harness redirects <c>Console.In</c>.
/// </remarks>
[NotInParallel]
public class OutputScrollbackTests
{
    private const int Width = 120;
    private const int Height = 32;

    /// <summary>The window the demo scene's own output lands in.</summary>
    private const string Main = "main";

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    private static SharpMUTermApp App()
    {
        // The window system reads the console for input even headless; a null reader returns EOF.
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(Width, Height));
    }

    /// <summary>
    /// The demo app with a session bound to the main window but no socket under it, so
    /// <c>WorldSession.PrintSystem</c> drives the app's real line handler (and so its unread badging).
    /// Logging is disabled first: opening the demo character's HTML log would write a file into the
    /// user's config directory, which a test has no business doing.
    /// </summary>
    private static (SharpMUTermApp App, WorldSession Session) Bound()
    {
        Console.SetIn(TextReader.Null);
        var config = DemoScene.Build();

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        var session = app.BindWorldWithoutConnecting(config.Worlds[0]);
        return (app, session);
    }

    private static ConsoleKeyInfo Chord(ConsoleKey key, bool ctrl = false, bool shift = false) =>
        new('\0', key, shift, false, ctrl);

    /// <summary>
    /// The frame as rows of text. Walked the way a terminal walks it — the cursor-addressing moves the
    /// write position, everything printable lands where it points — because the only trustworthy answer
    /// to "is that line on screen" is the cells the driver actually emitted.
    /// </summary>
    private static string[] Rows(string ansi)
    {
        var cells = new char[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                cells[y, x] = ' ';
            }
        }

        var (row, column) = (0, 0);
        foreach (Match token in Regex.Matches(ansi, @"\x1b\[([0-9;]*)([A-Za-z])|([^\x1b\r\n])"))
        {
            if (token.Groups[3].Success)
            {
                if (row < Height && column < Width)
                {
                    cells[row, column] = token.Groups[3].Value[0];
                }

                column++;
                continue;
            }

            if (token.Groups[2].Value == "H")
            {
                var at = token.Groups[1].Value.Split(';');
                row = at[0].Length > 0 ? int.Parse(at[0]) - 1 : 0;
                column = at.Length > 1 && at[1].Length > 0 ? int.Parse(at[1]) - 1 : 0;
            }
        }

        var lines = new string[Height];
        for (var y = 0; y < Height; y++)
        {
            var buffer = new char[Width];
            for (var x = 0; x < Width; x++)
            {
                buffer[x] = cells[y, x];
            }

            lines[y] = new string(buffer);
        }

        return lines;
    }

    /// <summary>
    /// The frame a following viewport has <em>settled</em> on, as rows of text. Two frames, because
    /// auto-scroll moves the offset during paint — the children of the frame that discovers new content
    /// were already arranged at the old offset — and then asks for the relayout that puts them right. A
    /// terminal spends one frame getting there and nobody sees it; a test that renders once reads the
    /// stale frame and concludes the tail is not being followed.
    /// </summary>
    private static string[] SettledRows(SharpMUTermApp app)
    {
        app.RenderWholeFrame();
        return Rows(app.RenderWholeFrame());
    }

    /// <summary>The plain text a numbered scene line paints as — what to look for in a frame.</summary>
    private static string SceneText(int number) => $"line {number:0000}";

    private static bool Shows(string[] rows, int number) =>
        rows.Any(r => r.Contains(SceneText(number), StringComparison.Ordinal));

    /// <summary>Every scene line number visible in a frame, in screen order.</summary>
    private static List<int> VisibleLines(string[] rows)
    {
        var found = new List<int>();
        foreach (var row in rows)
        {
            var match = Regex.Match(row, @"line (\d{4})");
            if (match.Success)
            {
                found.Add(int.Parse(match.Groups[1].Value));
            }
        }

        return found;
    }

    // --- the bug ---------------------------------------------------------------------------------

    /// <summary>
    /// The one that would have caught it: with far more output than the pane can hold, the newest line
    /// is on screen and the oldest is not.
    /// <para>
    /// Under the old code this reads the other way round exactly — <c>line 0001</c> painted, the tail
    /// absent — because a bare markup control starts painting at its first row and stops when the box
    /// runs out. The arranged bounds say why it is now right rather than coincidentally right: the
    /// viewport arranges its child at its <em>full</em> content height and a top edge above the pane, and
    /// clips it, which is the shape of a scrolled viewport and is impossible for a control that fills its
    /// box.
    /// </para>
    /// </summary>
    [Test]
    public async Task TheNewestLineIsOnScreenWhenTheBufferOutgrowsThePane()
    {
        var app = App();
        var rows = Rows(app.RenderSnapshot("scrollback"));
        var last = SharpMUTermApp.ScrollbackSceneLines;

        // Painted cells: the tail is there, the head is long gone.
        await Assert.That(Shows(rows, last)).IsTrue();
        await Assert.That(Shows(rows, last - 1)).IsTrue();
        await Assert.That(Shows(rows, 1)).IsFalse();

        // And what is on screen is a contiguous run ending at the newest line — a tail, not a sample.
        var visible = VisibleLines(rows);
        await Assert.That(visible.Count).IsGreaterThan(10);
        await Assert.That(visible[^1]).IsEqualTo(last);
        await Assert.That(visible).IsEquivalentTo(Enumerable.Range(visible[0], visible.Count).ToList());

        // Arranged bounds: the child is taller than the pane and its top is above it.
        var view = app.ScrollbackOf(Main)!.Value;
        var content = app.OutputContentBounds(Main)!.Value;
        var pane = OutputRectOf(app, Main);

        await Assert.That(content.Height).IsEqualTo(view.ContentRows);
        await Assert.That(content.Height).IsGreaterThan(view.ViewportRows);
        await Assert.That(content.Y).IsLessThan(pane.Y);
        await Assert.That(pane.Y - content.Y).IsEqualTo(view.Offset);

        // The viewport itself agrees it is at the end of its content and still attached to the tail.
        await Assert.That(view.AutoScroll).IsTrue();
        await Assert.That(view.CanScrollUp).IsTrue();
        await Assert.That(view.CanScrollDown).IsFalse();
    }

    /// <summary>
    /// A pane that fits its content is not scrolled, and its child is arranged inside the pane rather
    /// than above it. The counterpart to the test above: the fix must not push a six-line room off the
    /// top of the window, which is exactly what a bottom-anchoring hand-rolled offset would have done.
    /// </summary>
    [Test]
    public async Task AShortBufferSitsAtTheTopOfThePaneUnscrolled()
    {
        var app = App();
        var rows = Rows(app.RenderSnapshot());

        await Assert.That(rows.Any(r => r.Contains("The Grand Plaza", StringComparison.Ordinal))).IsTrue();

        var view = app.ScrollbackOf(Main)!.Value;
        var content = app.OutputContentBounds(Main)!.Value;
        var pane = OutputRectOf(app, Main);

        await Assert.That(view.Offset).IsEqualTo(0);
        await Assert.That(view.CanScrollUp).IsFalse();
        await Assert.That(view.CanScrollDown).IsFalse();
        await Assert.That(content.Y).IsEqualTo(pane.Y);
    }

    // --- the keys --------------------------------------------------------------------------------

    /// <summary>
    /// PgUp moves back through the scrollback and PgDn comes forward again, with the frame showing an
    /// earlier region of the buffer and then the tail. Driven through <see cref="SharpMUTermApp.SimulateKey"/>
    /// — the handler the window's <c>PreviewKeyPressed</c> raises — because the panel never receives a key
    /// of its own: this app pins focus to the armed command line, and the panel's <c>ProcessKey</c>
    /// returns false when it does not have focus.
    /// </summary>
    [Test]
    public async Task PageUpShowsAnEarlierRegionAndPageDownComesBack()
    {
        var app = App();
        app.RenderSnapshot("scrollback");
        var bottom = app.ScrollbackOf(Main)!.Value;

        app.SimulateKey(Chord(ConsoleKey.PageUp));
        var up = app.ScrollbackOf(Main)!.Value;
        var upRows = Rows(app.RenderWholeFrame());

        await Assert.That(up.Offset).IsLessThan(bottom.Offset);
        await Assert.That(up.AutoScroll).IsFalse(); // detached: new output no longer drags the view
        await Assert.That(up.CanScrollDown).IsTrue();
        await Assert.That(Shows(upRows, SharpMUTermApp.ScrollbackSceneLines)).IsFalse();

        // A page keeps a couple of rows of overlap, so the jump is a page less that.
        var moved = bottom.Offset - up.Offset;
        await Assert.That(moved).IsEqualTo(up.ViewportRows - 2);

        app.SimulateKey(Chord(ConsoleKey.PageDown));
        var back = app.ScrollbackOf(Main)!.Value;
        var backRows = Rows(app.RenderWholeFrame());

        await Assert.That(back.Offset).IsEqualTo(bottom.Offset);
        await Assert.That(back.AutoScroll).IsTrue(); // re-attached on reaching the bottom
        await Assert.That(Shows(backRows, SharpMUTermApp.ScrollbackSceneLines)).IsTrue();
    }

    /// <summary>Shift+↑/↓ move one line — the fine adjustment a page key cannot make.</summary>
    [Test]
    public async Task ShiftArrowsMoveOneLine()
    {
        var app = App();
        app.RenderSnapshot("scrollback");
        var bottom = app.ScrollbackOf(Main)!.Value;

        app.SimulateKey(Chord(ConsoleKey.UpArrow, shift: true));
        await Assert.That(app.ScrollbackOf(Main)!.Value.Offset).IsEqualTo(bottom.Offset - 1);

        app.SimulateKey(Chord(ConsoleKey.DownArrow, shift: true));
        await Assert.That(app.ScrollbackOf(Main)!.Value.Offset).IsEqualTo(bottom.Offset);
    }

    /// <summary>
    /// ⌃Home reaches the very first line — the oldest thing the buffer still holds.
    /// </summary>
    [Test]
    public async Task CtrlHomeReachesTheOldestLine()
    {
        var app = App();
        app.RenderSnapshot("scrollback");

        app.SimulateKey(Chord(ConsoleKey.Home, ctrl: true));
        var rows = Rows(app.RenderWholeFrame());

        await Assert.That(app.ScrollbackOf(Main)!.Value.Offset).IsEqualTo(0);
        await Assert.That(Shows(rows, 1)).IsTrue();
        await Assert.That(rows.Any(r => r.Contains("The Grand Plaza", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// ⌃End does not merely jump to the bottom, it goes back to <em>following</em> it: output arriving
    /// afterwards is on screen. A one-shot scroll to the end would pass a "the newest line is visible"
    /// check and then walk away from the reader on the very next line, which is the failure this
    /// asserts against by appending after the key rather than before it.
    /// </summary>
    [Test]
    public async Task CtrlEndGoesBackToFollowingTheLiveTail()
    {
        var app = App();
        app.RenderSnapshot("scrollback");
        app.SimulateKey(Chord(ConsoleKey.PageUp));

        // While detached, new output is not shown — not even after the frame a following pane would
        // have settled on.
        app.LoadLongScene(Main, 1, first: 9001);
        await Assert.That(Shows(SettledRows(app), 9001)).IsFalse();

        app.SimulateKey(Chord(ConsoleKey.End, ctrl: true));
        await Assert.That(Shows(Rows(app.RenderWholeFrame()), 9001)).IsTrue();
        await Assert.That(app.ScrollbackOf(Main)!.Value.AutoScroll).IsTrue();

        // And it keeps following: the next line arrives on screen without another keystroke.
        app.LoadLongScene(Main, 1, first: 9002);
        await Assert.That(Shows(SettledRows(app), 9002)).IsTrue();
    }

    /// <summary>
    /// The scroll keys are claimed by the app before they reach the command line, and the keys the
    /// command line needs are not. ⌃Home/⌃End are the app's; bare Home/End stay the caret's.
    /// </summary>
    [Test]
    public async Task OnlyTheScrollbackChordsAreClaimed()
    {
        var app = App();
        app.RenderSnapshot("scrollback");

        await Assert.That(app.SimulateScrollKey(Chord(ConsoleKey.PageUp))).IsTrue();
        await Assert.That(app.SimulateScrollKey(Chord(ConsoleKey.PageDown))).IsTrue();
        await Assert.That(app.SimulateScrollKey(Chord(ConsoleKey.Home, ctrl: true))).IsTrue();
        await Assert.That(app.SimulateScrollKey(Chord(ConsoleKey.End, ctrl: true))).IsTrue();
        await Assert.That(app.SimulateScrollKey(Chord(ConsoleKey.UpArrow, shift: true))).IsTrue();

        await Assert.That(app.SimulateScrollKey(Chord(ConsoleKey.Home))).IsFalse();
        await Assert.That(app.SimulateScrollKey(Chord(ConsoleKey.End))).IsFalse();
        await Assert.That(app.SimulateScrollKey(Chord(ConsoleKey.UpArrow))).IsFalse();
        await Assert.That(app.SimulateScrollKey(Chord(ConsoleKey.DownArrow))).IsFalse();
        await Assert.That(app.SimulateScrollKey(new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false))).IsFalse();
    }

    /// <summary>
    /// Typing still reaches the command line while the pane is scrolled back. The scroll keys are routed
    /// from the same handler that routes typing, so getting the order wrong there is how a client ends up
    /// unable to type after pressing PgUp.
    /// </summary>
    [Test]
    public async Task TypingStillReachesTheCommandLineWhileScrolledBack()
    {
        var app = App();
        app.RenderSnapshot("scrollback");
        app.SimulateKey(Chord(ConsoleKey.PageUp));
        app.SimulateKey(new ConsoleKeyInfo('!', ConsoleKey.None, false, false, false));

        var rows = Rows(app.RenderWholeFrame());
        await Assert.That(rows.Any(r => r.Contains("say hello there!", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// A pane whose content fits keeps following its output even after a scroll key. ⌃Home on a
    /// six-line room has nowhere to go, and detaching it anyway would leave that window refusing to
    /// follow its own output the moment it grew past the pane — a keystroke that looked like a no-op and
    /// silently reintroduced the bug this whole change is about.
    /// </summary>
    [Test]
    public async Task ScrollKeysOnAPaneThatFitsLeaveItFollowing()
    {
        var app = App();
        app.RenderSnapshot();

        app.SimulateKey(Chord(ConsoleKey.Home, ctrl: true));
        app.SimulateKey(Chord(ConsoleKey.PageUp));
        app.SimulateKey(Chord(ConsoleKey.UpArrow, shift: true));

        await Assert.That(app.ScrollbackOf(Main)!.Value.AutoScroll).IsTrue();
        await Assert.That(app.ScrollbackOf(Main)!.Value.Offset).IsEqualTo(0);

        // …and it proves it by following: output past the pane's height still shows its newest line.
        app.LoadLongScene(Main, SharpMUTermApp.ScrollbackSceneLines);
        await Assert.That(Shows(SettledRows(app), SharpMUTermApp.ScrollbackSceneLines)).IsTrue();
    }

    /// <summary>
    /// The command surface offers the same two moves and teaches their keys, and offers the way back only
    /// when there is somewhere to come back from. It is where this client is discovered from; every other
    /// thing you can do to an output window is listed there.
    /// </summary>
    [Test]
    public async Task TheCommandSurfaceOffersTheScrollbackMoves()
    {
        var app = App();
        app.RenderSnapshot("scrollback");

        var atLive = app.BuildCatalog();
        var oldest = atLive.Single(c => c.Id == "term:scroll-oldest");
        await Assert.That(oldest.Title).IsEqualTo("Scroll to oldest");
        await Assert.That(oldest.Subtitle).Contains("PgUp");
        await Assert.That(atLive.Any(c => c.Id == "term:scroll-live")).IsFalse();

        app.SimulateKey(Chord(ConsoleKey.PageUp));
        var scrolled = app.BuildCatalog();
        var live = scrolled.Single(c => c.Id == "term:scroll-live");
        await Assert.That(live.Title).IsEqualTo("Back to live output");
        await Assert.That(live.Subtitle).IsEqualTo("⌃End");

        // And dispatching it does what the key does.
        app.DispatchCommand("term:scroll-live");
        await Assert.That(app.ScrollbackOf(Main)!.Value.AutoScroll).IsTrue();

        app.DispatchCommand("term:scroll-oldest");
        await Assert.That(app.ScrollbackOf(Main)!.Value.Offset).IsEqualTo(0);
    }

    /// <summary>
    /// The wheel scrolls the pane the pointer is over, and rolling forward again re-attaches it. Driven
    /// through the headless driver's real mouse frame, which is possible only because the wheel is routed
    /// from the driver like the rest of this app's mouse layer — a route through the framework's input
    /// pump exists only inside <c>Run()</c> and could not be asserted at all.
    /// </summary>
    [Test]
    public async Task TheWheelScrollsThePaneUnderThePointer()
    {
        Console.SetIn(TextReader.Null);
        var driver = new HeadlessConsoleDriver(Width, Height);
        var app = new SharpMUTermApp(DemoScene.Build(), Headless, driver);
        app.RenderSnapshot("scrollback");
        var bottom = app.ScrollbackOf(Main)!.Value;

        var rect = OutputRectOf(app, Main);
        var pointer = new System.Drawing.Point(rect.X + 5, rect.Y + 3);

        driver.SimulateMouseEvent(new List<MouseFlags> { MouseFlags.WheeledUp }, pointer);
        var up = app.ScrollbackOf(Main)!.Value;
        await Assert.That(up.Offset).IsEqualTo(bottom.Offset - 3);
        await Assert.That(up.AutoScroll).IsFalse();
        await Assert.That(Shows(SettledRows(app), SharpMUTermApp.ScrollbackSceneLines)).IsFalse();

        driver.SimulateMouseEvent(new List<MouseFlags> { MouseFlags.WheeledDown }, pointer);
        var down = app.ScrollbackOf(Main)!.Value;
        await Assert.That(down.Offset).IsEqualTo(bottom.Offset);
        await Assert.That(down.AutoScroll).IsTrue();

        // Outside every pane, the wheel is nobody's.
        driver.SimulateMouseEvent(new List<MouseFlags> { MouseFlags.WheeledUp }, new System.Drawing.Point(0, 0));
        await Assert.That(app.ScrollbackOf(Main)!.Value.Offset).IsEqualTo(bottom.Offset);
    }

    // --- the badge -------------------------------------------------------------------------------

    /// <summary>
    /// Output arriving while the reader is up in their scrollback badges the tab unread, exactly as
    /// output arriving on a tab they are not looking at does — the same single count, not a second
    /// notion of "there is more". Coming back to the bottom is catching up, and clears it.
    /// </summary>
    [Test]
    public async Task OutputArrivingWhileScrolledBackBadgesTheTab()
    {
        var (app, session) = Bound();
        app.LoadLongScene(Main, SharpMUTermApp.ScrollbackSceneLines);
        app.RenderNextFrame();
        app.RenderNextFrame(); // auto-scroll settles on the second frame; see SettleScroll

        await Assert.That(app.UnreadOf(Main)).IsEqualTo(0);

        // Caught up: a line arriving into the visible, live tail badges nothing.
        session.PrintSystem("*** while the reader is looking");
        await Assert.That(app.UnreadOf(Main)).IsEqualTo(0);

        app.SimulateKey(Chord(ConsoleKey.PageUp));
        session.PrintSystem("*** while the reader is not");
        session.PrintSystem("*** and again");

        await Assert.That(app.UnreadOf(Main)).IsEqualTo(2);

        // …and the badge is on the tab strip, which is the row a reader would see it on.
        await Assert.That(SettledRows(app)[1]).Contains("(2)");

        app.SimulateKey(Chord(ConsoleKey.End, ctrl: true));
        await Assert.That(app.UnreadOf(Main)).IsEqualTo(0);
    }

    /// <summary>
    /// The status row says the pane is not showing its newest line, and says which key gets back to it.
    /// It is the only sign on screen — the panes carry no scrollbar on purpose — so it has to be there.
    /// </summary>
    [Test]
    public async Task TheStatusRowReportsTheScrollbackPosition()
    {
        var app = App();
        app.RenderSnapshot("scrollback");
        await Assert.That(app.StatusMarkup).DoesNotContain("scrollback");

        app.SimulateKey(Chord(ConsoleKey.PageUp));
        await Assert.That(app.StatusMarkup).Contains("scrollback");
        await Assert.That(app.StatusMarkup).Contains("⌃End");
        await Assert.That(Rows(app.RenderWholeFrame())
            .Any(r => r.Contains($"{Glyphs.Scrollback} scrollback", StringComparison.Ordinal))).IsTrue();

        app.SimulateKey(Chord(ConsoleKey.End, ctrl: true));
        await Assert.That(app.StatusMarkup).DoesNotContain("scrollback");
    }

    // --- composing with freeze -------------------------------------------------------------------

    /// <summary>
    /// A frozen pane's pinned half scrolls, and its live tail keeps following. Freeze holds a region
    /// still above the bar and leaves the tail live below it; before this the pinned half could only ever
    /// show its oldest screenful, which made ⌃F a way of pinning history you could not read.
    /// </summary>
    [Test]
    public async Task AFrozenPanesPinnedHalfScrollsWhileItsTailStaysLive()
    {
        var app = App();
        var rows = Rows(app.RenderSnapshot("freeze-scrollback"));
        var frozen = app.FrozenScrollbackOf(Main)!.Value;
        var tail = app.ScrollbackOf(Main)!.Value;

        // The bar is on screen, so this really is the frozen split.
        await Assert.That(rows.Any(r => r.Contains("FROZEN", StringComparison.Ordinal))).IsTrue();

        // The pinned half holds the whole pre-freeze buffer, shows a slice of it, and has been paged up.
        await Assert.That(frozen.ContentRows).IsGreaterThan(frozen.ViewportRows);
        await Assert.That(frozen.CanScrollUp).IsTrue();
        await Assert.That(frozen.CanScrollDown).IsTrue();
        await Assert.That(frozen.AutoScroll).IsFalse();

        // The live tail below the bar is still following: its newest line is on screen.
        await Assert.That(tail.AutoScroll).IsTrue();
        await Assert.That(Shows(rows, SharpMUTermApp.ScrollbackSceneLines + 6)).IsTrue();

        // …and because it is, nothing arriving is unread: the reader can see where it lands.
        await Assert.That(app.UnreadOf(Main)).IsEqualTo(0);

        // ⌃End aims at the pinned half while frozen — that is the region worth moving through.
        app.SimulateKey(Chord(ConsoleKey.End, ctrl: true));
        await Assert.That(app.FrozenScrollbackOf(Main)!.Value.CanScrollDown).IsFalse();
        await Assert.That(app.FrozenScrollbackOf(Main)!.Value.AutoScroll).IsTrue();
    }

    /// <summary>
    /// A pane's scroll position survives the pane-area rebuild a split forces. The viewports are kept
    /// across rebuilds for this reason: <c>RemoveContent</c> disposes the old tree, and a disposed
    /// scrollable panel clears its children, so a viewport rebuilt with the pane would drop both the
    /// reader's position and the control it was showing.
    /// </summary>
    [Test]
    public async Task ScrollPositionSurvivesAPaneRebuild()
    {
        var app = App();
        app.RenderSnapshot("scrollback");
        app.SimulateKey(Chord(ConsoleKey.PageUp));
        var scrolled = app.ScrollbackOf(Main)!.Value;

        app.SimulatePrefixedKey(new ConsoleKeyInfo('|', ConsoleKey.None, false, false, false)); // ⌃B | — split
        var rows = Rows(app.RenderWholeFrame());
        var after = app.ScrollbackOf(Main)!.Value;

        await Assert.That(after.Offset).IsEqualTo(scrolled.Offset);
        await Assert.That(after.AutoScroll).IsFalse();

        // And the pane still has its content — a viewport that lost its child would paint an empty pane.
        await Assert.That(VisibleLines(rows).Count).IsGreaterThan(5);
    }

    private static PaneRect OutputRectOf(SharpMUTermApp app, string windowId)
    {
        var surface = app.PaneSnapshot();
        var paneId = surface.Rects.Keys.Single(id => surface.ActiveWindow(id) == windowId);
        return app.PaneOutputRects()[paneId];
    }
}
