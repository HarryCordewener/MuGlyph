using System.Collections.Concurrent;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Telnet;
using SharpMUTerm.Core.Transport;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// <b>The reported defect, end to end: "the automatic connect does not seem to send the connect
/// string", and "same with Reconnect in general".</b>
/// <para>
/// One gate explained both. <c>WorldSession.SendLoginAsync</c> sent the connect line only when a
/// character's <c>autoLogin</c> flag was set, and every path — the startup dial and <c>Reconnect</c>
/// alike, since a reconnect re-enters <c>ConnectAsync</c> — went through it. The reporter's own
/// configuration had two characters marked <c>connectAtStartup</c> with a <b>password saved for each</b>
/// and that flag at its default, so the client opened two sockets and typed nothing into either. The
/// flag was not even reachable: the F5 form drew it as a well-less readout of a checkbox that
/// <c>WorldsScreenRenderer.CharacterRow</c> never rendered.
/// </para>
/// <para>
/// The rule now is that a saved password (or a connect line of the character's own) <em>is</em> the
/// instruction to log in — see <see cref="LoginPlan"/>. These tests are written in the reporter's exact
/// shape: a character with a password, <c>at start</c> on, and nothing else said anywhere.
/// </para>
/// <para>
/// <b>Two things about how they assert.</b> They go through what <c>Run</c> actually schedules
/// (<see cref="SharpMUTermApp.ScheduleStartup"/>) rather than calling <c>StartAsync</c> themselves —
/// <see cref="StartupConnectTests"/> once had thirteen passing tests while no launch ever happened. And
/// they read the lines off a <b>connected</b> <see cref="RecordingTelnetSession"/>:
/// <c>WorldSession.SendRawAsync</c> returns silently when the transport is not connected, so an
/// assertion made against an unconnected fake passes no matter how broken the login path is. Every test
/// below therefore asserts the socket is up before asserting what went down it.
/// </para>
/// </summary>
/// <remarks>Serialised like the other end-to-end suites: constructing the app touches the process-global console streams.</remarks>
[NotInParallel]
public class LoginLineTests
{
    private const int Width = 120;
    private const int Height = 34;

    /// <summary>
    /// Distinctive enough that finding it anywhere is unambiguous, and not a substring of anything the
    /// client, the telnet stack or the markup writes.
    /// </summary>
    private const string Secret = "wqjf-login-canary-42";

    private const string World = "Convergence MUSH";
    private const string Character = "Mannaz";
    private const string SessionKey = World + "." + Character;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

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

    /// <summary>
    /// The reporter's configuration, reduced to the two facts that matter: the character is marked to
    /// connect at launch, and it has a password saved. Nothing else is set — which is the whole point,
    /// because under the old rule "nothing else is set" was what made the password inert.
    /// </summary>
    private static AppConfiguration Config(
        string? password = Secret, string? connectString = null, string? onConnect = null) =>
        new()
        {
            Worlds =
            {
                new WorldDefinition
                {
                    Name = World,
                    Host = "game.convergencemush.org",
                    Port = 10000,
                    Characters =
                    {
                        new CharacterDefinition
                        {
                            Name = Character,
                            Password = password,
                            ConnectString = connectString,
                            OnConnect = onConnect,
                            ConnectAtStartup = true,
                            Logging = new LoggingSettings(),
                        },
                    },
                },
            },
        };

