using System.Text.RegularExpressions;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// <b>The pane numbering is readable from any character, not only the one you are in.</b>
/// <para>
/// ⌥N has always been global — <c>JumpToPane</c> indexes the workspace's one split tree, not the active
/// character's windows — so ⌥3 already reached a pane holding somebody else's session. The rail did not
/// say so. Window rows are listed for the <em>active</em> character only (<c>BuildRailWindows</c>'s owner
/// filter, which is load-bearing: a window row under a character means that window is theirs), so a reader
/// looking at Ann saw <c>pane 1</c> and nothing else while Bob and Cal sat in panes 2 and 3 with chords
/// pointing at them. A number that cannot be read off the screen is a number nobody presses, which is what
/// "pane numbering should be global" was reporting.
/// </para>
/// <para>
/// The fix is one column on a row that already exists: every character row carries the pane its session is
/// in. No new rows, no other character's windows listed under yours, and the same <c>pane N</c> vocabulary
/// the window rows, the ⌃P entries, the move overlay and the chord already use.
/// </para>
/// </summary>
/// <remarks>
/// Serialised with the other end-to-end suites: constructing the app and rendering a frame both touch the
/// process-global console streams.
/// </remarks>
[NotInParallel]
public class PaneNumberingRailTests
{
    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>
    /// <b>The claim.</b> Ann is active; the rail names the pane every one of the three characters is in.
    /// Before this it named exactly one, and it was always Ann's.
    /// </summary>
    [Test]
    public async Task TheRailNamesThePaneOfEveryCharacterAndNotJustTheActiveOne()
    {
        var app = await ThreePanes();
        app.SimulateKey(Alt(1));
        app.RenderNextFrame();

        await Assert.That(app.ActiveSessionKey).IsEqualTo("Alfa.Ann");
        await Assert.That(CharacterPane(app, "Ann")).IsEqualTo("⌥" + "1");
        await Assert.That(CharacterPane(app, "Bob"))
            .IsEqualTo("⌥" + "2")
            .Because("⌥2 goes to Bob from here, and the sidebar has to be where you find that out");
        await Assert.That(CharacterPane(app, "Cal")).IsEqualTo("⌥" + "3");
    }

    /// <summary>
    /// <b>The loop closes.</b> For each character: read the digit the rail prints against their name while
    /// somebody else is active, press it, and land on them. Nothing here writes down which pane holds
    /// whom — the digit comes out of the rendered sidebar, which is the only property that makes the number
    /// worth printing.
    /// </summary>
    [Test]
    public async Task PressingTheDigitTheRailPrintsAgainstACharacterGoesToThatCharacter()
    {
        var app = await ThreePanes();

        foreach (var (name, key) in new[] { ("Cal", "Cara.Cal"), ("Bob", "Bravo.Bob"), ("Ann", "Alfa.Ann") })
        {
            // Stand somewhere else first, so the row being read is an inactive character's.
            app.SimulateKey(Alt(1));
            app.RenderNextFrame();

            var digit = int.Parse(CharacterPane(app, name)![1..]);
            app.SimulateKey(Alt(digit));

            await Assert.That(app.ActiveSessionKey)
                .IsEqualTo(key)
                .Because($"the rail said {name} was in pane {digit}");
        }
    }

    /// <summary>
    /// It survives the character switch it enables: after ⌥3 the rail still names all three, with the
    /// marker moved. A column that only rendered for characters other than the active one would be a
    /// third thing to learn and would change the rows' widths on every switch.
    /// </summary>
    [Test]
    public async Task TheColumnIsStillThereAfterSwitching()
    {
        var app = await ThreePanes();
        app.SimulateKey(Alt(3));
        app.RenderNextFrame();

        await Assert.That(app.ActiveSessionKey).IsEqualTo("Cara.Cal");
        await Assert.That(CharacterPane(app, "Ann")).IsEqualTo("⌥" + "1");
        await Assert.That(CharacterPane(app, "Bob")).IsEqualTo("⌥" + "2");
        await Assert.That(CharacterPane(app, "Cal")).IsEqualTo("⌥" + "3");
    }

