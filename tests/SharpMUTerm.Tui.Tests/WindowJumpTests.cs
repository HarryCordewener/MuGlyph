using SharpMUTerm.Core.Automation;
using System.Text.RegularExpressions;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// <b>⌥1–⌥9 go to a numbered window and bring it forward.</b> The request was "I mentioned before how I
/// wanted Alt-1-9 etc to be able to switch between characters. I realize I may have used the wrong term.
/// I want it to be able to go between tabs? Panes? Whichever it is that allows me to switch not just
/// characters, but captures, etc." The answer to all of those at once is the <em>window</em>, and these
/// are the claims it stands on.
/// <para>
/// <b>1. It reaches a capture window, which is the half the pane chord could not do.</b> ⌥N used to name
/// a pane, and a capture sharing a pane with its character's own window had no number: it was reachable
/// only while it happened to be that pane's active tab. Every window a pane holds has a digit now,
/// whoever owns it and whichever tab is in front.
/// </para>
/// <para>
/// <b>2. The number is the number on the screen.</b> Windows are counted in <c>PlacedWindows</c> order,
/// which is the order the connection rail's second column prints <c>⌥N</c> in — so the assertions read
/// the label off the <em>live rail</em> and press the digit that label names, rather than writing down
/// which window ought to be third. A chord that lands somewhere other than the label says is worse than
/// no chord, and this repository has already paid for two spellings of one thing once
/// (<c>▪ main   main</c>).
/// </para>
/// <para>
/// <b>3. It is the full activation, on painted cells.</b> "Bring it forward" is not <c>FocusedPaneId</c>
/// being assigned: it is the window active in its pane's strip, that pane's plane on the frame, and the
/// command line talking to its character. All three are asserted, the plane off the frame the driver was
/// handed — a focus indicator can be set on a control arranged at zero rows and read back happily.
/// </para>
/// <para>
/// <b>4. Nothing falls through to the framework.</b> SharpConsoleUI claims Alt+1–9 for its own top-level
/// window selector (<c>InputCoordinator.HandleAltInput</c>), which — unlike the move and resize handlers
/// beside it — is <em>not</em> gated on <c>IsMovable</c>/<c>IsResizable</c>, so <c>Movable(false)</c> did
/// not switch it off. All nine digits are claimed as application shortcuts, which
/// <c>InputCoordinator</c> tries before it offers the key to a window at all; an out-of-range digit
/// therefore reports here and stops rather than reaching a window selector that would do something else.
/// </para>
/// <para>
/// <b>5. Alt, because Ctrl+digit is not a chord this terminal has.</b> Read off a real pty rather than
/// remembered: every Alt+digit is <c>ESC</c> + the digit, while Ctrl+digit is the bare digit for 1/9/0 and
/// a byte already spelt Escape (3), Backspace (8) or NUL (2) for the rest. <c>MacroKeys.Verdict</c> is
/// where that is recorded, and <see cref="MacroKeyCaptureTests"/> holds it. See
/// <see cref="PaneJumpTests"/> for the numbered <em>pane</em> jump this displaced onto ⌃B.
/// </para>
/// </summary>
/// <remarks>
/// Serialised with the other end-to-end suites: constructing the app and rendering a frame both touch the
/// process-global console streams.
/// </remarks>
[NotInParallel]
public class WindowJumpTests
{
    private const int Width = 160;
    private const int Height = 40;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    // --- the numbering, against the rail's own labels ----------------------------------------------

