using System.Text.RegularExpressions;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// <b>⌃B 1–⌃B 9 go to a numbered pane and bring it to the front.</b> This chord was ⌥1–⌥9 until that was
/// given to <em>windows</em> (see <see cref="WindowJumpTests"/> for why); a pane and a window are
/// different destinations and one key cannot mean both. These are the claims the move leaves standing.
/// <para>
/// <b>1. It is kept rather than dropped.</b> Every pane is reachable by ⌥N through whatever window it
/// holds, so this is not the only route there — but the pane <em>numbering</em> does not go away with the
/// chord. Move mode badges each pane with its digit, the move prompt and the drag overlay both say
/// <c>pane 2</c>, the ⌃P entry says <c>Go to pane 2</c>, and ⌃O counts in the same order. A numbering the
/// client prints and asks you to press inside a mode, with no key outside that mode that acts on it,
/// would be a numbering that only half exists. It is also the one motion that moves to a pane
/// <em>without</em> naming what is in it: the ordinal member of the ⌃O / ⌃arrow family.
/// </para>
/// <para>
/// <b>2. It is on ⌃B because that is where the pane keymap lives</b> — split, zoom, close, cycle, move,
/// freeze, rail. The digits were the one part of that keymap nothing claimed, so it costs no key, and the
/// which-key panel lists it beside the others rather than leaving it to be found.
/// </para>
/// <para>
/// <b>3. The number on the screen is the number pressed.</b> Panes are counted in <c>Layout.Panes</c>
/// order (creation order), which is what move mode badges and what the ⌃P entry names, so the assertions
/// drive the overlay and read its words rather than writing down which pane ought to be third.
/// </para>
/// <para>
/// <b>4. Arrival is unchanged.</b> Full activation of the pane's own window, the session and command line
/// following, the zoom carried, and an out-of-range digit reported rather than swallowed.
/// </para>
/// </summary>
/// <remarks>
/// Serialised with the other end-to-end suites: constructing the app and rendering a frame both touch the
/// process-global console streams.
/// </remarks>
[NotInParallel]
public class PaneJumpTests
{
    private const int Width = 160;
    private const int Height = 40;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    // --- the numbering ------------------------------------------------------------------------------

    /// <summary>
    /// <b>The claim, end to end.</b> Three panes, three characters. For every digit: ⌃B N, and the pane
    /// whose plane the frame paints as focused is the pane move mode calls <c>pane N</c> — with that
    /// pane's window active and its character on the command line.
    /// <para>
    /// The label comes out of the real move overlay rather than being written down here, so the two
    /// surfaces are held against each other. A chord that lands somewhere other than the label says is
    /// worse than no chord.
    /// </para>
    /// </summary>
    [Test]
    public async Task EachDigitLandsOnThePaneTheMoveOverlayNumbersWithIt()
    {
        var three = await ThreePanes();
        var (focused, _) = three.App.PaneBandColors;
        var rects = three.App.PaneOutputRects();

        for (var n = 1; n <= 3; n++)
        {
            Prefix(three.App, n);
            var frame = three.App.RenderWholeFrame();

            var landed = three.App.FocusedPaneId;

            // The session and the window, so the command line is talking to the pane you are looking at.
            await Assert.That(three.App.ActiveSessionKey).IsEqualTo(three.Sessions[n - 1]);
            await Assert.That(three.App.ActiveWindowId()).IsEqualTo(three.Windows[n - 1]);

            // And the paint: this pane's rectangle carries the focused plane and no other one does.
            await Assert.That(CellsPaintedIn(frame, rects[landed], focused))
                .IsGreaterThan(0)
                .Because($"⌃B {n} must paint pane {n} as the focused one");
            foreach (var other in three.App.PaneIds.Where(id => id != landed))
            {
                await Assert.That(CellsPaintedIn(frame, rects[other], focused)).IsEqualTo(0);
            }

            // And the overlay, which is where a user reads the pane's number, targets the same pane with
            // the same digit — driven through a real commit, so the answer is the id the overlay stored.
            var (target, prompt) = MoveOverlayTarget(three.App, n);
            await Assert.That(target)
                .IsEqualTo(landed)
                .Because($"⌃B {n} must land on the pane the overlay picks for {n}");
            await Assert.That(prompt)
                .Contains($"pane {n}")
                .Because("the prompt's words and the digit it accepted are one number");
        }
    }

