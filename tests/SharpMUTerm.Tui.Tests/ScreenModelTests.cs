using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What each settings screen actually offers the keyboard: how many panes it has, how many rows each
/// pane holds, and which config field the checkbox on a row writes to. These are the promises the
/// header hints make, so they are asserted per screen rather than only through the shared session.
/// </summary>
public class ScreenModelTests
{
    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Description = "chat routing",
            Triggers = new List<Trigger>
            {
                new() { Name = "Tell", Pattern = "tells you", Enabled = true, Actions = new TriggerActions() },
                new() { Name = "Spam", Pattern = "guild", Enabled = false, Actions = new TriggerActions { Gag = true } },
            },
            Aliases = new List<Alias> { new() { Name = "k", Pattern = "^k$", Substitution = "kill" } },
            Macros = new List<Macro> { new() { Name = "look", Key = "Num5", Command = "look" } },
            Timers = new List<TimerDefinition>
            {
                new() { Name = "ping", IntervalSeconds = 30, Command = "look", Enabled = true },
            },
        },
    };

    private static List<WorldDefinition> Worlds() => new()
    {
        new WorldDefinition
        {
            Name = "Aardwolf",
            Host = "aardmud.org",
            Characters = new List<CharacterDefinition>
            {
                new() { Name = "Kaz", AutoLogin = false, TriggerSets = new List<string> { "Comms" } },
                new() { Name = "Mira" },
            },
        },
        new WorldDefinition { Name = "Empty", Host = "example.org" },
    };

    [Test]
    public async Task Triggers_HasARuleListAndTheSelectedRulesToggles()
    {
        var sets = Sets();
        var model = TriggersScreenRenderer.Model(sets, selectedTrigger: 0);

        await Assert.That(model.PaneCount).IsEqualTo(2);
        await Assert.That(model.Sizes[0]).IsEqualTo(2);
        await Assert.That(model.Sizes[1]).IsEqualTo(2);

        model.ToggleAt(0, 1)!.Value.Flip();
        await Assert.That(sets[0].Triggers[1].Enabled).IsTrue();

        model.ToggleAt(1, 0)!.Value.Flip();
        await Assert.That(sets[0].Triggers[0].Actions.Gag).IsTrue();

        model.ToggleAt(1, 1)!.Value.Flip();
        await Assert.That(sets[0].Triggers[0].StopProcessing).IsTrue();
    }

    [Test]
    public async Task Triggers_EditorPaneIsEmptyWhenNothingIsSelected()
    {
        var model = TriggersScreenRenderer.Model(Sets(), selectedTrigger: -1);

        await Assert.That(model.Sizes[1]).IsEqualTo(0);
    }

    [Test]
    public async Task Aliases_ListTogglesEnabled_AndTheEditorTogglesCaseSensitivity()
    {
        var sets = Sets();
        var model = AliasesScreenRenderer.Model(sets, selected: 0);

        await Assert.That(model.PaneCount).IsEqualTo(2);
        await Assert.That(model.Sizes).IsEquivalentTo(new[] { 1, 1 });

        model.ToggleAt(0, 0)!.Value.Flip();
        await Assert.That(sets[0].Aliases[0].Enabled).IsFalse();

        model.ToggleAt(1, 0)!.Value.Flip();
        await Assert.That(sets[0].Aliases[0].CaseSensitive).IsTrue();
    }

    [Test]
    public async Task Aliases_FlippingCaseSensitivityRecompilesTheMatcher()
    {
        var sets = Sets();
        var alias = sets[0].Aliases[0];
        await Assert.That(alias.Regex.IsMatch("K")).IsTrue(); // case-insensitive by default

        AliasesScreenRenderer.Model(sets, selected: 0).ToggleAt(1, 0)!.Value.Flip();

        await Assert.That(alias.Regex.IsMatch("K")).IsFalse();
        await Assert.That(alias.Regex.IsMatch("k")).IsTrue();
    }

    [Test]
    public async Task Timers_ListTogglesEnabled_AndTheEditorTogglesOneShotThenEnabled()
    {
        var sets = Sets();
        var model = TimersScreenRenderer.Model(sets, selected: 0);

        await Assert.That(model.Sizes).IsEquivalentTo(new[] { 1, 2 });

        model.ToggleAt(1, 0)!.Value.Flip();
        await Assert.That(sets[0].Timers[0].OneShot).IsTrue();

        model.ToggleAt(1, 1)!.Value.Flip();
        await Assert.That(sets[0].Timers[0].Enabled).IsFalse();
    }

    [Test]
    public async Task Keypad_IsOnePaneOfMacroToggles()
    {
        var macros = Sets()[0].Macros;
        var model = KeypadScreenRenderer.Model(macros);

        await Assert.That(model.PaneCount).IsEqualTo(1);
        await Assert.That(model.Sizes[0]).IsEqualTo(1);

        model.ToggleAt(0, 0)!.Value.Flip();
        await Assert.That(macros[0].Enabled).IsFalse();
    }

    [Test]
    public async Task Worlds_HasWorldsThenCharactersThenTriggerSets()
    {
        var worlds = Worlds();
        var sets = Sets();
        var model = WorldsScreenRenderer.Model(worlds, sets, selectedWorld: 0, selectedCharacter: 0);

        await Assert.That(model.PaneCount).IsEqualTo(3);

        // Two worlds then [+ world] / [- del]; two characters then [+ add] / [⧉ duplicate] /
        // [- remove]. Buttons are appended after each list, so every index below still addresses
        // the same item it always did.
        await Assert.That(model.Sizes).IsEquivalentTo(new[] { 4, 5, 1 });

        // Worlds are selection only — there is no checkbox on a world row.
        await Assert.That(model.ToggleAt(0, 0)).IsNull();

        model.ToggleAt(1, 1)!.Value.Flip();
        await Assert.That(worlds[0].Characters[1].AutoLogin).IsTrue();
    }

    [Test]
    public async Task Worlds_TriggerSetRowsAssignAndUnassignByName()
    {
        var worlds = Worlds();
        var sets = Sets();
        var character = worlds[0].Characters[0];

        var assigned = WorldsScreenRenderer.Model(worlds, sets, 0, 0).ToggleAt(2, 0)!.Value;
        await Assert.That(assigned.Get()).IsTrue();

        assigned.Flip();
        await Assert.That(character.TriggerSets).IsEmpty();

        WorldsScreenRenderer.Model(worlds, sets, 0, 0).ToggleAt(2, 0)!.Value.Flip();
        await Assert.That(character.TriggerSets).IsEquivalentTo(new[] { "Comms" });
    }

    [Test]
    public async Task Worlds_UnassigningATriggerSetRestoresTheCharactersOwnOrderOnUndo()
    {
        var worlds = Worlds();
        var sets = Sets();
        sets.Add(new TriggerSet { Name = "Combat" });
        var character = worlds[0].Characters[0];
        character.TriggerSets.Insert(0, "Combat");

        var edits = new ScreenEdits();
        edits.Apply(WorldsScreenRenderer.Model(worlds, sets, 0, 0).ToggleAt(2, 0)!.Value);
        await Assert.That(character.TriggerSets).IsEquivalentTo(new[] { "Combat" });

        edits.Revert();

        // Order decides which set wins a conflict, so it has to come back as it was — not "Comms" last.
        await Assert.That(character.TriggerSets[0]).IsEqualTo("Combat");
        await Assert.That(character.TriggerSets[1]).IsEqualTo("Comms");
    }

    [Test]
    public async Task Worlds_CharacterAndTriggerSetPanesAreEmptyForAWorldWithNoCharacters()
    {
        var model = WorldsScreenRenderer.Model(Worlds(), Sets(), selectedWorld: 1, selectedCharacter: 0);

        // The character pane holds one row — [+ add character]. Duplicate and remove would act on
        // nothing, so they aren't drawn and ⏎ can't land on a silent no-op.
        await Assert.That(model.Sizes).IsEquivalentTo(new[] { 4, 1, 0 });
    }

    [Test]
    public async Task Options_NavigableRowsSkipSectionHeadersAndSpacers()
    {
        var screen = OptionsScreenRenderer.TextAnsiScreen();
        var model = OptionsScreenRenderer.Model(screen);

        // 8 display rows: 2 section headers + 1 spacer + 5 options.
        await Assert.That(screen.Rows.Count).IsEqualTo(8);
        await Assert.That(model.PaneCount).IsEqualTo(1);
        await Assert.That(model.Sizes[0]).IsEqualTo(5);
    }

    [Test]
    public async Task Options_TextAnsiRowsWriteBackToTheTextSettings()
    {
        var text = new TextSettings();
        var model = OptionsScreenRenderer.Model(OptionsScreenRenderer.TextAnsiScreen(text));

        model.ToggleAt(0, 0)!.Value.Flip();
        await Assert.That(text.StripIncomingColour).IsTrue();

        model.ToggleAt(0, 2)!.Value.Flip();
        await Assert.That(text.UnderlineHyperlinks).IsFalse();

        // "ambiguous width" is a value row: reachable, but nothing to press yet.
        await Assert.That(model.ToggleAt(0, 4)).IsNull();
    }

    [Test]
    public async Task Options_InputRowsWriteBackToTheInputSettings()
    {
        var input = new InputSettings();
        var model = OptionsScreenRenderer.Model(OptionsScreenRenderer.InputSpellcheckScreen(input));

        model.ToggleAt(0, 0)!.Value.Flip();
        await Assert.That(input.LocalEcho).IsFalse();

        model.ToggleAt(0, 3)!.Value.Flip();
        await Assert.That(input.CheckSpelling).IsFalse();
    }

    /// <summary>
    /// F9's auto-start checkbox lives on the <c>format</c> row itself (row 0) rather than on a third
    /// row of its own: it and the format are one stored value, so Space and ⏎ act on one row. Its
    /// snapshot still restores the format, not the boolean.
    /// </summary>
    [Test]
    public async Task Options_LoggingAutoStartTogglesTheFormat_AndUndoRestoresTheOriginalOne()
    {
        var logging = new LoggingSettings { Format = LogFormat.Html };
        var edits = new ScreenEdits();

        edits.Apply(OptionsScreenRenderer.Model(OptionsScreenRenderer.LoggingScreen(logging)).ToggleAt(0, 0)!.Value);
        await Assert.That(logging.Format).IsEqualTo(LogFormat.None);

        edits.Revert();

        await Assert.That(logging.Format).IsEqualTo(LogFormat.Html);
    }

    [Test]
    public async Task Options_LoggingAutoStartTurnsOnAsPlainWhenNothingWasChosen()
    {
        var logging = new LoggingSettings { Format = LogFormat.None };

        OptionsScreenRenderer.Model(OptionsScreenRenderer.LoggingScreen(logging)).ToggleAt(0, 0)!.Value.Flip();

        await Assert.That(logging.Format).IsEqualTo(LogFormat.Plain);
    }

    /// <summary>
    /// The same row carries both, which is what makes it one setting rather than two: ⏎ opens the
    /// format on the row Space starts and stops logging from.
    /// </summary>
    [Test]
    public async Task Options_LoggingFormatAndAutoStartAreOneRow()
    {
        var model = OptionsScreenRenderer.Model(
            OptionsScreenRenderer.LoggingScreen(new LoggingSettings { Format = LogFormat.Html }));

        await Assert.That(model.Sizes[0]).IsEqualTo(2);
        await Assert.That(model.RowAt(0, 0).Toggle).IsNotNull();
        await Assert.That(model.RowAt(0, 0).FieldCount).IsEqualTo(1);
        await Assert.That(model.FieldAt(0, 0, 0)!.Value.Get()).IsEqualTo("Html");
    }
}
