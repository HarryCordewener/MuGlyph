using System.Text.RegularExpressions;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Telnet.Mssp;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The F5 ▸ <c>i</c> MSSP report: that the key reaches it, that the three states are distinguishable
/// on a rendered frame, that a report is drawn as the lists it is, and that no value a stranger can
/// send widens or breaks a row.
/// </summary>
/// <remarks>
/// Serialised where a frame is rendered, for the same reason every snapshot test in this suite is:
/// capturing a frame redirects <c>Console.Out</c>, and that is process-global.
/// </remarks>
[NotInParallel]
public class MsspScreenTests
{
    private const int Width = 120;
    private const int Height = 34;

    private static readonly DateTimeOffset Noon = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>
    /// A rendered frame, decoded to the cells the driver was handed. Assertions go through the grid and
    /// never through the raw ANSI: one drawn phrase is several SGR runs, so <c>Contains</c> on the frame
    /// string can miss text that is plainly on the screen — and, worse, can pass on text that is not.
    /// </summary>
    private static IReadOnlyList<string> Frame(string view, int width = Width, int height = Height)
    {
        Console.SetIn(TextReader.Null);
        var app = new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(width, height));
        return FrameGrid.Decode(app.RenderSnapshot(view), width, height);
    }

    /// <summary>The whole frame as one string, for the assertions that only ask whether a phrase is on it.</summary>
    private static string Screen(string view, int width = Width, int height = Height) =>
        string.Join("\n", Frame(view, width, height));

    private static WorldDefinition World() =>
        new() { Name = "Aetherfall", Host = "aetherfall.mux", Port = 4201, UseTls = true };

    private static MsspObservation Observed(MsspData report, DateTimeOffset? at = null) =>
        new("aetherfall.mux:4201", Noon, report, at ?? Noon);

    private static MsspData Report(params (string Name, string[] Values)[] entries) =>
        MsspData.From(entries.Select(e =>
            new KeyValuePair<string, IReadOnlyList<string>>(e.Name, e.Values)));

    /// <summary>
    /// A body block as plain text: markup tags removed, escaped brackets put back. The inverse of what
    /// <see cref="MarkupText.VisibleLength"/> counts, written the same way — the escapes are guarded
    /// with two characters no value can carry (control characters never survive the renderer) before
    /// the tag pattern runs.
    /// </summary>
    private static List<string> Plain(IEnumerable<string> lines) =>
        lines.Select(StripMarkup).ToList();

    private static string StripMarkup(string markup)
    {
        const char openMark = '\u0001';
        const char closeMark = '\u0002';
        var guarded = markup.Replace("[[", openMark.ToString()).Replace("]]", closeMark.ToString());
        var stripped = Regex.Replace(guarded, @"\[[^\[\]]*\]", string.Empty);
        return stripped.Replace(openMark, '[').Replace(closeMark, ']');
    }

    /// <summary>
    /// The label column of a plain row — three cells of mark and gap, then the fixed-width name field.
    /// Rows are read by column because a substring match crosses fields: <c>transport</c> contains
    /// <c>port</c>, and a test that matched the wrong row would be asserting about the wrong thing while
    /// passing.
    /// </summary>
    private static string LabelOf(string row)
    {
        const int labelAt = 3;
        return row.Length <= labelAt
            ? string.Empty
            : row[labelAt..Math.Min(row.Length, labelAt + MsspScreenRenderer.NameWidth)].Trim();
    }

    // ---- Reaching it ----

    [Test]
    public async Task TheWorldsScreenAdvertisesTheInfoKeyAndSaysWhichWorldItActsOn()
    {
        var frame = Screen("worlds");

        // Derived from the model, never written: the hint and the drawn row both come from the same
        // ScreenButton, so a screen cannot advertise a key it does not answer.
        await Assert.That(frame).Contains("i info");
        await Assert.That(frame).Contains($"{ScreenChrome.InfoWords} Aetherfall");
    }

    [Test]
    public async Task PressingIOnTheSelectedWorldOpensItsReport()
    {
        // The snapshot view drives the real key through the real button; nothing about the route is faked.
        var frame = Screen("mssp");

        await Assert.That(frame).Contains(MsspScreenRenderer.Title);
        await Assert.That(frame).Contains("Aetherfall");
        await Assert.That(frame).Contains("Esc back");

        // And it is a report, not a form: the world list it came from is gone from the screen.
        await Assert.That(frame).DoesNotContain("Worlds & Characters");
    }

    [Test]
    public async Task EscapeFromTheReportGoesBackToTheWorldItWasOpenedFrom()
    {
        Console.SetIn(TextReader.Null);
        var app = new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(Width, Height));
        app.RenderSnapshot("mssp");

        // One Esc pops the report; the screen behind comes back with its own cursor and hints. A second
        // Esc leaves the settings altogether, which is the layering Esc has everywhere else in the app.
        await Assert.That(app.Settings.IsShowingDetail).IsTrue();
        app.Settings.SimulateKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false));

        await Assert.That(app.Settings.IsShowingDetail).IsFalse();
        await Assert.That(app.Settings.IsOpen).IsTrue();
        await Assert.That(app.RenderWholeFrame()).Contains("Worlds & Characters");
    }

    // ---- The key's shape and scope ----

    /// <summary>
    /// Both targeted keys trail the pane's one cursor stop and neither is one. This is the invariant
    /// <see cref="ScreenModel.Sizes"/> is built on — an action with a target must not steal the cursor
    /// from the thing it acts on — and the reason an INFO <em>chip</em> would have been wrong: reaching
    /// it with ↑↓ walks the selection to the last world, so it could only ever have reported on that one.
    /// </summary>
    [Test]
    public async Task TheInfoRowIsDrawnAndIsNotSomewhereTheCursorCanGo()
    {
        var worlds = new List<WorldDefinition> { World(), new() { Name = "Grapevine" } };
        var model = WorldsScreenRenderer.Model(worlds, [], 0, 0, 0, _ => { });

        // Two worlds, then [+ world]; the `i` and `Del` rows are drawn past the end of the stops.
        await Assert.That(model.RowCount(WorldsScreenRenderer.WorldsPane)).IsEqualTo(5);
        await Assert.That(model.Sizes[WorldsScreenRenderer.WorldsPane]).IsEqualTo(3);
        await Assert.That(model.HasDetailRow).IsTrue();

        // And the drawn column says what the key would act on, which is why nothing is lost by it not
        // being a chip.
        var column = WorldsScreenRenderer.WorldsColumn(worlds, 0, info: true);
        await Assert.That(Plain(column).Any(l => l.Contains($"{ScreenChrome.InfoWords} Aetherfall"))).IsTrue();
    }

    [Test]
    public async Task AProjectionWithNowhereToOpenAReportOffersNeitherTheRowNorTheHint()
    {
        // The renderer is pure and cannot put a screen on the screen, so a caller that supplies no action
        // gets no `i` row — and therefore no `i info` hint, because the hint is derived from the row.
        var model = WorldsScreenRenderer.Model(new List<WorldDefinition> { World() }, [], 0, 0);

        await Assert.That(model.HasDetailRow).IsFalse();
        await Assert.That(WorldsScreenRenderer.HeaderLine(Width, model)).DoesNotContain(ScreenChrome.DetailHint);
    }

    [Test]
    public async Task TheKeyRunsOnAWorldRowAndIsDeclinedEverywhereElse()
    {
        var opened = new List<int>();
        var worlds = new List<WorldDefinition> { World(), new() { Name = "Grapevine" } };
        var session = new SettingsSession(selection => WorldsScreenRenderer.Model(
            worlds,
            [],
            selection.SelectionIn(WorldsScreenRenderer.WorldsPane),
            selection.SelectionIn(WorldsScreenRenderer.CharactersPane),
            selection.SelectionIn(WorldsScreenRenderer.TriggerSetsPane),
            opened.Add));

        // On the second world's row: opens that world, not the selected-at-seed one.
        session.Selection.Seed(WorldsScreenRenderer.WorldsPane, 1);
        await Assert.That(session.Handle(Key(ConsoleKey.I))).IsEqualTo(ScreenAction.Consumed);
        await Assert.That(opened).IsEquivalentTo(new[] { 1 });

        // On [+ world] — a button row, not a list row — it declines, exactly as Delete does there.
        session.Selection.Seed(WorldsScreenRenderer.WorldsPane, 2);
        await Assert.That(session.Handle(Key(ConsoleKey.I))).IsEqualTo(ScreenAction.None);

        // In the CHARACTERS pane, which offers no report, the letter is not ours at all — it must fall
        // through rather than be swallowed, or a pane that does nothing with `i` would eat it silently.
        session.Selection.FocusPane(WorldsScreenRenderer.CharactersPane);
        await Assert.That(session.Handle(Key(ConsoleKey.I))).IsEqualTo(ScreenAction.None);
        await Assert.That(opened).IsEquivalentTo(new[] { 1 });
    }

    [Test]
    public async Task TheLetterIsStillATypedCharacterWhileAFieldIsOpen()
    {
        // `i` is an ordinary letter, and the only thing that makes it safe as a command is that an open
        // field edit takes the whole keyboard several branches earlier. Without that, a world could not
        // be named `Riverside`.
        var opened = new List<int>();
        var worlds = new List<WorldDefinition> { new() { Name = "Old", Host = "h", Port = 1 } };
        var session = new SettingsSession(selection => WorldsScreenRenderer.Model(
            worlds,
            [],
            selection.SelectionIn(WorldsScreenRenderer.WorldsPane),
            selection.SelectionIn(WorldsScreenRenderer.CharactersPane),
            selection.SelectionIn(WorldsScreenRenderer.TriggerSetsPane),
            opened.Add));

        session.Handle(Key(ConsoleKey.Enter));
        foreach (var ch in "Riverside")
        {
            session.Handle(new ConsoleKeyInfo(ch, ConsoleKey.None, false, false, false));
        }

        session.Handle(Key(ConsoleKey.Enter));

        // Contains rather than equals: ⏎ opens the field on its existing text, and where the caret lands
        // in it is the edit buffer's business and not this test's. What matters is that all nine letters
        // — the `i` among them — went into the name, and that none of them opened a report.
        await Assert.That(worlds[0].Name).Contains("Riverside");
        await Assert.That(opened).IsEmpty();
    }

    [Test]
    public async Task OpeningAReportPersistsNothing()
    {
        // Every other button on these screens is an edit and is written to disk the moment it is
        // accepted. This one is navigation: routing it through ScreenEdits would write config.json and
        // re-periodise every running timer each time somebody looked at a world.
        var saves = 0;
        var worlds = new List<WorldDefinition> { World() };
        var session = new SettingsSession(
            selection => WorldsScreenRenderer.Model(
                worlds,
                [],
                selection.SelectionIn(WorldsScreenRenderer.WorldsPane),
                selection.SelectionIn(WorldsScreenRenderer.CharactersPane),
                selection.SelectionIn(WorldsScreenRenderer.TriggerSetsPane),
                _ => { }),
            () => saves++);

        session.Handle(Key(ConsoleKey.I));

        await Assert.That(saves).IsEqualTo(0);
        await Assert.That(session.Edits.HasDeletions).IsFalse();
    }

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    // ---- The three states ----

    [Test]
    public async Task AWorldNothingHasConnectedToSaysSoRatherThanShowingNothing()
    {
        var frame = Screen("mssp-never");

        await Assert.That(frame).Contains("connect once and this fills in");
        await Assert.That(frame).DoesNotContain("does not publish MSSP");

        // And it still shows what the client knows without asking anybody, which is what stops the
        // screen reading as broken.
        await Assert.That(frame).Contains("aetherfall.mux:4201");
    }

    [Test]
    public async Task AServerThatAnsweredAndPublishesNothingIsNotTheSameEmptyScreen()
    {
        var frame = Screen("mssp-none");

        await Assert.That(frame).Contains("does not publish MSSP");
        await Assert.That(frame).Contains("It is optional");
        await Assert.That(frame).DoesNotContain("connect once and this fills in");

        // It says when we last reached the server, because "we asked and it said nothing" is only a
        // claim worth making if the asking is dated.
        await Assert.That(frame).Contains("last seen");
    }

    [Test]
    public async Task AReportIsDatedSoAStalePlayerCountIsNotPresentedAsCurrent()
    {
        var week = Plain(MsspScreenRenderer.Body(
            World(),
            Observed(Report(("PLAYERS", ["37"])), Noon.AddDays(-7)),
            Noon,
            Width));

        await Assert.That(week.Any(l => l.Contains("captured") && l.Contains("7 days ago"))).IsTrue();
        await Assert.That(week.Any(l => l.Contains("2026-07-23"))).IsTrue();
    }

    // ---- What it shows ----

    [Test]
    public async Task AMultiValuedVariableIsDrawnAsTheListItIsAndNotAsOneOfItsValues()
    {
        var rows = Plain(MsspScreenRenderer.Body(
            World(), Observed(Report(("PORT", ["80", "23", "4201"]))), Noon, Width));

        // All three, in wire order, on three rows — least to most relevant, which is the order the
        // specification gives them meaning in. A model keeping one value per variable would print a
        // server's *least* preferred port and call it the port; one joining them would lose the ordering.
        // Matched on the label *column*, not on a substring: `transport` contains `port`, and a test
        // that found the wrong row would have been asserting about the world's TLS setting.
        var at = rows.FindIndex(l => LabelOf(l) == "port");
        await Assert.That(at).IsGreaterThanOrEqualTo(0);
        await Assert.That(rows[at].TrimEnd()).EndsWith("80", StringComparison.Ordinal);
        await Assert.That(rows[at + 1].TrimEnd()).EndsWith("23", StringComparison.Ordinal);
        await Assert.That(rows[at + 2].TrimEnd()).EndsWith("4201", StringComparison.Ordinal);

        // The name is printed once, so three values read as one variable rather than as three.
        await Assert.That(LabelOf(rows[at + 1])).IsEmpty();
    }

    [Test]
    public async Task EverythingTheServerSentIsShownAndTheUnofficialHalfIsMarked()
    {
        var rows = Plain(MsspScreenRenderer.Body(
            World(),
            Observed(Report(
                ("NAME", ["Corvid"]),
                ("ANSI", ["1"]),
                ("PUEBLO", ["1"]),
                ("CORVID SPECIFIC", ["nevermore"]))),
            Noon,
            Width));

        var text = string.Join("\n", rows);
        await Assert.That(text).Contains(MsspScreenRenderer.EverythingElse);
        await Assert.That(text).Contains("ANSI");
        await Assert.That(text).Contains("PUEBLO");
        await Assert.That(text).Contains("CORVID SPECIFIC");
        await Assert.That(text).Contains("nevermore");

        // Official and unofficial are both visible and are told apart. ANSI is in the specification's
        // tables; PUEBLO looks every bit as standard and is not, which is exactly why the reader cannot
        // be left to tell from the name.
        await Assert.That(rows.Single(l => l.Contains("PUEBLO")).TrimStart())
            .StartsWith(MsspScreenRenderer.UnofficialMark, StringComparison.Ordinal);
        await Assert.That(rows.Single(l => l.Contains(" ANSI")).TrimStart())
            .DoesNotStartWith(MsspScreenRenderer.UnofficialMark);
        await Assert.That(text).Contains(MsspScreenRenderer.UnofficialLegend);
    }

    [Test]
    public async Task AMinusOneWorldCountReadsAsUnknownRatherThanAsMinusOne()
    {
        var rows = Plain(MsspScreenRenderer.Body(
            World(), Observed(Report(("ROOMS", ["-1"]))), Noon, Width));

        await Assert.That(rows.Any(l => l.Contains("ROOMS") && l.Contains(MsspScreenRenderer.Unavailable)))
            .IsTrue();
        await Assert.That(rows.Any(l => l.Contains("-1"))).IsFalse();
    }

    [Test]
    public async Task AVariableTheServerNeverMentionedReadsDifferentlyFromOneItSentEmpty()
    {
        // Two different absences and the screen distinguishes them: "this server cannot tell you" is a
        // fact about the server, "it never came up" is a fact about the report.
        var rows = Plain(MsspScreenRenderer.Body(
            World(), Observed(Report(("CONTACT", []))), Noon, Width));

        await Assert.That(rows.Any(l => l.Contains("contact") && l.Contains(MsspScreenRenderer.Unavailable)))
            .IsTrue();
        await Assert.That(rows.Any(l => l.Contains("website") && l.Contains(MsspScreenRenderer.Unreported)))
            .IsTrue();
    }

    // ---- Hostile values ----

    [Test]
    public async Task NoValueAStrangerCanSendMakesARowWiderThanTheScreen()
    {
        var hostile = Report(
            ("NAME", [new string('W', 5000)]),
            ("WEBSITE", ["https://" + new string('x', 900)]),
            ("CONTACT", [new string('あ', 400)]));

        foreach (var width in new[] { 80, 100, 120, 160 })
        {
            var rows = MsspScreenRenderer.Body(World(), Observed(hostile), Noon, width);
            foreach (var row in rows)
            {
                await Assert.That(MarkupText.VisibleLength(row)).IsLessThanOrEqualTo(width)
                    .Because($"a row must fit {width} columns, and this one is off the wire");
            }
        }
    }

    [Test]
    public async Task AValueCarryingMarkupCannotOpenATagOfItsOwn()
    {
        // A world's value is escaped, so `[bold red]` is text rather than a colour — and, more to the
        // point, an unbalanced `[` cannot eat the rest of the row.
        var rows = MsspScreenRenderer.Body(
            World(),
            Observed(Report(("NAME", ["[bold #ff0000 on #ff0000]owned"]), ("STATUS", ["[/]["]))),
            Noon,
            Width);

        var text = string.Join("\n", rows);
        await Assert.That(text).Contains("[[bold #ff0000 on #ff0000]]owned");
        await Assert.That(Plain(rows).Any(l => l.Contains("[/]["))).IsTrue();
    }

    [Test]
    public async Task AValueCarryingControlCharactersCannotAddOrShiftARow()
    {
        // A raw newline inside one value would end its row early and put a fragment of a stranger's text
        // on a line of its own, below the row it belongs to and outside the column it was measured for.
        // An ESC would be worse: the frame is ANSI.
        var clean = MsspScreenRenderer.Body(
            World(), Observed(Report(("NAME", ["ordinary"]))), Noon, Width);
        var nasty = MsspScreenRenderer.Body(
            World(), Observed(Report(("NAME", ["a\nb\rc[31md\te"]))), Noon, Width);

        await Assert.That(nasty).HasCount().EqualTo(clean.Count);
        foreach (var row in nasty)
        {
            await Assert.That(row.Any(char.IsControl)).IsFalse();
        }
    }

    [Test]
    public async Task AVariableWithAThousandValuesSpendsABoundedNumberOfRows()
    {
        var flood = Report(("REFERRAL", Enumerable.Range(0, 1000).Select(i => $"h{i}.example.org 4000").ToArray()));
        var rows = Plain(MsspScreenRenderer.Body(World(), Observed(flood), Noon, Width));

        await Assert.That(rows.Count(l => l.Contains("example.org"))).IsEqualTo(MsspScreenRenderer.MaxValueRows);
        await Assert.That(rows.Any(l => l.Contains("more"))).IsTrue();
    }

    // ---- The shape of the screen ----

    [Test]
    public async Task EveryStopTheCursorHasIsARowTheScreenDraws()
    {
        // Model counts Body's rows and Render draws them, so a cursor stop that was never drawn is
        // structurally impossible rather than two functions agreeing by inspection.
        var observation = Observed(Report(("NAME", ["Corvid"]), ("PORT", ["23", "4201"])));
        var model = MsspScreenRenderer.Model(World(), observation, Noon, Width);

        await Assert.That(model.PaneCount).IsEqualTo(1);
        await Assert.That(model.Sizes[0])
            .IsEqualTo(MsspScreenRenderer.Body(World(), observation, Noon, Width).Count);
    }

    [Test]
    public async Task TheReportOffersNothingToEditToggleOrRemove()
    {
        // Read-only is the shape. Every hint on these screens is derived from the model, so a screen
        // that offered none of the three physically cannot advertise them.
        var model = MsspScreenRenderer.Model(World(), Observed(Report(("NAME", ["Corvid"]))), Noon, Width);

        await Assert.That(model.HasEditableRow).IsFalse();
        await Assert.That(model.HasRemovableRow).IsFalse();
        await Assert.That(model.HasDetailRow).IsFalse();
    }

    [Test]
    public async Task ALongReportScrollsRatherThanLosingItsTail()
    {
        var long_ = Report(Enumerable.Range(0, 60)
            .Select(i => ($"VAR{i}", new[] { $"value {i}" }))
            .ToArray());
        var observation = Observed(long_);

        var top = Plain(MsspScreenRenderer.Render(
            World(), observation, Noon, new ScreenFocus(0, 0), 20, Width));
        var down = Plain(MsspScreenRenderer.Render(
            World(), observation, Noon, new ScreenFocus(0, 60), 20, Width));

        await Assert.That(top).HasCount().EqualTo(20);
        await Assert.That(down).HasCount().EqualTo(20);
        await Assert.That(string.Join("\n", top)).IsNotEqualTo(string.Join("\n", down));

        // The edges say what they are hiding rather than silently ending.
        await Assert.That(top[^1]).Contains("more");
        await Assert.That(down[0]).Contains("more");
    }

    [Test]
    public async Task TheReportFitsEveryTerminalItIsRenderedIn()
    {
        // Read off the frame the driver was handed, not off the markup: a settings screen is composed
        // into real controls, and this repository has been bitten by text overrunning a narrow panel.
        foreach (var (width, height) in new[] { (80, 24), (100, 30), (120, 32), (160, 48) })
        {
            foreach (var row in Frame("mssp", width, height))
            {
                await Assert.That(row.TrimEnd().Length).IsLessThanOrEqualTo(width)
                    .Because($"the report must fit {width}x{height}");
            }
        }
    }
}
