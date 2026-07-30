using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The button rows on the F5 screen — <c>[+ world]</c>, <c>[+ add character]</c>, <c>[⧉ duplicate]</c>,
/// and the <c>Del</c> rows. These assert what each one does, that a deletion can be undone *back into
/// its own place* rather than onto the end, and that a duplicate shares nothing with the character it
/// copied.
/// <para>
/// Deletion is deliberately not behind a per-press confirmation. It goes into the screen's deletion log
/// instead, and the screen asks once on the way out whether to keep the batch of them
/// (<see cref="ScreenEditReview"/>) — so deleting three characters is one question rather than three,
/// and the undo asserted here is what "no, put them back" runs. Additions and committed values raise
/// nothing: they are kept, which is what <see cref="ScreenEdits"/>' scope rule is about.
/// </para>
/// <para>
/// The <c>Del</c> rows are also where "only the last world can be deleted" lived. They are drawn and are
/// not cursor stops; <see cref="NoReachableRowIsADestructiveButton"/> and
/// <see cref="TheFirstWorldOfSeveralCanBeDeletedFromTheKeyboard"/> are the two halves of that fix.
/// </para>
/// </summary>
public class ScreenButtonTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet { Name = "Comms", Triggers = new List<Trigger> { new() { Name = "T", Pattern = "x" } } },
    };

    private static List<WorldDefinition> Worlds() => new()
    {
        new WorldDefinition
        {
            Name = "Aardwolf",
            Host = "aardmud.org",
            Characters = new List<CharacterDefinition>
            {
                new() { Name = "Kaz", TriggerSets = new List<string> { "Comms" } },
                new() { Name = "Mira" },
            },
        },
        new WorldDefinition { Name = "Empty", Host = "example.org" },
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

    /// <summary>
    /// A pane's removal, reached the way <see cref="SettingsSession"/> reaches it — by asking the pane
    /// for it. Removals carry no label of their own any more (they are drawn as the key that runs them),
    /// so there is nothing to look one up by, and asking the model is what Delete does anyway.
    /// </summary>
    private static ScreenButton Removal(ScreenModel model, int pane) =>
        model.RemoveIn(pane) ?? throw new InvalidOperationException($"pane {pane} offers no removal");

    [Test]
    public async Task EachListPaneEndsInItsOwnButtons()
    {
        var worlds = Worlds();
        var model = WorldsScreenRenderer.Model(worlds, Sets(), selectedWorld: 0, selectedCharacter: 0);

        // 2 worlds + [+ world] + Del; 2 characters + [+ add] + [⧉ duplicate] + Del; 1 set + [+ set] +
        // Del; and the world's 2 security checkboxes, a pane with no buttons of its own. The list counts
        // are what every index below addresses and are unchanged by any of it.
        //
        // Sizes counts *cursor stops*, and a removal is no longer one — so each pane with a Del row
        // reports one fewer than it draws (was { 4, 5, 3, 2 }). That is the fix for "only the last world
        // can be deleted", not a weakening: RowCount still sees the drawn row, and ButtonAt still
        // resolves it, so everything the removal has to do it still does.
        await Assert.That(model.ListSizes).IsEquivalentTo(new[] { 2, 2, 1, 2 });
        await Assert.That(model.Sizes).IsEquivalentTo(new[] { 3, 4, 2, 2 });
        await Assert.That(model.RowCount(0)).IsEqualTo(4);
        await Assert.That(model.RowCount(1)).IsEqualTo(5);
        await Assert.That(model.RowCount(2)).IsEqualTo(3);

        await Assert.That(model.ButtonAt(0, 2)!.Value.Label).IsEqualTo(WorldsScreenRenderer.AddWorldLabel);
        await Assert.That(model.ButtonAt(0, 3)!.Value.Label).IsEqualTo(ScreenButton.RemoveKeyLabel);
        await Assert.That(model.ButtonAt(1, 2)!.Value.Label).IsEqualTo(WorldsScreenRenderer.AddCharacterLabel);
        await Assert.That(model.ButtonAt(1, 3)!.Value.Label)
            .IsEqualTo(WorldsScreenRenderer.DuplicateCharacterLabel);
        await Assert.That(model.ButtonAt(1, 4)!.Value.Label).IsEqualTo(ScreenButton.RemoveKeyLabel);
        await Assert.That(model.ButtonAt(2, 1)!.Value.Label).IsEqualTo(WorldsScreenRenderer.AddSetLabel);
        await Assert.That(model.ButtonAt(2, 2)!.Value.Label).IsEqualTo(ScreenButton.RemoveKeyLabel);

        // A world's own rows are still where they were — the buttons come after the list, so giving the
        // pane buttons doesn't renumber the rows the cursor navigates by.
        await Assert.That(model.ButtonAt(0, 0)).IsNull();
        await Assert.That(model.FieldAt(0, 0, 0)!.Value.Get()).IsEqualTo("Aardwolf");
    }

    /// <summary>
    /// A button that would act on nothing isn't drawn at all. A world with no characters can only be
    /// added to, so ⏎ can never land on a <c>duplicate</c> or <c>remove</c> that silently no-ops.
    /// </summary>
    [Test]
    public async Task APaneWithNothingSelectedOffersOnlyItsAddButton()
    {
        var model = WorldsScreenRenderer.Model(Worlds(), Sets(), selectedWorld: 1, selectedCharacter: 0);

        await Assert.That(model.ListSizes).IsEquivalentTo(new[] { 2, 0, 0, 2 });
        await Assert.That(model.Sizes).IsEquivalentTo(new[] { 3, 1, 0, 2 }); // was { 4, 1, 0, 2 }
        await Assert.That(model.ButtonAt(1, 0)!.Value.Label).IsEqualTo(WorldsScreenRenderer.AddCharacterLabel);
        await Assert.That(model.ButtonAt(1, 1)).IsNull();
    }

    /// <summary>
    /// A renderer handed a fixed projection (an array) must not offer a button whose only effect would
    /// be to throw — arrays report <c>IsReadOnly</c> through <c>IList&lt;T&gt;</c>.
    /// </summary>
    [Test]
    public async Task AReadOnlyWorldListOffersNoAddButton()
    {
        var model = WorldsScreenRenderer.Model(
            Array.Empty<WorldDefinition>(), Array.Empty<TriggerSet>(), -1, -1);

        await Assert.That(model.Sizes[0]).IsEqualTo(0);
    }

    [Test]
    public async Task AddWorld_AppendsABlankWorldAndAsksForTheCursorOnIt()
    {
        var worlds = Worlds();
        var edits = new ScreenEdits();
        var model = WorldsScreenRenderer.Model(worlds, Sets(), 0, 0);

        var select = edits.Apply(ButtonNamed(model, 0, WorldsScreenRenderer.AddWorldLabel));

        await Assert.That(worlds.Count).IsEqualTo(3);
        await Assert.That(worlds[2].Name).IsEqualTo("New World");
        await Assert.That(select).IsEqualTo(2);

        // Nothing was destroyed, so nothing is logged for the closing review and the new world stays.
        await Assert.That(edits.HasDeletions).IsFalse();
        edits.Revert();
        await Assert.That(worlds.Count).IsEqualTo(3);
    }

    /// <summary>
    /// A deletion's undo has to restore the row's *place*, not merely its existence: the list order is
    /// what the screen navigates by, and putting a cancelled deletion back on the end would be a
    /// second, invisible edit riding along with the first.
    /// </summary>
    [Test]
    public async Task RemoveWorld_UndoPutsItBackAtItsOwnIndex()
    {
        var worlds = Worlds();
        worlds.Insert(0, new WorldDefinition { Name = "First" });
        var edits = new ScreenEdits();

        edits.Apply(Removal(
            WorldsScreenRenderer.Model(worlds, Sets(), selectedWorld: 1, selectedCharacter: -1), 0));

        await Assert.That(worlds.Select(w => w.Name)).IsEquivalentTo(new[] { "First", "Empty" });

        edits.Revert();

        await Assert.That(worlds.Select(w => w.Name)).IsEquivalentTo(new[] { "First", "Aardwolf", "Empty" });
    }

    [Test]
    public async Task RemoveCharacter_UndoPutsItBackAtItsOwnIndex()
    {
        var worlds = Worlds();
        var characters = worlds[0].Characters;
        characters.Add(new CharacterDefinition { Name = "Tal" });
        var edits = new ScreenEdits();

        edits.Apply(Removal(WorldsScreenRenderer.Model(worlds, Sets(), 0, selectedCharacter: 1), 1));

        await Assert.That(characters.Select(c => c.Name)).IsEquivalentTo(new[] { "Kaz", "Tal" });

        edits.Revert();

        await Assert.That(characters.Select(c => c.Name)).IsEquivalentTo(new[] { "Kaz", "Mira", "Tal" });
    }

    /// <summary>
    /// The whole point of <c>duplicate</c>: the copy must be a copy. An aliased one would look right
    /// and then follow every later edit of its original around.
    /// </summary>
    [Test]
    public async Task DuplicateCharacter_IsADeepCopyWithItsOwnName()
    {
        var worlds = Worlds();
        var characters = worlds[0].Characters;
        var original = characters[0];
        original.Logging = new LoggingSettings { Format = LogFormat.Html, Directory = "/logs/kaz" };
        var edits = new ScreenEdits();

        var select = edits.Apply(ButtonNamed(
            WorldsScreenRenderer.Model(worlds, Sets(), 0, selectedCharacter: 0),
            1,
            WorldsScreenRenderer.DuplicateCharacterLabel));

        await Assert.That(characters.Count).IsEqualTo(3);
        await Assert.That(select).IsEqualTo(2);

        var copy = characters[2];
        await Assert.That(copy.Name).IsEqualTo("Kaz copy");
        await Assert.That(ReferenceEquals(copy, original)).IsFalse();
        await Assert.That(ReferenceEquals(copy.TriggerSets, original.TriggerSets)).IsFalse();
        await Assert.That(ReferenceEquals(copy.Logging, original.Logging)).IsFalse();

        copy.TriggerSets.Add("Combat");
        copy.Logging.Directory = "/logs/copy";
        copy.AutoLogin = true;

        await Assert.That(original.TriggerSets).IsEquivalentTo(new[] { "Comms" });
        await Assert.That(original.Logging.Directory).IsEqualTo("/logs/kaz");
        await Assert.That(original.AutoLogin).IsFalse();

        // Committed, and therefore kept: the review's undo only reaches deletions.
        edits.Revert();
        await Assert.That(characters.Count).IsEqualTo(3);
    }

    /// <summary>
    /// Sessions are keyed <c>world.character</c>, so two characters of one world may not share a name.
    /// Duplicating twice has to keep finding a free one rather than colliding on "copy".
    /// </summary>
    [Test]
    public async Task DuplicatingTwiceGivesEachCopyAFreeName()
    {
        var worlds = Worlds();
        var characters = worlds[0].Characters;
        var edits = new ScreenEdits();

        edits.Apply(ButtonNamed(
            WorldsScreenRenderer.Model(worlds, Sets(), 0, 0), 1, WorldsScreenRenderer.DuplicateCharacterLabel));
        edits.Apply(ButtonNamed(
            WorldsScreenRenderer.Model(worlds, Sets(), 0, 0), 1, WorldsScreenRenderer.DuplicateCharacterLabel));

        await Assert.That(characters.Select(c => c.Name))
            .IsEquivalentTo(new[] { "Kaz", "Mira", "Kaz copy", "Kaz copy 2" });
    }

    /// <summary>
    /// End to end through the keyboard: ⏎ on a button row runs it, leaves the cursor on the new row,
    /// <em>and</em> opens that row's name — which is the whole reason a new row is worth adding.
    /// <para>
    /// The name used to want a second ⏎, and that ⏎ was never in doubt: what has just been made is
    /// called <c>New World</c> and the only thing to do with it is say what it really is. The cursor
    /// landing on the row is still asserted, unchanged, beside the buffer now being open on it.
    /// </para>
    /// </summary>
    [Test]
    public async Task Enter_OnAButtonRowRunsItAndLeavesTheCursorOnTheNewRow()
    {
        var worlds = Worlds();
        var sets = Sets();
        var session = new SettingsSession(selection => WorldsScreenRenderer.Model(
            worlds, sets, selection.CursorIn(0), selection.CursorIn(1)));

        // Rows 0-1 are the two worlds; row 2 is [+ world].
        session.Handle(Key(ConsoleKey.DownArrow));
        session.Handle(Key(ConsoleKey.DownArrow));
        await Assert.That(session.Handle(Key(ConsoleKey.Enter))).IsEqualTo(ScreenAction.Redraw);

        await Assert.That(worlds.Count).IsEqualTo(3);
        await Assert.That(session.Focus().Index).IsEqualTo(2);
        await Assert.That(session.IsEditing).IsTrue();
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("New World");
        await Assert.That(session.Focus().Edit!.Value.Field).IsEqualTo(WorldsScreenRenderer.WorldNameField);

        // Esc abandons the buffer — the name goes back to the one the row was created with — and a
        // second Esc closes the screen. The world stays: an addition destroys nothing, so it is not
        // reviewed and not undone. (It used to be: Esc closed *and* unmade the world, which is the same
        // silent-discard bug as the address that "went back to the old setting".)
        session.Handle(Key(ConsoleKey.Escape));
        await Assert.That(session.Handle(Key(ConsoleKey.Escape))).IsEqualTo(ScreenAction.Close);
        await Assert.That(session.Edits.HasDeletions).IsFalse();
        session.Edits.Revert();
        await Assert.That(worlds.Count).IsEqualTo(3);
        await Assert.That(worlds[2].Name).IsEqualTo("New World");
    }

    /// <summary>
    /// A button is activated by ⏎ but doesn't *edit* anything, so it must not make a screen advertise
    /// <c>⏎ edit</c> — the same honesty rule the header hints are already held to.
    /// </summary>
    [Test]
    public async Task AButtonRowAloneDoesNotMakeAScreenClaimAnEditor()
    {
        var list = new List<string>();
        var buttons = new ScreenModel(new[]
        {
            ScreenRow.Of(ScreenButton.Add("+ thing", list, () => "thing")),
        });

        await Assert.That(buttons.HasEditableRow).IsFalse();
        await Assert.That(buttons.RowAt(0, 0).IsActivatable).IsTrue();
    }

    /// <summary>
    /// End reaches a pane's <em>building</em> button and stops there. The last cursor stop of the WORLDS
    /// pane is <c>[[+ world]]</c>, not the removal past it: the removal is drawn and is unreachable, and
    /// ⏎ on the row End lands on adds a world rather than deleting one.
    /// <para>
    /// It used to land on <c>[[- del]]</c> — index 4 of 3 worlds + add + del — which is the row this
    /// change exists to take out of the walk.
    /// </para>
    /// </summary>
    [Test]
    public async Task End_ReachesAPanesAddButtonAndStopsBeforeTheRemoval()
    {
        var worlds = Worlds();
        worlds.Add(new WorldDefinition { Name = "Third" });
        var sets = Sets();
        var session = new SettingsSession(selection => WorldsScreenRenderer.Model(
            worlds, sets, selection.SelectionIn(0), selection.SelectionIn(1)));

        // Sit on the *first* world, then jump to the last row of the pane.
        await Assert.That(session.Selection.SelectionIn(0)).IsEqualTo(0);
        session.Handle(Key(ConsoleKey.End));

        await Assert.That(session.Focus().Index).IsEqualTo(3); // 3 worlds + [+ world]; was 4, with [- del]
        await Assert.That(session.Selection.SelectionIn(0)).IsEqualTo(0);

        session.Handle(Key(ConsoleKey.Enter));

        await Assert.That(worlds.Select(w => w.Name))
            .IsEquivalentTo(new[] { "Aardwolf", "Empty", "Third", "New World" });
    }

    /// <summary>
    /// <b>Bug: "because it passes the last world, that one is the only one that is valid for deletion."</b>
    /// No reachable cursor position in any pane of any list screen runs a destructive button — so there
    /// is no route by which walking down to a button block can delete the wrong row, on any of them.
    /// <para>
    /// Before this change every one of these panes ended in a reachable <c>[[- del]]</c>, and reaching it
    /// with ↑↓ dragged the selection to the last item on the way (<see cref="ScreenSelection.Anchor"/>) —
    /// so the button deleted the final row whichever one you had chosen. It cannot be fixed by
    /// remembering where the cursor has been: the last list row visited before the buttons is always the
    /// last row of the list.
    /// </para>
    /// </summary>
    [Test]
    public async Task NoReachableRowIsADestructiveButton()
    {
        var worlds = Worlds();
        var sets = Sets();
        var macros = sets.SelectMany(s => s.Macros).ToList();

        // Every list kind populated, so each of the five screens really does offer a removal — otherwise
        // "no reachable row is one" would pass by there being none anywhere.
        sets[0].Aliases.Add(new Alias { Name = "gr", Pattern = "^gr$", Substitution = "greet" });
        sets[0].Timers.Add(new TimerDefinition { Name = "ping", IntervalSeconds = 30, Command = "look" });
        sets[0].Macros.Add(new Macro { Name = "Look", Key = "F1", Command = "look" });
        macros = sets.SelectMany(s => s.Macros).ToList();

        var screens = new (string Name, ScreenModel Model)[]
        {
            ("F2 triggers", TriggersScreenRenderer.Model(sets, 0)),
            ("F3 aliases", AliasesScreenRenderer.Model(sets, 0)),
            ("F4 keypad", KeypadScreenRenderer.Model(macros, sets, 0)),
            ("F5 worlds", WorldsScreenRenderer.Model(worlds, sets, 0, 0)),
            ("F6 timers", TimersScreenRenderer.Model(sets, 0)),
        };

        foreach (var (name, model) in screens)
        {
            // Each screen really does offer one, or the claim below passes vacuously.
            await Assert.That(model.HasRemovableRow).IsTrue().Because(name);

            for (var pane = 0; pane < model.PaneCount; pane++)
            {
                for (var row = 0; row < model.Sizes[pane]; row++)
                {
                    await Assert.That(model.ButtonAt(pane, row)?.Kind)
                        .IsNotEqualTo(ScreenButtonKind.Remove)
                        .Because($"{name} pane {pane} row {row}");
                }
            }
        }

        // And F5's three lists each have one, which is the screen the bug was reported against.
        var f5 = screens.Single(s => s.Name == "F5 worlds").Model;
        await Assert.That(f5.RemoveIn(WorldsScreenRenderer.WorldsPane)).IsNotNull();
        await Assert.That(f5.RemoveIn(WorldsScreenRenderer.CharactersPane)).IsNotNull();
        await Assert.That(f5.RemoveIn(WorldsScreenRenderer.TriggerSetsPane)).IsNotNull();
    }

    /// <summary>
    /// The other half, and the assertion whose absence let the bug ship: with more than one world, the
    /// <em>first</em> one can be deleted from the keyboard — Delete on the row the cursor is on, which is
    /// the row the eye is on and the one the detail column is showing.
    /// </summary>
    [Test]
    public async Task TheFirstWorldOfSeveralCanBeDeletedFromTheKeyboard()
    {
        var worlds = Worlds();
        worlds.Add(new WorldDefinition { Name = "Third" });
        var sets = Sets();
        var session = new SettingsSession(selection => WorldsScreenRenderer.Model(
            worlds, sets, selection.SelectionIn(0), selection.SelectionIn(1)));

        await Assert.That(session.Handle(Key(ConsoleKey.Delete))).IsEqualTo(ScreenAction.Redraw);

        await Assert.That(worlds.Select(w => w.Name)).IsEquivalentTo(new[] { "Empty", "Third" });
        await Assert.That(session.Edits.Deletions.Single()).Contains("Aardwolf");
    }

    /// <summary>And the middle one, which no ordering accident could have made pass.</summary>
    [Test]
    public async Task SoCanTheMiddleOne()
    {
        var worlds = Worlds();
        worlds.Add(new WorldDefinition { Name = "Third" });
        var sets = Sets();
        var session = new SettingsSession(selection => WorldsScreenRenderer.Model(
            worlds, sets, selection.SelectionIn(0), selection.SelectionIn(1)));

        session.Handle(Key(ConsoleKey.DownArrow));
        session.Handle(Key(ConsoleKey.Delete));

        await Assert.That(worlds.Select(w => w.Name)).IsEquivalentTo(new[] { "Aardwolf", "Third" });
    }

    /// <summary>
    /// Walking the list *does* move the selection, because the detail column follows the cursor — so
    /// the button names the row it would act on rather than leaving the user to infer it.
    /// </summary>
    [Test]
    public async Task ATargetedButtonNamesTheRowItWouldActOn()
    {
        var worlds = Worlds();
        var left = WorldsScreenRenderer.WorldsColumn(worlds, selectedWorld: 1);
        var right = WorldsScreenRenderer.DetailColumn(worlds, Sets(), 0, 0, ScreenPalette.Accent);

        // The removal row names the key and its victim rather than posing as a chip you could land on:
        // it is not a cursor stop any more, so brackets there would be an affordance for something the
        // keyboard cannot reach. The building buttons keep their chips, because ⏎ really does press them.
        await Assert.That(left.Any(l => l.Contains("Del") && l.Contains("removes") && l.Contains("Empty")))
            .IsTrue();
        await Assert.That(left.Any(l => l.Contains("[[- del]]"))).IsFalse();
        await Assert.That(left.Any(l => l.Contains("[[+ world]]") && l.Contains("Empty"))).IsFalse();
        await Assert.That(right.Any(l => l.Contains("Del") && l.Contains("removes") && l.Contains("Kaz")))
            .IsTrue();
        await Assert.That(right.Any(l => l.Contains("[[⧉ duplicate]]") && l.Contains("Kaz"))).IsTrue();
    }

    /// <summary>
    /// A cursor parked on a button row must still leave the screen showing the row it is about to act
    /// on — blanking the detail column there would also take the button out from under the cursor.
    /// </summary>
    [Test]
    public async Task ACursorPastTheEndOfAListStillResolvesToItsLastRow()
    {
        var worlds = Worlds();

        await Assert.That(WorldsScreenRenderer.Resolve(worlds, 3, 0)).IsEqualTo((1, -1));
        await Assert.That(WorldsScreenRenderer.Resolve(worlds, 0, 4)).IsEqualTo((0, 1));
        await Assert.That(WorldsScreenRenderer.Resolve(worlds, -1, -1)).IsEqualTo((-1, -1));
        await Assert.That(WorldsScreenRenderer.HasCharacter(worlds, 0, 9)).IsTrue();
    }

    /// <summary>A button asked to remove a row that is no longer there does nothing, undo included.</summary>
    [Test]
    public async Task RemovingARowThatIsNoLongerThereIsANoOp()
    {
        var list = new List<string> { "one" };
        var edits = new ScreenEdits();
        var button = ScreenButton.Remove(list, 4);

        var select = edits.Apply(button);

        await Assert.That(list).IsEquivalentTo(new[] { "one" });
        await Assert.That(select).IsNull();

        // And nothing to review either: a press that destroyed nothing hands back no undo, so the
        // closing prompt has nothing to name.
        await Assert.That(edits.HasDeletions).IsFalse();
        edits.Revert();
        await Assert.That(list).IsEquivalentTo(new[] { "one" });
    }
}