    /// <summary>
    /// The pane label the ⌃P surface offers names this chord and not the old one — the entry is a second
    /// door onto ⌃B N, and an entry still saying ⌥2 would send a reader to a key that now goes to a
    /// window. Both routes are driven and reach the same pane.
    /// </summary>
    [Test]
    public async Task TheCommandSurfaceOffersOneEntryPerPaneAndNamesTheNewChord()
    {
        var three = await ThreePanes();

        var entries = three.App.BuildCatalog()
            .Where(c => c.Id.StartsWith(CommandIds.PanePrefix, StringComparison.Ordinal))
            .ToList();
        await Assert.That(entries.Count).IsEqualTo(3);

        for (var n = 1; n <= 3; n++)
        {
            var entry = entries.Single(e => e.Id == CommandIds.Pane(n));
            await Assert.That(entry.Title).IsEqualTo($"Go to pane {n}");
            await Assert.That(entry.Subtitle)
                .IsEqualTo($"⌃B {n}")
                .Because("an entry that named the wrong chord would be worse than a bare one");

            // Both routes reach the same pane.
            Prefix(three.App, 1);
            Prefix(three.App, n);
            var viaKey = three.App.FocusedPaneId;

            Prefix(three.App, 1);
            await Assert.That(three.App.DispatchCommand(CommandIds.Pane(n))).IsTrue();
            await Assert.That(three.App.FocusedPaneId).IsEqualTo(viaKey);
        }
    }

    /// <summary>
    /// A single-pane workspace lists none of them. The directional entries are listed unconditionally
    /// because they teach that the workspace splits at all; <c>Go to pane 4</c> on a workspace with one
    /// pane teaches nothing and names a place there is no way to make.
    /// </summary>
    [Test]
    public async Task ASinglePaneWorkspaceOffersNoNumberedEntries()
    {
        var app = App();
        app.RenderSnapshot();

        await Assert.That(app.PaneIds.Count).IsEqualTo(1);
        await Assert.That(app.BuildCatalog().Any(c => c.Id.StartsWith(CommandIds.PanePrefix, StringComparison.Ordinal)))
            .IsFalse();
    }

    /// <summary>
    /// <b>Closing pane 2 of three makes the third pane into pane 2, on the chord and in the overlay
    /// together.</b> Creation sequences are never reused, so a number read straight off one would leave a
    /// hole here: ⌃B 2 would report "there is no pane 2" while two panes sat on the screen. The number is
    /// the pane's position in the numbering for exactly this reason.
    /// </summary>
    [Test]
    public async Task ClosingAPaneCompactsTheNumberingOnTheChordAndInTheOverlay()
    {
        var three = await ThreePanes();

        Prefix(three.App, 2);                                      // stand in Bob's pane
        await Assert.That(three.App.DispatchCommand("layout:close")).IsTrue();
        three.App.RenderNextFrame();
        await Assert.That(three.App.PaneIds.Count).IsEqualTo(2);

        var entries = three.App.BuildCatalog()
            .Where(c => c.Id.StartsWith(CommandIds.PanePrefix, StringComparison.Ordinal))
            .Select(c => c.Id)
            .ToList();
        await Assert.That(entries)
            .IsEquivalentTo(new[] { CommandIds.Pane(1), CommandIds.Pane(2) })
            .Because("the panes on the screen must be numbered 1 and 2, not 1 and 3");

        Prefix(three.App, 1);
        Prefix(three.App, 2);
        await Assert.That(three.App.ActiveSessionKey)
            .IsEqualTo("Cara.Cal")
            .Because("⌃B 2 must reach the second pane rather than the hole the closed one left");

        Prefix(three.App, 3);
        await Assert.That(three.App.StatusMarkup).Contains("there is no pane 3");
        await Assert.That(three.App.ActiveSessionKey).IsEqualTo("Cara.Cal");
    }

    // --- out of range: report, never a silent no-op -------------------------------------------------

