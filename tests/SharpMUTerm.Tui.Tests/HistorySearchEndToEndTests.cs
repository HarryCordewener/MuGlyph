using System.Collections.Concurrent;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Telnet;
using SharpMUTerm.Core.Transport;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The ⌃R history surface driven through the chord the app actually registers.
/// <see cref="HistorySearchPromptTests"/> pins what the surface means and says; these pin that the client
/// is wired to it — that the chord opens it, that ⏎ puts a line on the command line and <em>nothing on the
/// wire</em>, that cancelling leaves a half-typed draft exactly as it was, and that a hand-typed password
/// never gets into the list in the first place.
/// </summary>
/// <remarks>
/// Serialised for the same reason the other end-to-end suites are: constructing the app touches the
/// process-global console streams.
/// </remarks>
[NotInParallel]
public class HistorySearchEndToEndTests
{
    private const int Width = 120;
    private const int Height = 34;
    private const string MainWindow = "main";

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>Every transport the app opened, so a test can read back what did — and did not — go out.</summary>
    private sealed class Transports
    {
        private readonly ConcurrentQueue<RecordingTelnetSession> _opened = new();

        public IReadOnlyList<RecordingTelnetSession> Opened => _opened.ToArray();

        /// <summary>Every line written to any world this run, oldest first.</summary>
        public IReadOnlyList<string> Sent => Opened.SelectMany(t => t.Lines).ToArray();

        public ITelnetSession Open(ConnectionOptions options)
        {
            var telnet = new RecordingTelnetSession();
            _opened.Enqueue(telnet);
            return telnet;
        }
    }

    /// <summary>
    /// The demo workspace with a <em>connected</em> recording transport under it, and the command line
    /// emptied — the demo seeds a line into it, and every one of these tests is about what the bar holds.
    /// <para>
    /// The connection is the point: <c>WorldSession.SendRawAsync</c> drops everything while the session is
    /// not connected, so a test asserting "nothing reached the transport" against an unconnected one would
    /// pass whatever the surface did. Here the seeded lines really do go out, which is what makes the
    /// absence of a fifth line mean something.
    /// </para>
    /// </summary>
    private static async Task<(SharpMUTermApp App, Transports Telnet)> Demo(string? password = null)
    {
        Console.SetIn(TextReader.Null);

        var config = DemoScene.Build();
        config.Worlds[0].Characters[0].Password = password;

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        var telnet = new Transports();
        app.TelnetFactory = telnet.Open;
        var session = app.BindWorldWithoutConnecting(config.Worlds[0]);
        await session.ConnectAsync();
        app.SimulateWindowChange(MainWindow);
        Clear(app);
        return (app, telnet);
    }

    private static ConsoleKeyInfo Key(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static ConsoleKeyInfo Bare(ConsoleKey key) => new('\0', key, false, false, false);

    private static ConsoleKeyInfo Chord(ConsoleKey key) => new('\0', key, false, false, true);

    private static void Type(SharpMUTermApp app, string text)
    {
        foreach (var c in text)
        {
            app.SimulateKey(Key(c));
        }
    }

    /// <summary>Sends a line the way a user does: types it, then ⏎.</summary>
    private static void Send(SharpMUTermApp app, string text)
    {
        Type(app, text);
        app.SimulateKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
    }

    private static void Clear(SharpMUTermApp app) => Send(app, string.Empty);

    /// <summary>A few sent commands, in the order they were sent.</summary>
    private static void Seed(SharpMUTermApp app)
    {
        Send(app, "look");
        Send(app, "say hello");
        Send(app, "north");
        Send(app, "say the northern watch sent word");
    }

    private static void ToggleSecondBar(SharpMUTermApp app)
    {
        app.SimulateKey(Chord(ConsoleKey.B));
        app.SimulateKey(new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false));
    }

    // ---- Opening and closing ---------------------------------------------------------------------