    /// <summary>
    /// <b>The claim, end to end.</b> Three characters in three panes, and one of them with a capture
    /// window as well — four windows, four digits. For every digit: press ⌥N, and the window that comes
    /// to the front is the window the rail labels <c>⌥N</c>, with its pane focused on the painted frame
    /// and its character on the command line.
    /// <para>
    /// The label is read from <see cref="SharpMUTermApp.RailLines"/> after the jump, which is the rail the
    /// app really drew. Nothing here writes down which window is third: the assertion is that the two
    /// agree, which is the only property that makes the chord usable.
    /// </para>
    /// </summary>
    [Test]
    public async Task EachDigitLandsOnTheWindowTheRailNumbersWithIt()
    {
        var scene = await BuildScene();
        var (focused, _) = scene.App.PaneBandColors;

        for (var n = 1; n <= scene.Windows.Count; n++)
        {
            scene.App.SimulateKey(Alt(n));
            var frame = scene.App.RenderWholeFrame();

            await Assert.That(scene.App.ActiveWindowId())
                .IsEqualTo(scene.Windows[n - 1])
                .Because($"⌥{n} must bring forward the window the sidebar labels ⌥{n}");

            // The rail's own word for the window that is now in front.
            await Assert.That(WindowRowChord(scene.App, scene.Labels[n - 1]))
                .IsEqualTo($"⌥{n}")
                .Because("the digit pressed and the digit drawn beside the window that arrived are one number");

            // The session, so the command line is talking to the window you are looking at.
            await Assert.That(scene.App.ActiveSessionKey).IsEqualTo(scene.Owners[n - 1]);

            // And the paint: the hosting pane's rectangle carries the focused plane and no other one does.
            var rects = scene.App.PaneOutputRects();
            var landed = scene.App.FocusedPaneId;
            await Assert.That(landed).IsEqualTo(scene.App.PaneIdOf(scene.Windows[n - 1]));
            await Assert.That(CellsPaintedIn(frame, rects[landed], focused))
                .IsGreaterThan(0)
                .Because($"⌥{n} must paint the pane holding window {n} as the focused one");
            foreach (var other in scene.App.PaneIds.Where(id => id != landed))
            {
                await Assert.That(CellsPaintedIn(frame, rects[other], focused)).IsEqualTo(0);
            }
        }
    }

    /// <summary>
    /// <b>The half a pane-numbered chord could not do: a capture window behind another tab.</b> Corvid's
    /// Chat window shares a pane with Corvid's own window and is not the tab in front — under the old
    /// chord there was no digit that reached it, because the pane's digit went to whatever the pane was
    /// already showing. Pressing its digit switches the tab as well as the pane.
    /// </summary>
    [Test]
    public async Task ADigitReachesACaptureWindowSittingBehindAnotherTabInItsPane()
    {
        var scene = await BuildScene();
        var chat = Workspace.SpawnWindowId("Chat");
        var chatPane = scene.App.PaneIdOf(chat)!;

        // Stand in Chat's own pane, on the other tab, so nothing but the tab has to move.
        scene.App.SimulateKey(Alt(scene.Windows.ToList().IndexOf("main") + 1));
        await Assert.That(scene.App.PaneIdOf("main")).IsEqualTo(chatPane);
        await Assert.That(scene.App.ActiveWindowId()).IsNotEqualTo(chat);

        scene.App.SimulateKey(Alt(scene.Windows.ToList().IndexOf(chat) + 1));

        await Assert.That(scene.App.ActiveWindowId())
            .IsEqualTo(chat)
            .Because("a capture window is exactly what the chord was asked to reach");
        await Assert.That(scene.App.FocusedPaneId).IsEqualTo(chatPane);
    }

    /// <summary>
    /// And the line typed next goes to that window's character. Asserted on the bytes the transport
    /// received, because <c>SendUserInputAsync</c> returns immediately with nothing underneath it — "the
    /// right world got it" against an unconnected session is true however broken the routing is.
    /// </summary>
    [Test]
    public async Task TheLineTypedAfterAJumpReachesThatWindowsCharacter()
    {
        var scene = await BuildScene();

        Send(scene.App, Digit(scene, "char:Cara.Cal"), "look");
        Send(scene.App, Digit(scene, "char:Bravo.Bob"), "score");

        await Assert.That(scene.Transports[2].Lines).IsEquivalentTo(new[] { "look" });
        await Assert.That(scene.Transports[1].Lines).IsEquivalentTo(new[] { "score" });
        await Assert.That(scene.Transports[0].Lines).IsEmpty();
    }