    /// <summary>
    /// <b>⌃B 7 with three panes says so, and names the chord it is answering.</b> A silent no-op is the
    /// most-repeated defect in this codebase's history, and a digit with no pane behind it is the
    /// commonest way to press this wrong. Nothing moves.
    /// </summary>
    [Test]
    public async Task AnOutOfRangeDigitReportsAndMovesNothing()
    {
        var three = await ThreePanes();
        Prefix(three.App, 2);
        var pane = three.App.FocusedPaneId;
        var session = three.App.ActiveSessionKey;

        foreach (var digit in new[] { 4, 7, 9 })
        {
            Prefix(three.App, digit);

            await Assert.That(three.App.StatusMarkup).Contains($"there is no pane {digit}");
            await Assert.That(three.App.StatusMarkup).Contains("3");
            await Assert.That(three.App.StatusMarkup)
                .Contains("⌃B")
                .Because("the notice names the chord that was pressed, and ⌥ is no longer it");
            await Assert.That(three.App.FocusedPaneId).IsEqualTo(pane);
            await Assert.That(three.App.ActiveSessionKey).IsEqualTo(session);
        }
    }

    /// <summary>
    /// On a workspace with one pane every digit past the first is out of range, and the refusal says the
    /// useful thing instead of counting: how to make a second pane. The same sentence ⌃← gives.
    /// </summary>
    [Test]
    public async Task OnOnePaneTheRefusalSaysHowToSplit()
    {
        var app = App();
        app.RenderSnapshot();

        Prefix(app, 2);

        await Assert.That(app.StatusMarkup).Contains("one pane");
        await Assert.That(app.StatusMarkup).Contains("⌃B |");
    }

    // --- what the move off ⌥ must not have broken ---------------------------------------------------

    /// <summary>
    /// <b>The digits are not global shortcuts, and must not become them.</b> ⌃B 1 is a key on the prefix
    /// keymap, consumed by the armed prefix; a bare <c>1</c> with no prefix is typing, which is what F4
    /// reports and what the command line receives. Registering these as application shortcuts would take
    /// the digit row away from the prompt entirely.
    /// </summary>
    [Test]
    public async Task ABareDigitStillTypesRatherThanJumping()
    {
        var app = App();
        app.RenderSnapshot("split");
        var pane = app.FocusedPaneId;

        app.SimulateKey(Plain('2', ConsoleKey.D2));

        await Assert.That(app.ArmedInputText).Contains("2");
        await Assert.That(app.FocusedPaneId).IsEqualTo(pane);
        await Assert.That(MacroKeys.Verdict("2").Delivery).IsEqualTo(MacroKeyDelivery.Taken);
        await Assert.That(MacroKeys.AppShortcuts.Any(s => s.Modifiers == 0 && s.Key == ConsoleKey.D2)).IsFalse();
    }

    /// <summary>
    /// <b>The which-key panel lists it.</b> This surface is where the ⌃B keymap is discovered from, and a
    /// chord it does not name is a chord nobody finds — the state ⌃L's newline sat in until it was
    /// reported as missing. It is blocked on the same fact as zoom and cycle: with one pane there is
    /// nowhere to go.
    /// </summary>
    [Test]
    public async Task ThePrefixPanelNamesTheNumberedPaneJump()
    {
        var onOne = PrefixPanel.Entries(PrefixFacts.Fresh).Single(e => e.Keys == "1–9");
        await Assert.That(onOne.Title).Contains("numbered pane");
        await Assert.That(onOne.Available)
            .IsFalse()
            .Because("with one pane it can do nothing, and a panel listing it as live sends a reader to press it");

        var onTwo = PrefixPanel.Entries(PrefixFacts.Fresh with { PaneCount = 2 }).Single(e => e.Keys == "1–9");
        await Assert.That(onTwo.Available).IsTrue();

        await Assert.That(PrefixPanel.StripKeys).Contains("1–9");
    }

