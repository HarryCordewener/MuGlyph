using System.Text.RegularExpressions;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// <b>⌥1–⌥9 go to a numbered pane and bring it forward.</b> The request was "ALT-1 2 3 4 5, or
/// CTRL-1 2 3 4 5 — go to those numbered panes and bring them to the forefront"; this is the half of it
/// that can be delivered, and these are the four claims it stands on.
/// <para>
/// <b>1. The number is the number on the screen.</b> Panes are counted in <c>Layout.Panes</c> order, which
/// is the order the connection rail's hosting column spells <c>pane N</c> in — so the assertions read the
/// label off the <em>live rail</em> and press the digit that label names, rather than writing down which
/// pane ought to be third. A chord that lands somewhere other than the label says is worse than no chord,
/// and this repository has already paid for two spellings of one pane once (<c>▪ main   main</c>).
/// </para>
/// <para>
/// <b>2. It is the full activation, on painted cells.</b> "Bring it to the forefront" is not
/// <c>FocusedPaneId</c> being assigned: it is the pane's plane on the frame, its window active, and the
/// command line talking to its character. All three are asserted, the first off the frame the driver was
/// handed — a focus indicator can be set on a control arranged at zero rows and read back happily.
/// </para>
/// <para>
/// <b>3. Nothing falls through to the framework.</b> SharpConsoleUI claims Alt+1–9 for its own top-level
/// window selector (<c>InputCoordinator.HandleAltInput</c>), which — unlike the move and resize handlers
/// beside it — is <em>not</em> gated on <c>IsMovable</c>/<c>IsResizable</c>, so <c>Movable(false)</c> did
/// not switch it off. All nine digits are claimed as application shortcuts, which
/// <c>InputCoordinator</c> tries before it offers the key to a window at all; an out-of-range digit
/// therefore reports here and stops rather than reaching a window selector that would do something else.
/// </para>
/// <para>
/// <b>4. Alt, because Ctrl+digit is not a chord this terminal has.</b> Read off a real pty rather than
/// remembered: every Alt+digit is <c>ESC</c> + the digit, while Ctrl+digit is the bare digit for 1/9/0 and
/// a byte already spelt Escape (3), Backspace (8) or NUL (2) for the rest. <c>MacroKeys.Verdict</c> is
/// where that is recorded, and <see cref="MacroKeyCaptureTests"/> holds it.
/// </para>
/// </summary>
/// <remarks>
/// Serialised with the other end-to-end suites: constructing the app and rendering a frame both touch the
/// process-global console streams.
/// </remarks>
[NotInParallel]
public class PaneJumpTests
{
    private const int Width = 160;
    private const int Height = 40;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    // --- the numbering, against the rail's own labels ----------------------------------------------

    /// <summary>
    /// <b>The claim, end to end.</b> Three panes, three characters, one each. For every digit: press ⌥N,
    /// and the pane whose plane the frame paints as focused is the pane the rail labels <c>pane N</c> —
    /// with that pane's window active and its character on the command line.
    /// <para>
    /// The label is read from <see cref="SharpMUTermApp.RailLines"/> after the jump, which is the rail the
    /// app really drew. Nothing here writes down which pane is third: the assertion is that the two agree,
    /// which is the only property that makes the chord usable.
    /// </para>
    /// </summary>
    [Test]
    public async Task EachDigitLandsOnThePaneTheRailNumbersWithIt()
    {
        var three = await ThreePanes();
        var (focused, _) = three.App.PaneBandColors;
        var rects = three.App.PaneOutputRects();

        for (var n = 1; n <= 3; n++)
        {
            three.App.SimulateKey(Alt(n));
            var frame = three.App.RenderWholeFrame();

            // The rail's own word for where the now-active window is.
            await Assert.That(RailPaneLabel(three.App))
                .IsEqualTo($"pane {n}")
                .Because($"⌥{n} must land on the pane the sidebar calls pane {n}");

            // The session, so the command line is talking to the pane you are looking at.
            await Assert.That(three.App.ActiveSessionKey).IsEqualTo(three.Sessions[n - 1]);
            await Assert.That(three.App.ActiveWindowId()).IsEqualTo(three.Windows[n - 1]);

            // And the paint: this pane's rectangle carries the focused plane and no other one does.
            var landed = three.App.FocusedPaneId;
            await Assert.That(CellsPaintedIn(frame, rects[landed], focused))
                .IsGreaterThan(0)
                .Because($"⌥{n} must paint pane {n} as the focused one");
            foreach (var other in three.App.PaneIds.Where(id => id != landed))
            {
                await Assert.That(CellsPaintedIn(frame, rects[other], focused)).IsEqualTo(0);
            }
        }
    }

