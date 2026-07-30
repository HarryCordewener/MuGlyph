using System.Text.RegularExpressions;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// Alt+Shift+arrow, driven through the real app: a real window, a real frame, and the pane rectangles
/// the framework itself arranged.
/// <para>
/// The claim being checked is a claim about <em>cells</em> — "this pane is now one column wider" — and
/// it has three places to go wrong that a Core unit test cannot see. The chord may never reach the
/// handler (the scrollback keys and the command line are both downstream of it; Shift+↑/↓ is
/// scrollback's and Alt+←/→ is the command line's word movement, and neither looks at the other
/// modifier). The framework's grid may round its star weights somewhere other than
/// <see cref="LayoutSolver"/> does, so a border asked for one cell moves none or two. And the resize
/// re-reports NAWS to every server in the affected panes, so a held key is a flood aimed at someone
/// else's game unless it goes through the rate limit rather than around it. So these assert on arranged
/// bounds and on painted cells, and on the bytes a recording telnet transport received.
/// </para>
/// </summary>
/// <remarks>
/// Serialised for the same reason <see cref="PaneDragEndToEndTests"/> is: rendering a frame redirects the
/// process-global <c>Console.Out</c>, and the harness redirects <c>Console.In</c>.
/// </remarks>
[NotInParallel]
public class PaneResizeEndToEndTests
{
    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>The rate limit's window — how long a held-back NAWS size waits, at most.</summary>
    private static readonly TimeSpan Interval = SharpMUTermApp.WindowSizeReportInterval;

