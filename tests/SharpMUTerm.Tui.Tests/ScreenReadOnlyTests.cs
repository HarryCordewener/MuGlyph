using System.Text.RegularExpressions;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What a row looks like when you cannot change it. The settings screens already refuse to advertise a
/// key their <see cref="ScreenModel"/> doesn't offer (<see cref="ScreenCursorTests"/>,
/// <see cref="ScreenFooterTests"/>); these are the same rule one level down, where the claim is made by
/// a row rather than by the chrome.
/// <para>
/// Two halves, both pinned here because either alone can pass while the screen still lies. A value the
/// keyboard can change is drawn in a field well; a value it cannot is drawn without one, in the muted
/// ink. And nothing that is merely *derived* — a summary of two other rows, a projection of a list
/// elsewhere — may be drawn as a checkbox, because a checkbox is a promise that Space does something.
/// </para>
/// </summary>
public class ScreenReadOnlyTests
{
    /// <summary>
    /// The field well as it appears in markup — matched as a background so it catches both forms: the
    /// resting well tints nothing but the background, an open edit's buffer sets a foreground too.
    /// </summary>
    private static readonly string Well = "on " + ScreenPalette.FieldBg;

    /// <summary>A drawn checkbox, either state, as the renderers escape it.</summary>
    private static bool HasCheckbox(string line) =>
        line.Contains("[[x]]", StringComparison.Ordinal) || line.Contains("[[ ]]", StringComparison.Ordinal);