    /// <summary>⌃R opens it, and ⌃R again closes it — the toggle every surface here is on.</summary>
    [Test]
    public async Task CtrlROpensTheSurfaceAndCtrlRAgainClosesIt()
    {
        var (app, _) = await Demo();
        Seed(app);

        app.SimulateKey(Chord(ConsoleKey.R));
        await Assert.That(app.HistorySearchOpen).IsTrue();

        app.SimulateKey(Chord(ConsoleKey.R));
        await Assert.That(app.HistorySearchOpen).IsFalse();
    }

    /// <summary>It opens on an empty history too, and says so rather than drawing an empty box.</summary>
    [Test]
    public async Task ItOpensOnAnEmptyHistoryAndSaysThereIsNothingYet()
    {
        var (app, _) = await Demo();

        app.SimulateKey(Chord(ConsoleKey.R));

        await Assert.That(app.HistorySearchOpen).IsTrue();
        await Assert.That(string.Join("\n", app.HistorySearchLines)).Contains("nothing has been entered");
        await Assert.That(app.HistorySearchSelection).IsNull();
    }

    // ---- The chronological list -------------------------------------------------------------------

    /// <summary>The list is what was entered, newest first, and the pointer starts on the newest.</summary>
    [Test]
    public async Task ItListsWhatWasEnteredNewestFirst()
    {
        var (app, _) = await Demo();
        Seed(app);

        app.SimulateKey(Chord(ConsoleKey.R));

        await Assert.That(app.HistorySearchRows).IsEquivalentTo(new[]
        {
            "say the northern watch sent word", "north", "say hello", "look",
        });
        await Assert.That(app.HistorySearchSelection).IsEqualTo("say the northern watch sent word");
    }

    // ---- The search ------------------------------------------------------------------------------

    /// <summary>Typing filters the list, still newest first.</summary>
    [Test]
    public async Task TypingFiltersTheList()
    {
        var (app, _) = await Demo();
        Seed(app);
        app.SimulateKey(Chord(ConsoleKey.R));

        app.SimulateHistorySearchTyping("north");

        await Assert.That(app.HistorySearchRows)
            .IsEquivalentTo(new[] { "say the northern watch sent word", "north" });
        await Assert.That(string.Join("\n", app.HistorySearchLines)).Contains("2 of 4");
    }

    /// <summary>A filter that matches nothing lists nothing and says so; ⌫ brings the list back.</summary>
    [Test]
    public async Task AFilterThatMatchesNothingListsNothingAndBackspaceUndoesIt()
    {
        var (app, _) = await Demo();
        Seed(app);
        app.SimulateKey(Chord(ConsoleKey.R));

        app.SimulateHistorySearchTyping("zzz");

        await Assert.That(app.HistorySearchRows).IsEmpty();
        await Assert.That(app.HistorySearchSelection).IsNull();
        await Assert.That(string.Join("\n", app.HistorySearchLines)).Contains("no matches");

        app.SimulateHistorySearchKey(Bare(ConsoleKey.Backspace));
        app.SimulateHistorySearchKey(Bare(ConsoleKey.Backspace));
        app.SimulateHistorySearchKey(Bare(ConsoleKey.Backspace));

        await Assert.That(app.HistorySearchRows.Count).IsEqualTo(4);
    }

    /// <summary>⏎ with nothing listed does not insert anything, and does not close the surface either.</summary>
    [Test]
    public async Task EnterWithNoMatchesInsertsNothing()
    {
        var (app, telnet) = await Demo();
        Seed(app);
        Type(app, "half-typed");
        app.SimulateKey(Chord(ConsoleKey.R));
        app.SimulateHistorySearchTyping("zzz");

        var before = telnet.Sent;
        app.SimulateHistorySearchKey(Bare(ConsoleKey.Enter));

        await Assert.That(app.HistorySearchOpen).IsTrue();
        await Assert.That(app.ArmedInputText).IsEqualTo("half-typed");
        await Assert.That(telnet.Sent).IsEquivalentTo(before);
    }

    // ---- ⏎ inserts, and does not send -------------------------------------------------------------