    /// <summary>
    /// <b>A zoom follows the jump.</b> A zoomed workspace realises exactly one pane, so a mover that
    /// changed the selection and left the zoom where it was would put the selection, the session and the
    /// caret on a pane that is not on the screen. ⌃B 2 over a zoomed pane 1 therefore shows pane 2 zoomed,
    /// and ⌃B z still un-zooms.
    /// </summary>
    [Test]
    public async Task JumpingWhileZoomedBringsTheTargetToTheFrontRatherThanHidingIt()
    {
        var app = App();
        app.RenderSnapshot("split");
        var first = app.FocusedPaneId;
        var second = app.PaneIds.Single(id => id != first);

        await Assert.That(app.DispatchCommand("layout:zoom")).IsTrue();
        await Assert.That(app.ZoomedPaneId).IsEqualTo(first);

        Prefix(app, 2);
        var frame = app.RenderWholeFrame();

        await Assert.That(app.FocusedPaneId).IsEqualTo(second);
        await Assert.That(app.ZoomedPaneId)
            .IsEqualTo(second)
            .Because("the pane jumped to has to be the one that is rendered");

        // And it is genuinely still a zoom: one pane is realised, and it is the one selected.
        var rects = app.PaneOutputRects();
        await Assert.That(rects.ContainsKey(second)).IsTrue();
        await Assert.That(rects.ContainsKey(first)).IsFalse();

        var (focused, _) = app.PaneBandColors;
        await Assert.That(CellsPaintedIn(frame, rects[second], focused)).IsGreaterThan(0);
    }

    /// <summary>
    /// ⌃O is the other ordinal pane mover and counts the same order, so the two cannot come to mean
    /// different things — three presses of ⌃O from pane 1 land where ⌃B 4 does. It also still carries a
    /// zoom; it used to cycle the selection out from under one and leave it invisible.
    /// </summary>
    [Test]
    public async Task CyclingCountsTheSameOrderAsTheChordAndCarriesTheZoomToo()
    {
        var three = await ThreePanes();

        Prefix(three.App, 1);
        three.App.SimulateKey(Ctrl(ConsoleKey.O));
        three.App.SimulateKey(Ctrl(ConsoleKey.O));
        var viaCycle = three.App.FocusedPaneId;

        Prefix(three.App, 3);
        await Assert.That(three.App.FocusedPaneId)
            .IsEqualTo(viaCycle)
            .Because("the two ordinal pane movers must count one sequence");

        var app = App();
        app.RenderSnapshot("split");
        var first = app.FocusedPaneId;
        await Assert.That(app.DispatchCommand("layout:zoom")).IsTrue();
        await Assert.That(app.DispatchCommand("layout:cycle")).IsTrue();

        await Assert.That(app.FocusedPaneId).IsNotEqualTo(first);
        await Assert.That(app.ZoomedPaneId).IsEqualTo(app.FocusedPaneId);
    }

    // --- one spelling of one pane -------------------------------------------------------------------

    /// <summary>
    /// <b>The move overlay and the ⌃P entry call the first pane the same thing.</b> The overlay used to
    /// call it <c>main</c> — the spelling the rail abandoned because it collided with the <em>window</em>
    /// named main — so the same pane was <c>pane 1</c> in one place and <c>main</c> under the cursor.
    /// ⌃B 1 is a chord that lands on a pane a label names, and it cannot survive two labels.
    /// <para>
    /// The sidebar is the other half now, and it is asserted the other way round: it prints <c>⌥N</c>,
    /// which is the <em>window</em> numbering, and must never print <c>pane N</c> — two numberings sharing
    /// one vocabulary is the thing that would make either chord unreadable.
    /// </para>
    /// </summary>
    [Test]
    public async Task TheMoveOverlaySpellsTheFirstPanePaneOneAndTheSidebarNeverSaysPane()
    {
        var app = App();
        app.RenderSnapshot("split");

        // ⌃B m lifts the active window; '1' targets the first pane — the same digit ⌃B 1 uses.
        app.SimulateKey(Ctrl(ConsoleKey.B));
        app.SimulateKey(Plain('m', ConsoleKey.M));
        app.SimulateKey(Plain('1', ConsoleKey.D1));

        await Assert.That(app.StatusMarkup).Contains("pane 1");
        await Assert.That(app.StatusMarkup)
            .DoesNotContain("main")
            .Because("the first pane is pane 1 everywhere, or ⌃B 1 names something the screen does not");

        app.SimulateKey(Plain('\x1b', ConsoleKey.Escape)); // leave move mode

        await Assert.That(app.RailLines.Any(l => l.Contains("pane ", StringComparison.Ordinal)))
            .IsFalse()
            .Because("the sidebar numbers windows, and a pane noun there would be a second reading of ⌥N");
        await Assert.That(app.RailLines.Any(l => Regex.IsMatch(l, @"⌥\d"))).IsTrue();
    }