    private static (SharpMUTermApp App, Transports Telnet) App(AppConfiguration config)
    {
        Console.SetIn(TextReader.Null);
        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height), new ManualTimeProvider());
        var telnet = new Transports();
        app.TelnetFactory = telnet.Open;
        return (app, telnet);
    }

    /// <summary>The one transport this configuration opens, asserted to be genuinely connected first.</summary>
    private static async Task<RecordingTelnetSession> ConnectedAsync(Transports telnet, int index = 0)
    {
        await Assert.That(telnet.Opened.Count).IsGreaterThan(index);
        var session = telnet.Opened[index];
        await Assert.That(session.IsConnected)
            .IsTrue()
            .Because("SendRawAsync drops everything while unconnected, so an unconnected assertion proves nothing");
        return session;
    }

    // ---- The startup dial ------------------------------------------------------------------------

    /// <summary>
    /// <b>The regression.</b> A character with a saved password, marked <c>at start</c> and saying
    /// nothing else at all, is dialled by what <c>Run</c> schedules — and the resolved connect line
    /// reaches the wire. Before the rule changed this sent nothing whatsoever, because the character's
    /// <c>autoLogin</c> was at its default and there was no reachable way to change it.
    /// </summary>
    [Test]
    public async Task AStartupDial_SendsTheConnectLineForACharacterThatOnlyHasASavedPassword()
    {
        var config = Config();
        var (app, telnet) = App(config);

        app.ScheduleStartup(StartupConnections.Resolve(config));
        await app.LastCommand;

        var session = await ConnectedAsync(telnet);
        await Assert.That(session.Lines).Contains($"connect {Character} {Secret}");
        await Assert.That(app.ActiveSessionKey).IsEqualTo(SessionKey);
    }

    /// <summary>
    /// And the client says it did it. A login that goes out in total silence is indistinguishable from
    /// one that was never sent — which is exactly the position the reporter was in, watching a connect
    /// do nothing with no way to tell "not sent" from "sent and refused". The line names the character
    /// and never the login line, which carries the password.
    /// </summary>
    [Test]
    public async Task AStartupDial_SaysThatItSentTheLoginLine_WithoutSayingWhatItWas()
    {
        var config = Config();
        var (app, telnet) = App(config);

        app.ScheduleStartup(StartupConnections.Resolve(config));
        await app.LastCommand;

        await ConnectedAsync(telnet);
        var pane = string.Join("\n", app.PaneLines(SharpMUTermApp.MainWindowId));
        await Assert.That(pane).Contains($"Sent the login line for {Character}");
        await Assert.That(pane).DoesNotContain(Secret);
    }

    // ---- Reconnect -------------------------------------------------------------------------------

    /// <summary>
    /// The second half of the report, said separately because the reporter said it separately.
    /// <c>Reconnect</c> on a live connection drops and redials, and the character logs in again on the
    /// <em>new</em> transport — asserted on transport #1, not #0, so a test that merely re-read the
    /// first socket's lines could not pass by accident.
    /// </summary>
    [Test]
    public async Task Reconnect_SendsTheConnectLineAgainOnTheNewTransport()
    {
        var config = Config();
        var (app, telnet) = App(config);

        app.ScheduleStartup(StartupConnections.Resolve(config));
        await app.LastCommand;
        await ConnectedAsync(telnet);

        app.DispatchCommand("world:reconnect");
        await app.LastCommand;

        // A real redial: a second transport, with the first one dropped.
        await Assert.That(telnet.Opened.Count).IsEqualTo(2);
        await Assert.That(telnet.Opened[0].IsConnected).IsFalse();

        var redialled = await ConnectedAsync(telnet, index: 1);
        await Assert.That(redialled.Lines).Contains($"connect {Character} {Secret}");
    }

    /// <summary>
    /// Reconnect on a session that is already <em>down</em> — the other arm, and the one someone reaches
    /// for after a connection drops. It just dials, and the login goes with it.
    /// </summary>
    [Test]
    public async Task Reconnect_OnADeadSessionDialsAndLogsIn()
    {
        var config = Config();
        var (app, telnet) = App(config);

        app.ScheduleStartup(StartupConnections.Resolve(config));
        await app.LastCommand;
        var first = await ConnectedAsync(telnet);

        app.DispatchCommand("world:disconnect");
        await app.LastCommand;
        await Assert.That(first.IsConnected).IsFalse();

        app.DispatchCommand("world:reconnect");
        await app.LastCommand;

        var redialled = await ConnectedAsync(telnet, index: telnet.Opened.Count - 1);
        await Assert.That(redialled.Lines).Contains($"connect {Character} {Secret}");
    }

    /// <summary>
    /// The whole opening exchange, in order, on a reconnect: the login line first and the on-connect
    /// commands after it. Order is the claim — <c>+who</c> before <c>connect</c> reaches a login prompt
    /// that has no idea what to do with it.
    /// </summary>
    [Test]
    public async Task Reconnect_ReplaysTheWholeOpeningExchangeInOrder()
    {
        var config = Config(onConnect: "+who; look");
        var (app, telnet) = App(config);

        app.ScheduleStartup(StartupConnections.Resolve(config));
        await app.LastCommand;
        await ConnectedAsync(telnet);

        app.DispatchCommand("world:reconnect");
        await app.LastCommand;

        var redialled = await ConnectedAsync(telnet, index: 1);
        await Assert.That(redialled.Lines.ToArray())
            .IsEquivalentTo(new[] { $"connect {Character} {Secret}", "+who", "look" });
    }

    // ---- Logging in by hand still works ----------------------------------------------------------

    /// <summary>
    /// The state that has to keep working: no password and no connect line, which is how a user now says
    /// "leave the login to me". Nothing is typed at the prompt — <b>and the on-connect commands still
    /// run</b>, because they never were gated on the login and must not become so. They are the things
    /// you would type after logging in, and someone typing their own connect line still wants <c>+who</c>.
    /// </summary>
    [Test]
    public async Task ACharacterWithNothingConfigured_SendsNoLoginLineButStillRunsItsOnConnectCommands()
    {
        var config = Config(password: null, onConnect: "+who; look");
        var (app, telnet) = App(config);

        app.ScheduleStartup(StartupConnections.Resolve(config));
        await app.LastCommand;

        var session = await ConnectedAsync(telnet);
        await Assert.That(session.Lines.ToArray()).IsEquivalentTo(new[] { "+who", "look" });
        await Assert.That(session.Lines.Any(l => l.StartsWith("connect", StringComparison.Ordinal))).IsFalse();

        // And it is silent about it. "Nothing was sent" is a choice here, not a fault, so the client does
        // not announce it on every single connect.
        await Assert.That(string.Join("\n", app.PaneLines(SharpMUTermApp.MainWindowId)))
            .DoesNotContain("Sent the login line");
    }

    /// <summary>
    /// A passwordless world: no password, a connect line the user wrote. It is sent. "Send only when a
    /// password exists" would have made this configuration impossible to express, which is why an
    /// explicit connect line counts as intent in its own right.
    /// </summary>
    [Test]
    public async Task ACharacterWithOnlyAConnectLine_SendsIt()
    {
        var config = Config(password: null, connectString: "connect %CHARACTER%");
        var (app, telnet) = App(config);

        app.ScheduleStartup(StartupConnections.Resolve(config));
        await app.LastCommand;

        var session = await ConnectedAsync(telnet);
        await Assert.That(session.Lines).Contains($"connect {Character}");
    }

    // ---- The one remaining way to be inert -------------------------------------------------------

    /// <summary>
    /// A saved password whose connect line has nowhere to put it. The login still goes out — the line is
    /// a valid one, it simply has no credential in it — and the client <b>says so, in the session's own
    /// window</b>, naming the token that is missing. This is the residue of the reported bug, and the
    /// rule it is held to is the one the whole change is about: a configuration that cannot work must
    /// never be silent about it. F5 draws the same fact in <c>Warn</c> ink on the character's form.
    /// </summary>
    [Test]
    public async Task ASavedPasswordWithNoTokenToPutItIn_IsReportedRatherThanSilentlyDropped()
    {
        var config = Config(connectString: "connect %CHARACTER%");
        var (app, telnet) = App(config);

        app.ScheduleStartup(StartupConnections.Resolve(config));
        await app.LastCommand;

        var session = await ConnectedAsync(telnet);
        await Assert.That(session.Lines).Contains($"connect {Character}");
        await Assert.That(session.Lines.Any(l => l.Contains(Secret, StringComparison.Ordinal))).IsFalse();

        var pane = string.Join("\n", app.PaneLines(SharpMUTermApp.MainWindowId));
        await Assert.That(pane).Contains("saved password was not sent");
        await Assert.That(pane).Contains("%PASSWORD%");
        await Assert.That(pane).DoesNotContain(Secret);
    }

    // ---- The secret itself -----------------------------------------------------------------------

    /// <summary>
    /// The password reaches the wire and no rendered frame. <c>PasswordLeakTests</c> covers the
    /// scrollback, the session transcript and the diagnostics log at the Core level; this is the surface
    /// only the TUI has — the actual painted cells of a client that has just logged itself in, including
    /// the new line that announces it did.
    /// </summary>
    [Test]
    public async Task TheLoginItAnnounces_NeverPutsThePasswordOnScreen()
    {
        var config = Config();
        var (app, telnet) = App(config);

        app.ScheduleStartup(StartupConnections.Resolve(config));
        await app.LastCommand;

        var session = await ConnectedAsync(telnet);
        await Assert.That(session.Lines).Contains($"connect {Character} {Secret}");

        // The whole frame, as the driver was handed it — chrome, rail, panes and status row.
        await Assert.That(app.RenderSnapshot()).DoesNotContain(Secret);
        await Assert.That(app.Messages.Entries.Any(m => m.Text.Contains(Secret, StringComparison.Ordinal)))
            .IsFalse();
    }
}