    /// <summary>
    /// And the line typed next goes to that pane's character. Asserted on the bytes the transport received,
    /// because <c>SendUserInputAsync</c> returns immediately with nothing underneath it — "the right world
    /// got it" against an unconnected session is true however broken the routing is.
    /// </summary>
    [Test]
    public async Task TheLineTypedAfterAJumpReachesThatPanesCharacter()
    {
        var three = await ThreePanes();

        three.App.SimulateKey(Alt(3));
        Send(three.App, "look");
        three.App.SimulateKey(Alt(2));
        Send(three.App, "score");

        await Assert.That(three.Transports[2].Lines).IsEquivalentTo(new[] { "look" });
        await Assert.That(three.Transports[1].Lines).IsEquivalentTo(new[] { "score" });
        await Assert.That(three.Transports[0].Lines).IsEmpty();
    }

    /// <summary>
    /// The pane label the ⌃P surface offers and the label the sidebar draws are the same number, for every
    /// pane — the entry is a second door onto the chord, not a second numbering.
    /// </summary>
    [Test]
    public async Task TheCommandSurfaceOffersOneEntryPerPaneAndAgreesWithTheChord()
    {
        var three = await ThreePanes();

        var entries = three.App.BuildCatalog()
            .Where(c => c.Id.StartsWith(CommandIds.PanePrefix, StringComparison.Ordinal))
            .ToList();
        await Assert.That(entries.Count).IsEqualTo(3);

        for (var n = 1; n <= 3; n++)
        {
            var entry = entries.Single(e => e.Id == CommandIds.Pane(n));
            await Assert.That(entry.Title).IsEqualTo($"Go to pane {n}");
            await Assert.That(entry.Subtitle)
                .IsEqualTo($"⌥{n}")
                .Because("an entry that named the wrong chord would be worse than a bare one");

            // Both routes reach the same pane.
            three.App.SimulateKey(Alt(1));
            three.App.SimulateKey(Alt(n));
            var viaKey = three.App.FocusedPaneId;

            three.App.SimulateKey(Alt(1));
            await Assert.That(three.App.DispatchCommand(CommandIds.Pane(n))).IsTrue();
            await Assert.That(three.App.FocusedPaneId).IsEqualTo(viaKey);
        }
    }

    /// <summary>
    /// A single-pane workspace lists none of them. The directional entries are listed unconditionally
    /// because they teach that the workspace splits at all; <c>Go to pane 4</c> on a workspace with one
    /// pane teaches nothing and names a place there is no way to make.
    /// </summary>
    [Test]
    public async Task ASinglePaneWorkspaceOffersNoNumberedEntries()
    {
        var app = App();
        app.RenderSnapshot();

        await Assert.That(app.PaneIds.Count).IsEqualTo(1);
        await Assert.That(app.BuildCatalog().Any(c => c.Id.StartsWith(CommandIds.PanePrefix, StringComparison.Ordinal)))
            .IsFalse();
    }

    // --- out of range: report, never a silent no-op -------------------------------------------------

