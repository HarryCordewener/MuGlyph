using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The reported defect: <b>two connected characters whose triggers match the same pattern shared one
/// capture pane.</b> Whichever session matched first created the window — with its own
/// <c>sessionKey</c> on it — and every later session's lines were appended to that same window, which
/// somebody else owned. The second character's channel was therefore not merely mixed in with the
/// first's: it was filed under the wrong character, and <see cref="SharpMUTermApp.RailLines"/> draws
/// window rows for the active character only, so it was <em>invisible</em> from the character it
/// belonged to and reachable only from the one it did not.
/// <para>
/// The cause was <c>Workspace.SpawnWindowId</c>, which was <c>$"spawn:{target}"</c> — no owner in it, so
/// one window per workspace per target rather than one per session per target.
/// </para>
/// <para>
/// The fixture is the smallest one that can show it: <b>two characters on one world, both assigned the
/// same trigger set</b>, both connected over their own recording transport. One character per target
/// passes either way, which is why this shipped.
/// </para>
/// </summary>
/// <remarks>
/// Serialised with the other end-to-end suites: constructing the app and rendering a frame both touch
/// the process-global console streams.
/// </remarks>
[NotInParallel]
public class SpawnWindowPerSessionTests
{
    private const int Width = 160;
    private const int Height = 40;

    private const string Ann = "Convergence.Ann";
    private const string Bob = "Convergence.Bob";

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    // ---- The report -------------------------------------------------------------------------

    /// <summary>
    /// The headline. Both characters capture <c>Public</c>; there are two windows, one owned by each.
    /// Before the fix there was one, owned by whoever matched first.
    /// </summary>
    [Test]
    public async Task TwoCharactersCapturingOneTargetGetAWindowEach()
    {
        var two = await Two();

        two.Receive(two.AnnWire, "<Public> Ann says, \"first\"\n");
        two.Receive(two.BobWire, "<Public> Bob says, \"second\"\n");

        var annWindow = Workspace.SpawnWindowId(Ann, "Public");
        var bobWindow = Workspace.SpawnWindowId(Bob, "Public");

        await Assert.That(two.App.WindowIds()).Contains(annWindow);
        await Assert.That(two.App.WindowIds()).Contains(bobWindow);
        await Assert.That(annWindow).IsNotEqualTo(bobWindow);

        await Assert.That(two.App.WindowOwnerOf(annWindow)).IsEqualTo(Ann);
        await Assert.That(two.App.WindowOwnerOf(bobWindow)).IsEqualTo(Bob);
    }

    /// <summary>
    /// And the lines land in their own pane and only there. This is the assertion the user's sentence is
    /// about — "one wins and gets 2 redirects" — and it fails on the unfixed build with both lines in
    /// Ann's pane and Bob's pane not existing at all.
    /// </summary>
    [Test]
    public async Task EachCharactersCaptureLandsInItsOwnPaneAndNoOther()
    {
        var two = await Two();

        two.Receive(two.AnnWire, "<Public> Ann says, \"first\"\n");
        two.Receive(two.BobWire, "<Public> Bob says, \"second\"\n");

        var ann = string.Join("\n", two.App.PaneLines(Workspace.SpawnWindowId(Ann, "Public")));
        var bob = string.Join("\n", two.App.PaneLines(Workspace.SpawnWindowId(Bob, "Public")));

        await Assert.That(ann).Contains("first");
        await Assert.That(ann).DoesNotContain("second");
        await Assert.That(bob).Contains("second");
        await Assert.That(bob).DoesNotContain("first");
    }

    /// <summary>
    /// The visible half of the same defect. The rail lists the <em>active</em> character's windows, so a
    /// capture pane filed under the wrong owner cannot be seen or clicked from the character whose
    /// channel it is. Each character's rail now carries exactly one <c>Public</c> row: their own.
    /// </summary>
    [Test]
    public async Task EachCharactersRailListsItsOwnCapturePaneAndNotTheOthers()
    {
        var two = await Two();

        two.Receive(two.AnnWire, "<Public> Ann says, \"first\"\n");
        two.Receive(two.BobWire, "<Public> Bob says, \"second\"\n");

        // Bob is the character switched to last, so the rail is drawn for Bob.
        var bobsRail = two.App.RailLines;
        await Assert.That(RailTargets(bobsRail)).Contains("win:" + Workspace.SpawnWindowId(Bob, "Public"));
        await Assert.That(RailTargets(bobsRail)).DoesNotContain("win:" + Workspace.SpawnWindowId(Ann, "Public"));

        two.App.DispatchCommand(CommandIds.Character(Ann));
        var annsRail = two.App.RailLines;
        await Assert.That(RailTargets(annsRail)).Contains("win:" + Workspace.SpawnWindowId(Ann, "Public"));
        await Assert.That(RailTargets(annsRail)).DoesNotContain("win:" + Workspace.SpawnWindowId(Bob, "Public"));
    }