    /// <summary>
    /// <b>Move mode targets by the same number as the chord.</b> It used to letter the panes <c>a</c>–
    /// <c>j</c> while the prompt one line below named the target <c>pane N</c> — one ordering spelt in two
    /// alphabets. Driven for every pane: press the digit, and the target the prompt reports is the pane
    /// ⌃B with the same digit goes to.
    /// </summary>
    [Test]
    public async Task MoveModeTargetsThePaneTheChordGoesToWithTheSameDigit()
    {
        var three = await ThreePanes();

        for (var n = 1; n <= 3; n++)
        {
            Prefix(three.App, n);
            var viaChord = three.App.FocusedPaneId;
            var (target, prompt) = MoveOverlayTarget(three.App, n);
            await Assert.That(target)
                .IsEqualTo(viaChord)
                .Because($"pressing {n} in move mode must target the pane ⌃B {n} goes to");
            await Assert.That(prompt).Contains($"pane {n}");
        }

        // A digit past the last pane leaves the target where it was rather than clearing it.
        three.App.SimulateKey(Ctrl(ConsoleKey.B));
        three.App.SimulateKey(Plain('m', ConsoleKey.M));
        three.App.SimulateKey(Plain('3', ConsoleKey.D3));
        three.App.SimulateKey(Plain('9', ConsoleKey.D9));
        await Assert.That(three.App.StatusMarkup)
            .Contains("pane 3")
            .Because("an out-of-range digit must not drop the target you already picked");

        // And the letters are gone: 'b' is not a pane picker any more.
        three.App.SimulateKey(Plain('b', ConsoleKey.B));
        await Assert.That(three.App.StatusMarkup).Contains("pane 3");

        three.App.SimulateKey(Plain('\x1b', ConsoleKey.Escape));
    }

    // --- harness ------------------------------------------------------------------------------------

