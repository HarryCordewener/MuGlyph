using System.Collections.Concurrent;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Diagnostics;
using SharpMUTerm.Core.Telnet;
using SharpMUTerm.Core.Transport;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What a launch actually opens. Until <see cref="CharacterDefinition.ConnectAtStartup"/> existed the
/// answer was fixed: <c>config.Worlds.FirstOrDefault()</c>, as its first character, connected
/// unconditionally — so a user with three worlds got the first one's first character every time,
/// whether or not they wanted a connection at all. Half of the feature is therefore the removal, and
/// "connects nothing" is asserted here as a first-class outcome rather than as an edge case.
/// <para>
/// The no-connection assertions are made against transports that <em>would</em> have recorded a
/// connection: <see cref="RecordingTelnetSession.ConnectAttempts"/> counts refused dials too, and the
/// factory records every transport the app opened. An assertion that merely found nothing connected
/// would also pass on a client that had failed to open a socket for some quite different reason.
/// </para>
/// </summary>
/// <remarks>Serialised like the other end-to-end suites: constructing the app touches the process-global console streams.</remarks>
[NotInParallel]
public class StartupConnectTests
{
    private const int Width = 120;
    private const int Height = 34;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>Every transport the app opened, in order, with the host each was asked for.</summary>
    private sealed class Transports
    {
        private readonly ConcurrentQueue<(string Host, RecordingTelnetSession Telnet)> _opened = new();

        /// <summary>Hosts that refuse the dial, so one failure among several can be staged.</summary>
        public HashSet<string> Refusing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<(string Host, RecordingTelnetSession Telnet)> Opened => _opened.ToArray();

        public IReadOnlyList<RecordingTelnetSession> Connected =>
            _opened.Where(o => o.Telnet.IsConnected).Select(o => o.Telnet).ToArray();

        public ITelnetSession Open(ConnectionOptions options)
        {
            var telnet = new RecordingTelnetSession
            {
                ConnectFault = Refusing.Contains(options.Host)
                    ? new IOException($"no route to host {options.Host}")
                    : null,
            };
            _opened.Enqueue((options.Host, telnet));
            return telnet;
        }
    }