    /// <summary>
    /// The ⌃P surface offers each window with the chord that goes to it, and the digit it names is the
    /// digit the sidebar draws — the entry is a second door onto the chord, not a second numbering. Both
    /// routes are then driven and land on the same window.
    /// </summary>
    [Test]
    public async Task TheCommandSurfaceNamesEachWindowsChordAndAgreesWithIt()
    {
        var scene = await BuildScene();

        // Stand somewhere fixed: the catalog skips whichever window is active, so the entry under test
        // has to be one of the others.
        scene.App.SimulateKey(Alt(1));

        var entries = scene.App.BuildCatalog()
            .Where(c => c.Id.StartsWith(CommandIds.WindowPrefix, StringComparison.Ordinal))
            .ToDictionary(c => c.Id, c => c.Subtitle, StringComparer.Ordinal);

        for (var n = 2; n <= scene.Windows.Count; n++)
        {
            var subtitle = entries[CommandIds.Window(scene.Windows[n - 1])];
            await Assert.That(subtitle).IsNotNull();
            await Assert.That(subtitle!)
                .StartsWith($"⌥{n} · ")
                .Because("an entry that named the wrong chord would be worse than a bare one");

            scene.App.SimulateKey(Alt(n));
            var viaKey = scene.App.ActiveWindowId();

            scene.App.SimulateKey(Alt(1));
            await Assert.That(scene.App.DispatchCommand(CommandIds.Window(scene.Windows[n - 1]))).IsTrue();
            await Assert.That(scene.App.ActiveWindowId()).IsEqualTo(viaKey);
        }
    }

    /// <summary>
    /// <b>The chord the rail prints against another character reaches that character.</b> Window rows are
    /// drawn for the active character only, so a background character's digit has to be legible on their
    /// own row or the chord is not a character switch at all — which is what "switch between characters"
    /// asked for. The digit is read out of the rendered sidebar while somebody else is active, and then
    /// pressed.
    /// </summary>
    [Test]
    public async Task PressingTheDigitTheRailPrintsAgainstACharacterGoesToThatCharacter()
    {
        var scene = await BuildScene();

        foreach (var (name, key) in new[] { ("Cal", "Cara.Cal"), ("Bob", "Bravo.Bob"), ("Ann", "Alfa.Ann") })
        {
            // Stand somewhere else first, so the row being read is an inactive character's.
            scene.App.SimulateKey(Alt(1));
            scene.App.RenderNextFrame();

            var digit = int.Parse(CharacterChord(scene.App, name)![1..]);
            scene.App.SimulateKey(Alt(digit));

            await Assert.That(scene.App.ActiveSessionKey)
                .IsEqualTo(key)
                .Because($"the rail said {name} was ⌥{digit}");
        }
    }

    /// <summary>
    /// <b>Closing a window compacts the numbering, on the chord and in the sidebar together.</b> Creation
    /// sequences are never reused, so a number read straight off one would leave a hole: a digit would
    /// report "there is no window" while the windows sat on the screen, and the last one would be
    /// reachable only by a digit past the count. The number is the position in the numbering for exactly
    /// this reason, and the two surfaces are asserted together because a chord that disagrees with the
    /// label is the defect this numbering exists to avoid.
    /// </summary>
    [Test]
    public async Task ClosingAWindowCompactsTheNumberingOnTheChordAndInTheSidebar()
    {
        var scene = await BuildScene();
        var chat = Workspace.SpawnWindowId("Chat");
        var count = scene.Windows.Count;

        // Cal's window is last, so Cal's row wears the highest digit — until Chat, ahead of it, goes away.
        await Assert.That(CharacterChord(scene.App, "Cal")).IsEqualTo($"⌥{count}");

        scene.App.SimulateKey(Alt(scene.Windows.ToList().IndexOf(chat) + 1));
        await Assert.That(scene.App.ActiveWindowId()).IsEqualTo(chat);
        await Assert.That(scene.App.DispatchCommand("layout:close")).IsTrue();
        scene.App.RenderNextFrame();

        await Assert.That(scene.App.PlacedWindowIds).DoesNotContain(chat);
        await Assert.That(CharacterChord(scene.App, "Cal"))
            .IsEqualTo($"⌥{count - 1}")
            .Because("the windows on the screen must be numbered without a hole where the closed one was");

        scene.App.SimulateKey(Alt(1));
        scene.App.SimulateKey(Alt(count - 1));
        await Assert.That(scene.App.ActiveSessionKey)
            .IsEqualTo("Cara.Cal")
            .Because("the last window's digit must follow the compaction the sidebar drew");

        scene.App.SimulateKey(Alt(count));
        await Assert.That(scene.App.StatusMarkup).Contains($"there is no window {count}");
        await Assert.That(scene.App.ActiveSessionKey).IsEqualTo("Cara.Cal");
    }

