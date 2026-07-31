using System.Collections.Concurrent;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Telnet;
using SharpMUTerm.Core.Transport;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The dedicated chords for Reconnect and Disconnect: ⌥D drops the focused character's connection and
/// ⌥R drops and redials it, both at once and neither asking anything.
/// <para>
/// <b>They share a modifier, and that is the point of the pair.</b> Disconnect was ⌃D — "⌃D is the
/// terminal's own hang-up chord" and "Alt+R is one modifier over from ⌃R" being two separate
/// justifications bolted together, each fine alone and jointly making a reader learn two modifiers for
/// one concept. It was reported as exactly that: "It's 'CTRL-D' to disconnect, but 'ALT-R' to reconnect?
/// Why are they not both under Alt?" ⌃D is released rather than kept as a second binding, because a
/// second key for one action is either a secret or a duplicate row on every surface that lists chords.
/// </para>
/// <para>
/// The claim that needs proving first is that the chords <em>arrive</em>. Both are ESC-prefixed
/// printables (<c>1b 64</c> and <c>1b 72</c>, read off a pty), so both survive SharpConsoleUI's input
/// parser — unlike ⌃I/⌃M/⌃J/⌃H, which collapse onto Tab, Enter and Backspace and have already cost this
/// client four features. The second is that they act on the connection in the window <em>in front of
/// you</em>: nothing asks before either of these runs, so a session resolved from <c>_active</c> rather
/// than the focused window would drop the wrong world's connection on one keystroke and say nothing
/// about it.
/// </para>
/// </summary>
/// <remarks>
/// Serialised for the same reason the other end-to-end suites are: constructing the app touches the
/// process-global console streams.
/// </remarks>
[NotInParallel]
public class ConnectionKeysTests
{
    private const int Width = 120;
    private const int Height = 34;
    private const string Corvid = "Aetherfall.Corvid";
    private const string Rookery = "Aetherfall.Rookery";

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>Every transport the app opened, in order, so a test can see what was connected.</summary>
    private sealed class Transports
    {
        private readonly ConcurrentQueue<RecordingTelnetSession> _opened = new();

        public IReadOnlyList<RecordingTelnetSession> Opened => _opened.ToArray();

        public ITelnetSession Open(ConnectionOptions options)
        {
            var telnet = new RecordingTelnetSession();
            _opened.Enqueue(telnet);
            return telnet;
        }
    }

    private static (SharpMUTermApp App, Transports Telnet) App()
    {
        Console.SetIn(TextReader.Null);
        var config = DemoScene.Build();
        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        var telnet = new Transports();
        app.TelnetFactory = telnet.Open;
        return (app, telnet);
    }

    private static ConsoleKeyInfo AltD() => new('d', ConsoleKey.D, false, true, false);

    /// <summary>The chord disconnect used to be on, kept so a test can prove it is now inert.</summary>
    private static ConsoleKeyInfo CtrlD() => new('\x04', ConsoleKey.D, false, false, true);

    private static ConsoleKeyInfo AltR() => new('r', ConsoleKey.R, false, true, false);

    private static async Task Connect(SharpMUTermApp app, string sessionKey)
    {
        app.DispatchCommand($"char:{sessionKey}");
        app.DispatchCommand("world:reconnect");
        await app.LastCommand;
    }

    /// <summary>
    /// <b>Releasing ⌃D hands it to nothing.</b> That was the risk worth checking rather than assuming:
    /// the framework's <c>HandleMoveInput</c> swallows unclaimed Ctrl chords and its <c>X</c> case closes
    /// the active window — the defect that made ⌃X blank the UI. It cannot fire here for two independent
    /// reasons (it is gated on <c>IsMovable</c>, which this app sets false, and it only acts on the
    /// arrows and <c>X</c>), and <c>InputBarControl</c>'s Ctrl table has no <c>D</c> either. So the key
    /// is inert: the connection stays up, the window stays open, and nothing is typed.
    /// </summary>
    [Test]
    public async Task CtrlDIsInertNowThatNothingClaimsIt()
    {
        var (app, _) = App();
        await Connect(app, Corvid);
        var windows = app.WindowIds().Count;

        app.SimulateKey(CtrlD());

        await Assert.That(app.FindSession(Corvid)!.IsConnected)
            .IsTrue()
            .Because("⌃D no longer disconnects, and must not have been taken by anything else either");
        await Assert.That(app.WindowIds().Count)
            .IsEqualTo(windows)
            .Because("the framework's move handler closes a window on an unclaimed Ctrl chord");
        await Assert.That(app.ArmedInputText).DoesNotContain("\x04");
    }