    private static bool InAWell(string line) => line.Contains(Well, StringComparison.Ordinal);

    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Description = "channel routing",
            Triggers = new List<Trigger>
            {
                new()
                {
                    Name = "Tell",
                    Pattern = "tells you",
                    Actions = new TriggerActions
                    {
                        HighlightForeground = TerminalColor.FromRgb(0xff, 0xd7, 0x00),
                        SpawnTarget = "Chat",
                    },
                },
                new() { Name = "Quiet", Pattern = "guild", Actions = new TriggerActions() },
            },
            Aliases = new List<Alias> { new() { Name = "k", Pattern = "^k$", Substitution = "kill\nflee" } },
            Macros = new List<Macro> { new() { Name = "Look", Key = "Num5", Command = "look" } },
            Timers = new List<TimerDefinition> { new() { Name = "ping", IntervalSeconds = 30, Command = "look" } },
        },
    };

    private static List<WorldDefinition> Worlds() => new()
    {
        new WorldDefinition
        {
            Name = "Aetherfall",
            Host = "aetherfall.mux",
            Port = 4201,
            UseTls = true,
            KeepaliveSeconds = 30,
            Characters = new List<CharacterDefinition>
            {
                new() { Name = "Corvid", AutoLogin = true, OnConnect = "look", TriggerSets = new List<string> { "Comms" } },
            },
        },
    };

    /// <summary>
    /// F5's world block is the case this rule was written for: <c>host</c> can be typed into and
    /// <c>security</c> cannot, and until the well was drawn the only way to find out which was which
    /// was to walk the cursor into one.
    /// </summary>
    [Test]
    public async Task Worlds_EditableRowsSitInAFieldWellAndReadOnlyOnesDoNot()
    {
        var lines = WorldsScreenRenderer.DetailColumn(Worlds(), Sets(), 0, 0, ScreenPalette.Accent);

        foreach (var label in new[] { "host", "port", "encoding", "keepalive" })
        {
            await Assert.That(InAWell(Row(lines, label))).IsTrue().Because(label + " is editable");
        }

        // The world's own name, told apart from the characters table's "name" column header by its
        // right-aligned label — the same discriminator ScreenFieldRenderingTests uses.
        var worldName = lines.Single(l => l.Contains("     name[/]", StringComparison.Ordinal));
        await Assert.That(InAWell(worldName)).IsTrue();

        // The two booleans that used to be summarised here now have checkboxes, so the row is no longer
        // a readout — but it is still not a *field*, and must not grow a well: Space presses it, ⏎ does
        // nothing to it, and the well would promise the wrong key.
        await Assert.That(InAWell(Row(lines, "security"))).IsFalse();
        await Assert.That(HasCheckbox(Row(lines, "security"))).IsTrue();
    }

    /// <summary>
    /// The other half of that rule, on the screen that grew the checkboxes: every checkbox the WORLD
    /// block draws belongs to a row the cursor can reach and Space can press. The security pane's rows
    /// are exactly those two, so the counts have to match — a summary redrawn as a checkbox is exactly
    /// the case where they wouldn't.
    /// </summary>
    [Test]
    public async Task Worlds_EveryCheckboxInTheWorldBlockIsANavigableToggleRow()
    {
        var worlds = Worlds();
        var lines = WorldsScreenRenderer.DetailColumn(worlds, Sets(), 0, 0, ScreenPalette.Accent);
        var model = WorldsScreenRenderer.Model(worlds, Sets(), 0, 0);
        var pane = WorldsScreenRenderer.SecurityPane;

        // The characters table below draws no checkboxes of its own, so every box in this column is a
        // security row.
        await Assert.That(lines.Count(HasCheckbox)).IsEqualTo(model.Sizes[pane]);
        for (var row = 0; row < model.Sizes[pane]; row++)
        {
            await Assert.That(model.ToggleAt(pane, row)).IsNotNull();
        }
    }

    /// <summary>
    /// The one row on these screens that can switch off a check the user is entitled to assume is
    /// running. Checked while TLS is on, it is drawn in the warn ink with the <c>▲</c> a refused value
    /// gets and says what the state costs; unchecked, it is as quiet as the encoding above it. Asserted
    /// both ways round, so the shouting can neither disappear nor spread.
    /// </summary>
    [Test]
    public async Task Worlds_TheCertificateRowShoutsOnlyWhileItIsActuallySwitchedOff()
    {
        var worlds = Worlds();
        var strict = CertificateRow(WorldsScreenRenderer.DetailColumn(worlds, Sets(), 0, 0, ScreenPalette.Accent));
        await Assert.That(strict).DoesNotContain(ScreenPalette.Warn);
        await Assert.That(strict).Contains("[[ ]]");

        worlds[0].AllowInvalidCertificates = true;
        var lax = CertificateRow(WorldsScreenRenderer.DetailColumn(worlds, Sets(), 0, 0, ScreenPalette.Accent));
        await Assert.That(lax).Contains(ScreenPalette.Warn);
        await Assert.That(lax).Contains("▲");
        await Assert.That(lax).Contains("[[x]]");

        // With nothing encrypted there is no certificate to check either way, and a warning that fired
        // there would train the eye to skip the one that matters.
        worlds[0].UseTls = false;
        var plaintext = CertificateRow(WorldsScreenRenderer.DetailColumn(worlds, Sets(), 0, 0, ScreenPalette.Accent));
        await Assert.That(plaintext).DoesNotContain(ScreenPalette.Warn);
        await Assert.That(plaintext).Contains("no effect until TLS is on");
    }

    /// <summary>
    /// The character form, where two of eight rows are readouts: a mirror of the character row's own
    /// checkbox, and the state of a connection. The other six — the name, the password, the connect
    /// line, the on-connect line and the two log values — are the character row's own fields.
    /// <para>
    /// The <c>password</c> row moved sides. It used to be asserted here as a readout, drawn muted and
    /// unwelled, and that was the honest answer while there was nowhere to put a password: the value was
    /// <c>[JsonIgnore]</c> with no field to type it into. It is a real field now, so it takes the well —
    /// which is not a weakening of the rule but the rule applied to a row that changed. Both halves are
    /// still pinned in both directions, and the mask and the label the row carries instead of
    /// <c>keychain</c> are pinned in <see cref="ScreenPasswordFieldTests"/>.
    /// </para>
    /// </summary>
    [Test]
    public async Task Worlds_TheCharacterFormWellsOnlyTheRowsThatCanBeTyped()
    {
        var lines = WorldsScreenRenderer.FormColumn(Worlds()[0].Characters[0], ScreenPalette.Accent);

        foreach (var label in new[] { "name", "password", "connect", "on connect", "log", "log folder" })
        {
            await Assert.That(InAWell(Row(lines, label))).IsTrue().Because(label + " is editable");
        }

        foreach (var label in new[] { "auto-login", "session" })
        {
            await Assert.That(InAWell(Row(lines, label))).IsFalse().Because(label + " is a readout");
            await Assert.That(Row(lines, label)).Contains(ScreenPalette.Muted).Because(label);
        }
    }

    /// <summary>
    /// The whole point of a well is that it is visible without focus. Opening the field must therefore
    /// *deepen* the affordance already drawn rather than conjure a new one — same well, plus a caret.
    /// </summary>
    [Test]
    public async Task AFieldIsWelledAtRestAndStillWelledWhileItIsBeingTyped()
    {
        var resting = ScreenChrome.Field("[#d7deec]aetherfall.mux[/]", null);
        var open = ScreenChrome.Field(
            "[#d7deec]aetherfall.mux[/]", new ScreenFieldEdit(0, "aetherfall.mux", 14, null));

        await Assert.That(InAWell(resting)).IsTrue();
        await Assert.That(InAWell(open)).IsTrue();
        await Assert.That(open).Contains($"[{ScreenPalette.Ink} on {ScreenPalette.Accent}]");
        await Assert.That(resting).DoesNotContain($"[{ScreenPalette.Ink} on {ScreenPalette.Accent}]");
    }

    /// <summary>A read-only value carries the muted ink and, decisively, no well.</summary>
    [Test]
    public async Task AReadOnlyValueIsMutedAndNeverWelled()
    {
        var value = ScreenChrome.ReadOnly("TLS on · certs strict");

        await Assert.That(value).Contains(ScreenPalette.Muted);
        await Assert.That(InAWell(value)).IsFalse();

        // It is drawn from config, so it is escaped like every other configured string.
        await Assert.That(ScreenChrome.ReadOnly("weird [value]")).Contains("weird [[value]]");
    }

    /// <summary>
    /// F2's highlight summary. It reports whether either colour row above it is set — the cursor cannot
    /// land on it and Space does nothing to it — so it is a caption on the section, not a fourth
    /// checkbox under it. Asserted both ways round, so it can't come back in either state.
    /// </summary>
    [Test]
    public async Task Triggers_TheHighlightSummaryIsACaptionInBothStates()
    {
        var sets = Sets();

        foreach (var selected in new[] { 0, 1 })
        {
            var editor = TriggersScreenRenderer.EditorColumn(sets, selected, Array.Empty<string>());
            var heading = editor.Single(l => l.Contains("highlight", StringComparison.Ordinal));

            await Assert.That(HasCheckbox(heading)).IsFalse().Because("trigger " + selected);
            await Assert.That(editor.Any(l => l.Contains("highlight line", StringComparison.Ordinal)))
                .IsFalse()
                .Because("trigger " + selected);
        }
    }

    /// <summary>
    /// The general form of the rule for F2's editor pane: every checkbox it draws is a row the cursor
    /// can reach and Space can press. The pane's navigable rows are its second pane, so the two counts
    /// have to match — a derived indicator drawn as a checkbox is exactly the case where they don't.
    /// </summary>
    [Test]
    public async Task Triggers_EveryCheckboxInTheEditorIsANavigableToggleRow()
    {
        var sets = Sets();
        var editor = TriggersScreenRenderer.EditorColumn(sets, 0, Array.Empty<string>());
        var model = TriggersScreenRenderer.Model(sets, 0);

        await Assert.That(editor.Count(HasCheckbox)).IsEqualTo(model.Sizes[1]);
        for (var row = 0; row < model.Sizes[1]; row++)
        {
            await Assert.That(model.ToggleAt(1, row)).IsNotNull();
        }
    }

    /// <summary>
    /// The same rule for the options screens, which is where F9's auto-start indicator was: every
    /// checkbox drawn in the list belongs to a row whose model entry actually carries a toggle.
    /// </summary>
    [Test]
    public async Task Options_EveryCheckboxBelongsToARowTheModelCanToggle()
    {
        var screens = new[]
        {
            OptionsScreenRenderer.TextAnsiScreen(),
            OptionsScreenRenderer.InputScreen(),
        };

        foreach (var screen in screens)
        {
            var model = OptionsScreenRenderer.Model(screen);
            var drawn = OptionsScreenRenderer.BodyColumn(screen.Rows).Count(HasCheckbox);
            var toggles = 0;
            for (var row = 0; row < model.Sizes[0]; row++)
            {
                if (model.ToggleAt(0, row) is not null)
                {
                    toggles++;
                }
            }

            await Assert.That(drawn).IsEqualTo(toggles).Because(screen.Title);
        }
    }

    /// <summary>
    /// F4's numpad grid mirrors the binding list beside it and has no cursor of its own, so a cell is a
    /// readout: the commands it shows are muted and none of them is drawn in a well. The list's own
    /// commands, which ⏎ opens, are.
    /// </summary>
    [Test]
    public async Task Keypad_TheNumpadGridIsAReadoutAndTheBindingListIsNot()
    {
        var macros = Sets()[0].Macros;

        var grid = KeypadScreenRenderer.NumpadColumn(macros);
        await Assert.That(grid.Any(InAWell)).IsFalse();
        await Assert.That(grid.Single(l => l.Contains("look", StringComparison.Ordinal)))
            .Contains(ScreenPalette.Muted);

        var list = KeypadScreenRenderer.HotkeysColumn(macros);
        await Assert.That(list.Single(l => l.Contains("Num5", StringComparison.Ordinal))).Contains(Well);
    }

    /// <summary>
    /// The <c>‹ back</c> affordance is gone. It appeared on F7/F8/F9 only and pointed at a navigation
    /// stack that does not exist — Esc closes a settings screen, and the header says so.
    /// </summary>
    [Test]
    public async Task NoScreenOffersABackAffordance()
    {
        foreach (var header in Headers())
        {
            await Assert.That(header).DoesNotContain("‹");
            await Assert.That(header).DoesNotContain("back");
        }
    }

    /// <summary>
    /// Every footer's context line answers the same question in the same shape — <c>where is the cursor
    /// in this screen's list</c>, optionally followed by what identifies the thing it points at. They
    /// used to answer whatever each screen's author found interesting: two of the eight reported an
    /// inventory instead (<c>9 bindings · 8 of 9 numpad keys bound</c>, <c>3 options · 1 section</c>).
    /// </summary>
    [Test]
    public async Task EveryFooterContextLineOpensWithTheCursorsPosition()
    {
        foreach (var (name, footer) in Footers())
        {
            var context = Visible(footer).TrimStart();
            await Assert.That(Regex.IsMatch(context, @"^[a-z]+ \d+/\d+")).IsTrue().Because($"{name}: {context}");
        }
    }

    /// <summary>
    /// The certificate checkbox, found by its label: it is the one security row with no label column of
    /// its own, because it is the second switch of the <c>security</c> row above it.
    /// </summary>
    private static string CertificateRow(IReadOnlyList<string> lines) =>
        lines.Single(l => l.Contains("accept invalid certificates", StringComparison.Ordinal));

    /// <summary>The line of a label/value block whose label is <paramref name="label"/>.</summary>
    private static string Row(IReadOnlyList<string> lines, string label) =>
        lines.Single(l => Regex.IsMatch(Visible(l), $@"^\s*{Regex.Escape(label)}\s\s"));

    /// <summary>A markup line as it prints: tags stripped, escaped brackets folded back to one.</summary>
    private static string Visible(string markup)
    {
        var guarded = markup.Replace("[[", "\u0001", StringComparison.Ordinal)
            .Replace("]]", "\u0002", StringComparison.Ordinal);
        return Regex.Replace(guarded, @"\[[^\[\]]*\]", string.Empty)
            .Replace('\u0001', '[')
            .Replace('\u0002', ']');
    }

    private static List<string> Headers()
    {
        var sets = Sets();
        var worlds = Worlds();
        return new List<string>
        {
            TriggersScreenRenderer.HeaderLine(80, TriggersScreenRenderer.Model(sets, 0)),
            AliasesScreenRenderer.HeaderLine(80, AliasesScreenRenderer.Model(sets, 0)),
            KeypadScreenRenderer.HeaderLine(80, KeypadScreenRenderer.Model(sets[0].Macros)),
            WorldsScreenRenderer.HeaderLine(80, WorldsScreenRenderer.Model(worlds, sets, 0, 0)),
            TimersScreenRenderer.HeaderLine(80, TimersScreenRenderer.Model(sets, 0)),
            Header(OptionsScreenRenderer.TextAnsiScreen()),
            Header(OptionsScreenRenderer.InputScreen()),
            WorldsScreenRenderer.HeaderLine(
                80, WorldsScreenRenderer.Model(worlds, sets, 0, 0), null, WorldsScreenRenderer.LogFKey),
        };
    }

    private static string Header(OptionsScreenRenderer.OptionsScreen screen) =>
        OptionsScreenRenderer.HeaderLine(screen.Title, screen.FKey, 80, OptionsScreenRenderer.Model(screen));

    private static List<(string Name, string Footer)> Footers()
    {
        var sets = Sets();
        var worlds = Worlds();
        var accent = WorldsScreenRenderer.AccentFor(worlds, 0);

        return new List<(string, string)>
        {
            ("F2 triggers", TriggersScreenRenderer.FooterLine(sets, 0, 80)),
            ("F3 aliases", AliasesScreenRenderer.FooterLine(sets, 0, 80)),
            ("F4 keypad", KeypadScreenRenderer.FooterLine(sets[0].Macros, 80)),
            ("F5 worlds", WorldsScreenRenderer.FooterLine(worlds, 0, 0, accent, 80)),
            ("F6 timers", TimersScreenRenderer.FooterLine(sets, 0, 80)),
            ("F7 text", OptionsScreenRenderer.FooterLine(OptionsScreenRenderer.TextAnsiScreen().Rows, 80)),
            ("F8 input", OptionsScreenRenderer.FooterLine(OptionsScreenRenderer.InputScreen().Rows, 80)),
            ("F9 worlds (the character's log)", WorldsScreenRenderer.FooterLine(worlds, 0, 0, accent, 80)),
        };
    }
}