    /// <summary>
    /// <b>A window arriving from the wire lands at the end and displaces nobody.</b> A capture opening is
    /// the commonest way this numbering changes and it is not a thing the user did — so if the order were
    /// a function of position rather than of creation, a channel's first line would renumber the chords
    /// under someone mid-sentence. Driven through a real trigger set and real server text, because the
    /// claim is about the path that actually opens these windows.
    /// </summary>
    [Test]
    public async Task AWindowOpenedByTheWireTakesTheNextDigitAndMovesNobodyElses()
    {
        var scene = await BuildScene();
        scene.App.SimulateKey(Alt(1));
        scene.App.RenderNextFrame();

        var before = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["main"] = WindowRowChord(scene.App, "main"),
            ["Chat"] = WindowRowChord(scene.App, "Chat"),
            ["Bob"] = CharacterChord(scene.App, "Bob"),
            ["Cal"] = CharacterChord(scene.App, "Cal"),
        };

        scene.Transports[0].Receive("<Trade> Ann offers a lamp\n");
        scene.App.RenderNextFrame();

        var arrival = Workspace.SpawnWindowId("Trade");
        await Assert.That(scene.App.PlacedWindowIds).Contains(arrival);

        foreach (var (what, chord) in before)
        {
            var now = what is "main" or "Chat"
                ? WindowRowChord(scene.App, what)
                : CharacterChord(scene.App, what);
            await Assert.That(now)
                .IsEqualTo(chord)
                .Because($"{what} was {chord} before a channel opened and must still be");
        }

        await Assert.That(WindowRowChord(scene.App, "Trade")).IsEqualTo($"⌥{scene.Windows.Count + 1}");

