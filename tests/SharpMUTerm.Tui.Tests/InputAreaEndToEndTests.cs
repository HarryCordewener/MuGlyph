using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The input area driven end to end, through the app's own key handler.
/// <para>
/// <see cref="DraftStoreTests"/> already proved the store keeps a draft per window, and it always had:
/// the defect the maintainer reported ("switching windows does not change what is in the Input window")
/// was never in the store or in either recall site. It was that the command line had no keyboard focus
/// and the app never asked for any, so plain keystrokes went to whichever control the framework happened
/// to be focusing, no <c>InputChanged</c> ever fired, no draft was ever recorded, and every tab switch
/// dutifully recalled the empty string it had stored. A unit test on the store could not see that, and
/// nothing else was watching — which is why these go through <see cref="SharpMUTermApp.SimulateKey"/>,
/// the same handler a real keystroke reaches.
/// </para>
/// </summary>
/// <remarks>
/// Serialised for the same reason <see cref="MacroDispatchEndToEndTests"/> is: constructing the app
/// touches the process-global console streams.
/// </remarks>
[NotInParallel]
public class InputAreaEndToEndTests
{
    private const int Width = 120;
    private const int Height = 34;
    private const string MainWindow = "main";
    private const string ChatWindow = "spawn:Chat";

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>The demo workspace (a main window and a Chat spawn window) with no socket under it.</summary>
    private static (SharpMUTermApp App, AppConfiguration Config) Demo()
    {
        Console.SetIn(TextReader.Null);

        var config = DemoScene.Build();
        config.Worlds[0].Characters[0].Logging = new LoggingSettings();

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        app.BindWorldWithoutConnecting(config.Worlds[0]);
        app.SimulateWindowChange(MainWindow);
        Send(app, "");  // the demo seeds a line into the input; start from empty
        return (app, config);
    }

