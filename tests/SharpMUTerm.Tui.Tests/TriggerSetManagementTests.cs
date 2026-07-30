using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// Managing the trigger sets themselves, which nothing could do: they organise the whole automation
/// configuration — F2's triggers, F3's aliases, F4's bindings and F6's timers all live inside one — and
/// the only set operation anywhere in the UI was ticking a character's assignment on F5.
/// <para>
/// Three gaps, and they are closed in two places. Sets are made, renamed and unmade on <b>F5's
/// trigger-set pane</b>, because that pane is the only view of sets as objects rather than as a column
/// of somebody's rules — and because it is therefore the only place an <em>empty</em> set can be seen at
/// all. Items move between sets through a <b>set field on the item's own row</b> on F2/F3/F4/F6, which
/// makes "which set owns this" an edit like any other: the same closed dropdown, the same validator, the
/// same undo log.
/// </para>
/// <para>
/// The hazards are the reason most of these exist. A set's name is a <em>key</em> — a character opts in
/// by name — so renaming or deleting one reaches into every world's characters, and undo has to restore
/// position and not merely existence.
/// </para>
/// </summary>
public class TriggerSetManagementTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    private static ConsoleKeyInfo Char(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Description = "channel routing",
            Triggers = new List<Trigger>
            {
                new() { Name = "Tell", Pattern = "tells you" },
                new() { Name = "Spam", Pattern = "guild" },
            },
            Aliases = new List<Alias> { new() { Name = "gr", Pattern = "^gr$", Substitution = "greet" } },
            Macros = new List<Macro> { new() { Name = "Look", Key = "F1", Command = "look" } },
            Timers = new List<TimerDefinition> { new() { Name = "ping", IntervalSeconds = 30, Command = "look" } },
        },
        new TriggerSet
        {
            Name = "Trade",
            Description = "auction watch",
            Triggers = new List<Trigger> { new() { Name = "Offer", Pattern = "offers" } },
        },
    };

    private static List<WorldDefinition> Worlds() => new()
    {
        new WorldDefinition
        {
            Name = "Aetherfall",
            Host = "aetherfall.mux",
            Characters = new List<CharacterDefinition>
            {
                new() { Name = "Corvid", TriggerSets = new List<string> { "Comms", "Trade" } },
                new() { Name = "Rookery", TriggerSets = new List<string> { "Trade" } },
            },
        },
    };

    private static ScreenButton ButtonNamed(ScreenModel model, int pane, string label)
    {
        for (var i = 0; i < 32; i++)
        {
            if (model.ButtonAt(pane, i) is { } button && button.Label == label)
            {
                return button;
            }
        }

        throw new InvalidOperationException($"no button labelled '{label}' in pane {pane}");
    }

    private static SettingsSession WorldsSession(List<WorldDefinition> worlds, List<TriggerSet> sets) =>
        new(selection => WorldsScreenRenderer.Model(
            worlds,
            sets,
            selection.SelectionIn(WorldsScreenRenderer.WorldsPane),
            selection.SelectionIn(WorldsScreenRenderer.CharactersPane),
            selection.SelectionIn(WorldsScreenRenderer.TriggerSetsPane)));

    // ---- creating ---------------------------------------------------------------------------------

    /// <summary>
    /// The first gap: nothing made a set. A new one is empty and lands under the cursor, ready to be
    /// named — the same contract every other <c>[[+ …]]</c> on these screens keeps.
    /// </summary>
    [Test]
    public async Task AddingASetAppendsAnEmptyOneAndLeavesTheCursorOnIt()
    {
        var sets = Sets();
        var edits = new ScreenEdits();
        var model = WorldsScreenRenderer.Model(Worlds(), sets, 0, 0);

        var select = edits.Apply(ButtonNamed(model, WorldsScreenRenderer.TriggerSetsPane, WorldsScreenRenderer.AddSetLabel));

        await Assert.That(sets.Select(s => s.Name)).IsEquivalentTo(new[] { "Comms", "Trade", "New Set" });
        await Assert.That(sets[2].Triggers).IsEmpty();
        await Assert.That(select).IsEqualTo(2);

        // Kept: making a set destroyed nothing, so it is not among the deletions the closing review asks
        // about. Deleting it is how you change your mind.
        await Assert.That(edits.HasDeletions).IsFalse();
        edits.Revert();
        await Assert.That(sets.Select(s => s.Name)).IsEquivalentTo(new[] { "Comms", "Trade", "New Set" });
    }

    /// <summary>
    /// A set's name is a key, not a label — a character opts in by name — so two called <c>New Set</c>
    /// could not both be assigned to anything. The button therefore takes the first free name rather
    /// than the same one twice.
    /// </summary>
    [Test]
    public async Task AddingTwiceGivesEachSetAFreeName()
    {
        var sets = Sets();
        var worlds = Worlds();
        var edits = new ScreenEdits();

        edits.Apply(ButtonNamed(
            WorldsScreenRenderer.Model(worlds, sets, 0, 0),
            WorldsScreenRenderer.TriggerSetsPane,
            WorldsScreenRenderer.AddSetLabel));
        edits.Apply(ButtonNamed(
            WorldsScreenRenderer.Model(worlds, sets, 0, 0),
            WorldsScreenRenderer.TriggerSetsPane,
            WorldsScreenRenderer.AddSetLabel));

        await Assert.That(sets.Select(s => s.Name))
            .IsEquivalentTo(new[] { "Comms", "Trade", "New Set", "New Set 2" });
    }

    /// <summary>
    /// End to end through the keyboard, which is what the button is for: ⇥ into the set pane, ↓ past
    /// the two sets onto <c>[[+ set]]</c>, ⏎ to run it — which makes the set and opens its name. The
    /// cursor lands on the set that was made, not on the button that made it.
    /// <para>
    /// The set pane is the <em>fourth</em> ⇥ stop, not the third: ⇥ walks the panes in the order they
    /// are drawn, and the security checkboxes are drawn above the characters list even though their
    /// pane index is appended last. <see cref="ScreenPaneNavigationTests"/> pins that order itself.
    /// </para>
    /// </summary>
    [Test]
    public async Task Enter_OnAddSetLeavesTheCursorOnTheNewSetReadyToNameIt()
    {
        var sets = Sets();
        var session = WorldsSession(Worlds(), sets);

        session.Handle(Key(ConsoleKey.Tab));
        session.Handle(Key(ConsoleKey.Tab));
        session.Handle(Key(ConsoleKey.Tab));
        await Assert.That(session.Focus().Pane).IsEqualTo(WorldsScreenRenderer.TriggerSetsPane);

        // Rows 0-1 are the two sets; row 2 is [+ set] and row 3 is [- del], so End would delete rather
        // than add — ↓ ↓ is how the add button is reached, and the selection stays on the last set.
        session.Handle(Key(ConsoleKey.DownArrow));
        session.Handle(Key(ConsoleKey.DownArrow));
        await Assert.That(session.Handle(Key(ConsoleKey.Enter))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(sets).Count().IsEqualTo(3);
        await Assert.That(session.Focus().Index).IsEqualTo(2);
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo(WorldsScreenRenderer.NewSetName);
    }

    // ---- renaming ---------------------------------------------------------------------------------

    /// <summary>
    /// The hazard: <see cref="CharacterDefinition.TriggerSets"/> holds <em>names</em>, so a set renamed
    /// on its own leaves every character that used it pointing at nothing, and the automation stops
    /// silently at the next connect. The rename carries the references with it, in place, so the order
    /// that decides which set wins a conflict survives.
    /// </summary>
    [Test]
    public async Task RenamingASetCarriesEveryCharactersAssignmentWithIt()
    {
        var worlds = Worlds();
        var sets = Sets();
        var (corvid, rookery) = (worlds[0].Characters[0], worlds[0].Characters[1]);
        var field = WorldsScreenRenderer.Model(worlds, sets, 0, 0)
            .FieldAt(WorldsScreenRenderer.TriggerSetsPane, 0, WorldsScreenRenderer.SetNameField)!.Value;

        await Assert.That(new ScreenEdits().Apply(field, "Channels")).IsNull();

        await Assert.That(sets[0].Name).IsEqualTo("Channels");
        await Assert.That(corvid.TriggerSets).IsEquivalentTo(new[] { "Channels", "Trade" });
        await Assert.That(corvid.TriggerSets[0]).IsEqualTo("Channels");
        await Assert.That(rookery.TriggerSets).IsEquivalentTo(new[] { "Trade" });
    }

    /// <summary>
    /// A rename is a committed field and is kept when the screen closes, references and all. It used to be
    /// put back by the screen-wide revert; the reason that test existed — the two halves of a rename must
    /// not come apart — is now pinned in the direction that can still happen: the set and every character
    /// pointing at it are renamed together, and stay that way.
    /// </summary>
    [Test]
    public async Task ARenameAndItsAssignmentsAreKeptTogether()
    {
        var worlds = Worlds();
        var sets = Sets();
        var corvid = worlds[0].Characters[0];
        var edits = new ScreenEdits();
        var field = WorldsScreenRenderer.Model(worlds, sets, 0, 0)
            .FieldAt(WorldsScreenRenderer.TriggerSetsPane, 0, WorldsScreenRenderer.SetNameField)!.Value;

        await Assert.That(edits.Apply(field, "Channels")).IsNull();
        edits.Revert();

        await Assert.That(sets[0].Name).IsEqualTo("Channels");
        await Assert.That(corvid.TriggerSets[0]).IsEqualTo("Channels");
        await Assert.That(corvid.TriggerSets[1]).IsEqualTo("Trade");
    }

    /// <summary>
    /// A set's name being a key is exactly why it is the one name on these screens that must be unique:
    /// <see cref="AppConfiguration.ResolveTriggerSets"/> takes the first match, so the second of two
    /// <c>Comms</c> could never be assigned to anything. Refused case-insensitively, as the resolver
    /// matches — and nothing is written, so a refused rename cannot half-happen.
    /// </summary>
    [Test]
    public async Task RenamingASetOntoAnothersNameIsRefusedAndWritesNothing()
    {
        var worlds = Worlds();
        var sets = Sets();
        var corvid = worlds[0].Characters[0];
        var field = WorldsScreenRenderer.Model(worlds, sets, 0, 0)
            .FieldAt(WorldsScreenRenderer.TriggerSetsPane, 0, WorldsScreenRenderer.SetNameField)!.Value;

        await Assert.That(field.Validate("trade")).IsNotNull();
        await Assert.That(new ScreenEdits().Apply(field, "Trade")).IsNotNull();

        await Assert.That(sets[0].Name).IsEqualTo("Comms");
        await Assert.That(corvid.TriggerSets).IsEquivalentTo(new[] { "Comms", "Trade" });

        // Its own name is not "taken" by itself — committing a set's name unchanged has to be legal, or
        // the field could never be closed with ⏎ on the value it opened with.
        await Assert.That(field.Validate("Comms")).IsNull();
        await Assert.That(field.Validate(" ")).IsNotNull();
    }

    /// <summary>A set's description is editable too, and is the row's second field.</summary>
    [Test]
    public async Task ASetsDescriptionIsTheRowsSecondField()
    {
        var sets = Sets();
        var row = WorldsScreenRenderer.Model(Worlds(), sets, 0, 0)
            .RowAt(WorldsScreenRenderer.TriggerSetsPane, 0);

        await Assert.That(row.FieldCount).IsEqualTo(2);
        await Assert.That(row.FieldAt(WorldsScreenRenderer.SetDescriptionField)!.Value.Get())
            .IsEqualTo("channel routing");

        await Assert.That(new ScreenEdits().Apply(
            row.FieldAt(WorldsScreenRenderer.SetDescriptionField)!.Value, "chat + pages")).IsNull();
        await Assert.That(sets[0].Description).IsEqualTo("chat + pages");
    }

    // ---- deleting ---------------------------------------------------------------------------------

    /// <summary>
    /// Deleting a set is the destructive edit with the longest reach: the set goes, and so does every
    /// character's opt-in, because an assignment naming a set that no longer exists resolves to nothing
    /// while still being drawn as an assignment.
    /// </summary>
    [Test]
    public async Task DeletingASetStripsEveryCharactersAssignment()
    {
        var worlds = Worlds();
        var sets = Sets();
        var (corvid, rookery) = (worlds[0].Characters[0], worlds[0].Characters[1]);

        new ScreenEdits().Apply(WorldsScreenRenderer.Model(worlds, sets, 0, 0, selectedSet: 1)
            .RemoveIn(WorldsScreenRenderer.TriggerSetsPane)!.Value);

        await Assert.That(sets.Select(s => s.Name)).IsEquivalentTo(new[] { "Comms" });
        await Assert.That(corvid.TriggerSets).IsEquivalentTo(new[] { "Comms" });
        await Assert.That(rookery.TriggerSets).IsEmpty();
    }

    /// <summary>
    /// Undo restores <em>position</em>, on both halves: the set back at its index in the configuration,
    /// and each assignment back at its index inside the character that held it. Put back on the end,
    /// either would come back at a priority it did not leave at.
    /// </summary>
    [Test]
    public async Task UndoingADeleteRestoresTheSetAndItsAssignmentsWhereTheyWere()
    {
        var worlds = Worlds();
        var sets = Sets();
        var corvid = worlds[0].Characters[0];
        var comms = sets[0];
        var edits = new ScreenEdits();

        edits.Apply(WorldsScreenRenderer.Model(worlds, sets, 0, 0, selectedSet: 0)
            .RemoveIn(WorldsScreenRenderer.TriggerSetsPane)!.Value);
        await Assert.That(sets.Select(s => s.Name)).IsEquivalentTo(new[] { "Trade" });
        await Assert.That(corvid.TriggerSets).IsEquivalentTo(new[] { "Trade" });

        edits.Revert();

        await Assert.That(sets[0]).IsSameReferenceAs(comms);
        await Assert.That(sets[1].Name).IsEqualTo("Trade");
        await Assert.That(corvid.TriggerSets[0]).IsEqualTo("Comms");
        await Assert.That(corvid.TriggerSets[1]).IsEqualTo("Trade");
    }

    /// <summary>
    /// The destructive button names its victim, as every targeted button on these screens does — and
    /// describes what goes with it, which is what the closing review reads out. A set's cost is the rules
    /// inside it and the characters whose automation it is; neither is visible once it is gone.
    /// </summary>
    [Test]
    public async Task TheDeleteSetButtonNamesTheSetItWouldRemoveAndWhatGoesWithIt()
    {
        var button = WorldsScreenRenderer.Model(Worlds(), Sets(), 0, 0, selectedSet: 1)
            .RemoveIn(WorldsScreenRenderer.TriggerSetsPane)!.Value;

        await Assert.That(button.Target).IsEqualTo("Trade");
        await Assert.That(button.Kind).IsEqualTo(ScreenButtonKind.Remove);

        var described = button.Describe!();
        await Assert.That(described).Contains("trigger set Trade");
        await Assert.That(described).Contains("1 rule");
        await Assert.That(described).Contains("2 characters using it");
    }

    /// <summary>
    /// A world's removal describes the characters it takes with it, in the words the user asked for:
    /// "Delete Aetherfall and its 2 characters?" rather than an abstract count of deletions.
    /// </summary>
    [Test]
    public async Task TheDeleteWorldButtonNamesTheCharactersItWouldTakeWithIt()
    {
        var button = WorldsScreenRenderer.Model(Worlds(), Sets(), 0, 0)
            .RemoveIn(WorldsScreenRenderer.WorldsPane)!.Value;

        await Assert.That(button.Describe!()).IsEqualTo("world Aetherfall and its 2 characters");
    }

    // ---- moving an item between sets ---------------------------------------------------------------

    /// <summary>
    /// The third gap: a rule created in the wrong set stayed there. It is the last field of the rule's
    /// own row, so moving one is an edit like any other — and the list it offers is <em>closed</em>,
    /// because a set is a real object with characters assigned to it and a name typed here could only
    /// ever be a set that does not exist.
    /// </summary>
    [Test]
    public async Task ATriggersSetFieldOffersEveryConfiguredSetAndNothingElse()
    {
        var sets = Sets();
        var field = TriggersScreenRenderer.Model(sets, 0)
            .FieldAt(0, 0, TriggersScreenRenderer.SetField)!.Value;

        await Assert.That(field.Get()).IsEqualTo("Comms");
        await Assert.That(field.Choices).IsEquivalentTo(new[] { "Comms", "Trade" });
        await Assert.That(field.ClosedChoices).IsTrue();
        await Assert.That(field.Validate("Nowhere")).IsNotNull();
        await Assert.That(field.Validate("trade")).IsNull();
    }

    /// <summary>
    /// Committing it moves the rule out of one set's list and onto the end of the other's — and the
    /// cursor goes with it, because the pane is flattened across every set and the row genuinely changes
    /// position. Left behind, the cursor would be pointing at whatever slid into the vacated row.
    /// </summary>
    [Test]
    public async Task MovingATriggerToAnotherSetTakesTheCursorWithIt()
    {
        var sets = Sets();
        var spam = sets[0].Triggers[1];
        var field = TriggersScreenRenderer.Model(sets, 1)
            .FieldAt(0, 1, TriggersScreenRenderer.SetField)!.Value;

        await Assert.That(new ScreenEdits().Apply(field, "Trade")).IsNull();

        await Assert.That(sets[0].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Tell" });
        await Assert.That(sets[1].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Offer", "Spam" });
        await Assert.That(sets[1].Triggers[1]).IsSameReferenceAs(spam);

        // Flattened, the rule is now the third row: Tell, Offer, Spam.
        await Assert.That(field.Follow!()).IsEqualTo(2);
    }

    /// <summary>
    /// A move is a committed field and is kept: the rule stays in the set it was moved to, and closing the
    /// screen does not drag it back. It replaces a test asserting the reverse — a move was undone by the
    /// screen-wide revert — which is the behaviour the whole of this change removes. A move destroys
    /// nothing, so there is nothing to review either.
    /// </summary>
    [Test]
    public async Task AMoveBetweenSetsIsKept()
    {
        var sets = Sets();
        sets[0].Triggers.Add(new Trigger { Name = "Third", Pattern = "third" });
        var edits = new ScreenEdits();
        var field = TriggersScreenRenderer.Model(sets, 1)
            .FieldAt(0, 1, TriggersScreenRenderer.SetField)!.Value;

        await Assert.That(edits.Apply(field, "Trade")).IsNull();
        await Assert.That(sets[0].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Tell", "Third" });

        await Assert.That(edits.HasDeletions).IsFalse();
        edits.Revert();

        await Assert.That(sets[0].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Tell", "Third" });
        await Assert.That(sets[1].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Offer", "Spam" });
    }

    /// <summary>
    /// Committing the set a rule is already in does nothing at all. Left as a remove-and-append it would
    /// silently drop the rule to the bottom of its own set every time the field was closed on the value
    /// it opened with, which is what ⏎ does.
    /// </summary>
    [Test]
    public async Task CommittingTheSetAnItemIsAlreadyInLeavesItWhereItIs()
    {
        var sets = Sets();
        var field = TriggersScreenRenderer.Model(sets, 0)
            .FieldAt(0, 0, TriggersScreenRenderer.SetField)!.Value;

        await Assert.That(new ScreenEdits().Apply(field, "Comms")).IsNull();

        await Assert.That(sets[0].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Tell", "Spam" });
        await Assert.That(sets[0].Triggers[0].Name).IsEqualTo("Tell");
    }

    /// <summary>
    /// All four flattened screens carry it, because all four show one kind of a set's contents and all
    /// four could put a new one in the wrong place. Each is the row's <em>last</em> field, so no ordinal
    /// the renderers, the snapshot key scripts or the other tests address was renumbered to make room.
    /// </summary>
    [Test]
    public async Task EveryFlattenedScreenCanMoveItsItemsBetweenSets()
    {
        var sets = Sets();

        var alias = AliasesScreenRenderer.Model(sets, 0).FieldAt(0, 0, AliasesScreenRenderer.SetField)!.Value;
        await Assert.That(new ScreenEdits().Apply(alias, "Trade")).IsNull();
        await Assert.That(sets[0].Aliases).IsEmpty();
        await Assert.That(sets[1].Aliases.Select(a => a.Name)).IsEquivalentTo(new[] { "gr" });

        var timer = TimersScreenRenderer.Model(sets, 0).FieldAt(0, 0, TimersScreenRenderer.SetField)!.Value;
        await Assert.That(new ScreenEdits().Apply(timer, "Trade")).IsNull();
        await Assert.That(sets[0].Timers).IsEmpty();
        await Assert.That(sets[1].Timers.Select(t => t.Name)).IsEquivalentTo(new[] { "ping" });

        var macros = sets.SelectMany(s => s.Macros).ToList();
        var binding = KeypadScreenRenderer.Model(macros, sets, 0)
            .FieldAt(0, 0, KeypadScreenRenderer.SetField)!.Value;
        await Assert.That(new ScreenEdits().Apply(binding, "Trade")).IsNull();
        await Assert.That(sets[0].Macros).IsEmpty();
        await Assert.That(sets[1].Macros.Select(m => m.Name)).IsEquivalentTo(new[] { "Look" });
    }

    /// <summary>
    /// F4 is the one screen that can be handed the bindings without the sets they came from (the header
    /// hints, the tests). With no sets there is no vocabulary to move between and no list to move out
    /// of, so the field is not offered at all — the same condition that withholds that screen's buttons.
    /// </summary>
    [Test]
    public async Task TheKeypadOffersNoSetFieldWhenItWasNotToldWhichSetsTheBindingsCameFrom()
    {
        var row = KeypadScreenRenderer.Model(Sets()[0].Macros).RowAt(0, 0);

        await Assert.That(row.FieldCount).IsEqualTo(3);
        await Assert.That(row.FieldAt(KeypadScreenRenderer.SetField)).IsNull();
    }

    /// <summary>
    /// Through the keyboard: ⏎ opens the rule's name, ⇥ walks to the last field, the dropdown lists the
    /// sets, and ⏎ commits the move with the cursor landing on the row the rule now occupies.
    /// </summary>
    [Test]
    public async Task Tab_ReachesTheSetFieldAndCommittingItMovesTheRule()
    {
        var sets = Sets();
        var session = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));

        session.Handle(Key(ConsoleKey.DownArrow)); // onto "Spam", flattened row 1
        session.Handle(Key(ConsoleKey.Enter));
        for (var i = 0; i < TriggersScreenRenderer.SetField; i++)
        {
            session.Handle(Key(ConsoleKey.Tab));
        }

        var edit = session.Focus().Edit!.Value;
        await Assert.That(edit.Field).IsEqualTo(TriggersScreenRenderer.SetField);
        await Assert.That(edit.Text).IsEqualTo("Comms");
        await Assert.That(edit.ClosedChoices).IsTrue();

        // ↓ walks the drawn list, exactly as it does on every other field with choices.
        session.Handle(Key(ConsoleKey.DownArrow));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("Trade");

        session.Handle(Key(ConsoleKey.Enter));

        await Assert.That(session.IsEditing).IsFalse();
        await Assert.That(sets[1].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Offer", "Spam" });
        await Assert.That(session.Focus().Index).IsEqualTo(2);
    }

    /// <summary>
    /// ⇥ off the set field wraps to the row's name — and it has to be the <em>moved</em> rule's name.
    /// The model is re-projected after a commit for exactly this: stepping through the projection the
    /// key arrived with would open the next field of whichever row used to be under the cursor.
    /// </summary>
    [Test]
    public async Task Tab_AfterAMoveOpensTheMovedRulesOwnNextField()
    {
        var sets = Sets();
        var session = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));

        session.Handle(Key(ConsoleKey.DownArrow));
        session.Handle(Key(ConsoleKey.Enter));
        for (var i = 0; i < TriggersScreenRenderer.SetField; i++)
        {
            session.Handle(Key(ConsoleKey.Tab));
        }

        session.Handle(Key(ConsoleKey.DownArrow));
        session.Handle(Key(ConsoleKey.Tab)); // commits the move and wraps to field 0

        var edit = session.Focus().Edit!.Value;
        await Assert.That(edit.Field).IsEqualTo(TriggersScreenRenderer.NameField);
        await Assert.That(edit.Text).IsEqualTo("Spam");
    }

    // ---- an empty set is visible -------------------------------------------------------------------

    /// <summary>
    /// A set with none of a screen's items owns none of that screen's rows, so a flattened pane draws it
    /// nowhere at all — and a set you have just made holds nothing at all. All four screens say it is
    /// there instead. It is markup and not a row: it stands for a set rather than an item, so the cursor
    /// cannot land on it and the pane's row counts are untouched.
    /// </summary>
    [Test]
    public async Task AnEmptySetIsNamedOnEveryFlattenedScreen()
    {
        var sets = Sets();
        sets.Add(new TriggerSet { Name = "Combat" });
        var macros = sets.SelectMany(s => s.Macros).ToList();

        await Assert.That(TriggersScreenRenderer.RulesColumn(sets, 0)
            .Any(l => l.Contains("▪ Combat — no triggers", StringComparison.Ordinal))).IsTrue();
        await Assert.That(AliasesScreenRenderer.ListColumn(sets, 0)
            .Any(l => l.Contains("▪ Combat — no aliases", StringComparison.Ordinal))).IsTrue();
        await Assert.That(TimersScreenRenderer.ListColumn(sets, 0)
            .Any(l => l.Contains("▪ Combat — no timers", StringComparison.Ordinal))).IsTrue();
        await Assert.That(KeypadScreenRenderer.HotkeysColumn(macros, null, sets, 0)
            .Any(l => l.Contains("▪ Combat — no bindings", StringComparison.Ordinal))).IsTrue();

        // Trade holds triggers but no aliases, so it is named on F3 and not on F2 — the placeholder is
        // per screen, because "empty" means empty of the thing that screen edits.
        await Assert.That(AliasesScreenRenderer.ListColumn(sets, 0)
            .Any(l => l.Contains("▪ Trade — no aliases", StringComparison.Ordinal))).IsTrue();
        await Assert.That(TriggersScreenRenderer.RulesColumn(sets, 0)
            .Any(l => l.Contains("▪ Trade — no triggers", StringComparison.Ordinal))).IsFalse();

        // And it costs no cursor stops: three sets, four rules between them, unchanged.
        await Assert.That(TriggersScreenRenderer.Model(sets, 0).ListSizes[0]).IsEqualTo(3);
    }

    /// <summary>
    /// F5 is where an empty set is <em>always</em> visible, whatever it is empty of, because that pane
    /// lists the sets themselves. Its inventory counts everything a set can hold, so a set carrying two
    /// timers and no triggers cannot read as though it were empty.
    /// </summary>
    [Test]
    public async Task TheTriggerSetPaneListsEverySetAndSaysWhatEachHolds()
    {
        var sets = Sets();
        sets.Add(new TriggerSet { Name = "Combat" });
        var character = Worlds()[0].Characters[0];

        var lines = WorldsScreenRenderer.TriggersColumn(character, sets, ScreenPalette.Accent);

        await Assert.That(lines.Any(l => l.Contains("▪ Combat", StringComparison.Ordinal)
            && l.Contains("empty", StringComparison.Ordinal))).IsTrue();

        // Comms holds two triggers, an alias, a macro and a timer — five, not the two the row used to
        // report by counting triggers alone.
        await Assert.That(lines.Any(l => l.Contains("▪ Comms", StringComparison.Ordinal)
            && l.Contains("5 rules", StringComparison.Ordinal))).IsTrue();

        await Assert.That(lines.Any(l => l.Contains("[[+ set]]", StringComparison.Ordinal))).IsTrue();
    }
}
