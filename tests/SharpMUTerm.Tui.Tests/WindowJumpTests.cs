using SharpMUTerm.Core.Automation;
using System.Text.RegularExpressions;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// <b>⌥1–⌥9 go to a numbered window of the <em>active character</em>, and bring it forward.</b> The
/// request was "I want it to be able to go between tabs? Panes? Whichever it is that allows me to switch
/// not just characters, but captures, etc." — answered by the window — and then, once a global numbering
/// was in front of them: "I am looking for the characters to have different numbers? Am I not
/// communicating something right here? … Let's create a different mechanic to easily be able to switch
/// characters then!" These are the claims the scoped numbering stands on.
/// <para>
/// <b>1. It reaches a capture window, which is the half the pane chord could not do.</b> ⌥N used to name
/// a pane, and a capture sharing a pane with its character's own window had no number: it was reachable
/// only while it happened to be that pane's active tab. Every window has a digit now, whichever tab is
/// in front.
/// </para>
/// <para>
/// <b>2. The digits re-base per character.</b> ⌥1 is <em>this</em> character's own window whoever you
/// are, ⌥2 their first capture. Globally numbered, three characters sharing pane 1 as tabs all read
/// <c>⌥1</c> on the sidebar, and nine digits do not stretch over everybody's windows. Characters are
/// reached by the ⌥J/⌥K cycle instead. The fixture puts a capture <em>second</em> in creation order on
/// purpose, so the window digits are not the pane digits wearing a new name.
/// </para>
/// <para>
/// <b>3. The number is the number on the screen.</b> Windows are counted in <c>WindowsFor</c> order,
/// which is exactly the set and order the rail draws window rows in — so the assertions read the label
/// off the <em>live rail</em> and press the digit that label names, rather than writing down which
/// window ought to be second. A chord that lands somewhere other than the label says is worse than no
/// chord, and this repository has already paid for two spellings of one thing once
/// (<c>▪ main   main</c>).
/// </para>
/// <para>
/// <b>4. It is the full activation, on painted cells.</b> "Bring it forward" is not <c>FocusedPaneId</c>
/// being assigned: it is the window active in its pane's strip, that pane's plane on the frame, and the
/// command line talking to its character. All three are asserted, the plane off the frame the driver was
/// handed — a focus indicator can be set on a control arranged at zero rows and read back happily.
/// </para>
/// <para>
/// <b>5. Nothing falls through to the framework.</b> SharpConsoleUI claims Alt+1–9 for its own top-level
/// window selector (<c>InputCoordinator.HandleAltInput</c>), which — unlike the move and resize handlers
/// beside it — is <em>not</em> gated on <c>IsMovable</c>/<c>IsResizable</c>, so <c>Movable(false)</c> did
/// not switch it off. All nine digits are claimed as application shortcuts, which
/// <c>InputCoordinator</c> tries before it offers the key to a window at all; an out-of-range digit
/// therefore reports here and stops rather than reaching a window selector that would do something else.
/// </para>
/// <para>
/// <b>6. Alt, because Ctrl+digit is not a chord this terminal has.</b> Read off a real pty rather than
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

        // Ann is active and owns two of the four windows, so her digits are 1 and 2 — not 1 and 2 of a
        // run that continues into Bob's and Cal's.
        await Assert.That(scene.App.NumberedWindowIds).IsEquivalentTo(scene.Ann);

        for (var n = 1; n <= scene.Ann.Count; n++)
        {
            scene.App.SimulateKey(Alt(n));
            var frame = scene.App.RenderWholeFrame();

            await Assert.That(scene.App.ActiveWindowId())
                .IsEqualTo(scene.Ann[n - 1])
                .Because($"⌥{n} must bring forward the window the sidebar labels ⌥{n}");

            // The rail's own word for the window that is now in front.
            await Assert.That(WindowRowChord(scene.App, scene.AnnLabels[n - 1]))
                .IsEqualTo($"⌥{n}")
                .Because("the digit pressed and the digit drawn beside the window that arrived are one number");

            // The session, so the command line is talking to the window you are looking at.
            await Assert.That(scene.App.ActiveSessionKey).IsEqualTo("Alfa.Ann");

            // And the paint: the hosting pane's rectangle carries the focused plane and no other one does.
            var rects = scene.App.PaneOutputRects();
            var landed = scene.App.FocusedPaneId;
            await Assert.That(landed).IsEqualTo(scene.App.PaneIdOf(scene.Ann[n - 1]));
            await Assert.That(FrameGrid.CellsPaintedIn(frame, rects[landed], focused))
                .IsGreaterThan(0)
                .Because($"⌥{n} must paint the pane holding window {n} as the focused one");
            foreach (var other in scene.App.PaneIds.Where(id => id != landed))
            {
                await Assert.That(FrameGrid.CellsPaintedIn(frame, rects[other], focused)).IsEqualTo(0);
            }
        }
    }

    /// <summary>
    /// <b>The claim the redesign turned on: the digits re-base when the character does.</b> Ann has two
    /// windows and Bob has one, so ⌥1 is Ann's own window while she is active and <em>Bob's</em> the
    /// moment he is — and ⌥2, which reached Ann's capture, has nothing behind it under Bob and says so.
    /// <para>
    /// Globally numbered this was the reported confusion: every character's row read <c>⌥1</c> because
    /// their windows happened to share a run, and a digit meant a different thing depending on who you
    /// had last been.
    /// </para>
    /// </summary>
    [Test]
    public async Task TheDigitsAreTheActiveCharactersAndReBaseWhenItChanges()
    {
        var scene = await BuildScene();

        scene.App.SimulateKey(Alt(1));
        await Assert.That(scene.App.ActiveWindowId()).IsEqualTo("main");
        await Assert.That(scene.App.NumberedWindowIds).IsEquivalentTo(scene.Ann);

        scene.App.SimulateKey(AltKey(ConsoleKey.J));       // to Bob
        await Assert.That(scene.App.ActiveSessionKey).IsEqualTo("Bravo.Bob");
        await Assert.That(scene.App.NumberedWindowIds)
            .IsEquivalentTo(new[] { "char:Bravo.Bob" })
            .Because("Bob owns one window, so his numbering is just ⌥1");

        scene.App.SimulateKey(Alt(1));
        await Assert.That(scene.App.ActiveWindowId())
            .IsEqualTo("char:Bravo.Bob")
            .Because("⌥1 is whoever is active's own window, not a fixed window in the workspace");

        scene.App.SimulateKey(Alt(2));
        await Assert.That(scene.App.StatusMarkup)
            .Contains("Bob has one window")
            .Because("Ann's second window is not Bob's second window; it is not his at all, and the "
                + "refusal names whose windows it counted");
        await Assert.That(scene.App.ActiveWindowId()).IsEqualTo("char:Bravo.Bob");
    }

    /// <summary>
    /// <b>A window nobody owns is in everybody's numbering.</b> The web view belongs to no session and is
    /// reachable from wherever you are, so it takes a digit under each character — a different one under
    /// each, since it sits after that character's own windows. That is not a second numbering: it is
    /// exactly the set the rail draws window rows for, which admits a character's own windows plus the
    /// unowned ones, so the sidebar and the chord read one list.
    /// </summary>
    [Test]
    public async Task AnUnownedWindowIsNumberedUnderEveryCharacter()
    {
        var scene = await BuildScene();
        scene.App.OpenUnownedWindowForTest("web", "Web");
        scene.App.RenderNextFrame();

        // Ann owns two, so the web view is her third.
        scene.App.SimulateKey(Alt(1));
        scene.App.RenderNextFrame();
        await Assert.That(WindowRowChord(scene.App, "Web")).IsEqualTo("⌥3");
        scene.App.SimulateKey(Alt(3));
        await Assert.That(scene.App.ActiveWindowId()).IsEqualTo("web");

        // Bob owns one, so it is his second — a different digit for the same window, which is what
        // "re-based per character" means and why the sidebar has to print it rather than be inferred.
        scene.App.SimulateKey(AltKey(ConsoleKey.J));
        scene.App.RenderNextFrame();
        await Assert.That(WindowRowChord(scene.App, "Web")).IsEqualTo("⌥2");
        scene.App.SimulateKey(Alt(2));
        await Assert.That(scene.App.ActiveWindowId()).IsEqualTo("web");
    }

    /// <summary>
    /// <b>The half a pane-numbered chord could not do: a capture window behind another tab.</b> Ann's
    /// Chat window shares a pane with Ann's own window and is not the tab in front — under the old chord
    /// there was no digit that reached it, because the pane's digit went to whatever the pane was already
    /// showing. Pressing its digit switches the tab as well as the pane.
    /// </summary>
    [Test]
    public async Task ADigitReachesACaptureWindowSittingBehindAnotherTabInItsPane()
    {
        var scene = await BuildScene();
        var chat = DemoScene.ChatWindowId;
        var chatPane = scene.App.PaneIdOf(chat)!;

        // Stand in Chat's own pane, on the other tab, so nothing but the tab has to move.
        scene.App.SimulateKey(Alt(1));
        await Assert.That(scene.App.PaneIdOf("main")).IsEqualTo(chatPane);
        await Assert.That(scene.App.ActiveWindowId()).IsNotEqualTo(chat);

        scene.App.SimulateKey(Alt(2));

        await Assert.That(scene.App.ActiveWindowId())
            .IsEqualTo(chat)
            .Because("a capture window is exactly what the chord was asked to reach");
        await Assert.That(scene.App.FocusedPaneId).IsEqualTo(chatPane);
    }

    /// <summary>
    /// And the line typed next goes to the character you cycled to. Asserted on the bytes the transport
    /// received, because <c>SendUserInputAsync</c> returns immediately with nothing underneath it — "the
    /// right world got it" against an unconnected session is true however broken the routing is.
    /// <para>
    /// Driven with ⌥K so the backwards half of the cycle is exercised too: Ann is first, so ⌥K wraps to
    /// Cal and a second one lands on Bob.
    /// </para>
    /// </summary>
    [Test]
    public async Task TheLineTypedAfterACharacterCycleReachesThatCharacter()
    {
        var scene = await BuildScene();

        scene.App.SimulateKey(AltKey(ConsoleKey.K));            // Ann -> Cal, wrapping
        await Assert.That(scene.App.ActiveSessionKey).IsEqualTo("Cara.Cal");
        Send(scene.App, "look");

        scene.App.SimulateKey(AltKey(ConsoleKey.K));            // Cal -> Bob
        await Assert.That(scene.App.ActiveSessionKey).IsEqualTo("Bravo.Bob");
        Send(scene.App, "score");

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

        for (var n = 2; n <= scene.Ann.Count; n++)
        {
            var subtitle = entries[CommandIds.Window(scene.Ann[n - 1])];
            await Assert.That(subtitle).IsNotNull();
            await Assert.That(subtitle!)
                .StartsWith($"⌥{n} · ")
                .Because("an entry that named the wrong chord would be worse than a bare one");

            scene.App.SimulateKey(Alt(n));
            var viaKey = scene.App.ActiveWindowId();

            scene.App.SimulateKey(Alt(1));
            await Assert.That(scene.App.DispatchCommand(CommandIds.Window(scene.Ann[n - 1]))).IsTrue();
            await Assert.That(scene.App.ActiveWindowId()).IsEqualTo(viaKey);
        }

        // And a window belonging to somebody else carries no chord at all: ⌥2 from here reaches Ann's
        // capture, not Bob's window, so an entry claiming a digit for it would name a key that goes
        // somewhere else.
        await Assert.That(entries[CommandIds.Window("char:Bravo.Bob")]!.Contains('⌥'))
            .IsFalse()
            .Because("the numbering is the active character's, and Bob's window is not in it");
    }

    /// <summary>
    /// <b>The chord the rail prints against a character reaches that character.</b> Window rows are drawn
    /// for the active character only, so the way to another character has to be legible on their own row
    /// or the cycle is a key nobody finds. Only the two <em>neighbours</em> carry one, because only they
    /// are a single keystroke away; the chord is read out of the rendered sidebar and pressed.
    /// <para>
    /// The row used to carry the chord of that character's own window. Scoped to the active character
    /// that printed <c>⌥1</c> against every character on the screen, which is the confusion this design
    /// replaced.
    /// </para>
    /// </summary>
    [Test]
    public async Task PressingTheChordTheRailPrintsAgainstACharacterGoesToThatCharacter()
    {
        var scene = await BuildScene();

        // Three characters: from each one, exactly two rows carry a chord and pressing either arrives.
        foreach (var _ in new[] { 1, 2, 3 })
        {
            scene.App.RenderNextFrame();
            var here = scene.App.ActiveSessionKey!;

            var forward = CharacterWearing(scene.App, "⌥J");
            var back = CharacterWearing(scene.App, "⌥K");
            await Assert.That(forward).IsNotNull();
            await Assert.That(back).IsNotNull();
            await Assert.That(forward).IsNotEqualTo(back);
            await Assert.That(forward).IsNotEqualTo(NameOf(here));

            // Nobody else wears one — including the row you are standing on, whose ▸ already says so.
            await Assert.That(CharactersWearingAChord(scene.App).Count)
                .IsEqualTo(2)
                .Because("only the two neighbours are one keystroke away, and a third would be a lie");

            scene.App.SimulateKey(AltKey(ConsoleKey.K));
            await Assert.That(NameOf(scene.App.ActiveSessionKey!))
                .IsEqualTo(back)
                .Because($"the rail said ⌥K went to {back} from {here}");

            scene.App.SimulateKey(AltKey(ConsoleKey.J));
            await Assert.That(scene.App.ActiveSessionKey)
                .IsEqualTo(here)
                .Because("⌥J undoes ⌥K");

            scene.App.SimulateKey(AltKey(ConsoleKey.J));
        }
    }

    /// <summary>
    /// <b>The cycle never opens a character it was not already holding.</b> Switching to one the client
    /// has never opened <em>creates</em> a session and a window (the shell's <c>SwitchToCharacter</c>),
    /// and a cycle key that did that per press would dial through a configuration by accident. Driven
    /// twice: round a fixture with three characters open and two more configured and untouched, where the
    /// count must not move; and on a client with none open, where it says so rather than appearing dead.
    /// </summary>
    [Test]
    public async Task TheCycleWalksOnlyOpenCharactersAndOpensNothing()
    {
        var scene = await BuildScene();

        // Two more characters exist in the configuration and have never been opened.
        var configured = scene.App.BuildCatalog()
            .Count(c => c.Id.StartsWith(CommandIds.CharacterPrefix, StringComparison.Ordinal));
        await Assert.That(configured).IsGreaterThanOrEqualTo(2);

        var windows = scene.App.WindowIds().Count;
        var visited = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            scene.App.SimulateKey(AltKey(ConsoleKey.J));
            visited.Add(scene.App.ActiveSessionKey!);
        }

        await Assert.That(scene.App.WindowIds().Count)
            .IsEqualTo(windows)
            .Because("a cycle key must not open a session for a character you have never been to");
        await Assert.That(visited.Distinct().Count())
            .IsEqualTo(3)
            .Because("six steps round three open characters visit those three and nobody else");

        // And with nothing open at all it reports rather than doing nothing quietly.
        var fresh = App();
        fresh.RenderSnapshot();
        var before = fresh.WindowIds().Count;
        fresh.SimulateKey(AltKey(ConsoleKey.J));

        await Assert.That(fresh.StatusMarkup).Contains("no character is open");
        await Assert.That(fresh.WindowIds().Count).IsEqualTo(before);
    }

    /// <summary>
    /// <b>Closing a window compacts the numbering, on the chord and in the sidebar together.</b> Creation
    /// sequences are never reused, so a number read straight off one would leave a hole: a digit would
    /// report "there is no window" while the windows sat on the screen. The number is the position in the
    /// numbering for exactly this reason, and the two surfaces are asserted together because a chord that
    /// disagrees with the label is the defect this numbering exists to avoid.
    /// </summary>
    [Test]
    public async Task ClosingAWindowCompactsTheNumberingOnTheChordAndInTheSidebar()
    {
        var scene = await BuildScene();
        scene.App.OpenUnownedWindowForTest("web", "Web");
        scene.App.SimulateKey(Alt(1));
        scene.App.RenderNextFrame();

        // Ann: main ⌥1, Chat ⌥2, the web view ⌥3.
        await Assert.That(WindowRowChord(scene.App, "Web")).IsEqualTo("⌥3");

        scene.App.SimulateKey(Alt(2));                       // Chat
        await Assert.That(scene.App.ActiveWindowId()).IsEqualTo(DemoScene.ChatWindowId);
        await Assert.That(scene.App.DispatchCommand("layout:close")).IsTrue();
        scene.App.RenderNextFrame();

        await Assert.That(scene.App.NumberedWindowIds).DoesNotContain(DemoScene.ChatWindowId);
        await Assert.That(WindowRowChord(scene.App, "Web"))
            .IsEqualTo("⌥2")
            .Because("the windows on the screen must be numbered without a hole where the closed one was");

        scene.App.SimulateKey(Alt(1));
        scene.App.SimulateKey(Alt(2));
        await Assert.That(scene.App.ActiveWindowId())
            .IsEqualTo("web")
            .Because("the last window's digit must follow the compaction the sidebar drew");

        scene.App.SimulateKey(Alt(3));
        await Assert.That(scene.App.StatusMarkup).Contains("there is no window 3");
        await Assert.That(scene.App.ActiveWindowId()).IsEqualTo("web");
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
        };

        scene.Transports[0].Receive("<Trade> Ann offers a lamp\n");
        scene.App.RenderNextFrame();

        // Transports[0] is Alfa.Ann's, and the line says so — the window belongs to the session whose
        // wire it arrived on, which is the whole point of the per-session id.
        var arrival = Workspace.SpawnWindowId("Alfa.Ann", "Trade");
        await Assert.That(scene.App.NumberedWindowIds).Contains(arrival);

        foreach (var (what, chord) in before)
        {
            await Assert.That(WindowRowChord(scene.App, what))
                .IsEqualTo(chord)
                .Because($"{what} was {chord} before a channel opened and must still be");
        }

        await Assert.That(WindowRowChord(scene.App, "Trade")).IsEqualTo($"⌥{scene.Ann.Count + 1}");

        scene.App.SimulateKey(Alt(scene.Ann.Count + 1));
        await Assert.That(scene.App.ActiveWindowId()).IsEqualTo(arrival);
    }

    /// <summary>
    /// A single-window workspace lists no chord on any row: with one place to be, the digit is not
    /// information, and three cells of sidebar come out of the pane the reader is looking at.
    /// </summary>
    [Test]
    public async Task ASingleWindowWorkspaceNamesNoChordOnAnyRow()
    {
        var app = SingleWindowApp();

        await Assert.That(app.NumberedWindowIds.Count).IsEqualTo(1);
        await Assert.That(app.RailLines.Any(l => Regex.IsMatch(l, @"⌥\d")))
            .IsFalse()
            .Because("with one window, naming it says nothing and costs the panes their columns");
    }

    // --- out of range: report, never a silent no-op -------------------------------------------------

    /// <summary>
    /// <b>⌥7 with two windows says so.</b> A silent no-op is the most-repeated defect in this codebase's
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

        foreach (var digit in new[] { 3, 7, 9 })
        {
            scene.App.SimulateKey(Alt(digit));

            await Assert.That(scene.App.StatusMarkup).Contains($"there is no window {digit}");
            await Assert.That(scene.App.StatusMarkup)
                .Contains("Ann")
                .Because("the numbering is per character, so a count with no subject names the wrong set");
            await Assert.That(scene.App.StatusMarkup).Contains(scene.Ann.Count.ToString());
            await Assert.That(scene.App.ActiveWindowId()).IsEqualTo(window);
            await Assert.That(scene.App.ActiveSessionKey).IsEqualTo(session);
        }
    }

    /// <summary>
    /// With one window of your own every digit past the first is out of range, and the refusal says the
    /// useful thing instead of counting: the numbering is yours, so the way out of it is a character.
    /// </summary>
    [Test]
    public async Task OnOneWindowTheRefusalPointsAtTheCharacterCycle()
    {
        var app = SingleWindowApp();

        app.SimulateKey(Alt(2));

        await Assert.That(app.StatusMarkup).Contains("one window");
        await Assert.That(app.StatusMarkup)
            .Contains("⌥J")
            .Because("with one window of your own, the useful next move is another character");
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
        var elsewhere = app.NumberedWindowIds.ToList().FindIndex(id => app.PaneIdOf(id) == second) + 1;
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
        await Assert.That(FrameGrid.CellsPaintedIn(frame, rects[second], focused)).IsGreaterThan(0);
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

    /// <summary>
    /// The demo with its capture window removed, rendered once — one window in one pane, which is both
    /// "the chord column has nothing to say" and "every digit past the first is out of range".
    /// </summary>
    private static SharpMUTermApp SingleWindowApp()
    {
        Console.SetIn(TextReader.Null);
        var app = new SharpMUTermApp(DemoConfigs.SingleWindow(), Headless, new HeadlessConsoleDriver(120, 34));
        app.RenderSnapshot();
        return app;
    }

    /// <summary>The chord as the terminal delivers it: <c>ESC</c> + the digit, decoded as that digit with Alt.</summary>
    private static ConsoleKeyInfo Alt(int digit) =>
        new((char)('0' + digit), ConsoleKey.D0 + digit, false, true, false);

    /// <summary>An Alt+letter chord, as the terminal delivers it: <c>ESC</c> + the letter.</summary>
    private static ConsoleKeyInfo AltKey(ConsoleKey key) =>
        new(char.ToLowerInvariant(key.ToString()[0]), key, false, true, false);

    private static ConsoleKeyInfo Ctrl(ConsoleKey key) => new('\0', key, false, false, true);

    private static ConsoleKeyInfo Plain(char c, ConsoleKey key) => new(c, key, false, false, false);

    /// <summary>Empties the armed bar and sends <paramref name="line"/> from wherever the client is.</summary>
    private static void Send(SharpMUTermApp app, string line)
    {
        app.SimulateKey(Ctrl(ConsoleKey.E));
        app.SimulateKey(Ctrl(ConsoleKey.U));
        foreach (var c in line)
        {
            app.SimulateKey(Plain(c, ConsoleKey.NoName));
        }

        app.SimulateKey(Plain('\r', ConsoleKey.Enter));
    }

    /// <summary>
    /// The <c>⌥N</c> the rail prints on the window row labelled <paramref name="label"/>. Read out of the
    /// rendered rows rather than recomputed, because the whole assertion is that the chord and the
    /// sidebar agree — a re-derived label would agree with itself. Window rows are picked out by their
    /// <c>▪</c> bullet, because character rows carry the same column; the rail calls a character's own
    /// session window <c>main</c> and gives everything else its title.
    /// </summary>
    private static string? WindowRowChord(SharpMUTermApp app, string label)
    {
        foreach (var line in app.RailLines.Select(FrameGrid.Visible))
        {
            if (!Regex.IsMatch(line, $@"▪ {Regex.Escape(label)}(?![^\s])"))
            {
                continue;
            }

            var match = Regex.Match(line, @"⌥\d");
            return match.Success ? match.Value : null;
        }

        throw new InvalidOperationException(
            $"no rail window row for {label}: {string.Join(" / ", app.RailLines.Select(FrameGrid.Visible))}");
    }

    /// <summary>
    /// The character whose rail row carries <paramref name="chord"/>, or null when no row does.
    /// <para>
    /// Read off the rows' <em>visible</em> cells, with the markup stripped first. A rail row is wrapped in
    /// a <c>[link=cmd%3Acharacter%3AAlfa.Ann]</c> span, so matching raw markup would find a name in a
    /// link target as well as in the text. Character rows are told apart from window rows by the <c>▪</c>
    /// bullet the latter carry; both use this same column.
    /// </para>
    /// </summary>
    private static string? CharacterWearing(SharpMUTermApp app, string chord)
    {
        foreach (var line in app.RailLines.Select(FrameGrid.Visible))
        {
            if (line.Contains('▪', StringComparison.Ordinal) || !line.Contains(chord, StringComparison.Ordinal))
            {
                continue;
            }

            var match = Regex.Match(line, @"[A-Za-z][A-Za-z0-9]*\s*$");
            var name = Regex.Match(line.Replace(chord, string.Empty, StringComparison.Ordinal), @"[A-Za-z]+");
            return name.Success ? name.Value : match.Value.Trim();
        }

        return null;
    }

    /// <summary>Every character row currently carrying a chord, so "and nobody else" is checkable.</summary>
    private static List<string> CharactersWearingAChord(SharpMUTermApp app) =>
        app.RailLines.Select(FrameGrid.Visible)
            .Where(l => !l.Contains('▪', StringComparison.Ordinal) && Regex.IsMatch(l, @"⌥[JK]"))
            .ToList();

    /// <summary>The character half of a <c>world.character</c> key.</summary>
    private static string NameOf(string sessionKey) => sessionKey[(sessionKey.IndexOf('.') + 1)..];



    /// <summary>Three panes, one connected character each, plus one capture window behind a tab.</summary>
    /// <param name="Ann">
    /// Ann's own windows in numbering order — the digits ⌥1… mean while she is the active character, and
    /// the whole of the numbering, because the other two characters' windows are not in it.
    /// </param>
    /// <param name="AnnLabels">
    /// What the rail calls each of them on its own row, parallel to <paramref name="Ann"/>: a character's
    /// own session window reads <c>main</c>, a capture keeps its title.
    /// </param>
    private sealed record Scene(
        SharpMUTermApp App,
        IReadOnlyList<string> Ann,
        IReadOnlyList<string> AnnLabels,
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

        var chat = DemoScene.ChatWindowId;
        var windows = new[] { "main", chat, "char:Bravo.Bob", "char:Cara.Cal" };

        // Ann's two, in creation order — which is the whole of ⌥N while she is active. Bob's and Cal's
        // windows sit in the same workspace with higher sequences and are deliberately *not* numbered
        // from here; that is the claim the redesign turned on.
        var ann = new[] { "main", chat };
        var annLabels = new[] { "main", "Chat" };
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

        // Back to Ann, so the numbering under test is hers and the cycle starts at a known place.
        app.DispatchCommand(CommandIds.Character(sessions[0]));
        app.RenderNextFrame();

        // The fixture's own claim: Ann is active and her two windows are the numbering. Everything after
        // this reads the rail, so a resumed workspace that came back differently must fail here and not
        // silently make the assertions vacuous.
        if (app.ActiveSessionKey != sessions[0] || !app.NumberedWindowIds.SequenceEqual(ann))
        {
            throw new InvalidOperationException(
                $"{app.ActiveSessionKey} is active and numbers {string.Join(", ", app.NumberedWindowIds)}");
        }

        return new Scene(app, ann, annLabels, transports);
    }
}
