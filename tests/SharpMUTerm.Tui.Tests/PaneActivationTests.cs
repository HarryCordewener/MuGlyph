using SharpConsoleUI.Drivers;
using SharpConsoleUI.Parsing;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// <b>Activating a window activates its session.</b> Whatever brings a window forward — a Ctrl+arrow, ⌃O,
/// a ⌃P <c>Focus pane …</c> entry, a click on a tab — the command line must end up talking to the character
/// whose output is now in front of you, holding that window's draft, and describing that character on the
/// prompt.
/// <para>
/// The reported defect: Ctrl+←/→ moved pane <em>selection</em> and nothing else. Its author reasoned that
/// because the keys never move keyboard focus, "move into the pane" needed no further state — true about
/// the focus pin, and it left the bar pointed at the world you had navigated away from. On a split showing
/// two characters that is a line typed at one and delivered to the other.
/// </para>
/// <para>
/// The sessions are <b>connected</b> on purpose, and the assertions are on the bytes each transport
/// received. <c>WorldSession.SendRawAsync</c> and <c>SendUserInputAsync</c> return immediately with no live
/// transport, so "the right world got it" asserted against an unconnected session is true no matter how
/// broken the routing is — the same trap the link work found.
/// </para>
/// </summary>
/// <remarks>
/// Serialised with the other end-to-end suites: constructing the app and rendering a frame both touch the
/// process-global console streams.
/// </remarks>
[NotInParallel]
public class PaneActivationTests
{
    private const int Width = 160;
    private const int Height = 40;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    // ---- Ctrl+arrow: the reported bug --------------------------------------------------------

    /// <summary>
    /// The regression test. Two characters of two worlds side by side; ⌃→ to the other pane; the line typed
    /// next reaches <em>that</em> character's transport and not the one left behind — and the active
    /// session, the prompt and the draft all say the same thing.
    /// </summary>
    [Test]
    public async Task CtrlArrowToAnotherPane_SendsTheNextLineToThatPanesCharacter()
    {
        var two = await SplitPanes();

        two.App.SimulateKey(Ctrl(two.TowardsQuiet));

        await Assert.That(two.App.ActiveWindowId()).IsEqualTo(two.QuietWindow);
        await Assert.That(two.App.ActiveSessionKey).IsEqualTo("Quiet.Ann");
        await Assert.That(two.App.PrimaryPromptMarkup).Contains("Ann");

        Send(two.App, "look");

        await Assert.That(two.Quiet.Lines).IsEquivalentTo(new[] { "look" });
        await Assert.That(two.Loud.Lines).IsEmpty();
    }

    /// <summary>
    /// And back again, so the tie is to the pane rather than to "the last thing switched to": ⌃← returns
    /// the command line to the character it started on.
    /// </summary>
    [Test]
    public async Task CtrlArrowBack_ReturnsTheCommandLineToTheFirstCharacter()
    {
        var two = await SplitPanes();

        two.App.SimulateKey(Ctrl(two.TowardsQuiet));
        Send(two.App, "look");
        two.App.SimulateKey(Ctrl(two.TowardsLoud));

        await Assert.That(two.App.ActiveSessionKey).IsEqualTo("Loud.Bob");
        Send(two.App, "score");

        await Assert.That(two.Quiet.Lines).IsEquivalentTo(new[] { "look" });
        await Assert.That(two.Loud.Lines).IsEquivalentTo(new[] { "score" });
    }

    /// <summary>
    /// The draft goes with the window, which is the other half of "the input area is that window's".
    /// Drafts were already per window and <c>ChangeWindow()</c> already recalled them on a pane move — this
    /// pins that the reunification of the paths did not lose it.
    /// </summary>
    [Test]
    public async Task CtrlArrowCarriesEachWindowsOwnDraft()
    {
        var two = await SplitPanes();

        Type(two.App, "half a line to Bob");
        two.App.SimulateKey(Ctrl(two.TowardsQuiet));

        await Assert.That(two.App.ArmedInputText).IsEmpty();
        Type(two.App, "half a line to Ann");

        two.App.SimulateKey(Ctrl(two.TowardsLoud));
        await Assert.That(two.App.ArmedInputText).IsEqualTo("half a line to Bob");

        two.App.SimulateKey(Ctrl(two.TowardsQuiet));
        await Assert.That(two.App.ArmedInputText).IsEqualTo("half a line to Ann");
    }

