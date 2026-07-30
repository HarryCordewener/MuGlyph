using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// Three reports from one live session against a real MUSH, all of them the same defect: session state
/// accounted against the wrong thing.
/// <list type="number">
/// <item>
/// <b>Captures never fired.</b> The engines were composed from the trigger sets a session was handed at
/// construction, so a session opened before its character had the capture set assigned — or before the set
/// gained the rule being written — ran an empty engine for the rest of its life. No line matched, no
/// <c>SpawnLine</c> was raised, no window appeared, and the client said nothing about any of it.
/// </item>
/// <item>
/// <b>The header's fraction compared two units.</b> Connected <em>characters</em> over configured
/// <em>worlds</em>: two characters across three worlds read <c>2/3</c>.
/// </item>
/// <item>
/// <b>The quit prompt undercounted.</b> It reduced its connections to distinct world names, so two
/// characters logged in on one world were announced as "1 world connected".
/// </item>
/// </list>
/// <para>
/// The fixture is the shape that exposed all three and that no other fixture in this suite has: the number
/// of worlds and the number of characters <strong>differ</strong> (3 and 5), and the two connected
/// characters are on <strong>one</strong> world. One character per world passes either way, which is why
/// these shipped.
/// </para>
/// <para>
/// The sessions are <strong>connected</strong>, over a recording transport. That is not decoration:
/// <see cref="WorldSession.SendRawAsync"/> drops everything while there is no live transport, and a session
/// that was never connected never runs its receive path at all — so a capture assertion against an
/// unconnected session is true no matter what the code does.
/// </para>
/// </summary>
/// <remarks>
/// Serialised with the other end-to-end suites: constructing the app and rendering a frame both touch the
/// process-global console streams.
/// </remarks>
[NotInParallel]
public class SessionStateAccountingTests
{
    private const int Width = 160;
    private const int Height = 40;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    // ---- 1. captures ------------------------------------------------------------------------

    /// <summary>
    /// <b>The decisive one.</b> A connected character is assigned the capture set while it is connected —
    /// what F5's assignment toggle does — and the very next matching line opens the spawn window and lands
    /// in it. Before the fix the session kept the empty engine it was built with: this line printed to the
    /// main window and the <c>Public</c> window was never created.
    /// </summary>
    [Test]
    public async Task ASetAssignedWhileConnected_OpensTheSpawnWindowOnTheNextMatchingLine()
    {
        var wired = await Wired();
        var mannaz = wired.Config.Worlds[0].Characters[0];

        wired.Receive(wired.MannazWire, PublicLine);
        await Assert.That(SpawnWindowExists(wired.App, "Public")).IsFalse(); // the state the bug lived in

        // WorldsScreenRenderer.Assignment: a character opts into a set by list membership…
        mannaz.TriggerSets.Add("Comms");
        // …and every settings screen persists each committed change through this one funnel.
        wired.App.SaveConfiguration();

        wired.Receive(wired.MannazWire, PublicLine);

        await Assert.That(SpawnWindowExists(wired.App, "Public")).IsTrue();
        await Assert.That(string.Join("\n", wired.App.PaneLines(Workspace.SpawnWindowId("Public"))))
            .Contains("Lol");
    }

    /// <summary>
    /// The other half of the same defect: the set was assigned all along, and the <em>rule</em> is the thing
    /// added mid-connection — F2's <c>[+ add trigger]</c>. Membership of a set was as frozen as assignment
    /// of one.
    /// </summary>
    [Test]
    public async Task ARuleAddedToAnAssignedSetWhileConnected_TakesEffectOnTheNextLine()
    {
        var wired = await Wired();
        var mannaz = wired.Config.Worlds[0].Characters[0];
        mannaz.TriggerSets.Add("Comms");
        wired.Config.TriggerSets.Single(s => s.Name == "Comms").Triggers.Clear();
        wired.App.SaveConfiguration();

        wired.Receive(wired.MannazWire, PublicLine);
        await Assert.That(SpawnWindowExists(wired.App, "Public")).IsFalse();

        wired.Config.TriggerSets.Single(s => s.Name == "Comms").Triggers.Add(new Trigger
        {
            Name = "Public",
            Pattern = "^<Public>",
            Actions = new TriggerActions { SpawnTarget = "Public" },
        });
        wired.App.SaveConfiguration();

        wired.Receive(wired.MannazWire, PublicLine);

        await Assert.That(SpawnWindowExists(wired.App, "Public")).IsTrue();
    }