    // ---- the chords reach their actions ----------------------------------------------------

    /// <summary>
    /// ⌥D runs Disconnect and ⌥R runs Reconnect, driven through the very shortcut table the app
    /// registers. With nothing selected both refuse out loud, which is the state that shows the key
    /// arrived at all: the refusals are written by <c>Disconnect</c> and <c>Reconnect</c> and by nothing
    /// else, so a chord the parser never delivered could not produce them.
    /// </summary>
    [Test]
    public async Task BothChordsReachTheirCommands()
    {
        var (reconnect, _) = App();
        reconnect.SimulateKey(AltR());
        await Assert.That(reconnect.StatusMarkup).Contains("nothing to reconnect");

        var (disconnect, _) = App();
        disconnect.SimulateKey(AltD());
        await Assert.That(disconnect.StatusMarkup).Contains("nothing to disconnect");
    }

    /// <summary>
    /// And they are declared where app chords are declared, so F4 reports them as taken without being
    /// told twice and the startup check would catch a claim with nothing behind it.
    /// </summary>
    [Test]
    public async Task TheChordsAreClaimedInTheOneShortcutList()
    {
        await Assert.That(MacroKeys.AppShortcuts.Any(
            s => s.Modifiers == ConsoleModifiers.Alt && s.Key == ConsoleKey.D)).IsTrue();
        await Assert.That(MacroKeys.AppShortcuts.Any(
            s => s.Modifiers == ConsoleModifiers.Alt && s.Key == ConsoleKey.R)).IsTrue();

        // And ⌃D is genuinely released rather than merely unused: nothing claims it, so F4 offers it.
        await Assert.That(MacroKeys.AppShortcuts.Any(
            s => s.Modifiers == ConsoleModifiers.Control && s.Key == ConsoleKey.D))
            .IsFalse()
            .Because("a second key for one action is a duplicate row on every surface that lists chords");
        await Assert.That(MacroKeys.Verdict("Ctrl+D").Fires).IsTrue();

        // Which is what makes F4 honest about a macro bound to either of them.
        foreach (var descriptor in new[] { "Alt+D", "Alt+R" })
        {
            var verdict = MacroKeys.Verdict(descriptor);
            await Assert.That(verdict.Delivery).IsEqualTo(MacroKeyDelivery.Taken).Because(descriptor);
            await Assert.That(verdict.Reason).Contains(descriptor);
        }
    }

    /// <summary>
    /// The chords are ones this terminal can deliver, which is the trap that has caught four features
    /// here. ⌥D and ⌥R are both ESC-prefixed printables — neither is one of the four letters whose control
    /// byte the terminal has already spent, and neither loses its modifier.
    /// </summary>
    [Test]
    public async Task NeitherChordIsOneTheTerminalCannotReport()
    {
        foreach (var dead in new[] { "Ctrl+I", "Ctrl+M", "Ctrl+J", "Ctrl+H" })
        {
            await Assert.That(MacroKeys.Verdict(dead).Delivery)
                .IsEqualTo(MacroKeyDelivery.NeverArrives)
                .Because($"{dead} collapses onto its ASCII byte — this is the list the new chords avoid");
        }

        // Alt+D and Alt+R are Taken rather than NeverArrives: they do arrive, and the app is what claims
        // them. Capture proves the round trip from a keystroke to a descriptor.
        await Assert.That(MacroKeys.Capture(AltD())).IsEqualTo("Alt+D");
        await Assert.That(MacroKeys.Capture(AltR())).IsEqualTo("Alt+R");
    }

    // ---- what one keystroke does -------------------------------------------------------------

