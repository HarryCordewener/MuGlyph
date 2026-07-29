using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The button rows on the F5 screen — <c>+ world</c>, <c>- del</c>, <c>+ add character</c>,
/// <c>⧉ duplicate</c>, <c>- remove</c>. They were painted but inert; these assert what each one now
/// does, that a deletion is undone *back into its own place* rather than onto the end, and that a
/// duplicate shares nothing with the character it copied.
/// <para>
/// Deletion is deliberately not behind a confirmation prompt. Nothing a settings screen does reaches
/// disk until Save, Esc replays the undo log — which now includes the removed row and its index — and
/// a second modal state inside a screen that already has one (an open field edit) would double the
/// key-routing rules for a change that is already reversible. The undo is asserted here instead.
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

    [Test]
    public async Task EachListPaneEndsInItsOwnButtons()
    {
        var worlds = Worlds();
        var model = WorldsScreenRenderer.Model(worlds, Sets(), selectedWorld: 0, selectedCharacter: 0);

        // 2 worlds + [+ world] + [- del]; 2 characters + [+ add] + [⧉ duplicate] + [- remove]; 1 set;
        // and the world's 2 security checkboxes, a pane with no buttons of its own.
        await Assert.That(model.ListSizes).IsEquivalentTo(new[] { 2, 2, 1, 2 });
        await Assert.That(model.Sizes).IsEquivalentTo(new[] { 4, 5, 1, 2 });

        await Assert.That(model.ButtonAt(0, 2)!.Value.Label).IsEqualTo(WorldsScreenRenderer.AddWorldLabel);
        await Assert.That(model.ButtonAt(0, 3)!.Value.Label).IsEqualTo(WorldsScreenRenderer.RemoveWorldLabel);
        await Assert.That(model.ButtonAt(1, 2)!.Value.Label).IsEqualTo(WorldsScreenRenderer.AddCharacterLabel);
        await Assert.That(model.ButtonAt(1, 3)!.Value.Label)
            .IsEqualTo(WorldsScreenRenderer.DuplicateCharacterLabel);
        await Assert.That(model.ButtonAt(1, 4)!.Value.Label).IsEqualTo(WorldsScreenRenderer.RemoveCharacterLabel);

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
        await Assert.That(model.Sizes).IsEquivalentTo(new[] { 4, 1, 0, 2 });
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

        edits.Revert();
        await Assert.That(worlds.Count).IsEqualTo(2);
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

        edits.Apply(ButtonNamed(
            WorldsScreenRenderer.Model(worlds, Sets(), selectedWorld: 1, selectedCharacter: -1),
            0,
            WorldsScreenRenderer.RemoveWorldLabel));

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

        edits.Apply(ButtonNamed(
            WorldsScreenRenderer.Model(worlds, Sets(), 0, selectedCharacter: 1),
            1,
            WorldsScreenRenderer.RemoveCharacterLabel));

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

        edits.Revert();
        await Assert.That(characters.Count).IsEqualTo(2);
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
    /// End to end through the keyboard: ⏎ on a button row runs it and leaves the cursor on the new row,
    /// where the next ⏎ opens that row's name — which is the whole reason a new row is worth adding.
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
        await Assert.That(session.IsEditing).IsFalse();

        session.Handle(Key(ConsoleKey.Enter));
        await Assert.That(session.IsEditing).IsTrue();
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("New World");

        // Esc abandons the buffer, then Esc cancels the screen — and cancelling unmakes the world.
        session.Handle(Key(ConsoleKey.Escape));
        await Assert.That(session.Handle(Key(ConsoleKey.Escape))).IsEqualTo(ScreenAction.Cancel);
        session.Edits.Revert();
        await Assert.That(worlds.Count).IsEqualTo(2);
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
    /// The interaction the button rows live or die by. A pane's buttons sit past the end of its list,
    /// so reaching them with ↑↓ alone would drag the selection to the last row and leave
    /// <c>- del</c> pointed at a world the user never chose. End steps over the list without
    /// re-anchoring, so the button acts on the row that is still on screen.
    /// </summary>
    [Test]
    public async Task End_ReachesAPanesButtonsWithoutMovingItsSelection()
    {
        var worlds = Worlds();
        worlds.Add(new WorldDefinition { Name = "Third" });
        var sets = Sets();
        var session = new SettingsSession(selection => WorldsScreenRenderer.Model(
            worlds, sets, selection.SelectionIn(0), selection.SelectionIn(1)));

        // Sit on the *first* world, then jump to the last row of the pane — the delete button.
        await Assert.That(session.Selection.SelectionIn(0)).IsEqualTo(0);
        session.Handle(Key(ConsoleKey.End));

        await Assert.That(session.Focus().Index).IsEqualTo(4); // 3 worlds + [+ world] + [- del]
        await Assert.That(session.Selection.SelectionIn(0)).IsEqualTo(0);

        session.Handle(Key(ConsoleKey.Enter));

        await Assert.That(worlds.Select(w => w.Name)).IsEquivalentTo(new[] { "Empty", "Third" });

        session.Edits.Revert();
        await Assert.That(worlds.Select(w => w.Name)).IsEquivalentTo(new[] { "Aardwolf", "Empty", "Third" });
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

        await Assert.That(left.Any(l => l.Contains("[[- del]]") && l.Contains("Empty"))).IsTrue();
        await Assert.That(left.Any(l => l.Contains("[[+ world]]") && l.Contains("Empty"))).IsFalse();
        await Assert.That(right.Any(l => l.Contains("[[- remove]]") && l.Contains("Kaz"))).IsTrue();
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
        var button = ScreenButton.Remove("- del", list, 4);

        var select = edits.Apply(button);

        await Assert.That(list).IsEquivalentTo(new[] { "one" });
        await Assert.That(select).IsNull();

        edits.Revert();
        await Assert.That(list).IsEquivalentTo(new[] { "one" });
    }
}