    private static (SharpMUTermApp App, Transports Telnet) App(AppConfiguration config)
    {
        Console.SetIn(TextReader.Null);
        foreach (var character in config.Worlds.SelectMany(w => w.Characters))
        {
            character.Logging = new LoggingSettings();
        }

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height), new ManualTimeProvider());
        var telnet = new Transports();
        app.TelnetFactory = telnet.Open;
        return (app, telnet);
    }

    /// <summary>The demo worlds with every startup mark cleared, so each test states its own.</summary>
    private static AppConfiguration Unmarked()
    {
        var config = DemoScene.Build();
        foreach (var character in config.Worlds.SelectMany(w => w.Characters))
        {
            character.ConnectAtStartup = false;
        }

        return config;
    }

    private static void Mark(AppConfiguration config, string world, string character) =>
        config.Worlds.Single(w => w.Name == world).Characters.Single(c => c.Name == character)
            .ConnectAtStartup = true;

    private static async Task Start(SharpMUTermApp app, AppConfiguration config, WorldDefinition? commandLine = null) =>
        await app.StartAsync(StartupConnections.Resolve(config, commandLine));

    // ---- Nothing marked --------------------------------------------------------------------

    /// <summary>
    /// The headline change. Worlds are configured, characters exist, and the client opens connected to
    /// nothing — no transport was even constructed, let alone dialled.
    /// </summary>
    [Test]
    public async Task NothingMarked_AttemptsNoConnectionAtAll()
    {
        var config = Unmarked();
        var (app, telnet) = App(config);

        await Start(app, config);

        await Assert.That(telnet.Opened).IsEmpty();
        await Assert.That(telnet.Opened.Sum(o => o.Telnet.ConnectAttempts)).IsEqualTo(0);
        await Assert.That(app.ActiveSessionKey).IsNull();
        await Assert.That(app.ConnectedCharacters()).IsEmpty();
    }

    /// <summary>
    /// And it says so. The empty workspace used to be unreachable, so it had never had to be usable;
    /// it now names both ways out — connect one now, or mark one and stop being asked — and every key
    /// it names is one this client has.
    /// </summary>
    [Test]
    public async Task NothingMarked_SaysWhatToDoRatherThanLookingBroken()
    {
        var config = Unmarked();
        var (app, _) = App(config);

        await Start(app, config);

        await Assert.That(app.StatusMarkup).Contains("nothing connects at start");
        await Assert.That(app.StatusMarkup).Contains("Switch to");
        await Assert.That(app.StatusMarkup).Contains("Reconnect");
        await Assert.That(app.StatusMarkup).Contains("at start");

        // A notice retires itself, so the ⌃P log is where it can be found again.
        await Assert.That(app.Messages.Entries.Any(m => m.Text.Contains("nothing connects at start"))).IsTrue();
    }

    /// <summary>
    /// The other empty state, told apart from it: a configuration with no worlds is a client with
    /// nothing to do yet, not one doing what it was told, and it keeps the message it always had.
    /// </summary>
    [Test]
    public async Task NoWorldsAtAll_KeepsItsOwnMessage()
    {
        var config = new AppConfiguration();
        var (app, telnet) = App(config);

        await Start(app, config);

        await Assert.That(telnet.Opened).IsEmpty();
        await Assert.That(app.StatusMarkup).Contains("No world configured");
        await Assert.That(app.StatusMarkup).DoesNotContain("nothing connects at start");
    }

    // ---- One marked ------------------------------------------------------------------------

    /// <summary>
    /// One mark, one connection — as the marked character, in the main window, with the command line
    /// pointed at it. The character is deliberately the world's <em>second</em>, which is the case the
    /// old behaviour could not express at all.
    /// </summary>
    [Test]
    public async Task OneMarked_ConnectsItIntoTheMainWindow()
    {
        var config = Unmarked();
        Mark(config, "Aetherfall", "Rookery");
        var (app, telnet) = App(config);

        await Start(app, config);

        await Assert.That(telnet.Connected.Count).IsEqualTo(1);
        await Assert.That(telnet.Opened[0].Host).IsEqualTo("aetherfall.mux");
        await Assert.That(app.ActiveSessionKey).IsEqualTo("Aetherfall.Rookery");
        await Assert.That(app.ConnectedCharacters()).IsEquivalentTo(new[] { "Aetherfall.Rookery" });
        await Assert.That(app.WindowOwnerOf(SharpMUTermApp.MainWindowId)).IsEqualTo("Aetherfall.Rookery");
    }

    // ---- Several marked --------------------------------------------------------------------

    /// <summary>
    /// Three marks on two worlds: three sessions, three sockets, and windows composed the way a
    /// <c>char:</c> switch composes them — the first takes the main window and every later one a tab of
    /// its own, because two characters sharing one buffer would interleave their output.
    /// </summary>
    [Test]
    public async Task SeveralMarked_AllOpenAndEachLaterOneGetsItsOwnTab()
    {
        var config = Unmarked();
        Mark(config, "Aetherfall", "Corvid");
        Mark(config, "Aetherfall", "Rookery");
        Mark(config, "Grapevine", "Thistle");
        var (app, telnet) = App(config);

        await Start(app, config);

        await Assert.That(telnet.Connected.Count).IsEqualTo(3);
        await Assert.That(app.ConnectedCharacters())
            .IsEquivalentTo(new[] { "Aetherfall.Corvid", "Aetherfall.Rookery", "Grapevine.Thistle" });

        await Assert.That(app.WindowOwnerOf(SharpMUTermApp.MainWindowId)).IsEqualTo("Aetherfall.Corvid");
        foreach (var later in new[] { "Aetherfall.Rookery", "Grapevine.Thistle" })
        {
            var windowId = app.WindowIds().Single(id => app.WindowOwnerOf(id) == later);
            await Assert.That(windowId).IsNotEqualTo(SharpMUTermApp.MainWindowId).Because(later);
        }
    }

    /// <summary>
    /// And you land somewhere predictable: the <em>first</em> in configuration order, which is decided
    /// while the windows are built and before any socket is dialled. Asserted with the first world's
    /// dials refused so a race that let "whichever connected first" win would put focus on Grapevine —
    /// the failure this ordering rule exists to make impossible.
    /// </summary>
    [Test]
    public async Task SeveralMarked_FocusIsTheFirstInConfigurationOrderWhateverTheWireDoes()
    {
        var config = Unmarked();
        Mark(config, "Aetherfall", "Corvid");
        Mark(config, "Grapevine", "Thistle");
        var (app, telnet) = App(config);
        telnet.Refusing.Add("aetherfall.mux");

        await Start(app, config);

        await Assert.That(app.ActiveSessionKey).IsEqualTo("Aetherfall.Corvid");
        await Assert.That(app.ActiveWindowId()).IsEqualTo(SharpMUTermApp.MainWindowId);
        await Assert.That(app.ConnectedCharacters()).IsEquivalentTo(new[] { "Grapevine.Thistle" });
    }

    // ---- Failures --------------------------------------------------------------------------

    /// <summary>
    /// One world refusing a connection does not stop the others, and it is not swallowed: the session
    /// prints it into its own window — a background tab, for all but the first — so the client also
    /// says which connection it was, and keeps it in the ⌃P log after the row retires itself.
    /// </summary>
    [Test]
    public async Task OneRefusedConnection_StopsNeitherTheOthersNorItsOwnReport()
    {
        var config = Unmarked();
        Mark(config, "Aetherfall", "Corvid");
        Mark(config, "Aetherfall", "Rookery");
        Mark(config, "Grapevine", "Thistle");
        var (app, telnet) = App(config);
        telnet.Refusing.Add("grapevine.haus");

        await Start(app, config);

        // Every one was dialled, and the two that could, connected.
        await Assert.That(telnet.Opened.Count).IsEqualTo(3);
        await Assert.That(telnet.Opened.All(o => o.Telnet.ConnectAttempts == 1)).IsTrue();
        await Assert.That(app.ConnectedCharacters())
            .IsEquivalentTo(new[] { "Aetherfall.Corvid", "Aetherfall.Rookery" });

        // Named, and findable after the status row has moved on.
        var reported = app.Messages.Entries.Where(m => m.Text.Contains("could not connect")).ToArray();
        await Assert.That(reported.Length).IsEqualTo(1);
        await Assert.That(reported[0].Text).Contains("Thistle");
        await Assert.That(reported[0].Severity).IsEqualTo(MessageSeverity.Error);

        // And in the failing session's own window, where a user looking at that connection would see it.
        var windowId = app.WindowIds().Single(id => app.WindowOwnerOf(id) == "Grapevine.Thistle");
        await Assert.That(string.Join("\n", app.PaneLines(windowId))).Contains("no route to host");
    }

    /// <summary>Every failure is reported, not just the survivor of a status row that holds one line.</summary>
    [Test]
    public async Task TwoRefusedConnections_AreBothKeptInTheMessageLog()
    {
        var config = Unmarked();
        Mark(config, "Aetherfall", "Corvid");
        Mark(config, "Grapevine", "Thistle");
        var (app, telnet) = App(config);
        telnet.Refusing.Add("aetherfall.mux");
        telnet.Refusing.Add("grapevine.haus");

        await Start(app, config);

        var reported = app.Messages.Entries.Where(m => m.Text.Contains("could not connect")).ToArray();
        await Assert.That(reported.Length).IsEqualTo(2);
        await Assert.That(app.ConnectedCharacters()).IsEmpty();

        // A client that connected nothing is still a client: the command line is pointed at the first
        // session, which ⌃P ▸ Reconnect can dial again.
        await Assert.That(app.ActiveSessionKey).IsEqualTo("Aetherfall.Corvid");
    }

    // ---- The command line ------------------------------------------------------------------

    /// <summary>
    /// A host typed on the command line is explicit intent for this run and wins: it is connected, and
    /// the marked characters are not — a client launched at one server must not drag in every world the
    /// user happens to have marked.
    /// </summary>
    [Test]
    public async Task AHostOnTheCommandLine_ConnectsInsteadOfEveryMark()
    {
        var config = Unmarked();
        Mark(config, "Aetherfall", "Corvid");
        Mark(config, "Grapevine", "Thistle");
        var (app, telnet) = App(config);
        var typed = new WorldDefinition { Name = "aardmud.org", Host = "aardmud.org", Port = 4000 };

        await Start(app, config, typed);

        await Assert.That(telnet.Opened.Select(o => o.Host).ToArray()).IsEquivalentTo(new[] { "aardmud.org" });
        await Assert.That(app.ConnectedCharacters()).IsEquivalentTo(new[] { "aardmud.org" });
        await Assert.That(app.ActiveSessionKey).IsEqualTo("aardmud.org");
    }

    /// <summary>And it connects with nothing marked at all, which is how it has always behaved.</summary>
    [Test]
    public async Task AHostOnTheCommandLine_ConnectsWithNothingMarked()
    {
        var config = Unmarked();
        var (app, telnet) = App(config);
        var typed = new WorldDefinition { Name = "aardmud.org", Host = "aardmud.org", Port = 4000 };

        await Start(app, config, typed);

        await Assert.That(telnet.Connected.Count).IsEqualTo(1);
        await Assert.That(app.StatusMarkup).DoesNotContain("nothing connects at start");
    }

    // ---- The demo scene --------------------------------------------------------------------

    /// <summary>
    /// The demo fakes a connection it never opens, so what it fakes has to be what a real launch would
    /// do — the gap between the two has hidden three bugs in this file already. Corvid is the demo's
    /// connected character; it is the demo's marked one, and nothing else is.
    /// </summary>
    [Test]
    public async Task TheDemoScenesConnectedCharacterIsTheOneAStartupWouldConnect()
    {
        var config = DemoScene.Build();

        var startup = StartupConnections.Resolve(config);

        await Assert.That(startup.Select(s => $"{s.World.Name}.{s.Character!.Name}").ToArray())
            .IsEquivalentTo(new[] { DemoScene.ActiveSessionKey });
    }

    // ---- The wiring, not just the work -----------------------------------------------------

    /// <summary>
    /// <b>That the launch is actually wired to this.</b> Every test above calls
    /// <see cref="SharpMUTermApp.StartAsync"/> itself, which proves what a launch <em>does</em> and says
    /// nothing about whether one ever happens — and for a while one never did. <c>Run</c> hung startup off
    /// <c>Window.OnShown</c>, but that event is raised by <c>AddWindow</c>, which is the last line of the
    /// app's constructor: it had already fired before <c>Run</c> subscribed, and is never raised again. So
    /// <c>StartAsync</c> was dead code in the running client. Nothing threw, nothing logged, and the whole
    /// of this file passed — a marked character was simply never dialled, and neither was a host typed on
    /// the command line.
    /// <para>
    /// This drives the scheduling half of <c>Run</c> instead (<see cref="SharpMUTermApp.ScheduleStartup"/>,
    /// which is exactly what <c>Run</c> calls before entering the blocking loop) and asserts the socket
    /// opened. Headless, <c>OnUiThread</c> runs the queued action inline; in a terminal it is drained on
    /// the loop's first pass.
    /// </para>
    /// </summary>
    [Test]
    public async Task WhatRunSchedules_ReallyDialsTheMarkedCharacter()
    {
        var config = Unmarked();
        Mark(config, "Aetherfall", "Corvid");
        var (app, telnet) = App(config);

        app.ScheduleStartup(StartupConnections.Resolve(config));
        await app.LastCommand;

        await Assert.That(telnet.Connected.Count)
            .IsEqualTo(1)
            .Because("Run's own scheduling has to reach StartAsync, or the client never dials at all");
        await Assert.That(app.ActiveSessionKey).IsEqualTo("Aetherfall.Corvid");
    }

    /// <summary>
    /// And the reason the hook had to change, stated as the fact it is: by the time anything can subscribe
    /// to the main window's <c>OnShown</c>, it has already been raised. A handler attached after the
    /// constructor returns never runs — not on the first frame, not ever — so nothing may be hung off it.
    /// </summary>
    [Test]
    public async Task TheMainWindowsOnShownHasAlreadyFiredBeforeAnyoneCanSubscribe()
    {
        var (app, _) = App(Unmarked());
        var fired = false;

        app.OnMainWindowShown((_, _) => fired = true);
        app.RenderSnapshot(); // a real arranged, painted frame

        await Assert.That(fired)
            .IsFalse()
            .Because("AddWindow raises OnShown inside the constructor, so startup must not hang off it");
    }
}