    /// <summary>
    /// ⌥D on a live connection drops it there and then. Nothing is asked — ⌃Q is the only key in this
    /// client that asks anything, and what it asks about is ending the client rather than a connection.
    /// </summary>
    [Test]
    public async Task AltDDropsTheConnectionAtOnce()
    {
        var (app, _) = App();
        await Connect(app, Corvid);
        await Assert.That(app.FindSession(Corvid)!.IsConnected).IsTrue();

        app.SimulateKey(AltD());
        await app.LastCommand;

        await Assert.That(app.FindSession(Corvid)!.IsConnected).IsFalse();
        await Assert.That(app.ExitRequested).IsFalse(); // it disconnects; it is not a quit
    }

    /// <summary>
    /// Alt+R on a live connection really drops and redials — a second transport, with the first closed.
    /// That is what the word has to mean, and what an earlier <c>ConnectAsync</c> could not do, because
    /// that returns silently on a session already connected.
    /// </summary>
    [Test]
    public async Task AltRDropsAndRedialsAtOnce()
    {
        var (app, telnet) = App();
        await Connect(app, Corvid);
        await Assert.That(telnet.Opened.Count).IsEqualTo(1);

        app.SimulateKey(AltR());
        await app.LastCommand;

        await Assert.That(telnet.Opened.Count).IsEqualTo(2);
        await Assert.That(telnet.Opened[0].IsConnected).IsFalse();
        await Assert.That(telnet.Opened[1].IsConnected).IsTrue();
    }

    /// <summary>Alt+R on a character that is not connected simply dials it.</summary>
    [Test]
    public async Task AltROnADeadSessionDials()
    {
        var (app, telnet) = App();
        app.DispatchCommand($"char:{Corvid}");

        app.SimulateKey(AltR());
        await app.LastCommand;

        await Assert.That(telnet.Opened).HasSingleItem();
        await Assert.That(app.FindSession(Corvid)!.IsConnected).IsTrue();
    }

    /// <summary>
    /// ⌥D with nothing connected says so and touches nothing — the pre-existing refusal, and where a
    /// shell user's "⌃D ends the session" reflex (which now lands on nothing) is likeliest to fire.
    /// </summary>
    [Test]
    public async Task AltDOnADeadSessionSaysSoAndEndsNothing()
    {
        var (app, _) = App();
        app.DispatchCommand($"char:{Corvid}");

        app.SimulateKey(AltD());

        await Assert.That(app.ExitRequested).IsFalse();
        await Assert.That(app.StatusMarkup).Contains("is not connected");
    }

    // ---- one action, one behaviour, however it is reached --------------------------------------

    /// <summary>
    /// The ⌃P entry and the chord are the same action, in both connection states — they call the very
    /// same method, and this is what stops the two doors drifting into two behaviours wearing one name.
    /// </summary>
    [Test]
    public async Task TheSurfaceEntryAndTheChordBehaveIdentically()
    {
        foreach (var (id, chord, connectFirst) in new (string Id, ConsoleKeyInfo Chord, bool Connect)[]
        {
            ("world:disconnect", AltD(), true),
            ("world:disconnect", AltD(), false),
            ("world:reconnect", AltR(), true),
            ("world:reconnect", AltR(), false),
        })
        {
            var (viaKey, keyTransports) = App();
            var (viaEntry, entryTransports) = App();
            foreach (var app in new[] { viaKey, viaEntry })
            {
                if (connectFirst)
                {
                    await Connect(app, Corvid);
                }
                else
                {
                    app.DispatchCommand($"char:{Corvid}");
                }
            }

            viaKey.SimulateKey(chord);
            await viaKey.LastCommand;
            viaEntry.DispatchCommand(id);
            await viaEntry.LastCommand;

            var state = $"{id}, {(connectFirst ? "connected" : "offline")}";
            await Assert.That(entryTransports.Opened.Count)
                .IsEqualTo(keyTransports.Opened.Count)
                .Because($"{state}: the entry and its chord must dial the same number of times");
            await Assert.That(viaEntry.FindSession(Corvid)!.IsConnected)
                .IsEqualTo(viaKey.FindSession(Corvid)!.IsConnected)
                .Because($"{state}: both must leave the connection in the same state");
            await Assert.That(viaEntry.StatusMarkup).IsEqualTo(viaKey.StatusMarkup).Because(state);
        }
    }