    /// <summary>
    /// And the funnel is really the screens'. This drives a genuine committed edit through a settings screen
    /// — F7, whose every row is a checkbox — and asserts that a rule added beforehand is live afterwards.
    /// Without it, hooking the reload into <see cref="SharpMUTermApp.SaveConfiguration"/> would only be
    /// provable through a seam a test calls and a user never does.
    /// </summary>
    [Test]
    public async Task ACommittedEditOnAnySettingsScreen_IsWhatMakesAnAutomationChangeLive()
    {
        var wired = await Wired();
        wired.Config.Worlds[0].Characters[0].TriggerSets.Add("Comms");

        wired.App.DispatchCommand("screen:textansi");
        wired.App.SimulateSettingsKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false));

        wired.Receive(wired.MannazWire, PublicLine);

        await Assert.That(SpawnWindowExists(wired.App, "Public")).IsTrue();
    }

    /// <summary>
    /// The second suspect, ruled out and now pinned: the <c>SpawnLine</c> subscription is per-session and
    /// set up in exactly one place (<c>BindSession</c>), and every path that opens a session goes through
    /// it. This drives the newest of those paths — the <c>char:</c> dispatch the command surface and the
    /// rail both use — and asserts a capture on that session routes. A session created by a path that
    /// skipped the block would print fine and silently route nothing, which is a shape this client has
    /// already produced twice.
    /// </summary>
    [Test]
    public async Task ASessionOpenedByTheCharacterSwitchPath_RoutesItsCaptures()
    {
        var wired = await Wired();
        wired.Config.Worlds[1].Characters[0].TriggerSets.Add("Comms");

        await wired.Open("Grapevine.Riko", wired.RikoWire);

        wired.Receive(wired.RikoWire, PublicLine);

        await Assert.That(SpawnWindowExists(wired.App, "Public")).IsTrue();
    }

    /// <summary>
    /// Observability, which is the other half of the fix: a capture that has never matched is
    /// <em>readable</em>. The report names each live rule, where it routes and how often it has fired, so
    /// "the rule is not loaded" and "the rule is loaded and does not match" stop looking identical from the
    /// screen — the state the maintainer spent an evening unable to tell apart.
    /// </summary>
    [Test]
    public async Task TheTriggerReportSaysWhetherACaptureHasEverMatched()
    {
        var wired = await Wired();
        wired.Config.Worlds[0].Characters[0].TriggerSets.Add("Comms");
        wired.App.SaveConfiguration();

        var before = string.Join("\n", wired.App.TriggerReportNow());
        await Assert.That(before).Contains("set Comms");
        await Assert.That(before).Contains("window 'Public'");
        await Assert.That(before).Contains("0 matches");

        wired.Receive(wired.MannazWire, PublicLine);

        await Assert.That(string.Join("\n", wired.App.TriggerReportNow())).Contains("1 match");
    }

    /// <summary>
    /// And the one state that is genuinely a misconfiguration is loud: a character assigned a set that does
    /// not exist. <see cref="AppConfiguration.ResolveTriggerSets"/> skips the name in silence, which is the
    /// one way automation can be configured, look configured, and do nothing.
    /// </summary>
    [Test]
    public async Task ASetAssignedToACharacterThatDoesNotExist_IsReported()
    {
        var wired = await Wired();
        wired.Config.Worlds[0].Characters[0].TriggerSets.Add("Cmoms"); // a typo, or a set since renamed
        wired.App.SaveConfiguration();

        await Assert.That(string.Join("\n", wired.App.TriggerReportNow()))
            .Contains("no such set exists");
    }

    // ---- 2. the header's fraction ----------------------------------------------------------

    /// <summary>
    /// Both halves count the same thing. Two connected characters out of five configured reads <c>2/5</c>;
    /// it used to read <c>2/3</c>, because the denominator counted worlds. The unit is named on the row, so
    /// the fraction cannot be read as worlds again by eye either.
    /// </summary>
    [Test]
    public async Task TheHeaderCountsCharactersOnBothSidesOfTheFraction()
    {
        var wired = await Wired();
        await wired.Open("Convergence.Riko", wired.RikoWire);
        wired.App.RenderNextFrame();

        await Assert.That(wired.App.HeaderText).Contains("2/5 characters");
        await Assert.That(wired.App.HeaderText).DoesNotContain("2/3");
    }

    /// <summary>
    /// A world with no characters still offers one connection — the anonymous one a host typed on the
    /// command line becomes — so the denominator counts it. Without that a numerator could exceed it.
    /// </summary>
    [Test]
    public async Task AWorldWithNoCharactersStillCountsAsOneConnection()
    {
        var wired = await Wired();
        wired.Config.Worlds.Add(new WorldDefinition { Name = "Bare", Host = "bare", Port = 4000 });
        wired.App.SaveConfiguration(); // what F5's [+ add world] runs after it commits the new row
        wired.App.RenderNextFrame();

        await Assert.That(wired.App.HeaderText).Contains("1/6 characters");
    }

    // ---- 3. the quit prompt ----------------------------------------------------------------

    /// <summary>
    /// The reported undercount. Two characters connected on <em>one</em> world is two connections a quit
    /// would drop, and the prompt says two and names both. It used to reduce them to distinct world names
    /// and say "1 world connected" — and the whole point of this prompt is that its summary can be trusted
    /// before something is discarded.
    /// </summary>
    [Test]
    public async Task TheQuitPromptCountsEveryConnectionRatherThanEveryWorld()
    {
        var wired = await Wired();
        await wired.Open("Convergence.Riko", wired.RikoWire);

        wired.App.SimulateKey(CtrlQ());

        var prompt = string.Join("\n", wired.App.QuitPromptLines);
        await Assert.That(prompt).Contains("2 characters connected");
        await Assert.That(prompt).Contains("Convergence.Mannaz");
        await Assert.That(prompt).Contains("Convergence.Riko");
        await Assert.That(prompt).DoesNotContain("1 world connected");
    }

    /// <summary>
    /// One derivation, so the two counters cannot drift apart again: whatever the header's numerator says,
    /// the prompt names that many connections. This is the assertion that would fail if either surface grew
    /// a private notion of "connected" — which is how they came to disagree in the first place.
    /// </summary>
    [Test]
    public async Task TheHeaderAndTheQuitPromptReadFromOneDerivation()
    {
        var wired = await Wired();
        await wired.Open("Convergence.Riko", wired.RikoWire);
        wired.App.RenderNextFrame();

        var connected = wired.App.ConnectedCharacters();
        wired.App.SimulateKey(CtrlQ());

        await Assert.That(wired.App.HeaderText).Contains($"{connected.Count}/5 characters");
        await Assert.That(string.Join("\n", wired.App.QuitPromptLines))
            .Contains($"{connected.Count} characters connected");
    }

    /// <summary>
    /// A background connection dropping is visible too. The old numerator was a set maintained only for the
    /// <em>active</em> session, so a world losing its connection in another pane changed neither the count
    /// nor the rail's dot until you switched to it.
    /// </summary>
    [Test]
    public async Task ABackgroundConnectionDropping_ChangesTheCount()
    {
        var wired = await Wired();
        await wired.Open("Convergence.Riko", wired.RikoWire);
        wired.App.RenderNextFrame();
        await Assert.That(wired.App.HeaderText).Contains("2/5 characters");

        // Riko is the active one now, so Mannaz is the background connection that drops.
        await wired.App.FindSession("Convergence.Mannaz")!.DisconnectAsync();
        wired.App.RenderNextFrame();

        await Assert.That(wired.App.ConnectedCharacters()).IsEquivalentTo(new[] { "Convergence.Riko" });
        await Assert.That(wired.App.HeaderText).Contains("1/5 characters");
    }

    // ---- harness ---------------------------------------------------------------------------

    private const string PublicLine = "<Public> Starfall Empress Lucille Wolfsbane says, \"Lol\"\n";

    private static bool SpawnWindowExists(SharpMUTermApp app, string target) =>
        app.WindowIds().Contains(Workspace.SpawnWindowId(target), StringComparer.Ordinal);

    private static ConsoleKeyInfo CtrlQ() => new('\0', ConsoleKey.Q, false, false, true);

    private static Task<WiredApp> Wired() => WiredApp.Build();

    /// <summary>
    /// An app on the maintainer's shape: <b>three worlds, five characters</b>, with Mannaz connected over a
    /// recording transport. <c>Comms</c> is defined but assigned to nobody, which is the state a session
    /// opened before its automation had been configured is in.
    /// </summary>
    private sealed class WiredApp
    {
        private WiredApp(
            SharpMUTermApp app,
            AppConfiguration config,
            RecordingTelnetSession mannaz,
            RecordingTelnetSession riko)
        {
            App = app;
            Config = config;
            MannazWire = mannaz;
            RikoWire = riko;
        }

        internal SharpMUTermApp App { get; }

        internal AppConfiguration Config { get; }

        internal RecordingTelnetSession MannazWire { get; }

        internal RecordingTelnetSession RikoWire { get; }

        internal static async Task<WiredApp> Build()
        {
            Console.SetIn(TextReader.Null);
            var config = new AppConfiguration();
            config.TriggerSets.Add(new TriggerSet
            {
                Name = "Comms",
                Triggers =
                {
                    new Trigger
                    {
                        Name = "Public",
                        Pattern = "^<Public>",
                        Actions = new TriggerActions { SpawnTarget = "Public" },
                    },
                },
            });

            // Three worlds, five characters — and two of the five share a world, which is what makes
            // "connections" and "worlds" different numbers in both directions.
            config.Worlds.Add(World("Convergence", "Mannaz", "Riko"));
            config.Worlds.Add(World("Grapevine", "Riko", "Thistle"));
            config.Worlds.Add(World("Aetherfall", "Corvid"));

            var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
            var wired = new WiredApp(app, config, new RecordingTelnetSession(), new RecordingTelnetSession());
            await wired.Open("Convergence.Mannaz", wired.MannazWire);
            app.RenderNextFrame();
            return wired;
        }

        /// <summary>Delivers server text as one of the wired worlds and renders a settling frame.</summary>
        internal void Receive(RecordingTelnetSession telnet, string text)
        {
            telnet.Receive(text);
            App.RenderNextFrame();
        }

        /// <summary>Switches to a character the way ⌃P and the rail do, over its own transport, and connects it.</summary>
        internal async Task Open(string sessionKey, RecordingTelnetSession telnet)
        {
            App.TelnetFactory = _ => telnet;
            if (!App.DispatchCommand(CommandIds.Character(sessionKey)))
            {
                throw new InvalidOperationException($"the app would not switch to {sessionKey}");
            }

            await App.FindSession(sessionKey)!.ConnectAsync();
        }

        private static WorldDefinition World(string name, params string[] characters)
        {
            var world = new WorldDefinition
            {
                Name = name,
                Host = $"{name.ToLowerInvariant()}.example.org",
                Port = 4201,
            };

            foreach (var character in characters)
            {
                world.Characters.Add(new CharacterDefinition
                {
                    Name = character,
                    Logging = new LoggingSettings(),
                });
            }

            return world;
        }
    }
}