    /// <summary>
    /// <b>The id changed and the name did not.</b> Both panes are still called <c>Public</c> — on the tab
    /// strip and in the sidebar — because every surface that names a window reads
    /// <see cref="WorkspaceWindow.Title"/> and none derives a display name from the id. A pane labelled
    /// <c>Convergence.Ann:Public</c> would be this fix leaking its own bookkeeping onto the screen.
    /// </summary>
    [Test]
    public async Task BothPanesAreStillCalledPublic()
    {
        var two = await Two();

        two.Receive(two.AnnWire, "<Public> Ann says, \"first\"\n");
        two.Receive(two.BobWire, "<Public> Bob says, \"second\"\n");

        await Assert.That(two.App.WindowTitleOf(Workspace.SpawnWindowId(Ann, "Public"))).IsEqualTo("Public");
        await Assert.That(two.App.WindowTitleOf(Workspace.SpawnWindowId(Bob, "Public"))).IsEqualTo("Public");

        // The tab strip and the sidebar say the same — asserted about *those two windows* rather than
        // over the whole frame, so the test cannot pass because some unrelated surface happened to
        // satisfy it.
        //
        // What must not leak is the *session key*. The character's own name is a different thing and is
        // on the strip deliberately: a spawn tab reads "Ann - Public" precisely so two characters each
        // capturing Public can be told apart — which is the feature this PR exists for. So the drawn
        // title carries "Ann" and must never carry "Convergence.Ann".
        two.App.RenderNextFrame();

        foreach (var owner in new[] { Ann, Bob })
        {
            var id = Workspace.SpawnWindowId(owner, "Public");

            var pane = two.App.PaneIdOf(id);
            await Assert.That(pane).IsNotNull().Because($"{owner}'s Public pane must be placed");

            // Read through StripMarkup: the strip's labels are markup — #14 tints the tab a line
            // arrived in — so the raw string carries colour tags around the name.
            var tab = two.App.PaneTabTitles[pane!]
                .Select(StripMarkup)
                .SingleOrDefault(t => t.Contains("Public", StringComparison.Ordinal) &&
                                      t.Contains(CharacterOf(owner), StringComparison.Ordinal));

            await Assert.That(tab).IsNotNull().Because($"{owner}'s Public pane must have a tab naming it");
            await Assert.That(tab!)
                .DoesNotContain(owner)
                .Because("the id carries the session key; the tab the reader sees must not");

            // The rail row is found by its `win:<id>` click payload — which does carry the owner — and
            // then read for what it *draws*, which must not. Only the active character's window rows are
            // drawn, so exactly one of the two is on screen.
            var row = two.App.RailLines.SingleOrDefault(l => l.Contains("win:" + id, StringComparison.Ordinal));
            if (row is null)
            {
                continue;
            }

            var drawn = StripMarkup(row);
            await Assert.That(drawn).Contains("Public");
            await Assert.That(drawn)
                .DoesNotContain(owner)
                .Because("the id carries the session key; the row the reader sees must not");
        }
    }

    /// <summary>The character half of a <c>World.Character</c> session key.</summary>
    private static string CharacterOf(string sessionKey) => sessionKey[(sessionKey.IndexOf('.') + 1)..];