    /// <summary>
    /// <b>⌥7 with three panes says so.</b> A silent no-op is the most-repeated defect in this codebase's
    /// history, and a digit with no pane behind it is the commonest way to press this chord wrong. The
    /// notice names the digit and the count, and nothing moves.
    /// </summary>
    [Test]
    public async Task AnOutOfRangeDigitReportsAndMovesNothing()
    {
        var three = await ThreePanes();
        three.App.SimulateKey(Alt(2));
        var pane = three.App.FocusedPaneId;
        var session = three.App.ActiveSessionKey;

        foreach (var digit in new[] { 4, 7, 9 })
        {
            three.App.SimulateKey(Alt(digit));

            await Assert.That(three.App.StatusMarkup).Contains($"there is no pane {digit}");
            await Assert.That(three.App.StatusMarkup).Contains("3");
            await Assert.That(three.App.FocusedPaneId).IsEqualTo(pane);
            await Assert.That(three.App.ActiveSessionKey).IsEqualTo(session);
        }
    }

    /// <summary>
    /// On a workspace with one pane every digit past the first is out of range, and the refusal says the
    /// useful thing instead of counting: how to make a second pane. The same sentence ⌃← gives.
    /// </summary>
    [Test]
    public async Task OnOnePaneTheRefusalSaysHowToSplit()
    {
        var app = App();
        app.RenderSnapshot();

        app.SimulateKey(Alt(2));

        await Assert.That(app.StatusMarkup).Contains("one pane");
        await Assert.That(app.StatusMarkup).Contains("⌃B |");
    }

    /// <summary>
    /// <b>Every digit the framework's selector would act on is claimed.</b> <c>HandleAltInput</c> matches
    /// <c>KeyChar</c> '1'–'9' and selects a top-level window by index; it is reached from
    /// <c>InputCoordinator</c>'s fall-through, and a registered application shortcut is tried before the
    /// key is offered to any window. Leaving one digit unclaimed — the out-of-range ones are the
    /// temptation — would hand exactly that digit back to it. So the claim is the whole range, and the
    /// app's own registration is the proof: <c>RegisterGlobalShortcuts</c> throws at startup for a claim
    /// with no action, so an app that constructs at all has all nine wired to something.
    /// </summary>
    [Test]
    public async Task AllNineDigitsAreClaimedSoNoneReachesTheFrameworksWindowSelector()
    {
        var app = App();
        app.RenderSnapshot(); // constructing and registering is itself half the assertion

        for (var n = 1; n <= 9; n++)
        {
            var key = ConsoleKey.D0 + n;
            await Assert.That(MacroKeys.AppShortcuts.Any(s => s.Modifiers == ConsoleModifiers.Alt && s.Key == key))
                .IsTrue()
                .Because($"⌥{n} must be claimed by this app, or the framework's window selector takes it");
            await Assert.That(MacroKeys.PaneJumpNumber(key)).IsEqualTo(n);
        }

        // ⌥0 is deliberately outside the range: the framework ignores it too, so it costs nothing to
        // leave bindable, and F4 says a macro on it fires.
        await Assert.That(MacroKeys.PaneJumpNumber(ConsoleKey.D0)).IsNull();
        await Assert.That(MacroKeys.AppShortcuts.Any(
            s => s.Modifiers == ConsoleModifiers.Alt && s.Key == ConsoleKey.D0)).IsFalse();
        await Assert.That(MacroKeys.Verdict("Alt+0").Fires).IsTrue();
    }

    /// <summary>
    /// And an out-of-range digit really is consumed rather than merely ignored: it produced a notice, which
    /// only this app can write. A key that fell through to the framework would leave the status line alone.
    /// </summary>
    [Test]
    public async Task AnUnusedDigitIsConsumedByThisAppRatherThanPassedOn()
    {
        var app = App();
        app.RenderSnapshot();
        var before = app.StatusMarkup;

        var routed = app.SimulateKey(Alt(9));

        await Assert.That(routed).IsNull();               // nothing was sent to a world
        await Assert.That(app.StatusMarkup).IsNotEqualTo(before);
        await Assert.That(app.ArmedInputText).DoesNotContain("9"); // and it did not type, either
    }

    // --- what must not regress ---------------------------------------------------------------------