    /// <summary>
    /// <b>The focus pin is not weakened.</b> Activating a window is about which session the bar talks to
    /// and which draft it holds, not about which control the framework hands a keystroke to. The keyboard
    /// stays on the armed command line across a move that now also switches session.
    /// </summary>
    [Test]
    public async Task MovingPaneSelectionStillLeavesTheKeyboardOnTheArmedBar()
    {
        var two = await SplitPanes();

        foreach (var key in new[] { two.TowardsQuiet, two.TowardsLoud, two.TowardsQuiet })
        {
            two.App.SimulateKey(Ctrl(key));
            two.App.RenderNextFrame();
            await Assert.That(two.App.ArmedBarHasFocus).IsTrue();
        }
    }

    // ---- the other movers, one rule ----------------------------------------------------------

    /// <summary>⌃O cycles panes, and cycling activates too.</summary>
    [Test]
    public async Task CyclingPanes_SendsTheNextLineToThatPanesCharacter()
    {
        var two = await SplitPanes();

        await Assert.That(two.App.DispatchCommand("layout:cycle")).IsTrue();

        await Assert.That(two.App.ActiveSessionKey).IsEqualTo("Quiet.Ann");
        Send(two.App, "look");

        await Assert.That(two.Quiet.Lines).IsEquivalentTo(new[] { "look" });
        await Assert.That(two.Loud.Lines).IsEmpty();
    }

    /// <summary>
    /// The ⌃P surface's <c>Focus pane …</c> entries go through the same mover the keys do (<c>MoveFocus</c>
    /// is the keyboard's path minus the command-line fall-through), so they activate as well.
    /// </summary>
    [Test]
    public async Task TheCommandSurfacesFocusPaneEntry_SendsTheNextLineToThatPanesCharacter()
    {
        var two = await SplitPanes();

        var entry = two.TowardsQuiet == ConsoleKey.LeftArrow ? "layout:focus-left" : "layout:focus-right";
        await Assert.That(two.App.DispatchCommand(entry)).IsTrue();

        await Assert.That(two.App.ActiveSessionKey).IsEqualTo("Quiet.Ann");
        Send(two.App, "look");

        await Assert.That(two.Quiet.Lines).IsEquivalentTo(new[] { "look" });
        await Assert.That(two.Loud.Lines).IsEmpty();
    }

    /// <summary>
    /// A <em>tab click</em> — the most-used gesture of the lot, and the only mouse route that moves pane
    /// selection. It reloaded the drafts and left the session behind exactly as the Ctrl+arrows did.
    /// Driven through the framework's own tab hit test, so what is asserted is what a click does.
    /// </summary>
    [Test]
    public async Task ClickingAnotherCharactersTab_SendsTheNextLineToThatCharacter()
    {
        var two = await TwoConnectedWorlds(); // one pane, two tabs: Quiet's main and Loud's own window
        await Assert.That(two.App.ActiveSessionKey).IsEqualTo("Loud.Bob");

        await Assert.That(ClickTab(two.App, two.QuietWindow)).IsTrue();

        await Assert.That(two.App.ActiveWindowId()).IsEqualTo(two.QuietWindow);
        await Assert.That(two.App.ActiveSessionKey).IsEqualTo("Quiet.Ann");
        Send(two.App, "look");

        await Assert.That(two.Quiet.Lines).IsEquivalentTo(new[] { "look" });
        await Assert.That(two.Loud.Lines).IsEmpty();
    }

    // ---- a pane with no session refuses rather than redirecting -------------------------------