    /// <summary>
    /// The headline: ⏎ puts the entry on the command line and nothing at all on the wire. A surface that
    /// fired commands at a live world on one keystroke would be a footgun — and with a filter in play, the
    /// pointed-at row is not always the one the user had in mind.
    /// </summary>
    [Test]
    public async Task EnterInsertsTheEntryAndSendsNothing()
    {
        var (app, telnet) = await Demo();
        Seed(app);
        var before = telnet.Sent;
        app.SimulateKey(Chord(ConsoleKey.R));
        app.SimulateHistorySearchTyping("hello");

        app.SimulateHistorySearchKey(Bare(ConsoleKey.Enter));

        await Assert.That(app.ArmedInputText).IsEqualTo("say hello");
        await Assert.That(app.HistorySearchOpen).IsFalse();

        // Nothing went out: the wire holds exactly what it held before the surface opened.
        await Assert.That(telnet.Sent).IsEquivalentTo(before);

        // And the recorder is not vacuous — the seeded lines really did reach it.
        await Assert.That(before).Contains("say hello");
    }

    /// <summary>↑↓ pick a different row, and ⏎ inserts the one the surface is pointing at.</summary>
    [Test]
    public async Task TheArrowsPickWhichEntryEnterInserts()
    {
        var (app, telnet) = await Demo();
        Seed(app);
        var before = telnet.Sent;
        app.SimulateKey(Chord(ConsoleKey.R));

        app.SimulateHistorySearchKey(Bare(ConsoleKey.DownArrow));
        app.SimulateHistorySearchKey(Bare(ConsoleKey.DownArrow));
        await Assert.That(app.HistorySearchSelection).IsEqualTo("say hello");

        app.SimulateHistorySearchKey(Bare(ConsoleKey.Enter));

        await Assert.That(app.ArmedInputText).IsEqualTo("say hello");
        await Assert.That(telnet.Sent).IsEquivalentTo(before);
    }

    /// <summary>
    /// An inserted line is a recalled line: it leaves the command line in recall, so <c>↓</c> walks forward
    /// through history and back to the draft the insert displaced. That reuse of
    /// <c>InputHistory.RecallAt</c> is the whole reason the surface needs no draft machinery of its own.
    /// </summary>
    [Test]
    public async Task AnInsertedLineIsRecalledSoDownWalksBackToTheDraft()
    {
        var (app, _) = await Demo();
        Seed(app);
        Type(app, "half-typed");

        app.SimulateKey(Chord(ConsoleKey.R));
        app.SimulateHistorySearchTyping("hello");
        app.SimulateHistorySearchKey(Bare(ConsoleKey.Enter));
        await Assert.That(app.ArmedInputText).IsEqualTo("say hello");

        // And the status line says so, in the words it already uses for a ↑-recalled line — a hint that is
        // true precisely because the insert went through the same recall cursor.
        await Assert.That(app.StatusMarkup).Contains("↓ back to draft");

        // Forward from the picked entry, exactly as ↑ recall behaves.
        app.SimulateKey(Bare(ConsoleKey.DownArrow));
        await Assert.That(app.ArmedInputText).IsEqualTo("north");
        app.SimulateKey(Bare(ConsoleKey.DownArrow));
        await Assert.That(app.ArmedInputText).IsEqualTo("say the northern watch sent word");
        app.SimulateKey(Bare(ConsoleKey.DownArrow));
        await Assert.That(app.ArmedInputText).IsEqualTo("half-typed");
    }

    // ---- Esc leaves the draft alone ---------------------------------------------------------------