    /// <summary>
    /// <b>The focus pin is untouched.</b> The chord moves pane selection and the session behind the command
    /// line; it does not move framework keyboard focus, which stays on the armed bar — the fix for the
    /// paste bug, and the reason typing lands where the caret is drawn.
    /// </summary>
    [Test]
    public async Task JumpingLeavesTheKeyboardOnTheArmedBar()
    {
        var app = App();
        app.RenderSnapshot("split");

        foreach (var digit in new[] { 2, 1, 2 })
        {
            app.SimulateKey(Alt(digit));
            app.RenderNextFrame();
            await Assert.That(app.ArmedBarHasFocus).IsTrue();
        }
    }

    /// <summary>
    /// And it moves no pane rectangle, so no connected world is told a new terminal size. Restated for this
    /// chord for the reason <c>FocusIndicationTests.MovingFocusDoesNotMoveAnyPaneRectangle</c> exists: the
    /// indicator recolours what is drawn and may never grow a cell.
    /// </summary>
    [Test]
    public async Task JumpingMovesNoPaneRectangle()
    {
        var app = App();
        app.RenderSnapshot("split");
        var before = app.PaneOutputRects().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        app.SimulateKey(Alt(2));
        app.RenderNextFrame();
        app.SimulateKey(Alt(1));
        app.RenderNextFrame();

        var after = app.PaneOutputRects();
        await Assert.That(after.Count).IsEqualTo(before.Count);
        foreach (var (paneId, rect) in before)
        {
            await Assert.That(after[paneId]).IsEqualTo(rect);
        }
    }

    // --- zoom ---------------------------------------------------------------------------------------

    /// <summary>
    /// <b>A zoom follows the jump.</b> A zoomed workspace realises exactly one pane, so a mover that
    /// changed the selection and left the zoom where it was would put the selection, the session and the
    /// caret on a pane that is not on the screen. ⌥2 over a zoomed pane 1 therefore shows pane 2 zoomed —
    /// the pane you asked for is the one filling the screen — and ⌃B z still un-zooms.
    /// </summary>
    [Test]
    public async Task JumpingWhileZoomedBringsTheTargetToTheFrontRatherThanHidingIt()
    {
        var app = App();
        app.RenderSnapshot("split");
        var first = app.FocusedPaneId;
        var second = app.PaneIds.Single(id => id != first);

        await Assert.That(app.DispatchCommand("layout:zoom")).IsTrue();
        await Assert.That(app.ZoomedPaneId).IsEqualTo(first);

        app.SimulateKey(Alt(2));
        var frame = app.RenderWholeFrame();

        await Assert.That(app.FocusedPaneId).IsEqualTo(second);
        await Assert.That(app.ZoomedPaneId)
            .IsEqualTo(second)
            .Because("the pane jumped to has to be the one that is rendered");

        // And it is genuinely still a zoom: one pane is realised, and it is the one selected.
        var rects = app.PaneOutputRects();
        await Assert.That(rects.ContainsKey(second)).IsTrue();
        await Assert.That(rects.ContainsKey(first)).IsFalse();

        var (focused, _) = app.PaneBandColors;
        await Assert.That(CellsPaintedIn(frame, rects[second], focused)).IsGreaterThan(0);
    }

    /// <summary>
    /// ⌃O is the other ordinal mover and gets the same rule, so the two cannot come to mean different
    /// things — it used to cycle the selection out from under a zoom and leave it invisible.
    /// </summary>
    [Test]
    public async Task CyclingWhileZoomedCarriesTheZoomToo()
    {
        var app = App();
        app.RenderSnapshot("split");
        var first = app.FocusedPaneId;

        await Assert.That(app.DispatchCommand("layout:zoom")).IsTrue();
        await Assert.That(app.DispatchCommand("layout:cycle")).IsTrue();

        await Assert.That(app.FocusedPaneId).IsNotEqualTo(first);
        await Assert.That(app.ZoomedPaneId).IsEqualTo(app.FocusedPaneId);
    }

    // --- one spelling of one pane -------------------------------------------------------------------

