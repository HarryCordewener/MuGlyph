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

        // Two rules, then the pane's own [+ trigger] / [⧉ duplicate] / [- del]. The list count is what
        // every index below addresses and is unchanged; the buttons are appended after it.
        await Assert.That(model.ListSizes[0]).IsEqualTo(2);
        await Assert.That(model.Sizes[0]).IsEqualTo(5);

        // Three checkbox rows: gag and stop-processing, plus case sensitivity — which F3 has always
        // offered on an alias and F2 arbitrarily did not. It is appended rather than inserted, so the
        // two rows below still mean what every other assertion here says they mean.
        await Assert.That(model.Sizes[1]).IsEqualTo(3);

        model.ToggleAt(0, 1)!.Value.Flip();
        await Assert.That(sets[0].Triggers[1].Enabled).IsTrue();

        model.ToggleAt(1, 0)!.Value.Flip();
        await Assert.That(sets[0].Triggers[0].Actions.Gag).IsTrue();

        model.ToggleAt(1, 1)!.Value.Flip();
        await Assert.That(sets[0].Triggers[0].StopProcessing).IsTrue();

        model.ToggleAt(1, 2)!.Value.Flip();
        await Assert.That(sets[0].Triggers[0].CaseSensitive).IsTrue();
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

        // One alias, then [+ alias] / [⧉ duplicate] / [- del].
        await Assert.That(model.ListSizes).IsEquivalentTo(new[] { 1, 1 });
        await Assert.That(model.Sizes).IsEquivalentTo(new[] { 4, 1 });

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

        // One timer, then [+ timer] / [- del] — no duplicate, deliberately (see TimersScreenRenderer).
        await Assert.That(model.ListSizes).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(model.Sizes).IsEquivalentTo(new[] { 3, 2 });

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

        // Handed no sets, the screen is the list and nothing else: a macro's home is a set, and without
        // one there is nowhere to add a binding to and no list to remove one from.
        await Assert.That(model.Sizes[0]).IsEqualTo(1);

        model.ToggleAt(0, 0)!.Value.Flip();
        await Assert.That(macros[0].Enabled).IsFalse();
    }

    /// <summary>
    /// Handed the sets the bindings live in, the pane grows the same buttons every other list screen
    /// has: one binding, then <c>[+ binding]</c> and <c>[- del]</c>. There is no duplicate — a copy
    /// would land on the key its original already holds, and the second macro on a key never fires.
    /// </summary>
    [Test]
    public async Task Keypad_GrowsItsButtonsOnceItKnowsWhichSetsTheBindingsLiveIn()
    {
        var sets = Sets();
        var model = KeypadScreenRenderer.Model(sets[0].Macros, sets, selected: 0);

        await Assert.That(model.ListSizes[0]).IsEqualTo(1);
        await Assert.That(model.Sizes[0]).IsEqualTo(3);
        await Assert.That(model.ButtonAt(0, 1)!.Value.Label).IsEqualTo(KeypadScreenRenderer.AddBindingLabel);
        await Assert.That(model.ButtonAt(0, 2)!.Value.Label).IsEqualTo(KeypadScreenRenderer.RemoveBindingLabel);
    }

    [Test]
    public async Task Worlds_HasWorldsThenCharactersThenTriggerSets()
    {
        var worlds = Worlds();
        var sets = Sets();
        var model = WorldsScreenRenderer.Model(worlds, sets, selectedWorld: 0, selectedCharacter: 0);

        // Four panes: the fourth is the selected world's two security checkboxes, appended so that the
        // three that were here keep the indices everything below (and every other test) addresses.
        await Assert.That(model.PaneCount).IsEqualTo(4);

        // Two worlds then [+ world] / [- del]; two characters then [+ add] / [⧉ duplicate] /
        // [- remove]; one trigger set then [+ set] / [- del]. Buttons are appended after each list, so
        // every index below still addresses the same item it always did — which is what the list counts
        // assert independently of the total.
        await Assert.That(model.ListSizes).IsEquivalentTo(new[] { 2, 2, 1, 2 });
        await Assert.That(model.Sizes).IsEquivalentTo(new[] { 4, 5, 3, 2 });

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
        // nothing, so they aren't drawn and ⏎ can't land on a silent no-op. The security pane still
        // holds its two checkboxes: they belong to the world, which is selected, not to a character.
        await Assert.That(model.Sizes).IsEquivalentTo(new[] { 4, 1, 0, 2 });
        await Assert.That(model.ListSizes).IsEquivalentTo(new[] { 2, 0, 0, 2 });
    }

    /// <summary>
    /// The world's two security booleans, which had no UI at all — the screen summarised them in a
    /// read-only line and left the only way to change them in the JSON. Two flags, two checkboxes, two
    /// rows, because a <c>ScreenRow</c> carries at most one checkbox.
    /// </summary>
    [Test]
    public async Task Worlds_SecurityPaneTogglesTlsAndCertificateValidation()
    {
        var worlds = Worlds();
        var world = worlds[0];
        var model = WorldsScreenRenderer.Model(worlds, Sets(), 0, 0);

        model.ToggleAt(WorldsScreenRenderer.SecurityPane, 0)!.Value.Flip();
        await Assert.That(world.UseTls).IsTrue();

        model.ToggleAt(WorldsScreenRenderer.SecurityPane, 1)!.Value.Flip();
        await Assert.That(world.AllowInvalidCertificates).IsTrue();
    }

    /// <summary>
    /// Both security toggles go through the undo log like everything else on these screens, so Esc puts
    /// certificate validation back on. This is the one row where an edit that outlived a cancelled
    /// screen would be a security change nobody agreed to.
    /// </summary>
    [Test]
    public async Task Worlds_SecurityTogglesAreUndone_ByCancellingTheScreen()
    {
        var worlds = Worlds();
        var world = worlds[0];
        world.UseTls = true;
        var edits = new ScreenEdits();
        var model = WorldsScreenRenderer.Model(worlds, Sets(), 0, 0);

        edits.Apply(model.ToggleAt(WorldsScreenRenderer.SecurityPane, 0)!.Value);
        edits.Apply(model.ToggleAt(WorldsScreenRenderer.SecurityPane, 1)!.Value);
        await Assert.That(world.UseTls).IsFalse();
        await Assert.That(world.AllowInvalidCertificates).IsTrue();

        edits.Revert();

        await Assert.That(world.UseTls).IsTrue();
        await Assert.That(world.AllowInvalidCertificates).IsFalse();
    }

    /// <summary>
    /// The security pane belongs to the <em>world</em>, so it follows the WORLDS selection rather than
    /// the character's — flipping TLS on the second world must not touch the first.
    /// </summary>
    [Test]
    public async Task Worlds_SecurityPaneFollowsTheSelectedWorld()
    {
        var worlds = Worlds();

        WorldsScreenRenderer.Model(worlds, Sets(), selectedWorld: 1, selectedCharacter: 0)
            .ToggleAt(WorldsScreenRenderer.SecurityPane, 0)!.Value.Flip();

        await Assert.That(worlds[1].UseTls).IsTrue();
        await Assert.That(worlds[0].UseTls).IsFalse();
    }

    [Test]
    public async Task Options_NavigableRowsSkipSectionHeadersAndSpacers()
    {
        var screen = OptionsScreenRenderer.TextAnsiScreen();
        var model = OptionsScreenRenderer.Model(screen);

        // 7 display rows: 2 section headers + 1 spacer + 4 options. It was 8/5 while F7 still carried
        // "ambiguous width"; that row named a measurement policy the framework does not expose, so it
        // went, and both the display count and the navigable count drop by exactly the one row.
        await Assert.That(screen.Rows.Count).IsEqualTo(7);
        await Assert.That(model.PaneCount).IsEqualTo(1);
        await Assert.That(model.Sizes[0]).IsEqualTo(4);
    }

    /// <summary>
    /// F7 is four checkboxes and nothing else now — every row is a toggle, so there is no value row
    /// for ⏎ to open. Asserted here rather than only in the renderer test, because it is what makes
    /// <c>HasEditableRow</c> false for this screen and so what removes <c>⏎ edit</c> from its header.
    /// </summary>
    [Test]
    public async Task Options_TextAnsiRowsWriteBackToTheTextSettings()
    {
        var text = new TextSettings();
        var model = OptionsScreenRenderer.Model(OptionsScreenRenderer.TextAnsiScreen(text));

        model.ToggleAt(0, 0)!.Value.Flip();
        await Assert.That(text.StripIncomingColour).IsTrue();

        model.ToggleAt(0, 2)!.Value.Flip();
        await Assert.That(text.UnderlineHyperlinks).IsFalse();

        model.ToggleAt(0, 3)!.Value.Flip();
        await Assert.That(text.EmojiSubstitution).IsFalse();

        await Assert.That(model.HasEditableRow).IsFalse();
    }

    [Test]
    public async Task Options_InputRowsWriteBackToTheInputSettings()
    {
        var input = new InputSettings();
        var model = OptionsScreenRenderer.Model(OptionsScreenRenderer.InputScreen(input));

        // Six rows now, in two sections. The first two are the checkboxes this screen has always had and
        // they keep their ordinals — spellcheck went with the feature it described, and nothing has been
        // inserted above them. The third is the history credential guard, which arrived with the ⌃R
        // surface that made a recorded password reachable. The COMMAND LINE section adds the two heights
        // and the second bar's default; the heights are typed values, which is what makes this screen
        // editable at all.
        await Assert.That(model.Sizes[0]).IsEqualTo(6);

        model.ToggleAt(0, 0)!.Value.Flip();
        await Assert.That(input.LocalEcho).IsFalse();

        model.ToggleAt(0, 1)!.Value.Flip();
        await Assert.That(input.KeepDrafts).IsFalse();

        model.ToggleAt(0, 2)!.Value.Flip();
        await Assert.That(input.ExcludeCredentials).IsFalse();

        await Assert.That(model.HasEditableRow).IsTrue();
    }

    /// <summary>
    /// The command-line rows write back the way the checkboxes do: the two heights through their
    /// fields, the second bar's default through its toggle. Asserted here rather than only in the
    /// renderer, because a row that draws a number and stores it nowhere is exactly what this screen's
    /// removed spellcheck settings were.
    /// </summary>
    [Test]
    public async Task Options_CommandLineRowsWriteBackToTheInputSettings()
    {
        var input = new InputSettings();
        var model = OptionsScreenRenderer.Model(OptionsScreenRenderer.InputScreen(input));
        var edits = new ScreenEdits();

        await Assert.That(edits.Apply(model.FieldAt(0, 3, 0)!.Value, "6")).IsNull();
        await Assert.That(input.Rows).IsEqualTo(6);

        await Assert.That(edits.Apply(model.FieldAt(0, 4, 0)!.Value, "12")).IsNull();
        await Assert.That(input.MaxRows).IsEqualTo(12);

        model.ToggleAt(0, 5)!.Value.Flip();
        await Assert.That(input.SecondBar).IsTrue();
    }

    /// <summary>
    /// A height outside the range the input area can honour is refused rather than stored: the control
    /// clamps whatever it is handed, so a field that accepted 0 would show a number the bar was quietly
    /// ignoring.
    /// </summary>
    [Test]
    public async Task Options_CommandLineHeightsRefuseValuesTheBarCannotHonour()
    {
        var input = new InputSettings();
        var model = OptionsScreenRenderer.Model(OptionsScreenRenderer.InputScreen(input));
        var height = model.FieldAt(0, 3, 0)!.Value;
        var edits = new ScreenEdits();

        await Assert.That(edits.Apply(height, "0")).IsNotNull();
        await Assert.That(edits.Apply(height, "21")).IsNotNull();
        await Assert.That(input.Rows).IsEqualTo(3);
    }

    /// <summary>
    /// Logging is two more fields of the character's own row — the format (whose <c>None</c> is "off",
    /// so one control covers one stored value) and the folder. This replaces F9's screen, whose rows
    /// edited whichever character happened to be active.
    /// <para>
    /// The count is six because the password and the connect line are fields of this row too, drawn
    /// between the name and the on-connect line. The ordinals are addressed by name, which is what let
    /// them be inserted in drawn order rather than appended past the log values — see
    /// <see cref="WorldsScreenRenderer.PasswordField"/>.
    /// </para>
    /// </summary>
    [Test]
    public async Task Worlds_TheCharacterRowCarriesItsLogFormatAndFolder()
    {
        var worlds = Worlds();
        var character = worlds[0].Characters[0];
        character.Logging = new LoggingSettings { Format = LogFormat.Html, Directory = "/logs/kaz" };
        var model = WorldsScreenRenderer.Model(worlds, Sets(), 0, 0);
        var row = model.RowAt(WorldsScreenRenderer.CharactersPane, 0);

        await Assert.That(row.FieldCount).IsEqualTo(6);
        await Assert.That(row.FieldAt(WorldsScreenRenderer.CharacterNameField)!.Value.Get()).IsEqualTo("Kaz");
        await Assert.That(row.FieldAt(WorldsScreenRenderer.LogFormatField)!.Value.Get()).IsEqualTo("Html");
        await Assert.That(row.FieldAt(WorldsScreenRenderer.LogDirectoryField)!.Value.Get()).IsEqualTo("/logs/kaz");

        // The format cycles like every other enum field, and None is how logging is turned off.
        await Assert.That(row.FieldAt(WorldsScreenRenderer.LogFormatField)!.Value.Choices)
            .IsEquivalentTo(new[] { "None", "Plain", "Html", "Both" });
    }

    /// <summary>
    /// The bug the move exists to kill: an edit made on this screen reaches the character whose row it
    /// was made on, and no other. F9 resolved "the active character, or else the first one configured"
    /// and never said which, so the same screen wrote to a different character's log depending on what
    /// was connected.
    /// </summary>
    [Test]
    public async Task Worlds_ALogEditReachesTheSelectedCharacterAndNoOther()
    {
        var worlds = Worlds();
        var (kaz, mira) = (worlds[0].Characters[0], worlds[0].Characters[1]);
        var edits = new ScreenEdits();

        var second = WorldsScreenRenderer.Model(worlds, Sets(), 0, 1)
            .FieldAt(WorldsScreenRenderer.CharactersPane, 1, WorldsScreenRenderer.LogFormatField)!.Value;
        await Assert.That(edits.Apply(second, "Both")).IsNull();

        await Assert.That(mira.Logging.Format).IsEqualTo(LogFormat.Both);
        await Assert.That(kaz.Logging.Format).IsEqualTo(LogFormat.None);

        edits.Revert();

        await Assert.That(mira.Logging.Format).IsEqualTo(LogFormat.None);
    }

    /// <summary>
    /// The folder is optional, and blank means null — "unset, use the per-session default" — rather
    /// than an empty string, so the two spellings of "no folder" cannot drift apart in config.
    /// </summary>
    [Test]
    public async Task Worlds_ABlankLogFolderIsStoredAsNull()
    {
        var worlds = Worlds();
        var character = worlds[0].Characters[0];
        character.Logging.Directory = "/logs/kaz";
        var field = WorldsScreenRenderer.Model(worlds, Sets(), 0, 0)
            .FieldAt(WorldsScreenRenderer.CharactersPane, 0, WorldsScreenRenderer.LogDirectoryField)!.Value;

        await Assert.That(new ScreenEdits().Apply(field, "   ")).IsNull();

        await Assert.That(character.Logging.Directory).IsNull();
    }
}