    /// <summary>
    /// Opening the surface and cancelling it leaves a half-typed line exactly as it was — and leaves it as
    /// the live draft rather than as a recalled line, so the next <c>↑</c> stashes it afresh.
    /// </summary>
    [Test]
    public async Task OpeningAndCancellingLeavesTheDraftIntact()
    {
        var (app, _) = await Demo();
        Seed(app);
        Type(app, "say half-typed");

        app.SimulateKey(Chord(ConsoleKey.R));
        await Assert.That(app.ArmedInputText).IsEqualTo("say half-typed");
        app.SimulateHistorySearchKey(Bare(ConsoleKey.Escape));

        await Assert.That(app.HistorySearchOpen).IsFalse();
        await Assert.That(app.ArmedInputText).IsEqualTo("say half-typed");

        // Still the live draft: ↑ parks it, and ↓ hands it back.
        app.SimulateKey(Bare(ConsoleKey.UpArrow));
        await Assert.That(app.ArmedInputText).IsEqualTo("say the northern watch sent word");
        app.SimulateKey(Bare(ConsoleKey.DownArrow));
        await Assert.That(app.ArmedInputText).IsEqualTo("say half-typed");
    }

    /// <summary>Filtering and then cancelling leaves the draft alone too — typing goes to the query.</summary>
    [Test]
    public async Task TypingIntoTheFilterNeverReachesTheCommandLine()
    {
        var (app, telnet) = await Demo();
        Seed(app);
        Type(app, "say half-typed");

        var before = telnet.Sent;
        app.SimulateKey(Chord(ConsoleKey.R));
        app.SimulateHistorySearchTyping("north");
        app.SimulateHistorySearchKey(Bare(ConsoleKey.Escape));

        await Assert.That(app.ArmedInputText).IsEqualTo("say half-typed");
        await Assert.That(telnet.Sent).IsEquivalentTo(before);
    }

    // ---- Both command lines ----------------------------------------------------------------------

    /// <summary>
    /// The surface belongs to the armed command line. The bars keep separate histories, so the second bar's
    /// surface shows the second bar's lines — and says which line it is showing.
    /// </summary>
    [Test]
    public async Task TheSurfaceShowsTheArmedBarsOwnHistory()
    {
        var (app, _) = await Demo();
        Seed(app);

        ToggleSecondBar(app); // raising it arms it
        await Assert.That(app.SecondBarArmed).IsTrue();
        Send(app, "ooc back in five");

        app.SimulateKey(Chord(ConsoleKey.R));

        await Assert.That(app.HistorySearchRows).IsEquivalentTo(new[] { "ooc back in five" });
        await Assert.That(string.Join("\n", app.HistorySearchLines)).Contains("second command line");
    }

    /// <summary>And ⏎ there inserts into the second bar, leaving the first bar's draft untouched.</summary>
    [Test]
    public async Task EnterInsertsIntoTheArmedBar()
    {
        var (app, telnet) = await Demo();
        Seed(app);
        Type(app, "say still typing");

        ToggleSecondBar(app);
        Send(app, "ooc back in five");
        var before = telnet.Sent;

        app.SimulateKey(Chord(ConsoleKey.R));
        app.SimulateHistorySearchKey(Bare(ConsoleKey.Enter));

        await Assert.That(app.SecondaryInputText).IsEqualTo("ooc back in five");
        await Assert.That(app.PrimaryInputText).IsEqualTo("say still typing");
        await Assert.That(before).Contains("ooc back in five"); // sending it is what put it in history
        await Assert.That(telnet.Sent).IsEquivalentTo(before);   // inserting it sent nothing
    }

    /// <summary>Back on the first bar, the surface shows the first bar's history again.</summary>
    [Test]
    public async Task EachBarsSurfaceShowsOnlyItsOwnLines()
    {
        var (app, _) = await Demo();
        Seed(app);
        ToggleSecondBar(app);
        Send(app, "ooc back in five");

        app.SimulateKey(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false)); // ⇥ back to the first
        await Assert.That(app.SecondBarArmed).IsFalse();

        app.SimulateKey(Chord(ConsoleKey.R));