    /// <summary>
    /// The id is stable across a reconnect: the same character dropping and dialling back in keeps the
    /// window it had, rather than accruing a second one beside it.
    /// </summary>
    [Test]
    public async Task ReconnectingKeepsTheSameCapturePane()
    {
        var two = await Two();
        two.Receive(two.AnnWire, "<Public> Ann says, \"first\"\n");

        var before = two.App.WindowIds().Count(id => id.Contains("Public", StringComparison.Ordinal));

        await two.App.FindSession(Ann)!.DisconnectAsync();
        await two.App.FindSession(Ann)!.ConnectAsync();
        two.Receive(two.AnnWire, "<Public> Ann says, \"again\"\n");

        await Assert.That(two.App.WindowIds().Count(id => id.Contains("Public", StringComparison.Ordinal)))
            .IsEqualTo(before);
        var pane = string.Join("\n", two.App.PaneLines(Workspace.SpawnWindowId(Ann, "Public")));
        await Assert.That(pane).Contains("first");
        await Assert.That(pane).Contains("again");
    }

    // ---- Harness ----------------------------------------------------------------------------

    /// <summary>
    /// One rail row with its Spectre markup removed, so an assertion can read what the sidebar
    /// <em>draws</em> rather than the <c>[link=win:&lt;id&gt;]</c> payload it carries underneath.
    /// </summary>
    private static string StripMarkup(string line)
    {
        var text = new System.Text.StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != '[')
            {
                text.Append(line[i]);
                continue;
            }

            if (i + 1 < line.Length && line[i + 1] == '[')
            {
                text.Append('[');
                i++;
                continue;
            }

            var close = line.IndexOf(']', i);
            i = close < 0 ? line.Length : close;
        }

        return text.ToString();
    }

    /// <summary>The <c>win:…</c> click payloads the rail is currently drawing.</summary>
    private static IReadOnlyList<string> RailTargets(IReadOnlyList<string> lines)
    {
        var targets = new List<string>();
        foreach (var line in lines)
        {
            var at = 0;
            while ((at = line.IndexOf("[link=", at, StringComparison.Ordinal)) >= 0)
            {
                at += "[link=".Length;
                var end = line.IndexOf(']', at);
                if (end < 0)
                {
                    break;
                }

                targets.Add(line[at..end]);
                at = end;
            }
        }

        return targets;
    }

    private static Task<TwoCharacters> Two() => TwoCharacters.Build();

    /// <summary>
    /// One world, two characters, one shared trigger set routing <c>^&lt;Public&gt;</c> to a spawn window
    /// called <c>Public</c> — and both characters connected over a transport of their own, because a
    /// session that was never connected never runs its receive path and would make any capture
    /// assertion true whatever the code does.
    /// </summary>
    private sealed class TwoCharacters
    {
        private TwoCharacters(
            SharpMUTermApp app, AppConfiguration config, RecordingTelnetSession ann, RecordingTelnetSession bob)
        {
            App = app;
            Config = config;
            AnnWire = ann;
            BobWire = bob;
        }

        internal SharpMUTermApp App { get; }

        internal AppConfiguration Config { get; }

        internal RecordingTelnetSession AnnWire { get; }

        internal RecordingTelnetSession BobWire { get; }

        internal static async Task<TwoCharacters> Build()
        {
            Console.SetIn(TextReader.Null);
            var config = Configuration();
            var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
            var two = new TwoCharacters(app, config, new RecordingTelnetSession(), new RecordingTelnetSession());

            await two.Open(Ann, two.AnnWire);
            await two.Open(Bob, two.BobWire);
            app.RenderNextFrame();
            return two;
        }

        internal void Receive(RecordingTelnetSession telnet, string text)
        {
            telnet.Receive(text);
            App.RenderNextFrame();
        }

        internal async Task Open(string sessionKey, RecordingTelnetSession telnet)
        {
            App.TelnetFactory = _ => telnet;
            if (!App.DispatchCommand(CommandIds.Character(sessionKey)))
            {
                throw new InvalidOperationException($"the app would not switch to {sessionKey}");
            }

            await App.FindSession(sessionKey)!.ConnectAsync();
        }
    }

    internal static AppConfiguration Configuration()
    {
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
                    Actions = new TriggerActions { SpawnTarget = "Public", Gag = true },
                },
            },
        });

        config.Worlds.Add(new WorldDefinition
        {
            Name = "Convergence",
            Host = "convergence.example.org",
            Port = 4201,
            Characters =
            {
                new CharacterDefinition { Name = "Ann", Logging = new LoggingSettings(), TriggerSets = { "Comms" } },
                new CharacterDefinition { Name = "Bob", Logging = new LoggingSettings(), TriggerSets = { "Comms" } },
            },
        });

        return config;
    }
}