    // ---- which session? the one the window in front of you is showing ----------------------------

    /// <summary>
    /// With two characters connected in two windows, the chord drops the one the <em>focused</em> window
    /// is showing — not whichever connected last. Driven by bringing the first character's window forward
    /// through the app's own activation path, which is where a ⌃arrow, a tab click and a rail row all end.
    /// </summary>
    [Test]
    public async Task TheChordActsOnTheSessionTheFocusedWindowIsShowing()
    {
        var (app, _) = App();
        await Connect(app, Corvid);
        await Connect(app, Rookery); // the last to connect, and the active window

        app.SimulateWindowChange("main"); // Corvid kept the main window
        app.SimulateKey(AltD());
        await app.LastCommand;

        await Assert.That(app.FindSession(Corvid)!.IsConnected).IsFalse();
        await Assert.That(app.FindSession(Rookery)!.IsConnected)
            .IsTrue()
            .Because("the window the user was looking at is the one that was dropped");
    }

    /// <summary>
    /// A window that belongs to no connection refuses rather than falling back on whichever character the
    /// bar happens to be pointed at. This is the safety property behind resolving through
    /// <c>SendTarget</c>: with no confirmation in the way, a fallback would drop a live connection the
    /// user was not even looking at, on one keystroke, silently. The web view is the one such window
    /// there is, and it is the same rule ⏎ and a link click already follow.
    /// </summary>
    [Test]
    public async Task AWindowThatOwnsNoConnectionRefusesRatherThanGuessing()
    {
        var (app, _) = App();
        await Connect(app, Corvid);
        app.SimulateWebPage(); // activates a window belonging to no connection

        app.SimulateKey(AltD());
        await app.LastCommand;

        await Assert.That(app.FindSession(Corvid)!.IsConnected)
            .IsTrue()
            .Because("⌥D on a window with no connection must not drop somebody else's");
        await Assert.That(app.StatusMarkup).Contains("nothing to disconnect");
    }

    // ---- discoverability ------------------------------------------------------------------------

    /// <summary>
    /// The honesty rule, both ways round, for what this change added: the two ⌃P entries name their
    /// chords, and those chords really run those entries. A key nobody can find is the same as no
    /// feature — which is how ⌃L's newline came to be reported missing.
    /// </summary>
    [Test]
    public async Task TheSurfaceNamesTheChordsThatRunItsEntries()
    {
        var (app, _) = App();
        app.RenderSnapshot();

        var catalog = app.BuildCatalog();
        await Assert.That(catalog.Single(c => c.Id == "world:disconnect").Subtitle).IsEqualTo("⌥D");
        await Assert.That(catalog.Single(c => c.Id == "world:reconnect").Subtitle).IsEqualTo("⌥R");
    }

    /// <summary>
    /// <c>--help</c> names them, and describes what they <em>do</em> rather than what they ask, because
    /// they ask nothing. A page promising a confirmation that is not there would be the honesty rule
    /// broken in the direction that costs a connection.
    /// </summary>
    [Test]
    public async Task HelpNamesBothChordsAndDescribesWhatTheyDo()
    {
        var help = Program.UsageText;

        await Assert.That(help).Contains("Alt+R reconnects");
        await Assert.That(help).Contains("Alt+D disconnects");
        await Assert.That(help).Contains("redials");
        await Assert.That(help).Contains("Neither asks");
    }

    /// <summary>
    /// And the status row that already told an offline character what would connect it now names the
    /// chord as well as the surface. It is the one place this fits without costing width elsewhere.
    /// </summary>
    [Test]
    public async Task SwitchingToAnOfflineCharacterNamesTheChord()
    {
        var (app, _) = App();

        app.DispatchCommand($"char:{Corvid}");

        await Assert.That(app.StatusMarkup).Contains("Alt+R");
        await Assert.That(app.StatusMarkup).Contains("Reconnect"); // the surface is still named too
    }
}