    /// <summary>
    /// A pane showing a window that belongs to no connection — the web view is the one that exists — must
    /// not quietly repoint the command line at something plausible. It cannot be given a session either,
    /// so it keeps the one it had and <b>says so</b>: the resolution rule is the window's own session, else
    /// its recorded owner, else refuse, and <c>_active</c> is never a third answer.
    /// </summary>
    [Test]
    public async Task APaneWithNoSession_RefusesAndSaysSoRatherThanRedirecting()
    {
        var two = await TwoConnectedWorlds();
        two.App.SimulateWebPage();           // the web view opens and is activated
        await Assert.That(two.App.DispatchCommand("layout:split-right")).IsTrue();
        two.App.RenderNextFrame();

        // The split leaves the web view alone in one pane and the two session windows in the other.
        var webPane = two.App.PaneIdOf("web")!;
        var other = two.App.PaneIds.Single(id => id != webPane);
        two.App.SimulateWindowChange(two.LoudWindow);
        await Assert.That(two.App.FocusedPaneId).IsEqualTo(other);
        await Assert.That(two.App.ActiveSessionKey).IsEqualTo("Loud.Bob");

        MoveTo(two.App, webPane);

        await Assert.That(two.App.ActiveWindowId()).IsEqualTo("web");
        await Assert.That(two.App.ActiveSessionKey).IsEqualTo("Loud.Bob"); // refused, not redirected
        await Assert.That(two.App.StatusMarkup).Contains("belongs to no connection");
        await Assert.That(two.App.StatusMarkup).Contains("Bob");

        // And the line still goes where the client said it would.
        Send(two.App, "look");
        await Assert.That(two.Loud.Lines).IsEquivalentTo(new[] { "look" });
        await Assert.That(two.Quiet.Lines).IsEmpty();
    }

    /// <summary>
    /// The same refusal for a window whose recorded owner has no session in this run — a resumed workspace
    /// where only some of last session's characters were reopened. The message names the character and the
    /// command that opens it, because "nothing happened" is what made this class of bug reportable.
    /// </summary>
    [Test]
    public async Task AWindowWhoseOwnerHasNoSession_RefusesAndNamesWhatWouldOpenIt()
    {
        Console.SetIn(TextReader.Null);
        var config = new AppConfiguration();
        var world = World("Quiet");
        world.Characters.Add(new CharacterDefinition { Name = "Ann", Logging = new LoggingSettings() });
        world.Characters.Add(new CharacterDefinition { Name = "Cal", Logging = new LoggingSettings() });
        config.Worlds.Add(world);

        // A saved session in which Cal had a window of her own; only Ann is opened this run.
        config.LastSession = new WorkspaceState
        {
            Windows =
            {
                new WorkspaceWindowState { Id = "main", Title = "Quiet", Kind = WindowKind.Main, SessionKey = "Quiet.Ann" },
                new WorkspaceWindowState
                {
                    Id = "char:Quiet.Cal", Title = "Cal", Kind = WindowKind.Main, SessionKey = "Quiet.Cal",
                },
            },
            Root = new LayoutNodeState { Type = "pane", Id = "p1", Tabs = { "main", "char:Quiet.Cal" }, ActiveIndex = 0 },
            FocusedPaneId = "p1",
        };

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        var telnet = new RecordingTelnetSession();
        app.TelnetFactory = _ => telnet;
        await Open(app, "Quiet.Ann");
        app.RenderNextFrame();
        await Assert.That(app.ActiveSessionKey).IsEqualTo("Quiet.Ann");

        await Assert.That(ClickTab(app, "char:Quiet.Cal")).IsTrue();

        await Assert.That(app.ActiveSessionKey).IsEqualTo("Quiet.Ann"); // refused, not redirected
        await Assert.That(app.StatusMarkup).Contains("Quiet.Cal");
        await Assert.That(app.StatusMarkup).Contains("no open session");
    }

