using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;
using static SharpMUTerm.Tui.MarkupText;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The ⌃B prefix as a user meets it: the terse strip at once, the which-key panel a moment later if
/// nothing was pressed, an advertised way out, and — the part <see cref="PrefixPanelTests"/> cannot claim
/// — every key the panel names really doing what the row says.
/// <para>
/// Three defects are pinned here as behaviour rather than as strings. <b>The prefix had no advertised
/// exit</b>: Esc worked only by falling into the "any other key disarms" arm and no surface said so.
/// <b>⌃B ⌃B did not disarm</b>, making this the one mode in the client you could not leave with the chord
/// that entered it. And <b>arming during a move left a prefix nothing could consume</b>, because
/// <c>HandleWindowKey</c> tests move mode first — so the first key after the move was eaten, and if that
/// key was <c>x</c> a window closed.
/// </para>
/// </summary>
/// <remarks>
/// Serialised for the same reason <see cref="PanePrefixEndToEndTests"/> is: constructing the app touches
/// the process-global console streams.
/// </remarks>
[NotInParallel]
public class PrefixWhichKeyTests
{
    private const int Width = 120;
    private const int Height = 34;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>Longer than the delay, whatever it is: these tests move the clock rather than sleeping.</summary>
    private static readonly TimeSpan PastTheDelay = TimeSpan.FromSeconds(2);

    private static SharpMUTermApp App(ManualTimeProvider? clock = null, int width = Width)
    {
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(
            DemoScene.Build(), Headless, new HeadlessConsoleDriver(width, Height), clock);
    }

    /// <summary>A fresh client: one pane, one window, nothing configured. What the report was made on.</summary>
    private static SharpMUTermApp Fresh()
    {
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(new AppConfiguration(), Headless, new HeadlessConsoleDriver(Width, Height));
    }

    /// <summary>
    /// The demo workspace with the <em>Chat</em> spawn brought forward. It is the one arrangement in which
    /// every command the panel calls live on a single-pane workspace really is: the pane holds two tabs (so
    /// the splits and the reorders have something to move) and the active tab is not the main window (so
    /// <c>x</c> has something to close).
    /// </summary>
    private static SharpMUTermApp ChatForward()
    {
        var app = App();
        app.SimulateWindowChange(Workspace.SpawnWindowId("Chat"));
        return app;
    }

