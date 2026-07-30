using System.Globalization;
using System.Text.RegularExpressions;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Parsing;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The new-activity indicator on a pane's tab strip: how many lines a tab has that you have not seen,
/// and the tint that makes the tab itself say so.
/// <para>
/// Most of this already existed and is <em>not</em> what these tests are for. <c>Workspace.NoteActivity</c>
/// has always kept the count, the sidebar has always drawn it, and <c>TabTitles</c> has always appended
/// <c>(n)</c>. What was missing was that the count was uncapped where the sidebar's was capped — so one
/// number had two spellings — and that the tab carried no colour at all, which is the half of the signal
/// a reader sees without reading.
/// </para>
/// <para>
/// <b>The test to keep above the others is <see cref="ActivityMovesNoPaneRectangle"/>.</b> Per-pane NAWS is
/// derived from the pane rectangle, and unread arrives <em>unbidden from the wire</em>: an indicator that
/// cost a cell as it appeared, or another as the count took a second digit, would re-announce a new
/// terminal size to every connected server on a line of output nobody asked for, and the game would
/// reflow. That is the reported failure this repository keeps paying for, and it is why the sidebar's
/// badge sits in a reserved field. The tab strip needs no reserved field — see that test for why — but it
/// still has to prove it.
/// </para>
/// </summary>
/// <remarks>
/// Serialised for the same reason <see cref="FocusIndicationTests"/> is: rendering a frame redirects the
/// process-global <c>Console.Out</c>, and the harness redirects <c>Console.In</c>.
/// </remarks>
[NotInParallel]
public class TabActivityIndicatorTests
{
    private const int Width = 120;
    private const int Height = 32;
    private const string Main = "main";

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    private static ConsoleKeyInfo Chord(ConsoleKey key, bool ctrl = false) => new('\0', key, false, false, ctrl);