    /// <summary>
    /// <b>The move overlay and the sidebar call the first pane the same thing.</b> The overlay used to call
    /// it <c>main</c> — the spelling the rail abandoned because it collided with the <em>window</em> named
    /// main — so the same pane was <c>pane 1</c> in the sidebar and <c>main</c> under the cursor. ⌥1 is a
    /// chord that lands on a pane a label names, and it cannot survive two labels.
    /// </summary>
    [Test]
    public async Task TheMoveOverlayAndTheSidebarSpellTheFirstPaneTheSameWay()
    {
        var app = App();
        app.RenderSnapshot("split");

        // ⌃B m lifts the active window; '1' targets the first pane — the same digit ⌥1 uses.
        app.SimulateKey(Ctrl(ConsoleKey.B));
        app.SimulateKey(Plain('m', ConsoleKey.M));
        app.SimulateKey(Plain('1', ConsoleKey.D1));

        await Assert.That(app.StatusMarkup).Contains("pane 1");
        await Assert.That(app.StatusMarkup)
            .DoesNotContain("main")
            .Because("the first pane is pane 1 everywhere, or ⌥1 names something the screen does not");
        await Assert.That(app.RailLines.Any(l => l.Contains("pane 1", StringComparison.Ordinal))).IsTrue();

        app.SimulateKey(Plain('\x1b', ConsoleKey.Escape)); // leave move mode
    }

    /// <summary>
    /// <b>Move mode targets by the same number as everything else.</b> It used to letter the panes
    /// <c>a</c>–<c>j</c> while the prompt one line below named the target <c>pane N</c> — one ordering
    /// spelt in two alphabets, which meant translating <c>B</c> into <c>pane 2</c> in your head to use
    /// the feature the prompt was explaining.
    /// <para>
    /// Driven for every pane: press the digit, and the target the prompt reports is the pane the rail
    /// numbers with that digit. The prompt is read rather than the badge because the prompt is what
    /// names the pane in words; the badge is asserted separately by the digit having worked at all — an
    /// unmapped digit leaves the target alone, so a wrong badge cannot produce a right prompt.
    /// </para>
    /// </summary>
    [Test]
    public async Task MoveModeTargetsThePaneTheRailNumbersWithTheSameDigit()
    {
        var three = await ThreePanes();

        three.App.SimulateKey(Ctrl(ConsoleKey.B));
        three.App.SimulateKey(Plain('m', ConsoleKey.M));

        for (var n = 1; n <= 3; n++)
        {
            three.App.SimulateKey(Plain((char)('0' + n), ConsoleKey.D0 + n));

            await Assert.That(three.App.StatusMarkup)
                .Contains($"pane {n}")
                .Because($"pressing {n} in move mode must target the pane the sidebar calls pane {n}");
        }

        // A digit past the last pane leaves the target where it was rather than clearing it.
        three.App.SimulateKey(Plain('9', ConsoleKey.D9));
        await Assert.That(three.App.StatusMarkup)
            .Contains("pane 3")
            .Because("an out-of-range digit must not drop the target you already picked");

        // And the letters are gone: 'b' is not a pane picker any more.
        three.App.SimulateKey(Plain('b', ConsoleKey.B));
        await Assert.That(three.App.StatusMarkup).Contains("pane 3");

        three.App.SimulateKey(Plain('\x1b', ConsoleKey.Escape));
    }

    // --- honesty ------------------------------------------------------------------------------------

    /// <summary>
    /// <c>--help</c> names the chord that works and says why the one that was asked for is absent. Both
    /// halves: a page that named Ctrl+digit would send a reader to press Escape and Backspace.
    /// </summary>
    [Test]
    public async Task HelpNamesAltDigitAndSaysWhyNotCtrlDigit()
    {
        var help = Program.UsageText;

        await Assert.That(help).Contains("Alt+1..Alt+9");
        await Assert.That(help).Contains("Ctrl+digit is not");
        await Assert.That(help).Contains("Escape and");
    }

    /// <summary>
    /// F4 reports each of the nine as taken, and the sentence it prints names the pane the chord goes to —
    /// so a user who tried to bind a macro there is told what has it, not merely that something does.
    /// </summary>
    [Test]
    public async Task TheKeypadScreenSaysWhatHasEachDigit()
    {
        for (var n = 1; n <= 9; n++)
        {
            var verdict = MacroKeys.Verdict($"Alt+{n}");

            await Assert.That(verdict.Delivery).IsEqualTo(MacroKeyDelivery.Taken);
            await Assert.That(verdict.Reason).Contains($"pane {n}");
        }
    }