    /// <summary>
    /// <b>And switching character moves no pane rectangle.</b> The rail's width is its widest row and the
    /// panes get what is left, so a column that changed width as the active character moved would
    /// re-announce a new terminal size to every connected server over per-pane NAWS — the reason
    /// <c>FocusIndicationTests.MovingFocusDoesNotMoveAnyPaneRectangle</c> exists, restated for the row this
    /// change writes to.
    /// </summary>
    [Test]
    public async Task SwitchingCharacterMovesNoPaneRectangle()
    {
        var app = await ThreePanes();
        app.SimulateKey(Alt(1));
        app.RenderNextFrame();
        var before = app.PaneOutputRects().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        foreach (var digit in new[] { 2, 3, 1 })
        {
            app.SimulateKey(Alt(digit));
            app.RenderNextFrame();

            var after = app.PaneOutputRects();
            await Assert.That(after.Count).IsEqualTo(before.Count);
            foreach (var (paneId, rect) in before)
            {
                await Assert.That(after[paneId]).IsEqualTo(rect);
            }
        }
    }

    /// <summary>
    /// <b>Closing pane 2 of three makes the third pane into pane 2, on the chord and in the sidebar
    /// together.</b> Creation sequences are never reused, so a number read straight off one would leave a
    /// hole here: ⌥2 would report "there is no pane 2" while two panes sat on the screen, and ⌥3 would be
    /// the only way to reach the second of them. The number is the pane's position in the numbering for
    /// exactly this reason, and the two surfaces are asserted together because a chord that disagrees with
    /// the label is the defect this whole numbering exists to avoid.
    /// </summary>
    [Test]
    public async Task ClosingAPaneCompactsTheNumberingOnTheChordAndInTheSidebar()
    {
        var app = await ThreePanes();

        app.SimulateKey(Alt(2));                                   // stand in Bob's pane
        await Assert.That(app.DispatchCommand("layout:close")).IsTrue();
        app.RenderNextFrame();
        await Assert.That(app.PaneIds.Count).IsEqualTo(2);

        await Assert.That(CharacterPane(app, "Ann")).IsEqualTo("⌥" + "1");
        await Assert.That(CharacterPane(app, "Cal"))
            .IsEqualTo("⌥" + "2")
            .Because("the panes on the screen must be numbered 1 and 2, not 1 and 3");

        app.SimulateKey(Alt(1));
        app.SimulateKey(Alt(2));
        await Assert.That(app.ActiveSessionKey)
            .IsEqualTo("Cara.Cal")
            .Because("⌥2 must reach the second pane rather than the hole the closed one left");

        app.SimulateKey(Alt(3));
        await Assert.That(app.StatusMarkup).Contains("there is no pane 3");
        await Assert.That(app.ActiveSessionKey).IsEqualTo("Cara.Cal");
    }

    /// <summary>
    /// <b>A single-pane workspace prints no pane column at all</b> — on character rows for the same reason
    /// window rows have never had one there: with one pane there is one answer, and three cells of sidebar
    /// come out of the pane the user is reading.
    /// </summary>
    [Test]
    public async Task ASinglePaneWorkspaceNamesNoPaneOnAnyRow()
    {
        Console.SetIn(TextReader.Null);
        var app = new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(120, 34));
        app.RenderSnapshot();