    private static SharpMUTermApp App(int width = 120, int height = 34)
    {
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(width, height));
    }

    private static ConsoleKeyInfo Ctrl(ConsoleKey key) => new('\0', key, false, false, true);

    private static ConsoleKeyInfo Plain(char c, ConsoleKey key) => new(c, key, false, false, false);

    /// <summary>The chord, as the two keystrokes it is: ⌃B and then the bare digit.</summary>
    private static void Prefix(SharpMUTermApp app, int digit)
    {
        app.SimulateKey(Ctrl(ConsoleKey.B));
        app.SimulateKey(Plain((char)('0' + digit), ConsoleKey.D0 + digit));
    }

    /// <summary>
    /// Which pane the real move overlay targets for <paramref name="digit"/>, and what its prompt calls
    /// that pane.
    /// <para>
    /// Read by driving the overlay and <em>committing</em>: ⏎ moves the active window into the pane the
    /// overlay picked, so the answer comes back through <c>PaneDrop</c> from the id the overlay stored.
    /// Nothing here indexes <c>Layout.Panes</c> — that is the list under test, and a helper that read it
    /// would agree with the chord by construction.
    /// </para>
    /// </summary>
    private static (string Pane, string Prompt) MoveOverlayTarget(SharpMUTermApp app, int digit)
    {
        var window = app.ActiveWindowId();
        app.SimulateKey(Ctrl(ConsoleKey.B));
        app.SimulateKey(Plain('m', ConsoleKey.M));
        app.SimulateKey(Plain((char)('0' + digit), ConsoleKey.D0 + digit));

        var prompt = Visible(app.StatusMarkup);
        app.SimulateKey(Plain('\r', ConsoleKey.Enter)); // commit: the window lands in the targeted pane

        return (app.PaneIdOf(window) ?? throw new InvalidOperationException($"{window} is in no pane"), prompt);
    }

    /// <summary>Markup with its style and link tags removed.</summary>
    private static string Visible(string markup) =>
        Regex.Replace(markup, @"\[(?:/|[^\]\[]*)\]", string.Empty).Replace("[[", "[").Replace("]]", "]");

    /// <summary>The truecolor background escape a colour is written as.</summary>
    private static string Sgr(SharpConsoleUI.Color color) => $"48;2;{color.R};{color.G};{color.B}";

    /// <summary>
    /// Walks a frame into a <c>{(row, column): background}</c> grid, the way a terminal walks it. Note
    /// <c>48</c> and not <c>38</c> — reading foreground here and concluding about planes is the classic
    /// mistake.
    /// </summary>
    private static Dictionary<(int Row, int Column), string?> Backgrounds(string ansi)
    {
        var cells = new Dictionary<(int, int), string?>();
        var current = (string?)null;
        var (row, column) = (0, 0);

        foreach (Match token in Regex.Matches(ansi, @"\x1b\[([0-9;]*)([A-Za-z])|([^\x1b\r\n])|(\n)"))
        {
            if (token.Groups[4].Success)
            {
                row++;
                column = 0;
                continue;
            }

            if (token.Groups[3].Success)
            {
                cells[(row, column)] = current;
                column++;
                continue;
            }

            var parameters = token.Groups[1].Value;
            switch (token.Groups[2].Value)
            {
                case "H":
                    var at = parameters.Split(';');
                    row = at[0].Length > 0 ? int.Parse(at[0]) - 1 : 0;
                    column = at.Length > 1 && at[1].Length > 0 ? int.Parse(at[1]) - 1 : 0;
                    break;
                case "m":
                    if (parameters.Length == 0 || parameters == "0" || parameters.Contains("49"))
                    {
                        current = null;
                    }

                    if (parameters.Contains("48;2;"))
                    {
                        current = parameters[parameters.IndexOf("48;2;", StringComparison.Ordinal)..];
                    }

                    break;
            }
        }

        return cells;
    }

    /// <summary>How many cells inside a rectangle are painted in a given background.</summary>
    private static int CellsPaintedIn(string ansi, PaneRect rect, SharpConsoleUI.Color colour)
    {
        var wanted = Sgr(colour);
        var cells = Backgrounds(ansi);
        var count = 0;
        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
        {
            for (var x = rect.X; x < rect.X + rect.Width; x++)
            {
                if (cells.GetValueOrDefault((y, x))?.StartsWith(wanted, StringComparison.Ordinal) == true)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Three panes side by side, one connected character each, in a known order.</summary>
    private sealed record Three(
        SharpMUTermApp App,
        IReadOnlyList<string> Windows,
        IReadOnlyList<string> Sessions);

    /// <summary>
    /// A <em>resumed</em> workspace of three panes, each holding one character's window, built the way the
    /// shell restores one. Three separate worlds so each pane holds a different session.
    /// </summary>
    private static async Task<Three> ThreePanes()
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
                    Id = windows[0], Title = "Ann", Kind = WindowKind.Main, SessionKey = sessions[0], Sequence = 1,
                },
                new WorkspaceWindowState
                {
                    Id = windows[1], Title = "Bob", Kind = WindowKind.Main, SessionKey = sessions[1], Sequence = 2,
                },
                new WorkspaceWindowState
                {
                    Id = windows[2], Title = "Cal", Kind = WindowKind.Main, SessionKey = sessions[2], Sequence = 3,
                },
            },
            Root = new LayoutNodeState
            {
                Type = "split",
                Direction = SplitDirection.Row,
                Children =
                {
                    new LayoutNodeState
                    {
                        Type = "pane", Id = "p1", Tabs = { windows[0] }, ActiveIndex = 0, Sequence = 1,
                    },
                    new LayoutNodeState
                    {
                        Type = "pane", Id = "p2", Tabs = { windows[1] }, ActiveIndex = 0, Sequence = 2,
                    },
                    new LayoutNodeState
                    {
                        Type = "pane", Id = "p3", Tabs = { windows[2] }, ActiveIndex = 0, Sequence = 3,
                    },
                },
            },
            FocusedPaneId = "p1",
        };

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
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

        // The fixture's own claim: three panes, in the order the windows were placed. Everything after
        // this reads the overlay, so a resumed layout that came back differently must fail here and not
        // silently make the assertions vacuous.
        if (app.PaneIds.Count != 3)
        {
            throw new InvalidOperationException($"the resumed workspace has {app.PaneIds.Count} panes, not 3");
        }

        for (var i = 0; i < 3; i++)
        {
            if (app.PaneIdOf(windows[i]) != app.PaneIds[i])
            {
                throw new InvalidOperationException(
                    $"{windows[i]} is in {app.PaneIdOf(windows[i])}, not the {i + 1}th pane {app.PaneIds[i]}");
            }
        }

        return new Three(app, windows, sessions);
    }
}