        scene.App.SimulateKey(Alt(scene.Windows.Count + 1));
        await Assert.That(scene.App.ActiveWindowId()).IsEqualTo(arrival);
    }

    /// <summary>
    /// A single-window workspace lists no chord on any row: with one place to be, the digit is not
    /// information, and three cells of sidebar come out of the pane the reader is looking at.
    /// </summary>
    [Test]
    public async Task ASingleWindowWorkspaceNamesNoChordOnAnyRow()
    {
        Console.SetIn(TextReader.Null);
        var config = DemoScene.Build();
        config.LastSession!.Windows.RemoveAll(w => w.Kind == WindowKind.Spawn);
        config.LastSession.Root.Tabs.RemoveAll(t => t.StartsWith("spawn:", StringComparison.Ordinal));

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(120, 34));
        app.RenderSnapshot();

        await Assert.That(app.PlacedWindowIds.Count).IsEqualTo(1);
        await Assert.That(app.RailLines.Any(l => Regex.IsMatch(l, @"⌥\d")))
            .IsFalse()
            .Because("with one window, naming it says nothing and costs the panes their columns");
    }

    // --- out of range: report, never a silent no-op -------------------------------------------------

    /// <summary>
    /// <b>⌥7 with four windows says so.</b> A silent no-op is the most-repeated defect in this codebase's
    /// history, and a digit with no window behind it is the commonest way to press this chord wrong. The
    /// notice names the digit and the count, and nothing moves.
    /// </summary>
    [Test]
    public async Task AnOutOfRangeDigitReportsAndMovesNothing()
    {
        var scene = await BuildScene();
        scene.App.SimulateKey(Alt(2));
        var window = scene.App.ActiveWindowId();
        var session = scene.App.ActiveSessionKey;

        foreach (var digit in new[] { 5, 7, 9 })
        {
            scene.App.SimulateKey(Alt(digit));

            await Assert.That(scene.App.StatusMarkup).Contains($"there is no window {digit}");
            await Assert.That(scene.App.StatusMarkup).Contains(scene.Windows.Count.ToString());
            await Assert.That(scene.App.ActiveWindowId()).IsEqualTo(window);
            await Assert.That(scene.App.ActiveSessionKey).IsEqualTo(session);
        }
    }

    /// <summary>
    /// On a workspace with one window every digit past the first is out of range, and the refusal says
    /// the useful thing instead of counting: the two ways a second window comes into being.
    /// </summary>
    [Test]
    public async Task OnOneWindowTheRefusalSaysHowToOpenAnother()
    {
        Console.SetIn(TextReader.Null);
        var config = DemoScene.Build();
        config.LastSession!.Windows.RemoveAll(w => w.Kind == WindowKind.Spawn);
        config.LastSession.Root.Tabs.RemoveAll(t => t.StartsWith("spawn:", StringComparison.Ordinal));

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(120, 34));
        app.RenderSnapshot();

        app.SimulateKey(Alt(2));

        await Assert.That(app.StatusMarkup).Contains("one window");
        await Assert.That(app.StatusMarkup).Contains("F5");
        await Assert.That(app.StatusMarkup).Contains("F2");
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
            await Assert.That(MacroKeys.WindowJumpNumber(key)).IsEqualTo(n);
        }

        // ⌥0 is deliberately outside the range: the framework ignores it too, so it costs nothing to
        // leave bindable, and F4 says a macro on it fires.
        await Assert.That(MacroKeys.WindowJumpNumber(ConsoleKey.D0)).IsNull();
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
    /// <b>The focus pin is untouched.</b> The chord moves which window is in front and the session behind
    /// the command line; it does not move framework keyboard focus, which stays on the armed bar — the fix
    /// for the paste bug, and the reason typing lands where the caret is drawn.
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
    /// changed which window is in front and left the zoom where it was would put the selection, the
    /// session and the caret on a pane that is not on the screen. ⌥N onto a window in another pane
    /// therefore shows that pane zoomed — the window you asked for is the one filling the screen — and
    /// ⌃B z still un-zooms.
    /// </summary>
    [Test]
    public async Task JumpingWhileZoomedBringsTheTargetToTheFrontRatherThanHidingIt()
    {
        var app = App();
        app.RenderSnapshot("split");
        var first = app.FocusedPaneId;
        var second = app.PaneIds.Single(id => id != first);
        var elsewhere = app.PlacedWindowIds.ToList().FindIndex(id => app.PaneIdOf(id) == second) + 1;
        await Assert.That(elsewhere).IsGreaterThan(0);

        await Assert.That(app.DispatchCommand("layout:zoom")).IsTrue();
        await Assert.That(app.ZoomedPaneId).IsEqualTo(first);

        app.SimulateKey(Alt(elsewhere));
        var frame = app.RenderWholeFrame();

        await Assert.That(app.FocusedPaneId).IsEqualTo(second);
        await Assert.That(app.ZoomedPaneId)
            .IsEqualTo(second)
            .Because("the pane holding the window jumped to has to be the one that is rendered");

        // And it is genuinely still a zoom: one pane is realised, and it is the one selected.
        var rects = app.PaneOutputRects();
        await Assert.That(rects.ContainsKey(second)).IsTrue();
        await Assert.That(rects.ContainsKey(first)).IsFalse();

        var (focused, _) = app.PaneBandColors;
        await Assert.That(CellsPaintedIn(frame, rects[second], focused)).IsGreaterThan(0);
    }

    // --- honesty ------------------------------------------------------------------------------------

    /// <summary>
    /// <c>--help</c> names the chord that works and says why the one that was asked for is absent. Both
    /// halves: a page that named Ctrl+digit would send a reader to press Escape and Backspace. It says
    /// <em>window</em>, because a page still calling this a pane jump would send a reader looking for the
    /// pane numbering on the sidebar, which is not where it is drawn.
    /// </summary>
    [Test]
    public async Task HelpNamesAltDigitAsAWindowJumpAndSaysWhyNotCtrlDigit()
    {
        var help = Program.UsageText;

        await Assert.That(help).Contains("Alt+1..Alt+9");
        await Assert.That(help).Contains("numbered window");
        await Assert.That(help).Contains("Ctrl+digit is not");
        await Assert.That(help).Contains("Escape");
    }

    /// <summary>
    /// F4 reports each of the nine as taken, and the sentence it prints names the window the chord goes to
    /// — so a user who tried to bind a macro there is told what has it, not merely that something does.
    /// </summary>
    [Test]
    public async Task TheKeypadScreenSaysWhatHasEachDigit()
    {
        for (var n = 1; n <= 9; n++)
        {
            var verdict = MacroKeys.Verdict($"Alt+{n}");

            await Assert.That(verdict.Delivery).IsEqualTo(MacroKeyDelivery.Taken);
            await Assert.That(verdict.Reason).Contains($"window {n}");
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

    /// <summary>Jumps to a window by id, empties the armed bar, and sends <paramref name="line"/>.</summary>
    private static void Send(SharpMUTermApp app, int digit, string line)
    {
        app.SimulateKey(Alt(digit));
        app.SimulateKey(Ctrl(ConsoleKey.E));
        app.SimulateKey(Ctrl(ConsoleKey.U));
        foreach (var c in line)
        {
            app.SimulateKey(Plain(c, ConsoleKey.NoName));
        }

        app.SimulateKey(Plain('\r', ConsoleKey.Enter));
    }

    private static int Digit(Scene scene, string windowId) => scene.Windows.ToList().IndexOf(windowId) + 1;

    /// <summary>
    /// The <c>⌥N</c> the rail prints on the window row labelled <paramref name="label"/>. Read out of the
    /// rendered rows rather than recomputed, because the whole assertion is that the chord and the
    /// sidebar agree — a re-derived label would agree with itself. Window rows are picked out by their
    /// <c>▪</c> bullet, because character rows carry the same column; the rail calls a character's own
    /// session window <c>main</c> and gives everything else its title.
    /// </summary>
    private static string? WindowRowChord(SharpMUTermApp app, string label)
    {
        foreach (var line in app.RailLines.Select(Visible))
        {
            if (!Regex.IsMatch(line, $@"▪ {Regex.Escape(label)}(?![^\s])"))
            {
                continue;
            }

            var match = Regex.Match(line, @"⌥\d");
            return match.Success ? match.Value : null;
        }

        throw new InvalidOperationException(
            $"no rail window row for {label}: {string.Join(" / ", app.RailLines.Select(Visible))}");
    }

    /// <summary>
    /// The <c>⌥N</c> the rail prints on <paramref name="character"/>'s own row, or null.
    /// <para>
    /// Read off the row's <em>visible</em> cells, with the markup stripped first. A rail row is wrapped in
    /// a <c>[link=cmd%3Acharacter%3AAlfa.Ann]</c> span, and a world row's target is one of its characters'
    /// — so matching the raw markup finds "Ann" on the <c>Alfa</c> row above hers, which has no chord and
    /// never should. Character rows are told apart from window rows by the <c>▪</c> bullet the latter
    /// carry, both using this same column.
    /// </para>
    /// </summary>
    private static string? CharacterChord(SharpMUTermApp app, string character)
    {
        foreach (var line in app.RailLines.Select(Visible))
        {
            if (line.Contains('▪', StringComparison.Ordinal) ||
                !Regex.IsMatch(line, $@"(?<![A-Za-z]){Regex.Escape(character)}(?![A-Za-z])"))
            {
                continue;
            }

            var match = Regex.Match(line, @"⌥\d");
            return match.Success ? match.Value : null;
        }

        throw new InvalidOperationException(
            $"no rail row for {character}: {string.Join(" / ", app.RailLines.Select(Visible))}");
    }

    /// <summary>A rail row's cells, with its style and link markup removed.</summary>
    private static string Visible(string markup) =>
        Regex.Replace(markup, @"\[(?:/|[^\]\[]*)\]", string.Empty).Replace("[[", "[").Replace("]]", "]");

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

    /// <summary>Three panes, one connected character each, plus one capture window behind a tab.</summary>
    /// <param name="Labels">
    /// What the rail calls each window on its own row, parallel to <paramref name="Windows"/>: a
    /// character's own session window reads <c>main</c>, a capture keeps its title.
    /// </param>
    private sealed record Scene(
        SharpMUTermApp App,
        IReadOnlyList<string> Windows,
        IReadOnlyList<string> Labels,
        IReadOnlyList<string> Owners,
        IReadOnlyList<RecordingTelnetSession> Transports);

    /// <summary>
    /// A <em>resumed</em> workspace built the way the shell restores one: three panes, one character's
    /// window in each, and a fourth window — Ann's <c>Chat</c> capture — sharing Ann's pane as a
    /// background tab. Three separate worlds so each session's writes are attributable by host; one
    /// world's characters would share a transport.
    /// <para>
    /// The capture is the point of the fixture. It is the window the old pane-numbered chord could not
    /// reach, and it is placed <em>second</em> in creation order so the digits are not simply the pane
    /// numbers wearing a new name — a suite where the two orders coincided would pass against either.
    /// </para>
    /// </summary>
    private static async Task<Scene> BuildScene()
    {
        Console.SetIn(TextReader.Null);
        var config = new AppConfiguration();

        // A live capture rule, so the "a window arrives from the wire" case can be driven through the real
        // path — a trigger matching a real line — rather than by poking a window into the workspace.
        config.TriggerSets.Add(new TriggerSet
        {
            Name = "Comms",
            Triggers =
            {
                new Trigger
                {
                    Name = "Trade",
                    Pattern = "^<Trade>",
                    Actions = new TriggerActions { SpawnTarget = "Trade" },
                },
            },
        });

        var names = new[] { ("Alfa", "Ann"), ("Bravo", "Bob"), ("Cara", "Cal") };
        foreach (var (world, character) in names)
        {
            var definition = new WorldDefinition
            {
                Name = world,
                Host = $"{world.ToLowerInvariant()}.example.org",
                Port = 4000,
            };
            definition.Characters.Add(new CharacterDefinition
            {
                Name = character,
                Logging = new LoggingSettings(),
                TriggerSets = { "Comms" },
            });
            config.Worlds.Add(definition);
        }

        var chat = Workspace.SpawnWindowId("Chat");
        var windows = new[] { "main", chat, "char:Bravo.Bob", "char:Cara.Cal" };
        var labels = new[] { "main", "Chat", "main", "main" };
        var sessions = names.Select(n => $"{n.Item1}.{n.Item2}").ToArray();
        var owners = new[] { sessions[0], sessions[0], sessions[1], sessions[2] };

        config.LastSession = new WorkspaceState
        {
            Windows =
            {
                new WorkspaceWindowState
                {
                    Id = windows[0], Title = "Ann", Kind = WindowKind.Main, SessionKey = owners[0], Sequence = 1,
                },
                new WorkspaceWindowState
                {
                    Id = windows[1], Title = "Chat", Kind = WindowKind.Spawn, SessionKey = owners[1],
                    OwnerLabel = "Ann", Sequence = 2,
                },
                new WorkspaceWindowState
                {
                    Id = windows[2], Title = "Bob", Kind = WindowKind.Main, SessionKey = owners[2], Sequence = 3,
                },
                new WorkspaceWindowState
                {
                    Id = windows[3], Title = "Cal", Kind = WindowKind.Main, SessionKey = owners[3], Sequence = 4,
                },
            },
            Root = new LayoutNodeState
            {
                Type = "split",
                Direction = SplitDirection.Row,
                Children =
                {
                    new LayoutNodeState
                    {
                        Type = "pane", Id = "p1", Tabs = { windows[0], windows[1] }, ActiveIndex = 0, Sequence = 1,
                    },
                    new LayoutNodeState
                    {
                        Type = "pane", Id = "p2", Tabs = { windows[2] }, ActiveIndex = 0, Sequence = 2,
                    },
                    new LayoutNodeState
                    {
                        Type = "pane", Id = "p3", Tabs = { windows[3] }, ActiveIndex = 0, Sequence = 3,
                    },
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

        // The fixture's own claim: four windows, in the order they were sequenced. Everything after this
        // reads the rail, so a resumed workspace that came back differently must fail here and not
        // silently make the assertions vacuous.
        if (!app.PlacedWindowIds.SequenceEqual(windows))
        {
            throw new InvalidOperationException(
                $"the resumed workspace numbers windows {string.Join(", ", app.PlacedWindowIds)}");
        }

        return new Scene(app, windows, labels, owners, transports);
    }
}
