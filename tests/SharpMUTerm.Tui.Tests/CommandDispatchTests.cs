using System.Collections.Concurrent;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Telnet;
using SharpMUTerm.Core.Transport;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What the ⌃P command surface's entries actually do — and, above all, that every entry it can offer
/// does <em>something</em>.
/// <para>
/// The reported defect: on the main screen, switching to a character and telling it to Reconnect
/// connected to nothing and said nothing. Both halves were real. <c>char:</c> was emitted by
/// <c>CommandCatalog</c> and handled nowhere, so it fell to the "isn't wired" arm — which reported
/// through the active session and therefore said nothing at all when there wasn't one. And
/// <c>Reconnect</c> was <c>_ = _active?.ConnectAsync()</c>: a no-op on a null session, silently, with a
/// thrown connection error swallowed by the discarded task.
/// </para>
/// </summary>
/// <remarks>
/// Serialised for the same reason the other end-to-end suites are: constructing the app touches the
/// process-global console streams.
/// </remarks>
[NotInParallel]
public class CommandDispatchTests
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

        public Exception? Fault { get; init; }

        public IReadOnlyList<RecordingTelnetSession> Opened => _opened.ToArray();

        public ITelnetSession Open(ConnectionOptions options)
        {
            var telnet = new RecordingTelnetSession { ConnectFault = Fault };
            _opened.Enqueue(telnet);
            return telnet;
        }
    }

    private static (SharpMUTermApp App, Transports Telnet, ManualTimeProvider Clock) App(Exception? fault = null)
    {
        Console.SetIn(TextReader.Null);
        var config = DemoScene.Build();
        var clock = new ManualTimeProvider();
        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height), clock);
        var telnet = new Transports { Fault = fault };
        app.TelnetFactory = telnet.Open;
        return (app, telnet, clock);
    }

    // ---- Switching -------------------------------------------------------------------------

    /// <summary>
    /// The first half of the report. A fresh client has no session at all; picking a character from the
    /// surface has to produce one and make it the session everything else acts on.
    /// </summary>
    [Test]
    public async Task SwitchingToACharacter_MakesItTheActiveSession()
    {
        var (app, _, _) = App();
        await Assert.That(app.ActiveSessionKey).IsNull(); // the state the bug was reported from

        var handled = app.DispatchCommand($"char:{Corvid}");

        await Assert.That(handled).IsTrue();
        await Assert.That(app.ActiveSessionKey).IsEqualTo(Corvid);
        await Assert.That(app.FindSession(Corvid)).IsNotNull();
    }

    /// <summary>
    /// Switching is navigation, not dialling: a character with a session that is offline is focused,
    /// nothing is connected, and the status row says which it is and what would connect it. A switch
    /// that opened a socket would make choosing a character to look at impossible.
    /// </summary>
    [Test]
    public async Task SwitchingToAnOfflineCharacter_FocusesItWithoutConnecting()
    {
        var (app, telnet, _) = App();

        app.DispatchCommand($"char:{Corvid}");

        await Assert.That(app.ActiveSessionKey).IsEqualTo(Corvid);
        await Assert.That(app.FindSession(Corvid)!.IsConnected).IsFalse();
        await Assert.That(telnet.Opened).IsEmpty(); // nothing was dialled
        await Assert.That(app.StatusMarkup).Contains("switched to Corvid");
        await Assert.That(app.StatusMarkup).Contains("Reconnect");
    }

    /// <summary>Switching back to a character that already has a session re-uses it rather than opening a second.</summary>
    [Test]
    public async Task SwitchingBack_ReusesTheOpenSession()
    {
        var (app, _, _) = App();

        app.DispatchCommand($"char:{Corvid}");
        var first = app.FindSession(Corvid);
        app.DispatchCommand($"char:{Rookery}");
        app.DispatchCommand($"char:{Corvid}");

        await Assert.That(app.ActiveSessionKey).IsEqualTo(Corvid);
        await Assert.That(app.FindSession(Corvid)).IsSameReferenceAs(first);
    }

    /// <summary>
    /// A second character gets a window of its own — two characters sharing one buffer would interleave
    /// their output — while the first keeps the main window the shell starts in.
    /// </summary>
    [Test]
    public async Task ASecondCharacter_GetsItsOwnWindow()
    {
        var (app, _, _) = App();

        app.DispatchCommand($"char:{Corvid}");
        app.DispatchCommand($"char:{Rookery}");

        await Assert.That(app.WindowIds()).Contains($"char:{Rookery}");
        await Assert.That(app.WindowIds()).DoesNotContain($"char:{Corvid}"); // the first one is "main"
    }

    /// <summary>An id naming no configured character says so, rather than opening a session for nobody.</summary>
    [Test]
    public async Task SwitchingToAnUnknownCharacter_SaysSo()
    {
        var (app, _, _) = App();

        app.DispatchCommand("char:Nowhere.Nobody");

        await Assert.That(app.ActiveSessionKey).IsNull();
        await Assert.That(app.StatusMarkup).Contains("no character called");
    }

    // ---- Reconnect -------------------------------------------------------------------------

    /// <summary>The second half of the report: after switching, Reconnect connects that character.</summary>
    [Test]
    public async Task ReconnectAfterSwitching_ConnectsTheSelectedCharacter()
    {
        var (app, telnet, _) = App();

        app.DispatchCommand($"char:{Corvid}");
        app.DispatchCommand("world:reconnect");
        await app.LastCommand;

        await Assert.That(telnet.Opened).HasSingleItem();
        await Assert.That(telnet.Opened[0].IsConnected).IsTrue();
        await Assert.That(app.FindSession(Corvid)!.IsConnected).IsTrue();
    }

    /// <summary>
    /// Reconnect on a live connection means drop and redial, which is what the word says. It used to
    /// call <c>ConnectAsync</c>, whose first act is to return silently when the session is already
    /// connected — so the entry did nothing at all and looked identical to the reported bug.
    /// </summary>
    [Test]
    public async Task ReconnectWhileConnected_DropsAndRedials()
    {
        var (app, telnet, _) = App();

        app.DispatchCommand($"char:{Corvid}");
        app.DispatchCommand("world:reconnect");
        await app.LastCommand;

        app.DispatchCommand("world:reconnect");
        await app.LastCommand;

        await Assert.That(telnet.Opened.Count).IsEqualTo(2);   // a second transport, i.e. a real redial
        await Assert.That(telnet.Opened[0].IsConnected).IsFalse();
        await Assert.That(telnet.Opened[1].IsConnected).IsTrue();
    }

    /// <summary>
    /// A refused connection is reported. Fire-and-forget meant the throw landed in a task nobody read,
    /// so the client showed nothing whatsoever.
    /// </summary>
    [Test]
    public async Task AFailingConnect_IsReported()
    {
        var (app, telnet, _) = App(new IOException("no route to host"));

        app.DispatchCommand($"char:{Corvid}");
        app.DispatchCommand("world:reconnect");
        await app.LastCommand;

        await Assert.That(telnet.Opened[0].ConnectAttempts).IsEqualTo(1);
        await Assert.That(app.StatusMarkup).Contains("could not connect");
        await Assert.That(app.StatusMarkup).Contains("no route to host");
        await Assert.That(app.Messages.Entries.Any(m => m.Text.Contains("could not connect"))).IsTrue();
    }

    /// <summary>
    /// Reconnect with nothing selected says so. This is the exact reported symptom — "it just does
    /// nothing" — and the fix is not only that it works after a switch, but that it explains itself
    /// before one.
    /// </summary>
    [Test]
    public async Task ReconnectWithNoSession_SaysSoInsteadOfNothing()
    {
        var (app, telnet, _) = App();

        var handled = app.DispatchCommand("world:reconnect");

        await Assert.That(handled).IsTrue();
        await Assert.That(telnet.Opened).IsEmpty();
        await Assert.That(app.StatusMarkup).Contains("nothing to reconnect");
    }

    /// <summary>Disconnect gets the same treatment: nothing to disconnect is said, not swallowed.</summary>
    [Test]
    public async Task DisconnectWithNothingConnected_SaysSo()
    {
        var (app, _, _) = App();

        app.DispatchCommand("world:disconnect");
        await Assert.That(app.StatusMarkup).Contains("nothing to disconnect");

        app.DispatchCommand($"char:{Corvid}");
        app.DispatchCommand("world:disconnect");
        await Assert.That(app.StatusMarkup).Contains("is not connected");
    }

    // ---- The catalog and the dispatcher cannot disagree ------------------------------------

    /// <summary>
    /// The one that stops this recurring. The catalog is generated from live state and the dispatcher is
    /// a hand-written switch, so they drift in silence — <c>char:</c> was offered and unimplemented for
    /// as long as the surface has existed, and <c>term:log-on</c>/<c>term:log-off</c> were found the same
    /// way while fixing it. Every id the surface can emit is dispatched here, for real, and an
    /// unhandled one fails.
    /// <para>
    /// It dispatches <c>term:log-on</c> against the demo worlds as they come — Corvid's format really is
    /// <see cref="LogFormat.Html"/> — and no file appears anywhere, because this app was handed no log
    /// root. It used to point every character at a throwaway directory it created and deleted, which is
    /// the per-test workaround the gate replaces; the id being dispatched for real is the point, and
    /// re-homing the write was only ever a way of surviving it.
    /// </para>
    /// </summary>
    [Test]
    public async Task EveryCatalogIdIsHandled()
    {
        var (app, _, _) = App();

        var ids = app.BuildCatalog().Select(c => c.Id).ToList();
        await Assert.That(ids).Contains($"char:{Corvid}"); // the catalog really is offering these
        await Assert.That(ids).Contains("term:log-on");

        foreach (var id in ids)
        {
            await Assert.That(app.DispatchCommand(id))
                .IsTrue()
                .Because($"the command surface offers '{id}', so dispatch must implement it");
            await app.LastCommand;
        }
    }

    /// <summary>An id nothing implements returns false and says so on the status line, session or none.</summary>
    [Test]
    public async Task AnUnwiredId_ReportsWithoutASession()
    {
        var (app, _, _) = App();

        var handled = app.DispatchCommand("nonesuch:thing");

        await Assert.That(handled).IsFalse();
        await Assert.That(app.ActiveSessionKey).IsNull();
        await Assert.That(app.StatusMarkup).Contains("isn't wired in this build yet");
    }

    // ---- Transient status ------------------------------------------------------------------

    /// <summary>
    /// The second report: a ⌃B refusal stayed on the status row for the rest of the run. It was a bare
    /// <c>SetStatus</c> waiting for the next <c>UpdateStatus</c> to displace it, and that only ever runs
    /// off a session event — so with nothing connected, nothing ever came. Now it is tmux's
    /// <c>display-message</c>: it holds the row for <see cref="SharpMUTermApp.NoticeDuration"/> and the
    /// resting line comes back on its own. Asserted with <em>no session present</em>, which is the exact
    /// condition both bugs needed.
    /// </summary>
    [Test]
    public async Task ATransientMessageClearsItself_WithNoSession()
    {
        var (app, _, clock) = App();

        app.DispatchCommand("nonesuch:thing");
        await Assert.That(app.StatusMarkup).Contains("isn't wired in this build yet");

        clock.Advance(SharpMUTermApp.NoticeDuration - TimeSpan.FromSeconds(1));
        await Assert.That(app.StatusMarkup).Contains("isn't wired in this build yet");

        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.That(app.StatusMarkup).DoesNotContain("isn't wired");
        await Assert.That(app.StatusMarkup).Contains("not connected"); // the resting line, restored
    }

    /// <summary>
    /// And a message that dismissed itself is still findable: every notice is recorded in the client
    /// message log the ⌃P viewer reads — including with no session and no window focused, which is the
    /// state the original bugs hid in. It is deliberately not the output window, because that stream is
    /// written to the character's log file and client chrome has no business in a transcript.
    /// </summary>
    [Test]
    public async Task ATransientMessageIsRecorded_EvenWithNoSession()
    {
        var (app, _, clock) = App();

        app.DispatchCommand("nonesuch:thing");
        clock.Advance(SharpMUTermApp.NoticeDuration + TimeSpan.FromSeconds(1));

        var recorded = app.Messages.Entries;
        await Assert.That(recorded.Any(m => m.Text.Contains("isn't wired in this build yet"))).IsTrue();
        await Assert.That(app.StatusMarkup).DoesNotContain("isn't wired"); // gone from the row, kept in the log
    }

    /// <summary>The viewer is reachable from the surface it is advertised on, and toggles like the screens do.</summary>
    [Test]
    public async Task TheMessageViewerOpensAndCloses()
    {
        var (app, _, _) = App();

        await Assert.That(app.DispatchCommand("term:messages")).IsTrue();
        await Assert.That(app.MessageLogIsOpen).IsTrue();

        app.DispatchCommand("term:messages");
        await Assert.That(app.MessageLogIsOpen).IsFalse();
    }
}