    /// <summary>
    /// A spawn window belongs to the character whose trigger opened it, so activating one is <em>not</em> a
    /// refusal — it resolves through the workspace's recorded owner, the second arm of the rule. Pinned
    /// because a fix that refused everything without its own session would pass the two tests above.
    /// </summary>
    [Test]
    public async Task ActivatingASpawnWindow_TalksToTheCharacterThatOwnsIt()
    {
        var two = await TwoConnectedWorlds();

        two.Quiet.Receive("[Chat] Rivane: anyone about?\n");   // Quiet's own trigger opens the spawn
        two.App.RenderNextFrame();
        var spawn = two.App.WindowIds().Single(id => id.StartsWith("spawn:", StringComparison.Ordinal));
        await Assert.That(two.App.ActiveSessionKey).IsEqualTo("Loud.Bob"); // spawns open in the background

        await Assert.That(ClickTab(two.App, spawn)).IsTrue();

        await Assert.That(two.App.ActiveSessionKey).IsEqualTo("Quiet.Ann");
        Send(two.App, "look");

        await Assert.That(two.Quiet.Lines).IsEquivalentTo(new[] { "look" });
        await Assert.That(two.Loud.Lines).IsEmpty();
    }

    // ---- what must not regress ---------------------------------------------------------------

    /// <summary>
    /// Pane selection still moves, and the vertical ladder into the command lines still composes with it:
    /// ⌃↓ off the last pane arms the second bar, ⌃↑ leaves it, and a horizontal move afterwards is still a
    /// pane move rather than being swallowed by the activation now hanging off it.
    /// <para>
    /// The second bar's visibility is <em>per window</em>, so the vertical steps are taken from the pane
    /// whose window has one. That is long-standing behaviour (<c>SyncInputBars</c> reads
    /// <c>_secondBars.IsShown(ActiveWindowId())</c>) and not something this change introduced — a first
    /// draft of this test moved panes first and then asked for a bar the new window never had.
    /// </para>
    /// </summary>
    [Test]
    public async Task TheLadderIntoTheCommandLinesStillWorks()
    {
        var app = App();
        app.RenderSnapshot("focus"); // two panes, second bar up on this window, primary armed
        var first = app.FocusedPaneId;
        await Assert.That(app.SecondBarShown).IsTrue();
        await Assert.That(app.SecondBarArmed).IsFalse();

        app.SimulateKey(Ctrl(ConsoleKey.DownArrow)); // no pane below: arms the second bar
        await Assert.That(app.SecondBarArmed).IsTrue();
        await Assert.That(app.FocusedPaneId).IsEqualTo(first); // and it was not a pane move

        app.SimulateKey(Ctrl(ConsoleKey.UpArrow));   // back out of it
        await Assert.That(app.SecondBarArmed).IsFalse();

        app.SimulateKey(Ctrl(ConsoleKey.RightArrow)); // and sideways still moves pane
        await Assert.That(app.FocusedPaneId).IsNotEqualTo(first);
        await Assert.That(app.ArmedBarHasFocus).IsTrue();
    }

    /// <summary>
    /// Moving pane selection still moves no pane rectangle, so no connected world is told a new terminal
    /// size for it. Restated here because activation now runs on the same keystroke and touches the input
    /// area, which is the sort of change that grows a cell.
    /// </summary>
    [Test]
    public async Task ActivatingOnAPaneMoveTellsNoServerANewSize()
    {
        var two = await SplitPanes();
        var before = two.App.PaneOutputRects().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var sizes = (Quiet: two.Quiet.Sizes.Distinct().ToList(), Loud: two.Loud.Sizes.Distinct().ToList());
        await Assert.That(sizes.Quiet).IsNotEmpty();
        await Assert.That(sizes.Loud).IsNotEmpty();

        two.App.SimulateKey(Ctrl(two.TowardsQuiet));
        two.App.RenderNextFrame();
        two.App.SimulateKey(Ctrl(two.TowardsLoud));
        two.App.RenderNextFrame();

        foreach (var (paneId, rect) in before)
        {
            await Assert.That(two.App.PaneOutputRects()[paneId]).IsEqualTo(rect);
        }

        await Assert.That(two.Quiet.Sizes.Distinct().ToList()).IsEquivalentTo(sizes.Quiet);
        await Assert.That(two.Loud.Sizes.Distinct().ToList()).IsEquivalentTo(sizes.Loud);
    }