        await Assert.That(app.PaneIds.Count).IsEqualTo(1);
        await Assert.That(app.RailLines.Any(l => Regex.IsMatch(l, @"⌥\d+")))
            .IsFalse()
            .Because("with one pane, naming it says nothing and costs the panes their columns");
    }

    /// <summary>
    /// The rail's answer for a character and the app's own <c>pane N</c> label for the pane holding that
    /// character's window are the same string — one numbering, read two ways, so a future change to either
    /// cannot quietly produce two.
    /// </summary>
    [Test]
    public async Task TheRailsColumnAgreesWithTheAppsOwnLabelForTheSamePane()
    {
        var app = await ThreePanes();
        app.RenderNextFrame();

        foreach (var (name, window) in new[] { ("Ann", "main"), ("Bob", "char:Bravo.Bob"), ("Cal", "char:Cara.Cal") })
        {
            var paneId = app.PaneIdOf(window);
            var ordinal = app.PaneIds.ToList().IndexOf(paneId!) + 1;
            await Assert.That(CharacterPane(app, name)).IsEqualTo($"⌥{ordinal}");
        }
    }

    // --- harness ------------------------------------------------------------------------------------

    /// <summary>
    /// The <c>pane N</c> the rail prints on <paramref name="character"/>'s own row, or null.
    /// <para>
    /// Read off the row's <em>visible</em> cells, with the markup stripped first. A rail row is wrapped in
    /// a <c>[link=cmd%3Acharacter%3AAlfa.Ann]</c> span, and a world row's target is one of its characters'
    /// — so matching the raw markup finds "Ann" on the <c>Alfa</c> row above hers, which has no pane and
    /// never should. Character rows are then told apart from window rows by the <c>▪</c> bullet the latter
    /// carry, both now using this same column.
    /// </para>
    /// </summary>
    private static string? CharacterPane(SharpMUTermApp app, string character)
    {
        foreach (var line in app.RailLines.Select(Visible))
        {
            if (line.Contains('▪', StringComparison.Ordinal) ||
                !Regex.IsMatch(line, $@"(?<![A-Za-z]){Regex.Escape(character)}(?![A-Za-z])"))
            {
                continue;
            }

            var match = Regex.Match(line, @"⌥\d+");
            return match.Success ? match.Value : null;
        }

        throw new InvalidOperationException(
            $"no rail row for {character}: {string.Join(" / ", app.RailLines.Select(Visible))}");
    }

    /// <summary>A rail row's cells, with its style and link markup removed.</summary>
    private static string Visible(string markup) =>
        Regex.Replace(markup, @"\[(?:/|[^\]\[]*)\]", string.Empty).Replace("[[", "[").Replace("]]", "]");

    private static ConsoleKeyInfo Alt(int digit) =>
        new((char)('0' + digit), ConsoleKey.D0 + digit, false, true, false);

    /// <summary>
    /// A resumed workspace of three panes holding one character each, from three separate worlds. Same
    /// shape as <see cref="PaneJumpTests"/>'s fixture, which is the suite this one is the sidebar half of.
    /// </summary>
    private static async Task<SharpMUTermApp> ThreePanes()
    {
        Console.SetIn(TextReader.Null);
        var config = new AppConfiguration();
        var names = new[] { ("Alfa", "Ann"), ("Bravo", "Bob"), ("Cara", "Cal") };
        foreach (var (world, character) in names)
        {
            var definition = new WorldDefinition
            {
                Name = world,
                Host = $"{world.ToLowerInvariant()}.example.org",
                Port = 4000,
            };
            definition.Characters.Add(new CharacterDefinition { Name = character, Logging = new LoggingSettings() });
            config.Worlds.Add(definition);
        }

        var windows = new[] { "main", "char:Bravo.Bob", "char:Cara.Cal" };
        var sessions = names.Select(n => $"{n.Item1}.{n.Item2}").ToArray();

        config.LastSession = new WorkspaceState
        {
            Windows =
            {
                new WorkspaceWindowState
                {
                    Id = windows[0], Title = "Ann", Kind = WindowKind.Main, SessionKey = sessions[0],
                },
                new WorkspaceWindowState
                {
                    Id = windows[1], Title = "Bob", Kind = WindowKind.Main, SessionKey = sessions[1],
                },
                new WorkspaceWindowState
                {
                    Id = windows[2], Title = "Cal", Kind = WindowKind.Main, SessionKey = sessions[2],
                },
            },
            Root = new LayoutNodeState
            {
                Type = "split",
                Direction = SplitDirection.Row,
                Children =
                {
                    new LayoutNodeState { Type = "pane", Id = "p1", Tabs = { windows[0] }, ActiveIndex = 0, Sequence = 1 },
                    new LayoutNodeState { Type = "pane", Id = "p2", Tabs = { windows[1] }, ActiveIndex = 0, Sequence = 2 },
                    new LayoutNodeState { Type = "pane", Id = "p3", Tabs = { windows[2] }, ActiveIndex = 0, Sequence = 3 },
                },
            },
            FocusedPaneId = "p1",
        };

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(160, 40));
        app.TelnetFactory = _ => new RecordingTelnetSession();
        foreach (var key in sessions)
        {
            if (!app.DispatchCommand(CommandIds.Character(key)))
            {
                throw new InvalidOperationException($"the app would not switch to {key}");
            }

            await app.FindSession(key)!.ConnectAsync();
        }

        app.RenderNextFrame();
        if (app.PaneIds.Count != 3)
        {
            throw new InvalidOperationException($"the resumed workspace has {app.PaneIds.Count} panes, not 3");
        }

        return app;
    }
}