    /// <summary>
    /// And every Ctrl+digit is reported as never arriving, each with the byte the terminal really sends —
    /// observed on a pty, not assumed. Three of them are keys this client cannot afford to bind over.
    /// </summary>
    [Test]
    [Arguments(0, "bare 0")]
    [Arguments(1, "bare 1")]
    [Arguments(2, "NUL")]
    [Arguments(3, "Escape")]
    [Arguments(8, "Backspace")]
    [Arguments(9, "bare 9")]
    public async Task CtrlDigitIsReportedAsUnreachableWithTheByteTheTerminalSends(int digit, string byteName)
    {
        var verdict = MacroKeys.Verdict($"Ctrl+{digit}");

        await Assert.That(verdict.Delivery).IsEqualTo(MacroKeyDelivery.NeverArrives);
        await Assert.That(verdict.Reason).Contains(byteName);
    }

    // --- harness ------------------------------------------------------------------------------------

    private static SharpMUTermApp App(int width = 120, int height = 34)
    {
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(width, height));
    }

    /// <summary>The chord as the terminal delivers it: <c>ESC</c> + the digit, decoded as that digit with Alt.</summary>
    private static ConsoleKeyInfo Alt(int digit) =>
        new((char)('0' + digit), ConsoleKey.D0 + digit, false, true, false);

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

    private static void Send(SharpMUTermApp app, string line)
    {
        Type(app, line);
        app.SimulateKey(Plain('\r', ConsoleKey.Enter));
    }

    /// <summary>
    /// The pane label the rail is drawing for the active character's <em>window</em>, e.g. <c>pane 2</c>.
    /// Read out of the rendered rows rather than recomputed, because the whole assertion is that the chord
    /// and the sidebar agree — a re-derived label would agree with itself.
    /// <para>
    /// Window rows are picked out by their <c>▪</c> bullet, because character rows now carry the same
    /// column: the rail says which pane every character is in, active or not, which is what makes the
    /// numbering readable from a character other than the one you are looking at. Taking the first
    /// <c>pane N</c> on any row would read the active character's own row and answer 1 for ever.
    /// </para>
    /// </summary>
    private static string RailPaneLabel(SharpMUTermApp app)
    {
        foreach (var line in app.RailLines.Where(l => l.Contains('▪', StringComparison.Ordinal)))
        {
            var match = Regex.Match(line, @"pane \d+");
            if (match.Success)
            {
                return match.Value;
            }
        }

        throw new InvalidOperationException(
            $"the rail is drawing no pane label: {string.Join(" / ", app.RailLines)}");
    }

    /// <summary>The truecolor background escape a colour is written as.</summary>
    private static string Sgr(SharpConsoleUI.Color color) => $"48;2;{color.R};{color.G};{color.B}";

    /// <summary>
    /// Walks a frame into a <c>{(row, column): background}</c> grid, the way a terminal walks it. Note
    /// <c>48</c> and not <c>38</c> — reading foreground here and concluding about planes is the classic
    /// mistake. Same walker as <see cref="FocusIndicationTests"/>, deliberately: this suite's claim is
    /// about the same painted planes.
    /// </summary>
    private static Dictionary<(int Row, int Column), string?> Backgrounds(string ansi)
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

    /// <summary>How many cells inside a rectangle are painted in a given background.</summary>
    private static int CellsPaintedIn(string ansi, PaneRect rect, SharpConsoleUI.Color colour)
    {
        var wanted = Sgr(colour);
        var cells = Backgrounds(ansi);
        var count = 0;
        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
        {
            for (var x = rect.X; x < rect.X + rect.Width; x++)
            {
                if (cells.GetValueOrDefault((y, x))?.StartsWith(wanted, StringComparison.Ordinal) == true)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Three panes side by side, one connected character each, in a known order.</summary>
    private sealed record Three(
        SharpMUTermApp App,
        IReadOnlyList<string> Windows,
        IReadOnlyList<string> Sessions,
        IReadOnlyList<RecordingTelnetSession> Transports);

    /// <summary>
    /// A <em>resumed</em> workspace of three panes, each holding one character's window, built the way the
    /// shell restores one. Three separate worlds so each session's writes are attributable by host — the
    /// suite turns on which transport a line reached, and one world's characters would share one.
    /// <para>
    /// The pane order is the split tree's, which is what <c>Layout.Panes</c> enumerates and what the rail
    /// numbers; the windows are placed p1/p2/p3 so the expected pairing is stated once, here, and every
    /// assertion afterwards reads the rail rather than restating it.
    /// </para>
    /// </summary>
    private static async Task<Three> ThreePanes()
    {
        Console.SetIn(TextReader.Null);
        var config = new AppConfiguration();
        var names = new[] { ("Alfa", "Ann"), ("Bravo", "Bob"), ("Cara", "Cal") };
        foreach (var (world, character) in names)
        {
            var definition = new WorldDefinition
            {
                Name = world,
                Host = $"{world.ToLowerInvariant()}.example.org",
                Port = 4000,
            };
            definition.Characters.Add(new CharacterDefinition { Name = character, Logging = new LoggingSettings() });
            config.Worlds.Add(definition);
        }

        var windows = new[] { "main", "char:Bravo.Bob", "char:Cara.Cal" };
        var sessions = names.Select(n => $"{n.Item1}.{n.Item2}").ToArray();

        config.LastSession = new WorkspaceState
        {
            Windows =
            {
                new WorkspaceWindowState
                {
                    Id = windows[0], Title = "Ann", Kind = WindowKind.Main, SessionKey = sessions[0],
                },
                new WorkspaceWindowState
                {
                    Id = windows[1], Title = "Bob", Kind = WindowKind.Main, SessionKey = sessions[1],
                },
                new WorkspaceWindowState
                {
                    Id = windows[2], Title = "Cal", Kind = WindowKind.Main, SessionKey = sessions[2],
                },
            },
            Root = new LayoutNodeState
            {
                Type = "split",
                Direction = SplitDirection.Row,
                Children =
                {
                    new LayoutNodeState { Type = "pane", Id = "p1", Tabs = { windows[0] }, ActiveIndex = 0 },
                    new LayoutNodeState { Type = "pane", Id = "p2", Tabs = { windows[1] }, ActiveIndex = 0 },
                    new LayoutNodeState { Type = "pane", Id = "p3", Tabs = { windows[2] }, ActiveIndex = 0 },
                },
            },
            FocusedPaneId = "p1",
        };

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        var transports = new[]
        {
            new RecordingTelnetSession(), new RecordingTelnetSession(), new RecordingTelnetSession(),
        };
        app.TelnetFactory = options => options.Host[0] switch
        {
            'a' => transports[0],
            'b' => transports[1],
            _ => transports[2],
        };

        foreach (var key in sessions)
        {
            if (!app.DispatchCommand(CommandIds.Character(key)))
            {
                throw new InvalidOperationException($"the app would not switch to {key}");
            }

            await app.FindSession(key)!.ConnectAsync();
        }

        app.RenderNextFrame();

        // The fixture's own claim: three panes, in the order the windows were placed. Everything after
        // this reads the rail, so a resumed layout that came back differently must fail here and not
        // silently make the assertions vacuous.
        if (app.PaneIds.Count != 3)
        {
            throw new InvalidOperationException($"the resumed workspace has {app.PaneIds.Count} panes, not 3");
        }

        for (var i = 0; i < 3; i++)
        {
            if (app.PaneIdOf(windows[i]) != app.PaneIds[i])
            {
                throw new InvalidOperationException(
                    $"{windows[i]} is in {app.PaneIdOf(windows[i])}, not the {i + 1}th pane {app.PaneIds[i]}");
            }
        }

        return new Three(app, windows, sessions, transports);
    }
}