    private static ConsoleKeyInfo Key(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static ConsoleKeyInfo Ctrl(ConsoleKey key) => new('\0', key, false, false, true);

    private static readonly ConsoleKeyInfo Escape = new('\x1b', ConsoleKey.Escape, false, false, false);

    /// <summary>The tabs of the focused pane, in strip order.</summary>
    private static IReadOnlyList<string> Tabs(SharpMUTermApp app) =>
        Flatten(app.CaptureSession().Root).First(n => n.Tabs.Count > 0).Tabs;

    private static List<LayoutNodeState> Flatten(LayoutNodeState node) =>
        node.Children is { Count: > 0 } children
            ? children.SelectMany(Flatten).ToList()
            : new List<LayoutNodeState> { node };

    /// <summary>Whether the status row is carrying a ⌃B refusal — how a command says it had nothing to do.</summary>
    private static bool Refused(SharpMUTermApp app) => app.StatusMarkup.Contains("⌃B");

    // --- which-key: the strip now, the panel in a moment ------------------------------------------

    /// <summary>
    /// Arming shows the strip immediately and nothing else. The panel is what happens to a user who does
    /// <em>not</em> already know the keymap — which is the whole reason this is a delay rather than a
    /// setting: the expert's second keystroke lands first and they never see it.
    /// </summary>
    [Test]
    public async Task TheStripIsImmediateAndThePanelWaits()
    {
        var clock = new ManualTimeProvider();
        var app = App(clock);

        app.SimulateKey(Ctrl(ConsoleKey.B));

        await Assert.That(app.PrefixArmed).IsTrue();
        await Assert.That(app.HeaderText).Contains("⌃B");
        await Assert.That(app.PrefixPanelOpen).IsFalse();

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await Assert.That(app.PrefixPanelOpen).IsFalse().Because("an expert is still mid-chord");

        clock.Advance(PastTheDelay);
        await Assert.That(app.PrefixPanelOpen).IsTrue();
    }

    /// <summary>
    /// A key pressed inside the delay spends the prefix, and the panel never appears — not then, and not
    /// later. The timer has to be disarmed rather than left to fire into a prefix that is already gone.
    /// </summary>
    [Test]
    public async Task AKeyInsideTheDelayMeansThePanelNeverOpens()
    {
        var clock = new ManualTimeProvider();
        var app = App(clock);

        app.SimulateKey(Ctrl(ConsoleKey.B));
        app.SimulateKey(Key('b')); // the rail toggle: fast, and nothing to refuse

        await Assert.That(app.PrefixArmed).IsFalse();
        await Assert.That(app.PrefixFactsSnapshot.RailCollapsed).IsTrue();

        clock.Advance(PastTheDelay);
        await Assert.That(app.PrefixPanelOpen).IsFalse();
    }

    /// <summary>
    /// A key pressed <em>into</em> the panel runs the same command the same key would have run before it
    /// appeared. The two timings are one behaviour: the panel hands the key back to the app's own prefix
    /// consumer rather than deciding anything itself.
    /// </summary>
    [Test]
    public async Task AKeyPressedIntoThePanelRunsTheCommandAndClosesIt()
    {
        var clock = new ManualTimeProvider();
        var app = App(clock);

        app.SimulateKey(Ctrl(ConsoleKey.B));
        clock.Advance(PastTheDelay);
        app.SimulatePrefixPanelKey(Key('i')); // the second command line

        await Assert.That(app.PrefixPanelOpen).IsFalse();
        await Assert.That(app.PrefixArmed).IsFalse();
        await Assert.That(app.SecondBarShown).IsTrue();
    }

    /// <summary>The panel explains the workspace it is over, not a workspace in general.</summary>
    [Test]
    public async Task ThePanelReadsTheWorkspaceItOpenedOver()
    {
        var clock = new ManualTimeProvider();
        var app = App(clock);
        app.RenderSnapshot("split"); // two panes, and the focused one down to a single tab

        app.SimulateKey(Ctrl(ConsoleKey.B));
        clock.Advance(PastTheDelay);

        var rows = app.PrefixPanelEntries;
        await Assert.That(rows.Single(r => r.Keys == "z").Available).IsTrue();
        await Assert.That(rows.Single(r => r.Keys == "o").Available).IsTrue();
        await Assert.That(rows.Single(r => r.Keys == "|").Blocked).IsNotNull();
    }

    // --- every key the panel lists does what the row says ------------------------------------------

    /// <summary>
    /// Each command the panel calls live on a one-pane, two-tab workspace, driven through the real handler
    /// and checked by its effect — not by the row it came from. A table asserted against itself would pass
    /// while every key was dead, which is exactly the state the prefix was reported in.
    /// </summary>
    [Test]
    public async Task EveryLiveCommandOnASingleP1WorkspaceDoesWhatItsRowSays()
    {
        await Drive('|', a => a.PrefixFactsSnapshot.PaneCount == 2, "splits the pane");
        await Drive('-', a => a.PrefixFactsSnapshot.PaneCount == 2, "splits the pane");
        await Drive('x', a => a.PrefixFactsSnapshot.TabCount == 1, "closes the tab");
        await Drive('b', a => a.PrefixFactsSnapshot.RailCollapsed, "hides the rail");
        await Drive('m', a => a.StatusMarkup.Contains("MOVE"), "enters move mode");
        await Drive('i', a => a.SecondBarShown, "raises the second command line");
        await Drive('<', a => Tabs(a)[0].EndsWith("Chat", StringComparison.Ordinal), "reorders the tab");
    }

    /// <summary>
    /// And the arrows the row also names are the same two commands. They are advertised on both surfaces,
    /// so they are checked as keys rather than trusted as an alias.
    /// </summary>
    [Test]
    public async Task TheArrowsTheRowNamesReorderJustAsTheAngleBracketsDo()
    {
        await Drive(
            new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false),
            a => Tabs(a)[0].EndsWith("Chat", StringComparison.Ordinal),
            "← reorders the tab");

        var app = ChatForward();
        var before = Tabs(app).ToList();
        app.SimulatePrefixedKey(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false));
        app.SimulatePrefixedKey(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false));
        await Assert.That(Tabs(app)).IsEquivalentTo(before).Because("→ undoes what ← did");
    }

    /// <summary>The two the panel dims on a lone pane, driven on a workspace that has a second one.</summary>
    [Test]
    public async Task ZoomAndCycleRunOnceThereIsASecondPane()
    {
        var zoom = App();
        zoom.RenderSnapshot("split");
        zoom.SimulatePrefixedKey(Key('z'));
        await Assert.That(zoom.PrefixFactsSnapshot.Zoomed).IsTrue();
        await Assert.That(Refused(zoom)).IsFalse();

        var cycle = App();
        cycle.RenderSnapshot("split");
        var before = cycle.FocusedPaneId;
        cycle.SimulatePrefixedKey(Key('o'));
        await Assert.That(cycle.FocusedPaneId).IsNotEqualTo(before);
        await Assert.That(Refused(cycle)).IsFalse();
    }

    /// <summary>
    /// The agreement, in both directions and on two different workspaces: what the panel dims refuses when
    /// pressed, and what it offers does not. This is the claim the panel makes about the client in front of
    /// the reader, and it is the one that rots silently as commands gain and lose conditions.
    /// </summary>
    [Test]
    public async Task WhatThePanelDimsRefusesAndWhatItOffersDoesNot()
    {
        foreach (var build in new Func<SharpMUTermApp>[] { Fresh, ChatForward })
        {
            foreach (var entry in build().PrefixPanelEntries)
            {
                var app = build();
                app.SimulatePrefixedKey(Key(entry.Keys[0]));

                await Assert.That(Refused(app))
                    .IsEqualTo(!entry.Available)
                    .Because(entry.Available
                        ? $"the panel offers '{entry.Title}', so pressing {entry.Keys[0]} must not refuse"
                        : $"the panel dims '{entry.Title}' ({entry.Blocked}), so pressing it must say why");
            }
        }
    }

    /// <summary>
    /// A dimmed row's short note and the sentence the status line prints are the same fact. They are two
    /// lengths of one string, so this checks they still describe the same refusal rather than that they
    /// match character for character.
    /// </summary>
    [Test]
    [Arguments('z', "needs a second pane", "nothing to zoom")]
    [Arguments('o', "needs a second pane", "nowhere to cycle to")]
    [Arguments('x', "the main window stays", "the main window stays open")]
    [Arguments('|', "needs a second tab", "nothing to split")]
    [Arguments('<', "needs a second tab", "nothing to reorder")]
    public async Task TheShortNoteAndTheSpelledOutRefusalAreTheSameFact(char key, string note, string spelled)
    {
        var app = Fresh();
        var row = app.PrefixPanelEntries.Single(e => e.Keys[0] == key);
        await Assert.That(row.Blocked).IsEqualTo(note);

        app.SimulatePrefixedKey(Key(key));
        await Assert.That(app.StatusMarkup).Contains(spelled);
    }

    // --- the way out -------------------------------------------------------------------------------

    /// <summary>
    /// <b>Esc leaves, and the key after it is typing again.</b> The exit existed only as a fall-through and
    /// no surface named it; now both surfaces do, so it has to be real — and it must not leave the prefix
    /// armed, because the key after that is eaten. Driven with <c>x</c> as the following key on purpose:
    /// eaten as a pane command, that is the keystroke that closes a window.
    /// </summary>
    [Test]
    public async Task EscapeLeavesThePrefixAndTheNextKeyIsTypingAgain()
    {
        var app = ChatForward();
        var tabs = app.PrefixFactsSnapshot.TabCount;

        app.SimulatePrefixedKey(Escape);
        await Assert.That(app.PrefixArmed).IsFalse();
        await Assert.That(Refused(app)).IsFalse().Because("cancelling is not a refusal");

        app.SimulateKey(Key('x'));
        await Assert.That(app.ArmedInputText).IsEqualTo("x");
        await Assert.That(app.PrefixFactsSnapshot.TabCount).IsEqualTo(tabs);
    }

    /// <summary>Esc pressed into the panel is the same exit, and takes the panel down with it.</summary>
    [Test]
    public async Task EscapeOutOfTheOpenPanelIsTheSameExit()
    {
        var clock = new ManualTimeProvider();
        var app = App(clock);

        app.SimulateKey(Ctrl(ConsoleKey.B));
        clock.Advance(PastTheDelay);
        app.SimulatePrefixPanelKey(Escape);

        await Assert.That(app.PrefixPanelOpen).IsFalse();
        await Assert.That(app.PrefixArmed).IsFalse();

        app.SimulateKey(Key('x'));
        await Assert.That(app.ArmedInputText).IsEqualTo("x");
    }

    /// <summary>
    /// <b>⌃B ⌃B disarms.</b> Every other surface in this client closes on the chord that opened it, and a
    /// held or fumbled chord used to leave a prefix armed that ate the next keystroke.
    /// </summary>
    [Test]
    public async Task TheChordThatArmedItDisarmsIt()
    {
        var app = ChatForward();

        app.SimulateKey(Ctrl(ConsoleKey.B));
        app.SimulateKey(Ctrl(ConsoleKey.B));

        await Assert.That(app.PrefixArmed).IsFalse();
        await Assert.That(app.HeaderText).DoesNotContain("awaiting");

        app.SimulateKey(Key('x'));
        await Assert.That(app.ArmedInputText).IsEqualTo("x");
    }

    /// <summary>And it takes the panel down too, once the panel is what ⌃B is showing.</summary>
    [Test]
    public async Task TheChordAlsoClosesTheOpenPanel()
    {
        var clock = new ManualTimeProvider();
        var app = App(clock);

        app.SimulateKey(Ctrl(ConsoleKey.B));
        clock.Advance(PastTheDelay);
        await Assert.That(app.PrefixPanelOpen).IsTrue();

        app.SimulateKey(Ctrl(ConsoleKey.B));
        await Assert.That(app.PrefixPanelOpen).IsFalse();
        await Assert.That(app.PrefixArmed).IsFalse();
    }

    // --- a prefix nothing could consume ------------------------------------------------------------

    /// <summary>
    /// <b>Arming during a move is ignored.</b> <c>HandleWindowKey</c> tests move mode first, so a prefix
    /// armed there survives the whole move and eats the first key after it — <c>x</c>, and a window is
    /// gone. The guard used to name overlays only.
    /// </summary>
    [Test]
    public async Task ArmingDuringAMoveIsIgnoredSoNothingIsEatenAfterIt()
    {
        var app = ChatForward();
        var tabs = app.PrefixFactsSnapshot.TabCount;

        app.SimulatePrefixedKey(Key('m'));
        await Assert.That(app.StatusMarkup).Contains("MOVE");

        app.SimulateKey(Ctrl(ConsoleKey.B));
        await Assert.That(app.PrefixArmed).IsFalse().Because("a move owns the keyboard; nothing could consume it");

        app.SimulateKey(Escape); // leave the move
        await Assert.That(app.StatusMarkup).DoesNotContain("MOVE");

        app.SimulateKey(Key('x'));
        await Assert.That(app.PrefixFactsSnapshot.TabCount).IsEqualTo(tabs);
        await Assert.That(app.ArmedInputText).IsEqualTo("x");
    }

    /// <summary>
    /// The same shape through the other door: a global shortcut pressed while the prefix is pending opens
    /// its surface and cancels the prefix, rather than leaving it armed behind the surface.
    /// </summary>
    [Test]
    public async Task AnotherClaimedChordCancelsAPendingPrefix()
    {
        var app = ChatForward();
        var tabs = app.PrefixFactsSnapshot.TabCount;

        app.SimulateKey(Ctrl(ConsoleKey.B));
        app.SimulateKey(Ctrl(ConsoleKey.P)); // the command surface
        await Assert.That(app.PrefixArmed).IsFalse();

        app.SimulateKey(Ctrl(ConsoleKey.P)); // and away again
        app.SimulateKey(Key('x'));
        await Assert.That(app.PrefixFactsSnapshot.TabCount).IsEqualTo(tabs);
        await Assert.That(app.ArmedInputText).IsEqualTo("x");
    }

    // --- honesty -----------------------------------------------------------------------------------

    /// <summary>
    /// <b>Nothing advertised is inert.</b> Every key named on the strip or on a panel row either acts or
    /// says why it cannot; none of them falls silently through to "any other key disarms". That
    /// fall-through is what an advertised-but-dead key would land in, and it is invisible from the outside
    /// — which is precisely how the whole feature read when it was reported.
    /// </summary>
    [Test]
    public async Task EveryAdvertisedKeyEitherActsOrSaysWhyNot()
    {
        var advertised = PrefixPanel.StripKeys
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Concat(Fresh().PrefixPanelEntries
                .SelectMany(e => e.Keys.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await Assert.That(advertised.Count).IsGreaterThanOrEqualTo(11);

        foreach (var token in advertised)
        {
            var app = Fresh();
            var before = (app.PrefixFactsSnapshot, app.StatusMarkup);
            app.SimulatePrefixedKey(Stroke(token));

            await Assert.That((app.PrefixFactsSnapshot, app.StatusMarkup))
                .IsNotEqualTo(before)
                .Because($"'{token}' is advertised, so it must act or refuse — not disarm in silence");
        }
    }

    /// <summary>
    /// The control: a key nothing advertises really does disarm in silence, so the assertion above has
    /// teeth rather than being satisfied by any keystroke at all.
    /// </summary>
    [Test]
    public async Task AnUnadvertisedKeyIsTheSilentCaseTheRuleIsAbout()
    {
        var app = Fresh();
        var before = (app.PrefixFactsSnapshot, app.StatusMarkup);

        app.SimulatePrefixedKey(Key('q'));

        await Assert.That((app.PrefixFactsSnapshot, app.StatusMarkup)).IsEqualTo(before);
    }

    /// <summary>
    /// <b>The armed header still fits the terminal it is on.</b> The header is one row, the framework wraps
    /// an overlong one, and a wrapped header costs a row of workspace — a pane getting shorter because you
    /// pressed ⌃B. The widest spelling of the strip ran the eighty-column layout to within four cells of
    /// the edge before this change named the exit on it, which is why the strip is chosen to fit.
    /// </summary>
    [Test]
    [Arguments(160)]
    [Arguments(120)]
    [Arguments(100)]
    [Arguments(80)]
    [Arguments(72)]
    public async Task TheArmedHeaderFitsTheTerminalItIsOn(int width)
    {
        var app = App(width: width);
        app.SimulateKey(Ctrl(ConsoleKey.B));

        await Assert.That(app.PrefixArmed).IsTrue();
        await Assert.That(app.HeaderMarkupWidth)
            .IsLessThanOrEqualTo(width)
            .Because($"an armed header wider than {width} cells wraps, and the wrap costs a row of output");
        await Assert.That(VisibleLength(app.HeaderText)).IsLessThanOrEqualTo(width);
    }

    /// <summary>The strip in a real frame names the exit, whatever spelling the width left room for.</summary>
    [Test]
    [Arguments(160)]
    [Arguments(80)]
    public async Task TheArmedHeaderNamesTheWayOut(int width)
    {
        var app = App(width: width);
        app.SimulateKey(Ctrl(ConsoleKey.B));

        await Assert.That(app.HeaderText).Contains("Esc");
    }

    /// <summary>
    /// <c>--help</c> names the way out as well as the keys. It is the only documentation a first-time user
    /// reads before the client opens, and "the prefix has no advertised exit" was reported from a surface
    /// that listed ten keys and no eleventh.
    /// </summary>
    [Test]
    public async Task HelpNamesTheWayOutOfThePrefix()
    {
        await Assert.That(Program.UsageText).Contains("Ctrl+B");
        await Assert.That(Program.UsageText).Contains("Esc");
    }

    /// <summary>The panel fits an eighty-column terminal, borders and all.</summary>
    [Test]
    public async Task ThePanelFitsAnEightyColumnTerminal()
    {
        var clock = new ManualTimeProvider();
        var app = App(clock, width: 80);

        app.SimulateKey(Ctrl(ConsoleKey.B));
        clock.Advance(PastTheDelay);

        await Assert.That(app.PrefixPanelOpen).IsTrue();
        foreach (var line in app.PrefixPanelLines)
        {
            await Assert.That(VisibleLength(line)).IsLessThanOrEqualTo(80 - 6);
        }
    }

    // --- helpers -----------------------------------------------------------------------------------

    /// <summary>Presses ⌃B and one key on a fresh <see cref="ChatForward"/> client, then checks the effect.</summary>
    private static async Task Drive(char key, Func<SharpMUTermApp, bool> did, string what) =>
        await Drive(Key(key), did, $"⌃B {key} {what}");

    private static async Task Drive(ConsoleKeyInfo key, Func<SharpMUTermApp, bool> did, string what)
    {
        var app = ChatForward();
        app.SimulatePrefixedKey(key);
        await Assert.That(did(app)).IsTrue().Because($"the panel says {what}");
    }

    /// <summary>The keystroke a strip token names — the arrows by key, everything else by character.</summary>
    private static ConsoleKeyInfo Stroke(string token) => token switch
    {
        "←" => new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false),
        "→" => new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false),
        _ => Key(token[0]),
    };
}