    private static SharpMUTermApp App(int width = 120, int height = 34)
    {
        // The window system reads the console for input even headless; a null reader returns EOF.
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(width, height));
    }

    private static (SharpMUTermApp App, ManualTimeProvider Clock) TimedApp(int width = 120, int height = 34)
    {
        Console.SetIn(TextReader.Null);
        var clock = new ManualTimeProvider();
        return (new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(width, height), clock),
            clock);
    }

    private static ConsoleKeyInfo Resize(ConsoleKey arrow) =>
        new('\0', arrow, shift: true, alt: true, control: false);

    private static ConsoleKeyInfo Ctrl(ConsoleKey key) => new('\0', key, false, false, true);

    private static ConsoleKeyInfo Plain(char c, ConsoleKey key) => new(c, key, false, false, false);

    /// <summary>Empties the armed command line and types <paramref name="text"/> into it, key by key.</summary>
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
    private static string ChatWindowId => Workspace.SpawnWindowId("Chat");

    /// <summary>Presses the chord and renders the frame it produces, the way the running app would.</summary>
    private static void Press(SharpMUTermApp app, ConsoleKey arrow)
    {
        app.SimulateKey(Resize(arrow));
        app.RenderNextFrame();
    }

    // --- the chord reaches the handler ----------------------------------------------------------

    /// <summary>
    /// <b>The chord is claimed before anything downstream can eat it.</b> Two things sit below it in
    /// <c>HandleWindowKey</c> and both would fail silently: the scrollback keys, which own Shift+↑/↓ and
    /// would make ⌥⇧↑ scroll the transcript, and the command line, which owns Alt+←/→ for word movement
    /// and looks at neither the Shift bit nor, before this, any modifier at all on the bare arrows.
    /// Rendered on the <c>scrollback</c> view — the only geometry with more output than a pane holds, so
    /// a scroll is observable at all — and against Shift+↑ itself, so the claim is that the chord is
    /// <em>distinct</em> from its neighbour rather than merely inert.
    /// </summary>
    [Test]
    public async Task TheChordNeitherTypesNorScrolls()
    {
        var app = App();
        app.RenderSnapshot("scrollback");

        app.SimulateKey(Ctrl(ConsoleKey.E));
        app.SimulateKey(Ctrl(ConsoleKey.U)); // a known-empty command line
        var resting = app.ScrollTargetView!.Value.Offset;

        // The neighbour it must not collapse onto really does scroll.
        app.SimulateKey(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, shift: true, alt: false, control: false));
        app.RenderNextFrame();
        await Assert.That(app.ScrollTargetView!.Value.Offset).IsNotEqualTo(resting);

        app.SimulateKey(Ctrl(ConsoleKey.End)); // back to live output
        app.RenderNextFrame();
        await Assert.That(app.ScrollTargetView!.Value.Offset).IsEqualTo(resting);

        foreach (var arrow in new[]
        {
            ConsoleKey.RightArrow, ConsoleKey.LeftArrow, ConsoleKey.DownArrow, ConsoleKey.UpArrow,
        })
        {
            app.SimulateKey(Resize(arrow));
            app.RenderNextFrame();

            await Assert.That(app.ArmedInputText)
                .IsEqualTo(string.Empty)
                .Because($"Alt+Shift+{arrow} must not be typed into the command line");
            await Assert.That(app.ScrollTargetView!.Value.Offset)
                .IsEqualTo(resting)
                .Because($"Alt+Shift+{arrow} must not reach the scrollback keys");
        }
    }

    /// <summary>
    /// <b>A command line with something typed in it must not eat the chord — and the caret must not
    /// move.</b> This is the maintainer's own situation and the assertion whose absence let the defect
    /// ship. <c>InputBarControl.ProcessKey</c> reached an <em>unguarded</em> arrow switch below its
    /// <c>alt</c> and <c>ctrl</c> blocks, so <c>Alt+Shift+←</c> was a plain caret move, claimed with
    /// <c>return true</c> — and <c>Move(Buffer.MoveLeft())</c> is true exactly when there is a character
    /// to the left of the caret. So the bug had a signature: the chord worked on an empty line and died
    /// on the first thing you typed. Both halves are pressed here, and the <em>same</em> resize is
    /// required of each, so a control that starts claiming modified arrows again fails on the pair rather
    /// than on a mood.
    /// <para>
    /// The caret is read off <see cref="SharpMUTermApp.CaretOnScreen"/> — the cell the driver was handed —
    /// rather than off the buffer's index, because "the caret did not move" is a claim about the screen.
    /// </para>
    /// </summary>
    [Test]
    [Arguments("")]
    [Arguments("look behind the altar")]
    public async Task TheChordResizesWhateverIsTypedAndNeverMovesTheCaret(string typed)
    {
        var app = App();
        app.RenderSnapshot("split");
        Type(app, typed);
        app.RenderNextFrame();
        await Assert.That(app.ArmedInputText).IsEqualTo(typed);

        var caret = app.CaretOnScreen();
        var before = app.PaneOutputRects()[app.FocusedPaneId];

        Press(app, ConsoleKey.LeftArrow);

        await Assert.That(app.PaneOutputRects()[app.FocusedPaneId].Width)
            .IsEqualTo(before.Width - PaneResize.StepCells)
            .Because($"⌥⇧← must narrow the pane with \"{typed}\" on the command line");
        await Assert.That(app.ArmedInputText)
            .IsEqualTo(typed)
            .Because("the chord is not text");
        await Assert.That(app.CaretOnScreen())
            .IsEqualTo(caret)
            .Because("the command line's caret must not have moved a cell");

        // And the way back, which is the arrow the caret would have moved *towards* if the bar had it.
        Press(app, ConsoleKey.RightArrow);
        await Assert.That(app.PaneOutputRects()[app.FocusedPaneId].Width).IsEqualTo(before.Width);
        await Assert.That(app.CaretOnScreen()).IsEqualTo(caret);
    }

    /// <summary>
    /// <b>One press is one cell.</b> Asserted as the literal number rather than through
    /// <see cref="PaneResize.StepCells"/>, and read off <em>painted</em> cells: every other arithmetic
    /// assertion in this file is written in terms of the constant, so all of them would follow the
    /// constant anywhere it went and none of them says what it should be. It was two.
    /// </summary>
    [Test]
    public async Task OnePressMovesThePaintedBorderByExactlyOneCell()
    {
        var app = App();
        app.RenderSnapshot("split");

        var (focused, _) = app.PaneBandColors;
        var rects = app.PaneOutputRects();
        var left = app.FocusedPaneId;
        var right = app.PaneIds.Single(id => id != left);
        var row = Math.Min(
            rects[left].Y + rects[left].Height, rects[right].Y + rects[right].Height) - 1;

        var before = ColumnsPainted(Grid(app.RenderWholeFrame()), row, Sgr(focused));
        Press(app, ConsoleKey.RightArrow);
        var after = ColumnsPainted(Grid(app.RenderWholeFrame()), row, Sgr(focused));

        await Assert.That(after - before).IsEqualTo(1).Because("one press moves the border one cell");
    }

    /// <summary>
    /// <b>The reported inversion, on painted cells, from both ends of a stacked split.</b> ⌥⇧↑ makes the
    /// focused pane taller whichever of the two it is — the arrow names what happens to <em>this</em>
    /// pane, and never which way the divider travels. Pressed from the bottom pane it used to do the
    /// opposite; a test that only exercised the top one could not tell the two rules apart, because on
    /// the top pane they agree.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task UpMakesTheFocusedPaneTallerFromEitherEndOfAStackedSplit(bool fromTheBottom)
    {
        var app = App();
        app.RenderSnapshot();
        app.SimulatePrefixedKey(Plain('-', ConsoleKey.Subtract)); // ⌃B - : stacked, top focused
        app.RenderNextFrame();

        if (fromTheBottom)
        {
            app.SimulateKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, control: true));
            app.RenderNextFrame();
        }

        var (focused, _) = app.PaneBandColors;
        var panes = app.PaneSnapshot().Rects;
        var mine = app.FocusedPaneId;
        var other = app.PaneIds.Single(id => id != mine);
        var column = Math.Min(
            panes[mine].X + panes[mine].Width, panes[other].X + panes[other].Width) - 1;

        var before = RowsPainted(Grid(app.RenderWholeFrame()), column, Sgr(focused));
        await Assert.That(before).IsEqualTo(panes[mine].Height);

        Press(app, ConsoleKey.UpArrow);

        await Assert.That(RowsPainted(Grid(app.RenderWholeFrame()), column, Sgr(focused)))
            .IsEqualTo(before + PaneResize.StepCells)
            .Because($"⌥⇧↑ on the {(fromTheBottom ? "bottom" : "top")} pane must make that pane taller");
        await Assert.That(app.PaneSnapshot().Rects[other].Height)
            .IsEqualTo(panes[other].Height - PaneResize.StepCells);
    }

    // --- the frames -----------------------------------------------------------------------------

    /// <summary>
    /// <b>The column boundary really moves, by the cells claimed.</b> Two frames of the same split, either
    /// side of one ⌥⇧→, decoded to cell grids: the left pane's band is one column wider and the right
    /// pane's is one narrower, in <em>painted</em> cells — and the arranged output rectangles agree,
    /// which is what the servers are told.
    /// </summary>
    [Test]
    public async Task OnePressMovesThePaintedColumnBoundaryByTheStep()
    {
        var app = App();
        app.RenderSnapshot("split");

        var (focused, unfocused) = app.PaneBandColors;
        var before = app.PaneOutputRects().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var left = app.FocusedPaneId;
        var right = app.PaneIds.Single(id => id != left);

        // The last row inside both panes' output areas: the bands sit side by side on it and neither has
        // text there, so a count of painted cells is the pane's width rather than the width less its
        // words. (The first output row would be a count of the gaps between them.)
        var row = Math.Min(
            before[left].Y + before[left].Height, before[right].Y + before[right].Height) - 1;

        var beforeFrame = Grid(app.RenderWholeFrame());
        var beforeLeft = ColumnsPainted(beforeFrame, row, Sgr(focused));
        var beforeRight = ColumnsPainted(beforeFrame, row, Sgr(unfocused));
        await Assert.That(beforeLeft).IsEqualTo(before[left].Width);
        await Assert.That(beforeRight).IsEqualTo(before[right].Width);

        Press(app, ConsoleKey.RightArrow);

        var afterFrame = Grid(app.RenderWholeFrame());
        var afterLeft = ColumnsPainted(afterFrame, row, Sgr(focused));
        var afterRight = ColumnsPainted(afterFrame, row, Sgr(unfocused));

        await Assert.That(afterLeft)
            .IsEqualTo(beforeLeft + PaneResize.StepCells)
            .Because("the focused pane's painted band is one column wider");
        await Assert.That(afterRight).IsEqualTo(beforeRight - PaneResize.StepCells);

        var after = app.PaneOutputRects();
        await Assert.That(after[left].Width).IsEqualTo(before[left].Width + PaneResize.StepCells);
        await Assert.That(after[right].Width).IsEqualTo(before[right].Width - PaneResize.StepCells);
        await Assert.That(after[right].X).IsEqualTo(before[right].X + PaneResize.StepCells);
    }

    /// <summary>
    /// The same read on a stacked split: ⌥⇧↑ makes the focused pane one row taller, counted in painted
    /// cells down a column that runs through both panes.
    /// </summary>
    [Test]
    public async Task OnePressMovesThePaintedRowBoundaryOnAStackedSplit()
    {
        var app = App();
        app.RenderSnapshot();
        app.SimulatePrefixedKey(Plain('-', ConsoleKey.Subtract)); // ⌃B - : stacked
        app.RenderNextFrame();

        var (focused, unfocused) = app.PaneBandColors;
        var before = app.PaneOutputRects().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var top = app.FocusedPaneId;
        var bottom = app.PaneIds.Single(id => id != top);

        // The last column inside both panes: no text reaches the right margin, so the count down it is
        // the pane's own height rather than its height less the rows whose lines happen to run that far.
        // Counted against the *whole* pane rectangle, tab strip included — a pane's plane is painted over
        // its strip too, which the horizontal read does not have to care about and this one does.
        var panes = app.PaneSnapshot().Rects;
        var column = Math.Min(
            before[top].X + before[top].Width, before[bottom].X + before[bottom].Width) - 1;

        var beforeTop = RowsPainted(Grid(app.RenderWholeFrame()), column, Sgr(focused));
        await Assert.That(beforeTop).IsEqualTo(panes[top].Height);

        Press(app, ConsoleKey.UpArrow);

        var afterFrame = Grid(app.RenderWholeFrame());
        await Assert.That(RowsPainted(afterFrame, column, Sgr(focused)))
            .IsEqualTo(beforeTop + PaneResize.StepCells)
            .Because("the focused pane's painted band is one row taller");
        await Assert.That(RowsPainted(afterFrame, column, Sgr(unfocused)))
            .IsEqualTo(panes[bottom].Height - PaneResize.StepCells);

        var after = app.PaneOutputRects();
        await Assert.That(after[top].Height).IsEqualTo(before[top].Height + PaneResize.StepCells);
        await Assert.That(after[bottom].Height).IsEqualTo(before[bottom].Height - PaneResize.StepCells);
        await Assert.That(after[bottom].Y).IsEqualTo(before[bottom].Y + PaneResize.StepCells);
    }

    /// <summary>
    /// Repeated presses keep moving it, and the reverse direction brings it back cell for cell — so the
    /// fractions the model keeps survive the round trip through the framework's own star rounding.
    /// </summary>
    [Test]
    public async Task FivePressesMoveFiveStepsAndFiveBackReturnIt()
    {
        var app = App(160, 40);
        app.RenderSnapshot("split");

        var left = app.FocusedPaneId;
        var start = app.PaneOutputRects()[left].Width;

        for (var press = 1; press <= 5; press++)
        {
            Press(app, ConsoleKey.RightArrow);
            await Assert.That(app.PaneOutputRects()[left].Width)
                .IsEqualTo(start + (press * PaneResize.StepCells))
                .Because($"press {press} must move the arranged border one more step");
        }

        for (var press = 0; press < 5; press++)
        {
            Press(app, ConsoleKey.LeftArrow);
        }

        await Assert.That(app.PaneOutputRects()[left].Width).IsEqualTo(start);
    }

    // --- what a resize must not touch -----------------------------------------------------------

    /// <summary>
    /// <b>Only the affected panes' rectangles move.</b> Three panes — two side by side over a third
    /// spanning the width — and a horizontal resize in the top row. The bottom pane's output rectangle is
    /// identical to the cell, because a server whose pane did not change must not be re-told its size.
    /// </summary>
    [Test]
    public async Task APaneOutsideTheResizedSplitKeepsItsRectangle()
    {
        var app = App(140, 40);
        ThreePanes(app);

        var top = app.FocusedPaneId;
        var before = app.PaneOutputRects().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        await Assert.That(before.Count).IsEqualTo(3);

        // The pane in the other row: the one spanning the width the other two share.
        // Identified by row, not by width. Width was the original test: "the pane wider than the focused
        // one" meant the full-width bottom pane, until the rail reserved cells for its draft pen and unread
        // count and left the pane area an odd 94 cells. The top row then splits 46/47, so the *sibling* —
        // the pane that pays for this very resize — is also wider than the focused one, and the predicate
        // matched two. Which row a pane is in is what the assertion was always about.
        var untouched = before
            .Where(p => p.Value.Y != before[top].Y)
            .Select(p => p.Key)
            .Single();

        Press(app, ConsoleKey.RightArrow);
        var after = app.PaneOutputRects();

        await Assert.That(after[untouched])
            .IsEqualTo(before[untouched])
            .Because("a resize in the other row must not move this pane by a cell");
        await Assert.That(after[top].Width).IsEqualTo(before[top].Width + PaneResize.StepCells);
        await Assert.That(after[top].Height).IsEqualTo(before[top].Height);
    }

    /// <summary>
    /// <b>The walk up the tree, on the real thing.</b> ⌥⇧↑ from a pane whose own parent is a <em>row</em>
    /// climbs to the column split above it and makes the whole top row taller — both panes in it, together.
    /// A resize that only knew about the focused pane's own parent would report "nothing that way" here,
    /// which is the case tmux's rule exists for.
    /// </summary>
    [Test]
    public async Task AVerticalResizeClimbsPastARowParentToTheColumnAboveIt()
    {
        var app = App(140, 40);
        ThreePanes(app);

        var top = app.FocusedPaneId;
        var before = app.PaneOutputRects().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var beside = before.Keys.Single(id => id != top && before[id].Y == before[top].Y);
        var below = before.Keys.Single(id => id != top && id != beside);

        Press(app, ConsoleKey.UpArrow);
        var after = app.PaneOutputRects();

        await Assert.That(after[top].Height).IsEqualTo(before[top].Height + PaneResize.StepCells);
        await Assert.That(after[beside].Height)
            .IsEqualTo(before[beside].Height + PaneResize.StepCells)
            .Because("the border belongs to the row, so its other pane moves with it");
        await Assert.That(after[below].Height).IsEqualTo(before[below].Height - PaneResize.StepCells);
        await Assert.That(after[top].Width).IsEqualTo(before[top].Width); // and nothing moved sideways
    }

    // --- refusing out loud ----------------------------------------------------------------------

    /// <summary>
    /// <b>A direction with no border says so.</b> On a side-by-side split ⌥⇧↓ has nothing to move; the
    /// status row carries the reason and the message log keeps it, so a notice that dismissed itself is
    /// still recoverable through ⌃P. Silence here is the client's single most-repeated defect.
    /// </summary>
    [Test]
    public async Task AnArrowWithNoSplitInItsAxisReportsOnTheStatusRow()
    {
        var app = App();
        app.RenderSnapshot("split");

        var before = app.PaneOutputRects().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var said = app.Messages.Entries.Count;

        app.SimulateKey(Resize(ConsoleKey.DownArrow));
        app.RenderNextFrame();

        await Assert.That(app.Messages.Entries.Count).IsGreaterThan(said);
        await Assert.That(app.Messages.Entries[^1].Text).Contains("⌥⇧↓");
        await Assert.That(app.StatusMarkup).Contains("rows");

        foreach (var (paneId, rect) in before)
        {
            await Assert.That(app.PaneOutputRects()[paneId]).IsEqualTo(rect);
        }
    }

    /// <summary>A one-pane workspace has no border at all, and every arrow of the chord says so.</summary>
    [Test]
    public async Task AOnePaneWorkspaceRefusesEveryArrow()
    {
        var app = App();
        app.RenderSnapshot();
        await Assert.That(app.PaneIds.Count).IsEqualTo(1);

        foreach (var arrow in new[]
        {
            ConsoleKey.RightArrow, ConsoleKey.LeftArrow, ConsoleKey.DownArrow, ConsoleKey.UpArrow,
        })
        {
            var said = app.Messages.Entries.Count;
            app.SimulateKey(Resize(arrow));
            await Assert.That(app.Messages.Entries.Count)
                .IsGreaterThan(said)
                .Because($"Alt+Shift+{arrow} on one pane must report, not sit there");
        }
    }

    /// <summary>
    /// Held against the floor, the client stops squeezing and says why — and the pane paying for it never
    /// goes under <see cref="PaneResize.MinimumPaneColumns"/> in its <em>arranged</em> width, which is the
    /// number a server is told.
    /// </summary>
    [Test]
    public async Task HoldingTheKeyStopsAtTheMinimumAndSaysSo()
    {
        var app = App();
        app.RenderSnapshot("split");
        var other = app.PaneIds.Single(id => id != app.FocusedPaneId);

        for (var press = 0; press < 80; press++)
        {
            Press(app, ConsoleKey.RightArrow);
            await Assert.That(app.PaneOutputRects()[other].Width)
                .IsGreaterThanOrEqualTo(PaneResize.MinimumPaneColumns);
        }

        await Assert.That(app.PaneOutputRects()[other].Width).IsEqualTo(PaneResize.MinimumPaneColumns);
        await Assert.That(app.Messages.Entries[^1].Text).Contains("columns");
    }

    // --- the NAWS trap --------------------------------------------------------------------------

    /// <summary>
    /// <b>A burst of resizes costs a bounded number of NAWS reports, ending on the final size.</b> This is
    /// the one that is not a local glitch if it is wrong: per-pane NAWS is derived from the pane
    /// rectangle, so a held Alt+Shift+→ without the rate limit writes a new terminal size to a real MU*
    /// server on every key repeat and makes it reflow its output each time. Forty presses inside a second
    /// span four of the limit's windows; the assertion is that the server hears a handful of sizes and
    /// that the last one is what is on screen.
    /// </summary>
    [Test]
    public async Task ABurstOfResizesIsRateLimitedAndEndsOnTheFinalSize()
    {
        var (app, clock) = TimedApp();
        app.RenderSnapshot("split");

        var world = await ConnectedAsync("Squeezed");
        app.AttachSession(world.Session, "main");
        app.RenderNextFrame();
        var opening = world.Telnet.Sizes.Count;

        // 40 presses over a second — a held key at a typical repeat rate.
        for (var press = 0; press < 40; press++)
        {
            Press(app, press % 2 == 0 ? ConsoleKey.RightArrow : ConsoleKey.LeftArrow);
            clock.Advance(TimeSpan.FromMilliseconds(25));
        }

        clock.Advance(Interval); // the key is released; the trailing flush carries the settled size

        var settled = app.PaneOutputRects()[app.FocusedPaneId];
        await Assert.That(world.Telnet.Sizes[^1]).IsEqualTo((settled.Width, settled.Height));

        // One second of presses spans four 250 ms windows; anything near forty means the limiter is
        // being bypassed, which is a flood aimed at someone else's game.
        await Assert.That(world.Telnet.Sizes.Count - opening).IsLessThanOrEqualTo(6);
    }

    /// <summary>
    /// The same burst, in one interval: the sizes it swept through are coalesced away entirely and only
    /// the one it settled on is written. The intermediate widths a server would have re-wrapped to are
    /// asserted absent, not merely outnumbered.
    /// </summary>
    [Test]
    public async Task TheWidthsABurstSweepsThroughAreNeverSent()
    {
        var (app, clock) = TimedApp();
        app.RenderSnapshot("split");

        var world = await ConnectedAsync("Swept");
        app.AttachSession(world.Session, "main");
        app.RenderNextFrame();
        var told = world.Telnet.Sizes.Count;

        var swept = new List<(int, int)>();
        for (var press = 0; press < 6; press++)
        {
            Press(app, ConsoleKey.RightArrow);
            var rect = app.PaneOutputRects()[app.FocusedPaneId];
            swept.Add((rect.Width, rect.Height));
        }

        var settled = swept[^1];
        clock.Advance(Interval);

        await Assert.That(world.Telnet.Sizes[^1]).IsEqualTo(settled);
        foreach (var intermediate in swept.Take(swept.Count - 1))
        {
            await Assert.That(world.Telnet.Sizes.Contains(intermediate))
                .IsFalse()
                .Because($"the server must never be made to re-wrap to {intermediate}");
        }

        await Assert.That(world.Telnet.Sizes.Count - told).IsEqualTo(1);
    }

    /// <summary>
    /// And the world in the pane that did <em>not</em> change is not written to at all. The resize moved
    /// one border; a session whose rectangle is the same owes its server nothing.
    /// </summary>
    [Test]
    public async Task AWorldWhoseRectangleDidNotChangeIsNotToldAnything()
    {
        var (app, clock) = TimedApp(140, 40);
        ThreePanes(app); // main and the web view side by side, over Chat

        var moved = await ConnectedAsync("Moved");
        var still = await ConnectedAsync("Still");
        app.AttachSession(moved.Session, "main");   // the pane the border is between
        app.AttachSession(still.Session, ChatWindowId); // the row below, which nothing touches
        app.RenderNextFrame();
        clock.Advance(Interval);

        var before = app.PaneOutputRects()[app.FocusedPaneId];
        var toldMoved = moved.Telnet.Sizes.Count;
        var toldStill = still.Telnet.Sizes.Count;

        Press(app, ConsoleKey.RightArrow);
        clock.Advance(Interval);

        await Assert.That(app.PaneOutputRects()[app.FocusedPaneId].Width)
            .IsEqualTo(before.Width + PaneResize.StepCells); // something really happened
        await Assert.That(moved.Telnet.Sizes.Count)
            .IsGreaterThan(toldMoved)
            .Because("this world's pane narrowed, so its server is told");
        await Assert.That(still.Telnet.Sizes.Count)
            .IsEqualTo(toldStill)
            .Because("this world's pane did not move, so its server hears nothing");
    }

    /// <summary>
    /// Three panes: <c>main</c> and the web view side by side in the top row, over <c>Chat</c>. Built by
    /// driving the real ⌃B commands so the tree is one the app actually makes.
    /// </summary>
    private static void ThreePanes(SharpMUTermApp app)
    {
        app.RenderSnapshot();
        app.SimulatePrefixedKey(Plain('-', ConsoleKey.Subtract)); // ⌃B - : main over Chat
        app.RenderNextFrame();
        app.SimulateWebPage(); // a second tab in the top pane — the one window owned by no connection
        app.RenderNextFrame();
        app.SimulatePrefixedKey(Plain('|', ConsoleKey.Oem5)); // ⌃B | : the top pane splits side by side
        app.RenderNextFrame();
    }

    // --- discoverability ------------------------------------------------------------------------

    /// <summary>
    /// Each <c>Make this pane …</c> entry names its chord, and the chord does what the entry does — the
    /// same two-directional honesty rule <see cref="AdvertisedKeyHonestyTests"/> holds the rest of the
    /// keyboard to. A chord nobody knows is the same as no feature, which is exactly how ⌃L's newline sat
    /// unused until it was reported missing.
    /// </summary>
    [Test]
    [Arguments("layout:wider", ConsoleKey.RightArrow, "⌥⇧→")]
    [Arguments("layout:narrower", ConsoleKey.LeftArrow, "⌥⇧←")]
    [Arguments("layout:taller", ConsoleKey.UpArrow, "⌥⇧↑")]
    [Arguments("layout:shorter", ConsoleKey.DownArrow, "⌥⇧↓")]
    public async Task EveryResizeEntryNamesTheChordThatDoesTheSameThing(string id, ConsoleKey arrow, string chord)
    {
        var viaKey = App();
        viaKey.RenderSnapshot("split");
        viaKey.SimulateKey(Resize(arrow));
        viaKey.RenderNextFrame();

        var viaEntry = App();
        viaEntry.RenderSnapshot("split");
        viaEntry.DispatchCommand(id);
        viaEntry.RenderNextFrame();

        var entry = viaEntry.BuildCatalog().Single(c => c.Id == id);
        await Assert.That(entry.Subtitle).IsEqualTo(chord);
        await Assert.That(viaEntry.PaneOutputRects())
            .IsEquivalentTo(viaKey.PaneOutputRects())
            .Because($"{chord} and '{entry.Title}' must leave the same layout");
    }

    /// <summary>
    /// <c>--help</c> names the chord, and the status row does too when there is room and something to
    /// resize. The row is width-constrained, so the resize hint is the first thing dropped on a narrow
    /// terminal — but the navigation hint it sits beside must survive that, which is the regression the
    /// narrow case pins.
    /// </summary>
    [Test]
    public async Task TheChordIsAdvertisedWhereKeysAreAdvertised()
    {
        await Assert.That(Program.UsageText).Contains("Alt+Shift+Up/Down/Left/Right");

        var roomy = App(160, 48);
        roomy.RenderSnapshot("split");
        await Assert.That(roomy.StatusMarkup).Contains("⌥⇧←→↑↓ size");
        await Assert.That(roomy.StatusMarkup).Contains("⌃←→↑↓ pane");

        // One pane: nothing to resize, so nothing is claimed.
        var solo = App(160, 48);
        solo.RenderSnapshot();
        await Assert.That(solo.StatusMarkup).DoesNotContain("size");

        // Narrow: the pane hint stays, the longer resize hint is the one that gives way. 76 columns is
        // just under where the pair fits; at 80 both are there, which is why the fallback is a list of
        // candidates rather than one string that either fits or takes the navigation hint down with it.
        var narrow = App(76, 30);
        narrow.RenderSnapshot("split");
        await Assert.That(narrow.StatusMarkup).Contains("⌃←→↑↓ pane");
        await Assert.That(narrow.StatusMarkup).DoesNotContain("⌥⇧←→↑↓ size");
    }

    /// <summary>
    /// <b>A macro bound to the chord still wins it.</b> The resize sits after <c>DispatchMacro</c> in the
    /// key chain for the same reason pane navigation does — a chord the user has deliberately bound is
    /// theirs — so the F4 screen's claim that a macro on <c>Alt+Shift+Right</c> fires stays true. This
    /// pins both halves: the verdict, and the behaviour it describes.
    /// </summary>
    [Test]
    public async Task ABoundMacroWinsTheChordAndTheLayoutStaysPut()
    {
        foreach (var descriptor in new[]
        {
            "Alt+Shift+Left", "Alt+Shift+Right", "Alt+Shift+Up", "Alt+Shift+Down",
        })
        {
            await Assert.That(MacroKeys.Verdict(descriptor).Fires)
                .IsTrue()
                .Because($"F4 says a macro on {descriptor} fires, so the resize must not pre-empt it");
        }

        Console.SetIn(TextReader.Null);
        var config = DemoScene.Build();
        config.TriggerSets[0].Macros.Add(new SharpMUTerm.Core.Automation.Macro
        {
            Name = "east", Key = "Alt+Shift+Right", Command = "east", Enabled = true,
        });

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(120, 34));
        app.BindWorldWithoutConnecting(config.Worlds[0]);
        app.RenderSnapshot("split");

        var before = app.PaneOutputRects().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var sent = app.SimulateKey(Resize(ConsoleKey.RightArrow));
        app.RenderNextFrame();

        await Assert.That(sent).IsEqualTo("east");
        foreach (var (paneId, rect) in before)
        {
            await Assert.That(app.PaneOutputRects()[paneId]).IsEqualTo(rect);
        }
    }

    // --- harness ---------------------------------------------------------------------------------

    private sealed record Reporter(WorldSession Session, RecordingTelnetSession Telnet);

    private static async Task<Reporter> ConnectedAsync(string name)
    {
        var telnet = new RecordingTelnetSession();
        var world = new WorldDefinition { Name = name, Host = "h", Port = 1 };
        var session = new WorldSession(world, sessionFactory: _ => telnet);
        await session.ConnectAsync();
        return new Reporter(session, telnet);
    }

    /// <summary>The truecolor background escape a colour is written as, e.g. <c>48;2;51;57;76</c>.</summary>
    private static string Sgr(SharpConsoleUI.Color color) => $"48;2;{color.R};{color.G};{color.B}";

    /// <summary>
    /// Walks a frame into a <c>{(row, column): background}</c> grid, the way a terminal walks it: the last
    /// <c>48;2;r;g;b</c> seen is the background of every cell written until the next one. Note <c>48</c>
    /// and not <c>38</c> — reading foreground here and concluding about bands is the classic mistake.
    /// <para>
    /// Deliberately a local copy rather than a helper shared with <see cref="FocusIndicationTests"/>: a
    /// thirty-line decoder is cheaper to duplicate than a shared file is to merge, and these two suites
    /// are edited by different changes.
    /// </para>
    /// </summary>
    private static Dictionary<(int Row, int Column), string?> Grid(string ansi)
    {
        var cells = new Dictionary<(int, int), string?>();
        var current = (string?)null;
        var (row, column) = (0, 0);

        foreach (Match token in Regex.Matches(ansi, @"\x1b\[([0-9;]*)([A-Za-z])|([^\x1b\r\n])|(\n)"))
        {
            if (token.Groups[4].Success)
            {
                row++;
                column = 0;
                continue;
            }

            if (token.Groups[3].Success)
            {
                cells[(row, column)] = current;
                column++;
                continue;
            }

            var parameters = token.Groups[1].Value;
            switch (token.Groups[2].Value)
            {
                case "H":
                    var at = parameters.Split(';');
                    row = at[0].Length > 0 ? int.Parse(at[0]) - 1 : 0;
                    column = at.Length > 1 && at[1].Length > 0 ? int.Parse(at[1]) - 1 : 0;
                    break;
                case "m":
                    if (parameters.Length == 0 || parameters == "0" || parameters.Contains("49"))
                    {
                        current = null;
                    }

                    if (parameters.Contains("48;2;"))
                    {
                        current = parameters[parameters.IndexOf("48;2;", StringComparison.Ordinal)..];
                    }

                    break;
            }
        }

        return cells;
    }

    /// <summary>How many cells of one row are painted in a background.</summary>
    private static int ColumnsPainted(
        Dictionary<(int Row, int Column), string?> grid, int row, string background) =>
        grid.Count(cell => cell.Key.Row == row && cell.Value == background);

    /// <summary>How many cells of one column are painted in a background.</summary>
    private static int RowsPainted(
        Dictionary<(int Row, int Column), string?> grid, int column, string background) =>
        grid.Count(cell => cell.Key.Column == column && cell.Value == background);
}