    /// <summary>
    /// The demo app with a session bound to the main window but no socket under it, so
    /// <c>WorldSession.PrintSystem</c> drives the app's real line handler and therefore its real unread
    /// accounting. The same shape <c>OutputScrollbackTests.Bound</c> uses, and for the same reason: a test
    /// that set the counter itself would be asserting about a number this code does not read.
    /// </summary>
    private static (SharpMUTermApp App, WorldSession Session) Bound(
        int width = Width, int height = Height, TimeProvider? clock = null)
    {
        Console.SetIn(TextReader.Null);
        var config = DemoScene.Build();
        var app = clock is null
            ? new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(width, height))
            : new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(width, height), clock);
        return (app, app.BindWorldWithoutConnecting(config.Worlds[0]));
    }

    /// <summary>
    /// Scrolls the main window off its live tail and prints <paramref name="lines"/> into it, so the count
    /// accrues on a window that is <em>visible</em>. That arm is deliberate: it is the one
    /// <c>WorkspaceTests.NoteActivity_OnAVisibleWindowScrolledBack_StillBadges</c> pins, and the one where
    /// a tab can be both focused and unread — which is exactly where a tint could be mistaken for the
    /// focus cue.
    /// </summary>
    private static void AccrueOnScrolledBackMain(SharpMUTermApp app, WorldSession session, int lines)
    {
        app.LoadLongScene(Main, SharpMUTermApp.ScrollbackSceneLines);
        app.RenderNextFrame();
        app.RenderNextFrame(); // auto-scroll settles on the second frame; see SettleScroll
        app.SimulateKey(Chord(ConsoleKey.PageUp));

        for (var i = 0; i < lines; i++)
        {
            session.PrintSystem($"*** unseen {i}");
        }

        app.RenderNextFrame();
    }

    /// <summary>The label the main window's tab is actually carrying, off the strip the framework will draw.</summary>
    private static string MainTabLabel(SharpMUTermApp app) =>
        app.PaneTabStrip(app.PaneIdOf(Main)!).Single(t => t.WindowId == Main).Title;

    // --- the title --------------------------------------------------------------------------------

    /// <summary>
    /// The asked-for shape, end to end and on a real app: <c>Corvid (36)</c> with thirty-six unread and
    /// plain <c>Corvid</c> at zero — read off the tab the framework will draw. (The demo's main window is
    /// titled after its character, because that is what a live <c>BindSession</c> writes; the requested
    /// <c>Mannaz (36)</c> spelling is pinned on the formatter in <see cref="TabTitlesTests"/>.)
    /// <para>
    /// The <c>▌</c> is on this label too, because this window's pane holds the focus. So the assertion is
    /// on the label's <em>printable</em> width — the string itself is markup, and measuring it any other
    /// way would be measuring the colour tag.
    /// </para>
    /// </summary>
    [Test]
    public async Task ATabWithUnreadReadsItsNameAndCountAndLosesBothWhenCaughtUp()
    {
        var (app, session) = Bound();
        AccrueOnScrolledBackMain(app, session, 36);

        await Assert.That(app.UnreadOf(Main)).IsEqualTo(36);
        await Assert.That(MainTabLabel(app)).Contains("Corvid (36)");
        await Assert.That(MarkupParser.StripLength(MainTabLabel(app)))
            .IsEqualTo($"{Glyphs.FocusedPane} Corvid (36)".Length);

        app.SimulateKey(Chord(ConsoleKey.End, ctrl: true)); // back to the live tail: caught up
        app.RenderNextFrame();

        await Assert.That(app.UnreadOf(Main)).IsEqualTo(0);
        await Assert.That(MainTabLabel(app)).IsEqualTo($"{Glyphs.FocusedPane} Corvid");
    }

    /// <summary>
    /// The sidebar and the tab strip never print different numbers for one count. They are two views of
    /// <c>WorkspaceWindow.Unread</c> and they format it through the same <see cref="UnreadBadge"/>; before
    /// that, the rail capped at ninety-nine and the tab did not, so a busy channel read <c>99+</c> in one
    /// place and <c>(150)</c> in the other.
    /// </summary>
    [Test]
    [Arguments(3)]
    [Arguments(99)]
    [Arguments(150)]
    public async Task TheSidebarAndTheTabPrintTheSameCount(int lines)
    {
        var (app, session) = Bound();
        AccrueOnScrolledBackMain(app, session, lines);

        var badge = UnreadBadge.Format(lines);
        await Assert.That(app.UnreadOf(Main)).IsEqualTo(lines);
        await Assert.That(MainTabLabel(app)).Contains($"({badge})");

        // The rail's own rows carry the same badge, right-aligned in the field it reserves for one.
        var railRows = app.RailLines
            .Where(l => Regex.IsMatch(l, $@"\[{Regex.Escape(UnreadBadge.Tint)}\]\s*\d+\+?\[/\]"))
            .ToList();
        await Assert.That(railRows).IsNotEmpty();
        foreach (var railRow in railRows)
        {
            await Assert.That(railRow)
                .Contains($"[{UnreadBadge.Tint}]{badge.PadLeft(UnreadBadge.FieldWidth)}[/]");
        }

        // Once the cap has bitten, the uncapped number is on neither surface. This is the assertion that
        // fails on a tab which formats the raw integer while the sidebar beside it says 99+.
        var raw = lines.ToString(CultureInfo.InvariantCulture);
        if (raw != badge)
        {
            await Assert.That(MainTabLabel(app)).DoesNotContain(raw);
            await Assert.That(string.Concat(railRows)).DoesNotContain(raw);
        }
    }

    // --- the tint ---------------------------------------------------------------------------------

    /// <summary>
    /// The tint is on the frame, on the tab strip's own row, and it is a <em>foreground</em>. Read off the
    /// painted cells rather than off the label, because a colour tag can be emitted into a string that
    /// nothing draws — which is precisely the way pane focus was once "indicated".
    /// </summary>
    [Test]
    public async Task TheTintIsPaintedOnTheTabStripRow()
    {
        var (app, session) = Bound();

        var before = Cells(app.RenderWholeFrame());
        await Assert.That(Tinted(before, StripRow(app), app)).IsEmpty();

        AccrueOnScrolledBackMain(app, session, 36);
        var after = Cells(app.RenderWholeFrame());

        // The tinted run *is* the name and the count — it stops before the ▌ and before the strip's rule.
        var tinted = Tinted(after, StripRow(app), app);
        await Assert.That(string.Concat(tinted.Select(c => c.Char))).IsEqualTo("Corvid (36)");
        await Assert.That(RowText(after, StripRow(app))).Contains("Corvid (36)");
    }

    /// <summary>
    /// <b>The tint cannot be confused with the focus cue.</b> Focus is said entirely in
    /// <em>backgrounds</em> drawn from the theme's chrome family — the pane's plane and the chip behind the
    /// active tab; activity is said in a <em>foreground</em> no plane in the workspace is ever painted in.
    /// So the two are separate channels and the four combinations are four distinct looks. This checks the
    /// one that would break a weaker design — a tab that is focused <em>and</em> unread — and asserts
    /// legibility as a measured contrast on both planes a chip can have, rather than assuming it.
    /// </summary>
    [Test]
    public async Task AFocusedTabWithUnreadShowsBothCuesAndTheyAreDifferentChannels()
    {
        var (app, session) = Bound();
        AccrueOnScrolledBackMain(app, session, 36);

        // This window's pane holds the focus, so its active chip is painted in the armed band.
        await Assert.That(app.FocusedPaneId).IsEqualTo(app.PaneIdOf(Main));

        var cells = Cells(app.RenderWholeFrame());
        var tinted = Tinted(cells, StripRow(app), app);
        await Assert.That(tinted).IsNotEmpty();

        var (focusedPlane, unfocusedPlane) = app.PaneBandColors;
        var planes = new[] { focusedPlane, unfocusedPlane }.Select(c => Hex(c.R, c.G, c.B)).ToList();

        // The tint is nothing the focus cue is ever painted in, and it stays clear of both planes by a
        // wide margin — so it reads whichever pane the tab is in.
        foreach (var plane in planes)
        {
            await Assert.That(plane).IsNotEqualTo(UnreadBadge.Tint);
            await Assert.That(Contrast(UnreadBadge.Tint, plane)).IsGreaterThan(4.5);
        }

        // The tinted cells are all drawn on one background — the focused chip's — which is what makes the
        // point: same cells, two channels, and the background is doing the focus half on its own.
        var chip = tinted.Select(c => c.Background).Distinct().ToList();
        await Assert.That(chip.Count).IsEqualTo(1);
        await Assert.That(chip[0]).IsNotEqualTo(UnreadBadge.Tint);
        await Assert.That(Contrast(UnreadBadge.Tint, chip[0]!)).IsGreaterThan(4.5);
    }

    /// <summary>
    /// The focus marker keeps its own colour. It says which pane holds the keyboard — a fact that is true
    /// or false whatever the count is — so a line of output arriving may not recolour it, or the two
    /// signals would be reporting each other.
    /// </summary>
    [Test]
    public async Task TheFocusMarkerKeepsItsOwnColourWhileTheTabIsUnread()
    {
        var (app, session) = Bound();
        AccrueOnScrolledBackMain(app, session, 36);
        await Assert.That(MainTabLabel(app)).StartsWith(Glyphs.FocusedPane);

        var cells = Cells(app.RenderWholeFrame());
        var marker = cells.Values.Single(c => c.Row == StripRow(app) && c.Char == Glyphs.FocusedPane[0]);
        await Assert.That(marker.Foreground).IsNotEqualTo(UnreadBadge.Tint);
    }

    // --- the NAWS trap ----------------------------------------------------------------------------

    /// <summary>
    /// <b>Activity moves no pane rectangle</b> — not when the badge appears out of nothing, not at 9 → 10
    /// when it takes a second digit, and not at 99 → 100 when it takes a third and the cap changes its
    /// spelling. Every one of those is a width change on a label that arrives from the wire.
    /// <para>
    /// <b>And the strip is deliberately <em>not</em> given the sidebar's reserved-width treatment.</b> The
    /// two surfaces are laid out differently and the argument does not carry across. A rail row's width
    /// feeds <c>SharpMUTermApp.RailWidth</c>, which sizes the sidebar's grid column, and the pane area is
    /// what is left over — so there a badge that appears really does narrow every pane. A tab strip is a
    /// <c>TabControl</c> arranged <c>Fill</c> + <c>Stretch</c> inside the pane it already fills; the
    /// framework paints the labels left to right and then fills the rest of the header row out to the
    /// pane's own edge (<c>TabControl.Rendering</c>), so a longer label moves the tabs beside it along a
    /// row whose width was never a function of them. Reserving three cells per tab would cost real width
    /// on every strip for ever — pushing a narrow pane's later tabs off the end — to prevent a reflow that
    /// this layout cannot produce. So: measured, argued, and declined. This test is the proof, and it is
    /// checked narrow as well as roomy because a one-cell change hides in a roomy default.
    /// </para>
    /// </summary>
    [Test]
    [Arguments(160, 48)]
    [Arguments(120, 34)]
    [Arguments(100, 24)]
    [Arguments(80, 20)]
    public async Task ActivityMovesNoPaneRectangle(int width, int height)
    {
        var (app, session) = Bound(width, height);
        app.RenderSnapshot("split"); // two panes, so the strip shares its row with a neighbour
        app.LoadLongScene(Main, SharpMUTermApp.ScrollbackSceneLines);
        app.RenderNextFrame();
        app.RenderNextFrame();
        app.SimulateKey(Chord(ConsoleKey.PageUp));
        app.RenderNextFrame();

        await Assert.That(app.UnreadOf(Main)).IsEqualTo(0);
        var before = app.PaneOutputRects().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var railBefore = app.RailColumnWidth;
        var widths = new List<int>();

        // Every boundary the badge crosses: nothing → 1, one digit → two, two → three, and past the cap.
        foreach (var stop in new[] { 1, 9, 10, 99, 100, 150 })
        {
            while (app.UnreadOf(Main) < stop)
            {
                session.PrintSystem("*** unseen");
            }

            app.RenderNextFrame();
            await Assert.That(app.UnreadOf(Main)).IsEqualTo(stop);
            widths.Add(MarkupParser.StripLength(MainTabLabel(app)));

            await Assert.That(app.RailColumnWidth).IsEqualTo(railBefore);
            foreach (var (paneId, rect) in before)
            {
                await Assert.That(app.PaneOutputRects()[paneId]).IsEqualTo(rect);
            }
        }

        // The label really did change width on the way — otherwise this passes on an indicator that
        // never appeared, which is how a NAWS test quietly stops testing anything.
        await Assert.That(widths.Distinct().Count()).IsGreaterThan(1);

        // …and stopped changing at the cap, which is the bound the sidebar's field also relies on.
        await Assert.That(widths[^1]).IsEqualTo(widths[^2]);
    }

    /// <summary>
    /// The same claim on the bytes a server would receive: a connected world is not told a new terminal
    /// size because a line arrived in a tab nobody is looking at. Driven on a
    /// <see cref="ManualTimeProvider"/> and wound past the report interval, because the NAWS throttle has a
    /// trailing flush — on a still clock a changed size is coalesced and never delivered inside the test,
    /// so this would pass on a broken client without the wind.
    /// </summary>
    [Test]
    public async Task ActivityTellsNoServerANewSize()
    {
        var clock = new ManualTimeProvider();
        var (app, printing) = Bound(clock: clock);
        app.RenderSnapshot("split");

        // Two sessions on the one window, because neither seam does both jobs: the demo-bound session is
        // what drives the app's real line handler and so its real unread accounting, while
        // AttachSession — which only registers a session's window for the size report — is the only way to
        // put a recording transport behind that pane. Both point at `main`, so the bytes below are the
        // sizes a server connected to this pane would have received.
        var telnet = new RecordingTelnetSession();
        var watcher = new WorldSession(
            new WorldDefinition { Name = "W", Host = "h", Port = 1 }, sessionFactory: _ => telnet);
        await watcher.ConnectAsync();
        app.AttachSession(watcher, Main);

        // More output than the pane holds, then up off the live tail — otherwise there is nothing below
        // the viewport, the window stays caught up, and no line that arrives is unread at all.
        app.LoadLongScene(Main, SharpMUTermApp.ScrollbackSceneLines);
        app.RenderNextFrame();
        app.RenderNextFrame();
        app.SimulateKey(Chord(ConsoleKey.PageUp));
        app.RenderNextFrame();
        clock.Advance(TimeSpan.FromSeconds(1));
        app.RenderNextFrame();

        var before = telnet.Sizes.Distinct().ToList();
        await Assert.That(before).IsNotEmpty();

        for (var i = 0; i < 150; i++)
        {
            printing.PrintSystem($"*** unseen {i}");
        }

        app.RenderNextFrame();
        clock.Advance(TimeSpan.FromSeconds(1));
        app.RenderNextFrame();

        // Past the cap, so every width step the badge can take has been taken.
        await Assert.That(app.UnreadOf(Main)).IsGreaterThan(UnreadBadge.Max);
        await Assert.That(telnet.Sizes.Distinct().ToList()).IsEquivalentTo(before);
    }

    // --- clearing ---------------------------------------------------------------------------------

    /// <summary>
    /// <b>The tab clears on exactly the condition the sidebar clears on, because it reads the same field.</b>
    /// Both render from <c>WorkspaceWindow.Unread</c>, so the interesting case is the one where a rule of
    /// one's own would get it wrong: <c>Workspace.ActivateWindow</c> deliberately does <em>not</em> clear a
    /// window the reader has scrolled back, because the unread lines are still below the viewport
    /// (<c>WorkspaceTests.ActivateWindow_DoesNotClearUnreadOfAScrolledBackWindow</c>). Picking the tab
    /// therefore leaves both badges up, and returning to the live tail takes both down together.
    /// </summary>
    [Test]
    public async Task PickingTheTabOfAScrolledBackWindowClearsNeitherBadgeAndTheTailClearsBoth()
    {
        var (app, session) = Bound();
        AccrueOnScrolledBackMain(app, session, 7);

        await Assert.That(app.UnreadOf(Main)).IsEqualTo(7);
        await Assert.That(MainTabLabel(app)).Contains("(7)");
        await Assert.That(RailShowsABadge(app)).IsTrue();

        // Activating the window it is already on: the sidebar keeps its badge, so the tab must keep its own.
        app.SimulateWindowChange(Main);
        app.RenderNextFrame();

        await Assert.That(app.UnreadOf(Main)).IsEqualTo(7);
        await Assert.That(MainTabLabel(app)).Contains("(7)");
        await Assert.That(RailShowsABadge(app)).IsTrue();

        // ⌃End is catching up, and it is the one gesture that clears — for both surfaces at once.
        app.SimulateKey(Chord(ConsoleKey.End, ctrl: true));
        app.RenderNextFrame();

        await Assert.That(app.UnreadOf(Main)).IsEqualTo(0);
        await Assert.That(MainTabLabel(app)).DoesNotContain("(");
        await Assert.That(MainTabLabel(app)).DoesNotContain(UnreadBadge.Tint);
        await Assert.That(RailShowsABadge(app)).IsFalse();
    }

    /// <summary>
    /// Whether any rail row is drawing an unread badge — the sidebar's half of the signal. Matched on the
    /// <em>field</em> (a count right-aligned into <see cref="UnreadBadge.FieldWidth"/>) and not merely on
    /// the accent, because the rail paints its world spine and its connection dots in that colour too.
    /// </summary>
    private static bool RailShowsABadge(SharpMUTermApp app) =>
        app.RailLines.Any(l => Regex.IsMatch(
            l, $@"\[{Regex.Escape(UnreadBadge.Tint)}\]\s*\d+\+?\[/\]"));

    // --- frame decoding ---------------------------------------------------------------------------

    private readonly record struct Cell(int Row, int Column, char Char, string? Foreground, string? Background);

    /// <summary>
    /// Walks a frame into cells carrying <em>both</em> channels. Note the <c>38</c> as well as the
    /// <c>48</c>: this indicator lives in the foreground and the focus cue it must not be confused with
    /// lives in the background, so a decoder that read only one of them could not tell them apart.
    /// </summary>
    private static Dictionary<(int Row, int Column), Cell> Cells(string ansi)
    {
        var cells = new Dictionary<(int, int), Cell>();
        var (row, column) = (0, 0);
        string? fg = null, bg = null;

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
                cells[(row, column)] = new Cell(row, column, token.Groups[3].Value[0], fg, bg);
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
                    if (parameters.Length == 0 || parameters == "0")
                    {
                        fg = bg = null;
                    }

                    fg = Truecolor(parameters, "38;2;") ?? fg;
                    bg = Truecolor(parameters, "48;2;") ?? bg;
                    break;
            }
        }

        return cells;
    }

    /// <summary>Reads an <c>r;g;b</c> triple out of an SGR parameter list as <c>#rrggbb</c>, or null.</summary>
    private static string? Truecolor(string parameters, string introducer)
    {
        var at = parameters.IndexOf(introducer, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var parts = parameters[(at + introducer.Length)..].Split(';');
        return parts.Length < 3 ? null : Hex(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
    }

    private static string Hex(int r, int g, int b) => $"#{r:x2}{g:x2}{b:x2}";

    /// <summary>
    /// The frame row a pane's tab strip is drawn on: the header sits immediately above the pane's output
    /// rectangle, which is what <c>PaneOutputRects</c> reports.
    /// </summary>
    private static int StripRow(SharpMUTermApp app) => app.PaneOutputRects()[app.PaneIdOf(Main)!].Y - 1;

    /// <summary>
    /// The tinted cells on one row, in column order and within that pane's own columns.
    /// <para>
    /// Scoped rather than swept, because <see cref="UnreadBadge.Tint"/> is the <em>app accent</em> and the
    /// chrome already uses it elsewhere — the header's connected ●, the rail's world spine ▚ and its status
    /// dots are all painted in it. That is fine where it is: those are fixed decorations in fixed places,
    /// and inside a tab strip nothing else is accent-coloured, so on this row the tint is unambiguous. But
    /// a test that swept the whole frame for the colour would be counting them too.
    /// </para>
    /// </summary>
    private static List<Cell> Tinted(
        Dictionary<(int Row, int Column), Cell> cells, int row, SharpMUTermApp app)
    {
        var rect = app.PaneOutputRects()[app.PaneIdOf(Main)!];
        return cells.Values
            .Where(c => c.Row == row
                        && c.Column >= rect.X && c.Column < rect.X + rect.Width
                        && c.Foreground == UnreadBadge.Tint)
            .OrderBy(c => c.Column)
            .ToList();
    }

    private static string RowText(Dictionary<(int Row, int Column), Cell> cells, int row)
    {
        var onRow = cells.Values.Where(c => c.Row == row).ToList();
        return onRow.Count == 0
            ? string.Empty
            : string.Concat(Enumerable.Range(0, onRow.Max(c => c.Column) + 1)
                .Select(x => cells.TryGetValue((row, x), out var c) ? c.Char : ' '));
    }

    /// <summary>WCAG relative-contrast ratio between two <c>#rrggbb</c> colours.</summary>
    private static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        var r = Convert.ToInt32(hex.Substring(1, 2), 16);
        var g = Convert.ToInt32(hex.Substring(3, 2), 16);
        var b = Convert.ToInt32(hex.Substring(5, 2), 16);
        return (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));
    }
}