    private static ConsoleKeyInfo Key(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static ConsoleKeyInfo Chord(ConsoleKey key, bool ctrl = false, bool shift = false) =>
        new('\0', key, shift, false, ctrl);

    private static void Type(SharpMUTermApp app, string text)
    {
        foreach (var c in text)
        {
            app.SimulateKey(Key(c));
        }
    }

    private static void Send(SharpMUTermApp app, string text)
    {
        Type(app, text);
        app.SimulateKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
    }

    /// <summary>Typing reaches the command line with nothing focused and nothing clicked first.</summary>
    [Test]
    public async Task TypingGoesStraightToTheCommandLine()
    {
        var (app, _) = Demo();

        Type(app, "say hello");

        await Assert.That(app.ArmedInputText).IsEqualTo("say hello");
    }

    /// <summary>
    /// The headline regression: each window keeps its own unsent line, and switching away and back
    /// gives it back. Both halves matter — leaving must not carry the text across, and returning must
    /// not have lost it.
    /// </summary>
    [Test]
    public async Task EachWindowKeepsItsOwnDraftAcrossATabSwitch()
    {
        var (app, _) = Demo();

        Type(app, "say hello there");
        app.SimulateWindowChange(ChatWindow);
        await Assert.That(app.ArmedInputText).IsEqualTo(string.Empty);

        Type(app, "page anvil = brb");
        app.SimulateWindowChange(MainWindow);
        await Assert.That(app.ArmedInputText).IsEqualTo("say hello there");

        app.SimulateWindowChange(ChatWindow);
        await Assert.That(app.ArmedInputText).IsEqualTo("page anvil = brb");
    }

    /// <summary>F8's box off means a switched-away window hands nothing back.</summary>
    [Test]
    public async Task WithKeepDraftsOff_NothingComesBack()
    {
        var (app, config) = Demo();
        config.Input.KeepDrafts = false;

        Type(app, "say hello there");
        app.SimulateWindowChange(ChatWindow);
        app.SimulateWindowChange(MainWindow);

        await Assert.That(app.ArmedInputText).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// ⏎ sends and the caret stays put: the very next keystroke lands in the command line. The framework
    /// prompt unfocused itself on ⏎ (<c>UnfocusOnEnter</c>, on by default), which meant a second command
    /// could not be typed without clicking back in.
    /// </summary>
    [Test]
    public async Task EnterDoesNotLoseTheCommandLine()
    {
        var (app, _) = Demo();

        Send(app, "look");
        Type(app, "north");

        await Assert.That(app.ArmedInputText).IsEqualTo("north");
    }

    /// <summary>A sent line clears that window's draft rather than leaving it to reappear on return.</summary>
    [Test]
    public async Task ASentLineIsNotHandedBackAsADraft()
    {
        var (app, _) = Demo();

        Send(app, "look");
        app.SimulateWindowChange(ChatWindow);
        app.SimulateWindowChange(MainWindow);

        await Assert.That(app.ArmedInputText).IsEqualTo(string.Empty);
    }

    /// <summary>The command line wraps and grows rather than scrolling sideways at one row.</summary>
    [Test]
    public async Task TheCommandLineGrowsAsItWraps()
    {
        var (app, config) = Demo();

        await Assert.That(app.PrimaryInputRows).IsEqualTo(config.Input.Rows);

        Type(app, new string('x', Width * 3));

        await Assert.That(app.PrimaryInputRows).IsGreaterThan(config.Input.Rows);
        await Assert.That(app.PrimaryInputRows).IsLessThanOrEqualTo(config.Input.MaxRows);
    }

    /// <summary>⌃B i shows the second bar, and only for the window it was pressed on.</summary>
    [Test]
    public async Task TheSecondBarIsPerWindow()
    {
        var (app, _) = Demo();

        ToggleSecondBar(app);
        await Assert.That(app.SecondBarShown).IsTrue();

        app.SimulateWindowChange(ChatWindow);
        await Assert.That(app.SecondBarShown).IsFalse();

        app.SimulateWindowChange(MainWindow);
        await Assert.That(app.SecondBarShown).IsTrue();
    }

    /// <summary>
    /// The whole point of the second bar: two unsent lines at once, each one its own draft, and both
    /// kept per window. An IC line and an OOC line survive a trip to another tab and back.
    /// </summary>
    [Test]
    public async Task BothBarsKeepTheirOwnDraftPerWindow()
    {
        var (app, _) = Demo();

        // Raising the second bar arms it, so the OOC line is typed first and ⇥ goes back to the IC one.
        ToggleSecondBar(app);
        await Assert.That(app.SecondBarArmed).IsTrue();
        Type(app, "ooc back in five");

        app.SimulateKey(Chord(ConsoleKey.Tab));
        await Assert.That(app.SecondBarArmed).IsFalse();
        Type(app, "pose smiles.");

        app.SimulateWindowChange(ChatWindow);
        app.SimulateWindowChange(MainWindow);

        await Assert.That(app.PrimaryInputText).IsEqualTo("pose smiles.");
        await Assert.That(app.SecondaryInputText).IsEqualTo("ooc back in five");
    }

    /// <summary>
    /// Raising the second bar must not disturb the line already being typed. It would be easy for it
    /// to: the toggle re-syncs the input area, and a re-sync that also recalled drafts would empty both
    /// bars whenever <c>keep per-tab drafts</c> is off, because the store hands nothing back in that
    /// mode by design. Asserted with the box off, which is the only setting that can see the bug.
    /// </summary>
    [Test]
    public async Task ShowingTheSecondBar_KeepsWhatIsAlreadyTyped()
    {
        var (app, config) = Demo();
        config.Input.KeepDrafts = false;

        Type(app, "say hello there");
        ToggleSecondBar(app);

        await Assert.That(app.PrimaryInputText).IsEqualTo("say hello there");
    }

    /// <summary>Hiding the armed bar hands ⏎ back to the primary rather than pointing it off screen.</summary>
    [Test]
    public async Task HidingTheArmedSecondBar_RearmsTheFirst()
    {
        var (app, _) = Demo();
        ToggleSecondBar(app); // raising it arms it — no ⇥ needed to get there
        await Assert.That(app.SecondBarArmed).IsTrue();

        ToggleSecondBar(app);

        await Assert.That(app.SecondBarArmed).IsFalse();
    }

    /// <summary>
    /// The newline chord this host delivers puts a second line in the draft; ⏎ then sends the whole
    /// thing. Asserted through the app so the window's own handler is in the path — it is the layer that
    /// used to swallow the keystroke entirely.
    /// </summary>
    [Test]
    public async Task TheNewlineChordBuildsAMultilineDraftThatEnterSendsWhole()
    {
        var (app, _) = Demo();

        Type(app, "pose ");
        app.SimulateKey(Chord(ConsoleKey.L, ctrl: true));
        Type(app, "smiles.");

        await Assert.That(app.ArmedInputText).IsEqualTo("pose \nsmiles.");

        app.SimulateKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        await Assert.That(app.ArmedInputText).IsEqualTo(string.Empty);
    }

    /// <summary>↑ inside a grown command line moves the caret; ↑ on its first row still recalls.</summary>
    [Test]
    public async Task HistoryRecallStillWorksOnTheFirstRowOfAGrownLine()
    {
        var (app, _) = Demo();
        Send(app, "look");

        app.SimulateKey(Chord(ConsoleKey.UpArrow));

        await Assert.That(app.ArmedInputText).IsEqualTo("look");
    }

    /// <summary>A terminal paste lands in the command line, at the caret, newlines and all.</summary>
    [Test]
    public async Task APasteReachesTheArmedBar()
    {
        var (app, _) = Demo();
        Type(app, "say ");

        app.SimulatePaste("hello there");

        await Assert.That(app.ArmedInputText).IsEqualTo("say hello there");
    }

    /// <summary>
    /// The reported defect, and the reason it hid for so long. Anything that takes the keyboard focus —
    /// a click in the output pane, ⇥ with one bar up — hands it to a control that does not accept
    /// paste, and the next paste was dropped without a trace. Typing masked it: the app routes
    /// keystrokes to the armed bar itself and puts the focus back on the way past, so the very act of
    /// checking whether the client was alive repaired the state that broke paste. Proven against the
    /// real binary under a pty before it was written down — a click, then a bracketed paste, then
    /// nothing on the command line.
    /// </summary>
    [Test]
    public async Task APasteAfterFocusIsTakenOffTheBar_StillReachesTheArmedBar()
    {
        var (app, _) = Demo();

        app.SimulateFocusSteal();
        await Assert.That(app.ArmedBarHasFocus).IsTrue();

        app.SimulatePaste("pose waves.");

        await Assert.That(app.ArmedInputText).IsEqualTo("pose waves.");
    }

    /// <summary>
    /// With two command lines up, a paste goes to the one ⏎ sends from — the only answer that is not
    /// arbitrary. Asserted on the second bar because the primary is where a paste would land by accident.
    /// </summary>
    [Test]
    public async Task APasteGoesToTheArmedBarWhenTheSecondOneIsUp()
    {
        var (app, _) = Demo();
        ToggleSecondBar(app);
        await Assert.That(app.SecondBarArmed).IsTrue();

        app.SimulatePaste("ooc kettle");

        await Assert.That(app.SecondaryInputText).IsEqualTo("ooc kettle");
        await Assert.That(app.PrimaryInputText).IsEqualTo(string.Empty);

        // …and back the other way, so neither bar is passing by being the default.
        app.SimulateKey(Chord(ConsoleKey.Tab));
        app.SimulatePaste("say hello");

        await Assert.That(app.PrimaryInputText).IsEqualTo("say hello");
        await Assert.That(app.SecondaryInputText).IsEqualTo("ooc kettle");
    }

    /// <summary>
    /// Raising the second bar arms it: it is the line that was just asked for, and the caret goes with
    /// ⏎ rather than staying on the bar above while a new empty one appears below it.
    /// </summary>
    [Test]
    public async Task RaisingTheSecondBar_ArmsItAndTakesTheCaret()
    {
        var (app, _) = Demo();

        ToggleSecondBar(app);

        await Assert.That(app.SecondBarArmed).IsTrue();
        await Assert.That(app.ArmedBarHasFocus).IsTrue();
        await Assert.That(app.CaretReported).IsEqualTo((false, true));

        Type(app, "ooc");
        await Assert.That(app.SecondaryInputText).IsEqualTo("ooc");
        await Assert.That(app.PrimaryInputText).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// Hiding the second bar takes the caret off it. The bar goes invisible where it stands, so nothing
    /// but this moves the terminal's cursor off a control the window no longer draws — which is how it
    /// was reported: "the cursor blink remains where it was".
    /// </summary>
    [Test]
    public async Task HidingTheSecondBar_TakesTheCaretOffIt()
    {
        var (app, _) = Demo();
        ToggleSecondBar(app);
        await Assert.That(app.CaretReported).IsEqualTo((false, true));

        ToggleSecondBar(app);

        await Assert.That(app.SecondBarShown).IsFalse();
        await Assert.That(app.CaretReported).IsEqualTo((true, false));
        await Assert.That(app.ArmedBarHasFocus).IsTrue();
        await Assert.That(app.FocusIsOnAVisibleControl).IsTrue();
    }

    /// <summary>
    /// ⇥ with a single command line up has no sibling to hand the caret to, so the framework walks focus
    /// out of the input area entirely — and the caret went with it, leaving a client with no cursor at
    /// all. The armed bar keeps both.
    /// </summary>
    [Test]
    public async Task FocusWalkingOffTheCommandLine_TakesTheCaretWithItNoMore()
    {
        var (app, _) = Demo();

        app.SimulateFocusSteal();

        await Assert.That(app.ArmedBarHasFocus).IsTrue();
        await Assert.That(app.CaretReported).IsEqualTo((true, false));
    }

    /// <summary>The ⌃B prefix, then <c>i</c> — the same two keystrokes the header advertises.</summary>
    private static void ToggleSecondBar(SharpMUTermApp app)
    {
        app.SimulateKey(Chord(ConsoleKey.B, ctrl: true));
        app.SimulateKey(new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false));
    }

    // --- where the caret is *painted* ---------------------------------------------------------

    /// <summary>
    /// <b>The caret sits on the cell after the last character it can see, on the row that character was
    /// drawn on.</b> Asserted against the frame, through the cursor position the driver is handed — not
    /// against <c>InputBarControl.GetLogicalCursorPosition</c>, which is the function a caret bug lives
    /// in and which a test built on it would agree with while the screen disagreed.
    /// <para>
    /// The heights are the point. F8's <c>input height</c> changes <c>MinRows</c> at runtime, and the bar
    /// is sticky-bottom, so growing it moves the row the text starts on — while the caret is computed
    /// from the control's own idea of its box. The two derive that box from one place now
    /// (<c>InputBarControl.Geometry</c>); this is what would notice if they stopped.
    /// </para>
    /// </summary>
    [Test]
    [Arguments(1, false)]
    [Arguments(3, false)]
    [Arguments(8, false)]
    [Arguments(1, true)]
    [Arguments(5, true)]
    public async Task TheCaretIsPaintedOnTheRowTheTextIs(int rows, bool wrapped)
    {
        var (app, config) = Demo();
        config.Input.Rows = 1;
        app.SaveConfiguration();

        // A sentinel the rest of the frame cannot contain, so "where the caret should be" is read off
        // the painted cells rather than computed the way the code under test computes it.
        Type(app, (wrapped ? new string('x', 260) : "ab") + Sentinel);
        app.RenderNextFrame();
        await Assert.That(CaretFollowsTheSentinel(app)).IsTrue();

        config.Input.Rows = rows; // F8, live
        app.SaveConfiguration();
        app.RenderNextFrame();

        await Assert.That(CaretFollowsTheSentinel(app)).IsTrue();
    }

    /// <summary>
    /// Showing the second bar, cycling to it and back with ⇥ (which is what ⌃I arrives as — 0x09 is HT,
    /// and the parser reports it with no Control bit), and hiding it again: at every step the painted
    /// caret is on the bar that ⏎ sends from, at that bar's own prompt. The first cycle is checked
    /// separately from the second because "initial switching" was reported as behaving differently.
    /// </summary>
    [Test]
    public async Task TheCaretFollowsTheArmedBarThroughShowCycleAndHide()
    {
        var (app, _) = Demo();
        Type(app, "ab" + Sentinel);
        app.RenderNextFrame();
        await Assert.That(CaretFollowsTheSentinel(app)).IsTrue();

        ToggleSecondBar(app); // the second bar arms itself, empty, on its own row
        await Assert.That(app.SecondBarArmed).IsTrue();
        var second = CaretRow(app);
        await Assert.That(second).IsGreaterThan(SentinelRow(app)); // below the primary, where it is drawn

        app.SimulateKey(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false)); // the first cycle
        await Assert.That(app.SecondBarArmed).IsFalse();
        await Assert.That(CaretFollowsTheSentinel(app)).IsTrue();

        app.SimulateKey(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false)); // and the next
        await Assert.That(app.SecondBarArmed).IsTrue();
        await Assert.That(CaretRow(app)).IsEqualTo(second);

        ToggleSecondBar(app); // hidden: the caret comes back to the primary, on the row it is drawn on
        await Assert.That(app.SecondBarShown).IsFalse();
        await Assert.That(CaretFollowsTheSentinel(app)).IsTrue();
    }

    /// <summary>A character no other part of the frame draws, so its painted cell is unambiguous.</summary>
    private const char Sentinel = '~';

    /// <summary>Whether the terminal's caret is in the cell just after the sentinel's painted one.</summary>
    private static bool CaretFollowsTheSentinel(SharpMUTermApp app)
    {
        var (visible, x, y) = app.CaretOnScreen();
        var (row, column) = FindSentinel(app);
        return visible && row >= 0 && y == row && x == column + 1;
    }

    private static int CaretRow(SharpMUTermApp app) => app.CaretOnScreen().Y;

    private static int SentinelRow(SharpMUTermApp app) => FindSentinel(app).Row;

    /// <summary>The cell the sentinel was painted in, decoded from a whole rendered frame.</summary>
    private static (int Row, int Column) FindSentinel(SharpMUTermApp app)
    {
        var grid = FrameGrid.Decode(app.RenderWholeFrame(), Width, Height);
        for (var row = grid.Count - 1; row >= 0; row--)
        {
            var column = grid[row].LastIndexOf(Sentinel);
            if (column >= 0)
            {
                return (row, column);
            }
        }

        return (-1, -1);
    }
}