    // ---- harness -----------------------------------------------------------------------------

    private static SharpMUTermApp App(int width = 120, int height = 34)
    {
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(width, height));
    }

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

    /// <summary>
    /// Types a line and sends it with ⏎. The app's send is fire-and-forget, but every await in the path
    /// (<c>SendUserInputAsync</c> → <c>SendRawAsync</c> → <see cref="RecordingTelnetSession.SendLineAsync"/>)
    /// completes synchronously, so the write has landed by the time ⏎ returns.
    /// </summary>
    private static void Send(SharpMUTermApp app, string line)
    {
        Type(app, line);
        app.SimulateKey(Plain('\r', ConsoleKey.Enter));
    }

    /// <summary>Moves pane selection to <paramref name="paneId"/> with the arrow that gets there.</summary>
    private static void MoveTo(SharpMUTermApp app, string paneId)
    {
        foreach (var key in new[]
        {
            ConsoleKey.RightArrow, ConsoleKey.LeftArrow, ConsoleKey.DownArrow, ConsoleKey.UpArrow,
        })
        {
            app.SimulateKey(Ctrl(key));
            if (app.FocusedPaneId == paneId)
            {
                return;
            }
        }

        throw new InvalidOperationException($"no Ctrl+arrow reached {paneId}");
    }

    /// <summary>
    /// Clicks a window's tab through the framework's own hit test on the pane's tab strip: the column is
    /// found by walking the strip's rendered titles, so nothing about the geometry is written down here.
    /// </summary>
    private static bool ClickTab(SharpMUTermApp app, string windowId)
    {
        var paneId = app.PaneIdOf(windowId)
            ?? throw new InvalidOperationException($"{windowId} is in no pane");
        var before = app.WindowIds().Order().ToList();
        var strip = app.PaneTabStrip(paneId);

        // Aimed at the label rather than swept across the row, because a sweep in *either* direction walks
        // over the active tab's × (which sits at that tab's right edge) and quietly closes the pane's tab
        // instead of selecting one — the first draft of this helper did exactly that. The step is
        // TabControl.ProcessMouseEvent's own: each tab occupies its title plus two cells of padding, plus a
        // cell for a × when it has one, plus a separator; column +1 is inside the label.
        var x = 0;
        var found = false;
        foreach (var (id, title, closable) in strip)
        {
            if (string.Equals(id, windowId, StringComparison.Ordinal))
            {
                found = true;
                break;
            }

            x += MarkupParser.StripLength(title) + 2 + (closable ? 1 : 0) + 1;
        }

        if (!found)
        {
            throw new InvalidOperationException(
                $"{windowId} has no tab in {paneId} (strip: {string.Join(" / ", strip.Select(t => t.WindowId))})");
        }

        var handled = app.SimulateTabStripClick(paneId, x + 1, 0);

        // Nothing was closed, or the assertions after this would be about a workspace the click destroyed
        // rather than one it navigated.
        if (!app.WindowIds().Order().SequenceEqual(before))
        {
            throw new InvalidOperationException(
                $"clicking {windowId} at column {x + 1} changed the window set: " +
                $"{string.Join(", ", before)} → {string.Join(", ", app.WindowIds().Order())}");
        }

        if (app.PaneActiveTab(paneId) != windowId)
        {
            throw new InvalidOperationException(
                $"clicking column {x + 1} of {paneId}'s strip left {app.PaneActiveTab(paneId)} up, not {windowId} " +
                $"(titles: {string.Join(" / ", strip.Select(t => t.Title))})");
        }

        return handled;
    }

    private static WorldDefinition World(string name) => new()
    {
        Name = name,
        Host = $"{name.ToLowerInvariant()}.example.org",
        Port = 4000,
    };

    /// <summary>Switches to a character the way ⌃P and the rail do, connects it, and reports its window.</summary>
    private static async Task<string> Open(SharpMUTermApp app, string sessionKey)
    {
        if (!app.DispatchCommand(CommandIds.Character(sessionKey)))
        {
            throw new InvalidOperationException($"the app would not switch to {sessionKey}");
        }

        await app.FindSession(sessionKey)!.ConnectAsync();
        return app.ActiveWindowId();
    }

    /// <summary>Two connected worlds in one app, each in its own window, with Loud's the active one.</summary>
    private sealed record TwoWorlds(
        SharpMUTermApp App,
        RecordingTelnetSession Quiet,
        RecordingTelnetSession Loud,
        string QuietWindow,
        string LoudWindow)
    {
        /// <summary>The Ctrl+arrow that reaches Quiet's pane from Loud's, once they are split apart.</summary>
        public ConsoleKey TowardsQuiet { get; init; }

        /// <summary>And the one that comes back.</summary>
        public ConsoleKey TowardsLoud =>
            TowardsQuiet == ConsoleKey.LeftArrow ? ConsoleKey.RightArrow : ConsoleKey.LeftArrow;
    }

    /// <summary>
    /// Two characters in two worlds, each with a connected recording transport, opened through the app's
    /// real character-switching command so each gets its own window the way the shell gives it one. Quiet
    /// carries a capture rule, so a <c>[Chat]</c> line of its own opens a spawn window it owns.
    /// </summary>
    private static async Task<TwoWorlds> TwoConnectedWorlds()
    {
        Console.SetIn(TextReader.Null);
        var config = new AppConfiguration();
        config.TriggerSets.Add(new TriggerSet
        {
            Name = "chat",
            Triggers =
            {
                new Trigger
                {
                    Name = "chat",
                    Pattern = @"^\[Chat\]",
                    Actions = new TriggerActions { SpawnTarget = "Chat" },
                },
            },
        });

        var quietWorld = World("Quiet");
        quietWorld.Characters.Add(new CharacterDefinition
        {
            Name = "Ann", Logging = new LoggingSettings(), TriggerSets = { "chat" },
        });

        var loudWorld = World("Loud");
        loudWorld.Characters.Add(new CharacterDefinition { Name = "Bob", Logging = new LoggingSettings() });

        config.Worlds.Add(quietWorld);
        config.Worlds.Add(loudWorld);

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        var quiet = new RecordingTelnetSession();
        var loud = new RecordingTelnetSession();

        // Keyed on the host so each session's writes are attributable — the whole suite turns on which
        // transport a command reached.
        app.TelnetFactory = options =>
            options.Host.StartsWith("quiet", StringComparison.Ordinal) ? quiet : loud;

        var quietWindow = await Open(app, "Quiet.Ann");
        var loudWindow = await Open(app, "Loud.Bob");
        app.RenderNextFrame();

        return new TwoWorlds(app, quiet, loud, quietWindow, loudWindow);
    }

    /// <summary>
    /// The same pair, split into two panes: ⌃B → moves the focused pane's other tabs across, so Loud's
    /// window stays where it is and Quiet's goes to the new pane. Which side that is comes from the
    /// arranged rectangles rather than from an assumption about the split.
    /// </summary>
    private static async Task<TwoWorlds> SplitPanes()
    {
        var two = await TwoConnectedWorlds();
        if (!two.App.DispatchCommand("layout:split-right"))
        {
            throw new InvalidOperationException("the app would not split");
        }

        two.App.RenderNextFrame();
        if (two.App.ActiveWindowId() != two.LoudWindow)
        {
            throw new InvalidOperationException(
                $"the split left {two.App.ActiveWindowId()} active, not {two.LoudWindow}");
        }

        var rects = two.App.PaneOutputRects();
        var quietPane = two.App.PaneIdOf(two.QuietWindow)!;
        var loudPane = two.App.PaneIdOf(two.LoudWindow)!;
        var towards = rects[quietPane].X < rects[loudPane].X ? ConsoleKey.LeftArrow : ConsoleKey.RightArrow;
        return two with { TowardsQuiet = towards };
    }
}
