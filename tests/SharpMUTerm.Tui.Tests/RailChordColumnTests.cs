using System.Text.RegularExpressions;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// <b>The chord column, and the width invariant it must not break.</b>
/// <para>
/// The reported complaint was the gap: "there is still way too much room after a window's name on the
/// left sidebar, before it hits 'alt-1' for instance, taking up too much room that could be spent on
/// other things." The five cells between the two were the reserved unsent-pen and unread-badge fields
/// (<c>RailRenderer.UnsentFieldWidth</c> / <c>UnreadFieldWidth</c>), which are blank far more often than
/// not — and which <b>cannot simply be removed</b>: a cell that appears only when it has something to say
/// widens the sidebar, the sidebar's width comes out of the pane area, and per-pane NAWS then re-announces
/// a new terminal size to every connected server. That is the reported "the screen jumps when I start
/// typing" bug and it is not being reintroduced to save three cells.
/// </para>
/// <para>
/// So the chord moved to the <em>front</em> of the row instead, where nothing blank separates it from the
/// name it belongs to, and the badges sit at the right edge where status belongs. The row's measured width
/// is unchanged by the move. This suite pins both halves: the gap is gone, and the width still does not
/// move on anything a keystroke or a line of output can do.
/// </para>
/// </summary>
/// <remarks>
/// Serialised with the other end-to-end suites: constructing the app and rendering a frame both touch the
/// process-global console streams.
/// </remarks>
[NotInParallel]
public class RailChordColumnTests
{
    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    // --- the complaint ------------------------------------------------------------------------------

    /// <summary>
    /// <b>Nothing sits between a window's name and its chord.</b> Read off the rendered rail rather than
    /// the markup: the assertion is about cells on a screen.
    /// </summary>
    [Test]
    public async Task TheChordSitsAgainstTheWindowsNameWithNoGap()
    {
        var app = Demo();
        app.RenderSnapshot();

        var rows = app.RailLines.Select(Visible).Where(l => l.Contains('▪', StringComparison.Ordinal)).ToList();
        await Assert.That(rows).IsNotEmpty();

        foreach (var row in rows)
        {
            var match = Regex.Match(row, @"⌥\d(\s*)▪");
            await Assert.That(match.Success)
                .IsTrue()
                .Because($"the chord must lead the row it names, and this one reads '{row.Trim()}'");
            await Assert.That(match.Groups[1].Value.Length)
                .IsEqualTo(1)
                .Because("one space separates the chord from the bullet; the badges live at the far end");
        }
    }

    /// <summary>
    /// And the badges are still there, at the end — this is a reordering, not a removal. A row that lost
    /// its pen or its count would pass the gap assertion above and be a worse rail.
    /// </summary>
    [Test]
    public async Task TheBadgesSurviveTheReordering()
    {
        var app = Demo();
        app.RenderSnapshot();

        var rows = app.RailLines.Select(Visible).Where(l => l.Contains('▪', StringComparison.Ordinal)).ToList();
        await Assert.That(rows.Any(r => r.Contains(Glyphs.Draft, StringComparison.Ordinal)))
            .IsTrue()
            .Because("the demo leaves a draft in the main window");
        await Assert.That(rows.Any(r => Regex.IsMatch(r, @"\d\s*$")))
            .IsTrue()
            .Because("the demo's Chat window has unread lines");
    }

    // --- the invariant ------------------------------------------------------------------------------

