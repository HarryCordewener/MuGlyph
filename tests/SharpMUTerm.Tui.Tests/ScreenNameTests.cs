using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The item's name, on every list screen. Each of the five drew a name as the row's primary identifier
/// and none of the four automation screens let you change it: you could rewrite a trigger's regex but
/// not what it was called. These assert that the name is now the <em>first</em> field of every list
/// row — so ⏎ on a row, and on a row that was just added, opens the one value that tells it apart —
/// that it writes back to the right object, that Cancel puts the old one back, and what a name is
/// allowed to be.
/// </summary>
public class ScreenNameTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Triggers = new List<Trigger> { new() { Name = "Tell", Pattern = "tells you" } },
            Aliases = new List<Alias> { new() { Name = "k", Pattern = "^k$", Substitution = "kill" } },
            Macros = new List<Macro> { new() { Name = "look", Key = "Num5", Command = "look" } },
            Timers = new List<TimerDefinition> { new() { Name = "ping", IntervalSeconds = 30, Command = "look" } },
        },
    };

    private static List<WorldDefinition> Worlds() => new()
    {
        new WorldDefinition
        {
            Name = "Aetherfall",
            Host = "aetherfall.mux",
            Characters = new List<CharacterDefinition> { new() { Name = "Corvid" } },
        },
    };

    /// <summary>Every list row of every screen, paired with what its name field currently reads.</summary>
    private static List<(string Screen, ScreenField Field, Func<string> Read)> NameFields(
        List<TriggerSet> sets, List<WorldDefinition> worlds)
    {
        var trigger = sets[0].Triggers[0];
        var alias = sets[0].Aliases[0];
        var macro = sets[0].Macros[0];
        var timer = sets[0].Timers[0];
        var world = worlds[0];
        var character = world.Characters[0];

        return new List<(string, ScreenField, Func<string>)>
        {
            ("F2 triggers",
                TriggersScreenRenderer.Model(sets, 0).FieldAt(0, 0, TriggersScreenRenderer.NameField)!.Value,
                () => trigger.Name),
            ("F3 aliases",
                AliasesScreenRenderer.Model(sets, 0).FieldAt(0, 0, AliasesScreenRenderer.NameField)!.Value,
                () => alias.Name),
            ("F4 keypad",
                KeypadScreenRenderer.Model(sets[0].Macros).FieldAt(0, 0, KeypadScreenRenderer.NameField)!.Value,
                () => macro.Name),
            ("F6 timers",
                TimersScreenRenderer.Model(sets, 0).FieldAt(0, 0, TimersScreenRenderer.NameField)!.Value,
                () => timer.Name),
            ("F5 worlds",
                WorldsScreenRenderer.Model(worlds, sets, 0, 0)
                    .FieldAt(
                        WorldsScreenRenderer.WorldsPane, 0, WorldsScreenRenderer.WorldNameField)!.Value,
                () => world.Name),
            ("F5 characters",
                WorldsScreenRenderer.Model(worlds, sets, 0, 0)
                    .FieldAt(
                        WorldsScreenRenderer.CharactersPane, 0, WorldsScreenRenderer.CharacterNameField)!.Value,
                () => character.Name),
        };
    }

    /// <summary>Field 0 of every list row — what ⏎ opens, whatever each screen calls that ordinal.</summary>
    private static List<ScreenField> FirstFields(List<TriggerSet> sets, List<WorldDefinition> worlds) =>
        new()
        {
            TriggersScreenRenderer.Model(sets, 0).FieldAt(0, 0, 0)!.Value,
            AliasesScreenRenderer.Model(sets, 0).FieldAt(0, 0, 0)!.Value,
            KeypadScreenRenderer.Model(sets[0].Macros).FieldAt(0, 0, 0)!.Value,
            TimersScreenRenderer.Model(sets, 0).FieldAt(0, 0, 0)!.Value,
            WorldsScreenRenderer.Model(worlds, sets, 0, 0).FieldAt(WorldsScreenRenderer.WorldsPane, 0, 0)!.Value,
            WorldsScreenRenderer.Model(worlds, sets, 0, 0)
                .FieldAt(WorldsScreenRenderer.CharactersPane, 0, 0)!.Value,
        };

    /// <summary>
    /// The rule the four automation screens broke: the row's first field is the thing it is called.
    /// F5 already worked this way, which is why it is in the same list rather than in a test of its own.
    /// </summary>
    [Test]
    public async Task TheFirstFieldOfEveryListRowIsItsName()
    {
        var sets = Sets();
        var worlds = Worlds();
        var expected = new[] { "Tell", "k", "look", "ping", "Aetherfall", "Corvid" };
        var fields = NameFields(sets, worlds);

        for (var i = 0; i < fields.Count; i++)
        {
            var (screen, field, _) = fields[i];
            await Assert.That(field.Label).IsEqualTo("name").Because(screen);
            await Assert.That(field.Get()).IsEqualTo(expected[i]).Because(screen);
        }

        // The name really is the *first* field, which is what makes ⏎ open it. Asserted through the
        // models rather than against the ordinal constants: comparing a constant to a literal is a
        // claim the compiler already settles, and says nothing about which field the screen built.
        await Assert.That(FirstFields(sets, worlds).Select(f => f.Label).Distinct())
            .IsEquivalentTo(new[] { "name" });
    }

    /// <summary>
    /// A committed name lands on the item and stays there, on every screen. It used to be put back by the
    /// screen's revert; a rename is a confirmed edit, so nothing on the way out undoes it.
    /// </summary>
    [Test]
    public async Task WritingANameLandsOnTheItemAndIsKept()
    {
        var sets = Sets();
        var worlds = Worlds();

        foreach (var (screen, field, read) in NameFields(sets, worlds))
        {
            var edits = new ScreenEdits();

            await Assert.That(edits.Apply(field, "Renamed")).IsNull().Because(screen);
            await Assert.That(read()).IsEqualTo("Renamed").Because(screen);

            edits.Revert();
            await Assert.That(read()).IsEqualTo("Renamed").Because(screen);
        }
    }

    /// <summary>
    /// What a name may be. Blank is refused because the row would then have no identifier at all, and
    /// control characters are refused because a name is drawn into one row of a fixed-width list — a
    /// tab or a newline inside one breaks the column, exactly as it would in a tab title. Nothing else
    /// is refused: names are deliberately not unique (see <see cref="ScreenField.Name"/>).
    /// </summary>
    [Test]
    public async Task ANameIsRefusedWhenBlankOrCarryingControlCharacters()
    {
        var sets = Sets();
        var worlds = Worlds();

        foreach (var (screen, field, read) in NameFields(sets, worlds))
        {
            var before = read();

            await Assert.That(field.Validate(string.Empty)).IsNotNull().Because(screen);
            await Assert.That(field.Validate("   ")).IsNotNull().Because(screen);
            await Assert.That(field.Validate("two\tnames")).IsNotNull().Because(screen);
            await Assert.That(field.Validate("two\nnames")).IsNotNull().Because(screen);

            // A refused value writes nothing, because Apply is the only path from a buffer into config.
            await Assert.That(new ScreenEdits().Apply(field, "  ")).IsNotNull().Because(screen);
            await Assert.That(read()).IsEqualTo(before).Because(screen);

            // Surrounding whitespace is trimmed rather than refused, and anything else is allowed.
            await Assert.That(field.Validate("  Night Watch  ")).IsNull().Because(screen);
            new ScreenEdits().Apply(field, "  Night Watch  ");
            await Assert.That(read()).IsEqualTo("Night Watch").Because(screen);
        }
    }

    /// <summary>
    /// Duplicates are allowed on purpose. Nothing keys off these names — the engines match on patterns
    /// and <see cref="MacroEngine"/> is keyed by <see cref="Macro.Key"/> — and two sets may each
    /// legitimately hold a rule called <c>Tell</c>. Refusing one would be a rule the configuration
    /// itself doesn't have.
    /// </summary>
    [Test]
    public async Task TwoItemsMayShareAName()
    {
        var sets = Sets();
        sets[0].Triggers.Add(new Trigger { Name = "Spam", Pattern = "guild" });
        var field = TriggersScreenRenderer.Model(sets, 1).FieldAt(0, 1, TriggersScreenRenderer.NameField)!.Value;

        await Assert.That(field.Validate("Tell")).IsNull();
        await Assert.That(new ScreenEdits().Apply(field, "Tell")).IsNull();
        await Assert.That(sets[0].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Tell", "Tell" });
    }

    /// <summary>
    /// End to end through the keyboard, on the screen the whole feature was missing from: ⏎ on a rule
    /// opens its name, typing replaces it, ⏎ commits, and the rule list draws the new one.
    /// </summary>
    [Test]
    public async Task Enter_OnARuleOpensItsNameAndTypingRenamesIt()
    {
        var sets = Sets();
        var session = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));

        session.Handle(Key(ConsoleKey.Enter));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("Tell");

        for (var i = 0; i < "Tell".Length; i++)
        {
            session.Handle(Key(ConsoleKey.Backspace));
        }

        foreach (var c in "Whisper")
        {
            session.Handle(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
        }

        session.Handle(Key(ConsoleKey.Enter));

        await Assert.That(session.IsEditing).IsFalse();
        await Assert.That(sets[0].Triggers[0].Name).IsEqualTo("Whisper");
        await Assert.That(TriggersScreenRenderer.RulesColumn(sets, 0).Any(l => l.Contains("Whisper"))).IsTrue();

        // And leaving the screen keeps it, like any other committed field: ⏎ was the confirmation.
        await Assert.That(session.Handle(Key(ConsoleKey.Escape))).IsEqualTo(ScreenAction.Close);
        session.Edits.Revert();
        await Assert.That(sets[0].Triggers[0].Name).IsEqualTo("Whisper");
    }

    /// <summary>
    /// The rename must not disturb the matcher. <see cref="Trigger.Pattern"/> and
    /// <see cref="Alias.CaseSensitive"/> deliberately drop the compiled regex on write; a name has no
    /// such derived state, and this pins that it stays that way rather than being assumed.
    /// </summary>
    [Test]
    public async Task RenamingARuleDoesNotDisturbWhatItMatches()
    {
        var sets = Sets();
        var trigger = sets[0].Triggers[0];
        var alias = sets[0].Aliases[0];
        await Assert.That(trigger.Regex.IsMatch("she tells you hi")).IsTrue();
        await Assert.That(alias.Regex.IsMatch("k")).IsTrue();

        new ScreenEdits().Apply(
            TriggersScreenRenderer.Model(sets, 0).FieldAt(0, 0, TriggersScreenRenderer.NameField)!.Value, "Whisper");
        new ScreenEdits().Apply(
            AliasesScreenRenderer.Model(sets, 0).FieldAt(0, 0, AliasesScreenRenderer.NameField)!.Value, "kill");

        await Assert.That(trigger.Regex.IsMatch("she tells you hi")).IsTrue();
        await Assert.That(alias.Regex.IsMatch("k")).IsTrue();
    }
}