        await Assert.That(app.HistorySearchRows).DoesNotContain("ooc back in five");
        await Assert.That(app.HistorySearchRows.Count).IsEqualTo(4);
        await Assert.That(string.Join("\n", app.HistorySearchLines)).Contains("command line");
    }

    // ---- The credential ignore rule ---------------------------------------------------------------

    /// <summary>
    /// A hand-typed login never enters history, so it cannot be listed, searched or recalled. The line is
    /// still sent — this is about what is kept, not what is delivered.
    /// </summary>
    [Test]
    public async Task ATypedLoginNeverReachesTheHistorySurface()
    {
        var (app, telnet) = await Demo();

        Send(app, "connect Corvid hunter2");
        Send(app, "look");

        app.SimulateKey(Chord(ConsoleKey.R));

        await Assert.That(app.HistorySearchRows).IsEquivalentTo(new[] { "look" });
        await Assert.That(string.Join("\n", app.HistorySearchLines)).DoesNotContain("hunter2");
        await Assert.That(telnet.Sent).Contains("connect Corvid hunter2"); // it did go to the world
    }

    /// <summary>Not even by searching for it: it is not in the store at all.</summary>
    [Test]
    public async Task NorCanItBeFoundByFilteringForIt()
    {
        var (app, _) = await Demo();
        Send(app, "connect Corvid hunter2");
        Send(app, "look");
        app.SimulateKey(Chord(ConsoleKey.R));

        app.SimulateHistorySearchTyping("hunter");

        await Assert.That(app.HistorySearchRows).IsEmpty();
    }

    /// <summary>Nor by <c>↑</c>, which is the same store.</summary>
    [Test]
    public async Task NorByRecall()
    {
        var (app, _) = await Demo();
        Send(app, "connect Corvid hunter2");
        Send(app, "look");

        app.SimulateKey(Bare(ConsoleKey.UpArrow));
        await Assert.That(app.ArmedInputText).IsEqualTo("look");
        app.SimulateKey(Bare(ConsoleKey.UpArrow));
        await Assert.That(app.ArmedInputText).IsEqualTo("look"); // there is nothing older
    }

    /// <summary>
    /// The rule is narrow: the same verb without a password is ordinary history. A rule that swallowed
    /// <c>connect guest</c> would be teaching the user that history is unreliable.
    /// </summary>
    [Test]
    public async Task ALoginLineWithNoPasswordIsKept()
    {
        var (app, _) = await Demo();

        Send(app, "connect guest");

        app.SimulateKey(Chord(ConsoleKey.R));

        await Assert.That(app.HistorySearchRows).IsEquivalentTo(new[] { "connect guest" });
    }

    /// <summary>The second bar's history is filtered by the same rule — it is the same kind of store.</summary>
    [Test]
    public async Task TheSecondBarIsGuardedToo()
    {
        var (app, _) = await Demo();
        ToggleSecondBar(app);

        Send(app, "connect Corvid hunter2");
        Send(app, "ooc hi");

        app.SimulateKey(Chord(ConsoleKey.R));

        await Assert.That(app.HistorySearchRows).IsEquivalentTo(new[] { "ooc hi" });
    }

    /// <summary>And it is a setting, defaulting to on. Untick it and the line is kept like any other.</summary>
    [Test]
    public async Task TheRuleIsASettingThatDefaultsToOn()
    {
        await Assert.That(new InputSettings().ExcludeCredentials).IsTrue();

        var (app, _) = await Demo();
        app.Configuration.Input.ExcludeCredentials = false;

        Send(app, "connect Corvid hunter2");

        app.SimulateKey(Chord(ConsoleKey.R));

        await Assert.That(app.HistorySearchRows).IsEquivalentTo(new[] { "connect Corvid hunter2" });
    }

    /// <summary>
    /// A <em>configured</em> auto-login was already safe, and this pins it: the connect string goes out
    /// through <c>WorldSession.SendLoginAsync</c> → <c>SendRawAsync</c>, which never touches the UI's
    /// history at all. The surface therefore cannot leak a stored password — only a hand-typed one was ever
    /// at risk, which is what the ignore rule is for.
    /// </summary>
    [Test]
    public async Task AConfiguredAutoLoginPasswordNeverEntersHistory()
    {
        var (app, telnet) = await Demo(password: "hunter2");

        await Assert.That(telnet.Sent).Contains("connect Corvid hunter2"); // it did go to the world
        await Assert.That(app.HistoryEntries(InputBar.Primary)).IsEmpty();

        app.SimulateKey(Chord(ConsoleKey.R));

        await Assert.That(string.Join("\n", app.HistorySearchLines)).DoesNotContain("hunter2");
    }

    // ---- Guards ----------------------------------------------------------------------------------

    /// <summary>
    /// ⌃R does not open a second surface over a modal that is already asking something. The quit
    /// confirmation is the case that matters: a list floating over it would be a second question.
    /// </summary>
    [Test]
    public async Task CtrlRIsIgnoredWhileAnotherSurfaceIsUp()
    {
        var (app, _) = await Demo();
        Seed(app);
        app.SimulateKey(Chord(ConsoleKey.Q));
        await Assert.That(app.QuitPromptOpen).IsTrue();

        app.SimulateKey(Chord(ConsoleKey.R));

        await Assert.That(app.HistorySearchOpen).IsFalse();
        await Assert.That(app.QuitPromptOpen).IsTrue();
    }

    /// <summary>
    /// The ⌃P entry opens it too. Worth its own test because of the interaction between the two: the entry
    /// is dispatched by the palette, and the surface refuses to open while another overlay is up — so this
    /// only works because <c>CommandPalette.Commit</c> closes itself <em>before</em> dispatching.
    /// </summary>
    [Test]
    public async Task TheCommandSurfaceEntryOpensItToo()
    {
        var (app, _) = await Demo();
        Seed(app);

        await Assert.That(app.DispatchCommand("term:history")).IsTrue();
        await Assert.That(app.HistorySearchOpen).IsTrue();
        await Assert.That(app.HistorySearchRows.Count).IsEqualTo(4);
    }

    /// <summary>And no macro fires while the surface is up — the workspace is not listening.</summary>
    [Test]
    public async Task NoMacroFiresWhileTheSurfaceIsUp()
    {
        var (app, _) = await Demo();
        Seed(app);
        app.SimulateKey(Chord(ConsoleKey.R));

        await Assert.That(app.SimulateKey(new ConsoleKeyInfo('\0', ConsoleKey.F1, false, false, true))).IsNull();
    }

    // ---- ⌃W, while we are in the neighbourhood ---------------------------------------------------

    /// <summary>
    /// <b>⌃W is the window command, not kill-word-left.</b> The command line's key table used to advertise
    /// both and could only ever have delivered one: <see cref="MacroKeys.AppShortcuts"/> claims ⌃W and a
    /// global shortcut runs before any window, so the bar's <c>case ConsoleKey.W</c> was unreachable from
    /// the day it was written. This is that fact, asserted: with the bar armed and holding two words, ⌃W
    /// leaves the text alone.
    /// <para>
    /// The dead case has been removed and the key table corrected. Which meaning <em>should</em> win is a
    /// judgement call and not this test's business — but a bar that names a key it never receives is a lie
    /// either way, and that part is fixed.
    /// </para>
    /// </summary>
    [Test]
    public async Task CtrlWDoesNotKillTheWordBeforeTheCaret()
    {
        var (app, _) = await Demo();
        Type(app, "say hello there");

        app.SimulateKey(Chord(ConsoleKey.W));

        await Assert.That(app.ArmedInputText).IsEqualTo("say hello there");
    }

    /// <summary>
    /// And the app-level meaning is the one that actually runs. On the main window ⌃W is deliberately
    /// refused (it is the session, not a closable tab), so the closable half is shown on a spawn window:
    /// the chord closes it. Between the two tests, ⌃W is the window command and nothing else.
    /// </summary>
    [Test]
    public async Task CtrlWClosesTheWindow()
    {
        var (app, _) = await Demo();
        app.SimulateWindowChange(DemoScene.ChatWindowId);
        await Assert.That(app.WindowIds()).Contains(DemoScene.ChatWindowId);

        app.SimulateKey(Chord(ConsoleKey.W));

        await Assert.That(app.WindowIds()).DoesNotContain(DemoScene.ChatWindowId);
    }
}