    /// <summary>
    /// <b>The rail's width does not move on an unread count, at any of the sizes where a reserved field
    /// could burst.</b> 0 → 1 is the badge appearing, 9 → 10 the second digit, 99 → 100 the cap. Each
    /// arrives <em>unbidden from the wire</em>, which is what makes this the worse of the two fields: the
    /// sidebar would narrow every pane on output the reader never asked for.
    /// <para>
    /// Asserted on the sidebar's own column count <em>and</em> on the pane rectangles, because the column
    /// is the cause and the rectangles are what is reported over NAWS — the same pairing
    /// <c>TabActivityIndicatorTests.ActivityMovesNoPaneRectangle</c> uses for the tab strip.
    /// </para>
    /// </summary>
    [Test]
    public async Task AnUnreadCountMovesNeitherTheRailNorAnyPaneRectangle()
    {
        var wired = await Wired();
        wired.App.RenderNextFrame();

        var railBefore = wired.App.RailColumnWidth;
        var before = wired.App.PaneOutputRects().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        var delivered = 0;
        foreach (var target in new[] { 1, 9, 10, 99, 100, 130 })
        {
            while (delivered < target)
            {
                wired.Telnet.Receive("<Trade> a lamp is offered\n");
                delivered++;
            }

            wired.App.RenderNextFrame();

            await Assert.That(wired.App.RailColumnWidth)
                .IsEqualTo(railBefore)
                .Because($"{delivered} unread must not widen the sidebar");
            foreach (var (paneId, rect) in before)
            {
                await Assert.That(wired.App.PaneOutputRects()[paneId])
                    .IsEqualTo(rect)
                    .Because($"{delivered} unread must not re-announce a pane size");
            }
        }

        // And the badge really did grow past its cap, so this cannot have passed by never rendering one.
        await Assert.That(wired.App.RailLines.Any(l => l.Contains("99+", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// The same for a draft: the ✎ appears on the first keystroke of every line, which is the moment the
    /// original defect was reported at.
    /// </summary>
    [Test]
    public async Task ADraftMovesNeitherTheRailNorAnyPaneRectangle()
    {
        var wired = await Wired();
        wired.App.RenderNextFrame();

        var railBefore = wired.App.RailColumnWidth;
        var before = wired.App.PaneOutputRects().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        foreach (var c in "hello there")
        {
            wired.App.SimulateKey(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
            wired.App.RenderNextFrame();

            await Assert.That(wired.App.RailColumnWidth).IsEqualTo(railBefore);
            foreach (var (paneId, rect) in before)
            {
                await Assert.That(wired.App.PaneOutputRects()[paneId]).IsEqualTo(rect);
            }
        }

        await Assert.That(wired.App.RailLines.Any(l => l.Contains(Glyphs.Draft, StringComparison.Ordinal)))
            .IsTrue()
            .Because("the pen must really have appeared, or this passed by toggling nothing");
    }

    /// <summary>
    /// <b>The ⌥J/⌥K pair travelling from row to row does not change any row's width.</b> That column is
    /// new, it is on character rows, and the two chords land on different rows after every switch — a
    /// field that changes what it holds on a plain keystroke, which is exactly the shape the reserved
    /// fields exist to contain.
    /// <para>
    /// Asserted at the renderer, on one set of rows with the chords on one pair and another with them on
    /// a different pair, because that isolates the claim. The <em>sidebar's</em> width is legitimately a
    /// function of who is active — window rows are drawn for the active character only, so switching
    /// changes which rows exist at all — and a test that pinned the column count across a character
    /// switch would be pinning that unrelated fact and would fail for the right reason.
    /// </para>
    /// </summary>
    [Test]
    public async Task TheCycleChordsMovingBetweenRowsChangesNoRowsWidth()
    {
        var onFirstPair = Rail(("Ann", null), ("Bob", "⌥J"), ("Cal", "⌥K"));
        var onSecondPair = Rail(("Ann", "⌥K"), ("Bob", null), ("Cal", "⌥J"));

        var a = RailRenderer.Render(onFirstPair).Select(SharpMUTermApp.MarkupWidth).ToList();
        var b = RailRenderer.Render(onSecondPair).Select(SharpMUTermApp.MarkupWidth).ToList();

        await Assert.That(b).IsEquivalentTo(a);
        await Assert.That(RailRenderer.Render(onFirstPair).Any(l => l.Contains("⌥J", StringComparison.Ordinal)))
            .IsTrue()
            .Because("the chords must really be drawn, or this passed by rendering nothing");
    }

    /// <summary>
    /// <b>A client with nothing to put in a column does not pay for it.</b> The field is reserved per row
    /// <em>kind</em>: with fewer than two characters open no character row can carry a cycle chord, and
    /// reserving across both kinds spent three cells on every character row of the commonest client there
    /// is. Measured as a width, because that is what it costs.
    /// </summary>
    [Test]
    public async Task ARailWithNoCharacterChordsDoesNotReserveTheColumnOnCharacterRows()
    {
        var app = Demo();
        app.RenderSnapshot();

        // The demo has one character's windows and no second character open: window rows carry chords,
        // character rows cannot.
        await Assert.That(app.RailLines.Any(l => Regex.IsMatch(Visible(l), @"⌥\d"))).IsTrue();
        await Assert.That(app.RailLines.Any(l => Regex.IsMatch(Visible(l), @"⌥[JK]"))).IsFalse();

        foreach (var row in app.RailLines.Select(Visible))
        {
            if (row.Contains('▪', StringComparison.Ordinal) || !Regex.IsMatch(row, @"[●○]"))
            {
                continue;
            }

            // Four of indent, then the active marker (a glyph on the active row, a blank on the others)
            // and one space. A reserved chord field would put three more in front of all of them.
            await Assert.That(Regex.Match(row, @"^\s*").Value.Length)
                .IsLessThanOrEqualTo(6)
                .Because($"a character row with no chord to hold pays nothing for the column: '{row}'");
        }
    }

    // --- harness ------------------------------------------------------------------------------------

    private static SharpMUTermApp Demo(int width = 120, int height = 34)
    {
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(width, height));
    }

    /// <summary>A rail row's cells, with its style and link markup removed.</summary>
    private static string Visible(string markup) =>
        Regex.Replace(markup, @"\[(?:/|[^\]\[]*)\]", string.Empty).Replace("[[", "[").Replace("]]", "]");

    /// <summary>Three character rows under one world, each with the chord it is given.</summary>
    private static IReadOnlyList<RailRow> Rail(params (string Name, string? Chord)[] characters) =>
        RailModel.Build(new[]
        {
            new RailWorld("Alfa", "h", 1, default, characters.Select(c => new RailCharacter(
                c.Name, $"Alfa.{c.Name}", Connected: true, Active: false, 0,
                Array.Empty<RailWindow>(), c.Chord)).ToList()),
        });

    private sealed record WiredApp(SharpMUTermApp App, RecordingTelnetSession Telnet)
    {
        /// <summary>Switches to a character the way ⌃P and the rail do, and connects it.</summary>
        internal async Task Open(string sessionKey)
        {
            App.TelnetFactory = _ => new RecordingTelnetSession();
            if (!App.DispatchCommand(CommandIds.Character(sessionKey)))
            {
                throw new InvalidOperationException($"the app would not switch to {sessionKey}");
            }

            await App.FindSession(sessionKey)!.ConnectAsync();
        }
    }

    /// <summary>
    /// Three characters over three worlds, one connected over a recording transport with a live capture
    /// rule — so unread can be made to arrive the way it really does, off the wire and into a background
    /// window, rather than by poking a counter.
    /// </summary>
    private static async Task<WiredApp> Wired()
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
                    Name = "Trade",
                    Pattern = "^<Trade>",
                    Actions = new TriggerActions { SpawnTarget = "Trade" },
                },
            },
        });

        foreach (var (world, character) in new[] { ("Alfa", "Ann"), ("Bravo", "Bob"), ("Cara", "Cal") })
        {
            var definition = new WorldDefinition
            {
                Name = world,
                Host = $"{world.ToLowerInvariant()}.example.org",
                Port = 4000,
            };
            definition.Characters.Add(new CharacterDefinition
            {
                Name = character,
                Logging = new LoggingSettings(),
                TriggerSets = { "Comms" },
            });
            config.Worlds.Add(definition);
        }

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(120, 34));
        var telnet = new RecordingTelnetSession();
        app.TelnetFactory = _ => telnet;
        if (!app.DispatchCommand(CommandIds.Character("Alfa.Ann")))
        {
            throw new InvalidOperationException("the app would not switch to Alfa.Ann");
        }

        await app.FindSession("Alfa.Ann")!.ConnectAsync();

        // A background capture window, so the unread the test drives lands somewhere the reader is not
        // looking — which is the only state a badge is drawn in.
        telnet.Receive("<Trade> opening\n");
        app.RenderNextFrame();

        return new WiredApp(app, telnet);
    }
}
