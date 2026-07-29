using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The cursor bar the screens draw under the keyboard. A screen's model can be perfectly navigable
/// and still be unusable if nothing shows where the cursor is, so each screen's list is asserted to
/// paint the focused row — and only that row, in the focused pane.
/// </summary>
public class ScreenCursorTests
{
    /// <summary>The background the focused row is painted with (see <see cref="ScreenPalette"/>).</summary>
    private const string Bar = "[on #2e3950]";

    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Triggers = new List<Trigger>
            {
                new() { Name = "Tell", Pattern = "tells you", Actions = new TriggerActions() },
                new() { Name = "Spam", Pattern = "guild", Actions = new TriggerActions() },
            },
            Aliases = new List<Alias>
            {
                new() { Name = "k", Pattern = "^k$", Substitution = "kill" },
                new() { Name = "s", Pattern = "^s$", Substitution = "say" },
            },
            Macros = new List<Macro>
            {
                new() { Name = "look", Key = "Num5", Command = "look" },
                new() { Name = "flee", Key = "Num1", Command = "flee" },
            },
            Timers = new List<TimerDefinition>
            {
                new() { Name = "ping", IntervalSeconds = 30, Command = "look" },
                new() { Name = "tick", IntervalSeconds = 60, Command = "score" },
            },
        },
    };

    private static int Barred(IEnumerable<string> lines) => lines.Count(l => l.Contains(Bar, StringComparison.Ordinal));

    [Test]
    public async Task NoFocus_DrawsNoCursorBarAtAll()
    {
        var sets = Sets();

        await Assert.That(Barred(TriggersScreenRenderer.RulesColumn(sets, 0))).IsEqualTo(0);
        await Assert.That(Barred(AliasesScreenRenderer.ListColumn(sets, 0))).IsEqualTo(0);
        await Assert.That(Barred(TimersScreenRenderer.ListColumn(sets, 0))).IsEqualTo(0);
        await Assert.That(Barred(KeypadScreenRenderer.HotkeysColumn(sets[0].Macros))).IsEqualTo(0);
    }

    [Test]
    public async Task Triggers_BarsTheFocusedRuleAndNothingElse()
    {
        var lines = TriggersScreenRenderer.RulesColumn(Sets(), 1, new ScreenFocus(0, 1));

        await Assert.That(Barred(lines)).IsEqualTo(1);
        await Assert.That(lines.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("Spam");
    }

    [Test]
    public async Task Triggers_EditorPaneBarsItsOwnRow_AndTheRuleListStaysUnbarred()
    {
        var sets = Sets();
        var editor = TriggersScreenRenderer.EditorColumn(sets, 0, Array.Empty<string>(), new ScreenFocus(1, 1));
        var rules = TriggersScreenRenderer.RulesColumn(sets, 0, new ScreenFocus(1, 1));

        await Assert.That(Barred(rules)).IsEqualTo(0);
        await Assert.That(Barred(editor)).IsEqualTo(1);
        await Assert.That(editor.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("stop processing");
    }

    [Test]
    public async Task Aliases_BarsTheFocusedAlias_AndTheEditorsCaseRow()
    {
        var sets = Sets();

        var list = AliasesScreenRenderer.ListColumn(sets, 1, new ScreenFocus(0, 1));
        await Assert.That(list.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("[bold]s[/]");

        var editor = AliasesScreenRenderer.EditorColumn(sets, 1, new ScreenFocus(1, 0));
        await Assert.That(editor.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("case sensitive");
    }

    [Test]
    public async Task Timers_BarsTheFocusedTimer_AndTheEditorsOneShotRow()
    {
        var sets = Sets();

        var list = TimersScreenRenderer.ListColumn(sets, 0, new ScreenFocus(0, 0));
        await Assert.That(list.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("ping");

        var editor = TimersScreenRenderer.EditorColumn(sets, 0, new ScreenFocus(1, 0));
        await Assert.That(editor.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("one-shot");
    }

    [Test]
    public async Task Keypad_BarsTheFocusedBinding()
    {
        var lines = KeypadScreenRenderer.HotkeysColumn(Sets()[0].Macros, new ScreenFocus(0, 1));

        await Assert.That(Barred(lines)).IsEqualTo(1);
        await Assert.That(lines.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("Num1");
    }

    [Test]
    public async Task Options_BarsTheFocusedOptionAndNeverASectionHeader()
    {
        // Navigable row 3 is "emoji substitution" — the two section headers and the spacer are skipped.
        var screen = OptionsScreenRenderer.TextAnsiScreen();
        var lines = OptionsScreenRenderer.BodyColumn(screen.Rows, new ScreenFocus(0, 3));

        await Assert.That(Barred(lines)).IsEqualTo(1);
        await Assert.That(lines.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("emoji substitution");
    }

    [Test]
    public async Task Worlds_BarsTheFocusedWorld_Character_AndTriggerSetInTheirOwnPanes()
    {
        var worlds = new List<WorldDefinition>
        {
            new()
            {
                Name = "Aardwolf",
                Host = "aardmud.org",
                Characters = new List<CharacterDefinition> { new() { Name = "Kaz" }, new() { Name = "Mira" } },
            },
            new() { Name = "Second", Host = "example.org" },
        };
        var sets = Sets();
        var accent = WorldsScreenRenderer.AccentFor(worlds, 0);

        var onWorld = WorldsScreenRenderer.WorldsColumn(worlds, 0, new ScreenFocus(0, 1));
        await Assert.That(onWorld.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("Second");

        var onCharacter = WorldsScreenRenderer.DetailColumn(worlds, sets, 0, 1, accent, new ScreenFocus(1, 1));
        await Assert.That(onCharacter.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("Mira");

        var onSet = WorldsScreenRenderer.TriggersColumn(worlds[0].Characters[0], sets, accent, new ScreenFocus(2, 0));
        await Assert.That(onSet.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("Comms");
    }

    [Test]
    public async Task Worlds_TriggerSetBarsAreAllTheSameWidthSoTheColumnDoesNotShift()
    {
        var character = new CharacterDefinition { Name = "Kaz" };
        var sets = new List<TriggerSet>
        {
            new() { Name = "A", Description = "short" },
            new() { Name = "Longer name", Description = "a much longer description" },
        };
        var widths = new List<int>();
        for (var i = 0; i < sets.Count; i++)
        {
            var row = WorldsScreenRenderer.TriggersColumn(character, sets, "#00f5b7", new ScreenFocus(2, i))
                .Single(l => l.Contains(Bar, StringComparison.Ordinal));
            widths.Add(MarkupText.VisibleLength(row));
        }

        await Assert.That(widths[0]).IsEqualTo(widths[1]);
    }

    [Test]
    public async Task HeaderHints_AdvertiseOnlyTheKeysTheScreensImplement()
    {
        await Assert.That(TriggersScreenRenderer.HeaderLine(0)).Contains(ScreenChrome.ListHints);
        await Assert.That(AliasesScreenRenderer.HeaderLine(0)).Contains(ScreenChrome.ListHints);
        await Assert.That(TimersScreenRenderer.HeaderLine(0)).Contains(ScreenChrome.ListHints);
        await Assert.That(WorldsScreenRenderer.HeaderLine(0)).Contains(ScreenChrome.ListHints);
        await Assert.That(KeypadScreenRenderer.HeaderLine(0)).Contains(ScreenChrome.SingleListHints);
        await Assert.That(OptionsScreenRenderer.HeaderLine("Input & spellcheck", "F8", 0))
            .Contains(ScreenChrome.SingleListHints);

        // A header handed no model describes a screen that only navigates, so it may not claim ⏎
        // opens an editor — nor any of the verbs an editor would be advertised under.
        foreach (var header in new[]
        {
            TriggersScreenRenderer.HeaderLine(0),
            AliasesScreenRenderer.HeaderLine(0),
            TimersScreenRenderer.HeaderLine(0),
            WorldsScreenRenderer.HeaderLine(0),
            KeypadScreenRenderer.HeaderLine(0),
            OptionsScreenRenderer.HeaderLine("Input & spellcheck", "F8", 0),
        })
        {
            await Assert.That(header).DoesNotContain("⏎ edit");
            await Assert.That(header).DoesNotContain("⏎ rebind");
            await Assert.That(header).DoesNotContain("⏎ change");
        }
    }

    /// <summary>
    /// F5 has two doors — F5 itself and F9, which opens it on the character whose log used to live on a
    /// screen of its own — and the header names the one that was used. A header that always said
    /// <c>F5</c> would be naming a key that, pressed there, re-opens the screen instead of closing it.
    /// </summary>
    [Test]
    public async Task HeaderHints_NameTheKeyTheScreenWasOpenedWith()
    {
        var model = WorldsScreenRenderer.Model(
            new List<WorldDefinition> { new() { Name = "Aardwolf", Host = "aardmud.org" } },
            Sets(),
            0,
            -1);

        var f5 = WorldsScreenRenderer.HeaderLine(80, model);
        var f9 = WorldsScreenRenderer.HeaderLine(80, model, null, WorldsScreenRenderer.LogFKey);

        await Assert.That(f5).Contains("F5");
        await Assert.That(f5).DoesNotContain("F9");
        await Assert.That(f9).Contains("F9");
        await Assert.That(f9).DoesNotContain("F5");
    }

    /// <summary>
    /// The hint honesty rule, in both directions: a screen advertises <c>⏎ edit</c> <b>if and only
    /// if</b> its <see cref="ScreenModel"/> actually holds a row ⏎ can open. Every header is built
    /// from the same model the live view hands it, so a screen that grew (or lost) an editable row
    /// cannot end up saying otherwise.
    /// </summary>
    [Test]
    public async Task HeaderHints_ClaimAnEditorExactlyWhenTheModelOffersOne()
    {
        var populated = Populated();
        var bare = Bare();

        // Both cases must be represented, or an "if and only if" would pass vacuously.
        await Assert.That(populated.Any(s => s.Model.HasEditableRow)).IsTrue();
        await Assert.That(bare.Any(s => !s.Model.HasEditableRow)).IsTrue();

        foreach (var (header, model) in populated.Concat(bare))
        {
            await Assert.That(header.Contains(ScreenChrome.EditHint, StringComparison.Ordinal))
                .IsEqualTo(model.HasEditableRow);
        }
    }

    /// <summary>
    /// While a field is open the header stops offering the screen's own verbs — Esc reverts the buffer
    /// rather than closing, and saying "Esc close" there would be the same lie pointed the other way.
    /// </summary>
    [Test]
    public async Task HeaderHints_SwapToTheEditingKeysWhileAFieldIsOpen()
    {
        var sets = Sets();
        var model = TimersScreenRenderer.Model(sets, 0);
        var editing = new ScreenFocus(0, 0, new ScreenFieldEdit(0, "30", 2, null, RowFields: 2));

        var header = TimersScreenRenderer.HeaderLine(0, model, editing);

        await Assert.That(header).Contains(ScreenChrome.EditingHints);
        await Assert.That(header).Contains(ScreenChrome.NextFieldHint);
        await Assert.That(header).DoesNotContain(ScreenChrome.ListHints);
        await Assert.That(header).DoesNotContain("Esc[/][#7c8699] close");

        // A row with a single field has nowhere for ⇥ to go, so it doesn't offer it.
        var single = TimersScreenRenderer.HeaderLine(
            0, model, new ScreenFocus(0, 0, new ScreenFieldEdit(0, "30", 2, null)));
        await Assert.That(single).DoesNotContain(ScreenChrome.NextFieldHint);
    }

    /// <summary>Screens whose models hold editable rows, each header built from that same model.</summary>
    private static List<(string Header, ScreenModel Model)> Populated()
    {
        var sets = Sets();
        var worlds = new List<WorldDefinition>
        {
            new() { Name = "Aardwolf", Host = "aardmud.org", Characters = new List<CharacterDefinition> { new() } },
        };

        // A synthetic options screen, not F7 or F8: both of those are all-checkbox screens now (their
        // value rows named features that do not exist and went with them), so neither can stand for
        // "an options screen that does offer an editor". The shared renderer still draws one.
        var value = "en_US";
        var options = new OptionsScreenRenderer.OptionsScreen(
            "Values",
            "F8",
            new List<OptionsScreenRenderer.OptionRow>
            {
                new("a value", value, null, null, null,
                    ScreenField.Text("a value", () => value, v => value = v)),
            });

        return new List<(string, ScreenModel)>
        {
            Pair(TriggersScreenRenderer.Model(sets, 0), m => TriggersScreenRenderer.HeaderLine(0, m)),
            Pair(AliasesScreenRenderer.Model(sets, 0), m => AliasesScreenRenderer.HeaderLine(0, m)),
            Pair(TimersScreenRenderer.Model(sets, 0), m => TimersScreenRenderer.HeaderLine(0, m)),
            Pair(KeypadScreenRenderer.Model(sets[0].Macros), m => KeypadScreenRenderer.HeaderLine(0, m)),
            Pair(
                WorldsScreenRenderer.Model(worlds, sets, 0, 0),
                m => WorldsScreenRenderer.HeaderLine(0, m)),
            Pair(
                OptionsScreenRenderer.Model(options),
                m => OptionsScreenRenderer.HeaderLine(options.Title, options.FKey, 0, m)),
        };
    }

    /// <summary>
    /// The same screens with nothing to edit — empty lists, and the two real options screens, which
    /// are now entirely checkboxes and so must <em>not</em> advertise <c>⏎ edit</c>.
    /// </summary>
    private static List<(string Header, ScreenModel Model)> Bare()
    {
        var empty = new List<TriggerSet>();
        var textAnsi = OptionsScreenRenderer.TextAnsiScreen();
        var input = OptionsScreenRenderer.InputScreen();
        var toggles = new OptionsScreenRenderer.OptionsScreen(
            "Toggles",
            "F7",
            new List<OptionsScreenRenderer.OptionRow>
            {
                new("local echo", null, true, null, ScreenToggle.Bind(() => true, _ => { })),
            });

        return new List<(string, ScreenModel)>
        {
            Pair(
                OptionsScreenRenderer.Model(textAnsi),
                m => OptionsScreenRenderer.HeaderLine(textAnsi.Title, textAnsi.FKey, 0, m)),
            Pair(
                OptionsScreenRenderer.Model(input),
                m => OptionsScreenRenderer.HeaderLine(input.Title, input.FKey, 0, m)),
            Pair(TriggersScreenRenderer.Model(empty, -1), m => TriggersScreenRenderer.HeaderLine(0, m)),
            Pair(AliasesScreenRenderer.Model(empty, -1), m => AliasesScreenRenderer.HeaderLine(0, m)),
            Pair(TimersScreenRenderer.Model(empty, -1), m => TimersScreenRenderer.HeaderLine(0, m)),
            Pair(
                KeypadScreenRenderer.Model(Array.Empty<Macro>()),
                m => KeypadScreenRenderer.HeaderLine(0, m)),
            Pair(
                WorldsScreenRenderer.Model(Array.Empty<WorldDefinition>(), empty, -1, -1),
                m => WorldsScreenRenderer.HeaderLine(0, m)),
            Pair(
                OptionsScreenRenderer.Model(toggles),
                m => OptionsScreenRenderer.HeaderLine(toggles.Title, toggles.FKey, 0, m)),
        };
    }

    private static (string Header, ScreenModel Model) Pair(ScreenModel model, Func<ScreenModel, string> header) =>
        (header(model), model);
}
