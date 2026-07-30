using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// Delete on a list row, which is the fix for the one usability hole F5's button rows left behind. A
/// pane's <c>[[- del]]</c> sits past the end of its list, so ↑↓ can only reach it by walking the cursor
/// over every row on the way — and the selection walks with it, which is why
/// <see cref="ScreenSelection.MoveTo"/> exists at all. <c>End</c> steps over the list without
/// re-anchoring and fixes that, but End is a key nothing on screen ever named, so nobody would find it.
/// <para>
/// Delete acts on the row the cursor is already on. It runs the pane's own remove button rather than
/// deleting around it, so the key and the drawn row are one command with one undo and one set of
/// conditions; and the header advertises it only when the model really offers one, which is the same
/// honesty rule <see cref="ScreenCursorTests"/> holds <c>⏎ edit</c> to.
/// </para>
/// </summary>
public class ScreenDeleteKeyTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Triggers = new List<Trigger>
            {
                new() { Name = "Tell", Pattern = "tells you" },
                new() { Name = "Spam", Pattern = "guild" },
            },
            Aliases = new List<Alias> { new() { Name = "gr", Pattern = "^gr$", Substitution = "greet" } },
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
            Characters = new List<CharacterDefinition> { new() { Name = "Corvid" }, new() { Name = "Wren" } },
        },
        new WorldDefinition { Name = "Grapevine", Host = "grapevine.haus" },
    };

    /// <summary>
    /// The interaction the whole change is for: sit on a row, press Delete, and that row goes — no walk
    /// to the end of the list, no key nobody knows about, and the same undo the button hands back.
    /// </summary>
    [Test]
    public async Task Delete_RemovesTheRowTheCursorIsOn()
    {
        var sets = Sets();
        var session = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));

        session.Handle(Key(ConsoleKey.DownArrow));
        await Assert.That(session.Handle(Key(ConsoleKey.Delete))).IsEqualTo(ScreenAction.Redraw);

        await Assert.That(sets[0].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Tell" });

        session.Edits.Revert();
        await Assert.That(sets[0].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Tell", "Spam" });
    }

    /// <summary>The same key, the same result, on all five screens with a list.</summary>
    [Test]
    public async Task Delete_WorksOnEveryScreenThatHasSomethingToRemove()
    {
        var sets = Sets();
        var worlds = Worlds();

        var triggers = new SettingsSession(s => TriggersScreenRenderer.Model(sets, s.SelectionIn(0)));
        var aliases = new SettingsSession(s => AliasesScreenRenderer.Model(sets, s.SelectionIn(0)));
        var timers = new SettingsSession(s => TimersScreenRenderer.Model(sets, s.SelectionIn(0)));
        var keypad = new SettingsSession(s => KeypadScreenRenderer.Model(
            sets.SelectMany(x => x.Macros).ToList(), sets, s.SelectionIn(0)));
        var f5 = new SettingsSession(s => WorldsScreenRenderer.Model(
            worlds, sets, s.SelectionIn(0), s.SelectionIn(1)));

        foreach (var session in new[] { triggers, aliases, timers, keypad, f5 })
        {
            await Assert.That(session.Handle(Key(ConsoleKey.Delete))).IsEqualTo(ScreenAction.Redraw);
        }

        await Assert.That(sets[0].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Spam" });
        await Assert.That(sets[0].Aliases).IsEmpty();
        await Assert.That(sets[0].Timers).IsEmpty();
        await Assert.That(sets[0].Macros).IsEmpty();
        await Assert.That(worlds.Select(w => w.Name)).IsEquivalentTo(new[] { "Grapevine" });
    }

    /// <summary>
    /// Delete belongs to the pane the cursor is in, so on F5 it takes out a character rather than the
    /// world above it — which is exactly the confusion a single global "delete the selection" would
    /// cause on a screen with three panes.
    /// </summary>
    [Test]
    public async Task Delete_ActsOnTheFocusedPane()
    {
        var worlds = Worlds();
        var sets = Sets();
        var session = new SettingsSession(selection => WorldsScreenRenderer.Model(
            worlds, sets, selection.SelectionIn(0), selection.SelectionIn(1)));

        session.Handle(Key(ConsoleKey.Tab));          // the world's security checkboxes, drawn first
        session.Handle(Key(ConsoleKey.Tab));          // into the character pane below them
        session.Handle(Key(ConsoleKey.DownArrow));    // onto the second character
        session.Handle(Key(ConsoleKey.Delete));

        await Assert.That(worlds).Count().IsEqualTo(2);
        await Assert.That(worlds[0].Characters.Select(c => c.Name)).IsEquivalentTo(new[] { "Corvid" });

        session.Edits.Revert();
        await Assert.That(worlds[0].Characters.Select(c => c.Name)).IsEquivalentTo(new[] { "Corvid", "Wren" });
    }

    /// <summary>
    /// On a button row Delete does nothing at all. The cursor there already has ⏎, and the row it would
    /// delete is somewhere else on screen — deleting it from under a cursor that isn't on it is the
    /// surprise the selection anchor exists to prevent.
    /// </summary>
    [Test]
    public async Task Delete_DoesNothingOnAButtonRow()
    {
        var sets = Sets();
        var session = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));

        session.Handle(Key(ConsoleKey.End)); // the last row of the pane is [- del]

        await Assert.That(session.Handle(Key(ConsoleKey.Delete))).IsEqualTo(ScreenAction.None);
        await Assert.That(sets[0].Triggers).Count().IsEqualTo(2);
        await Assert.That(session.Edits.HasDeletions).IsFalse();
    }

    /// <summary>
    /// A pane with no remove button doesn't answer the key either — the key and the drawn row are the
    /// same command, so they are offered under the same conditions. F5's security pane is two checkboxes
    /// belonging to the world above it: there is nothing there to take out of a list.
    /// <para>
    /// This claim used to be made about the trigger-set pane, which was assignment and nothing else.
    /// That pane now owns the sets themselves, so it answers Delete — see
    /// <see cref="Delete_RemovesTheSelectedTriggerSetAndItsAssignments"/>, which is the same rule read
    /// the other way.
    /// </para>
    /// </summary>
    [Test]
    public async Task Delete_IsLeftForTheFrameworkWhereThereIsNothingToRemove()
    {
        var worlds = Worlds();
        var sets = Sets();
        var session = new SettingsSession(selection => WorldsScreenRenderer.Model(
            worlds, sets, selection.SelectionIn(0), selection.SelectionIn(1), selection.SelectionIn(2)));

        // ⇥ walks the panes in the order they are drawn, and the security checkboxes are drawn inside
        // the WORLD block at the top of the detail column — so they are the first stop out of the
        // WORLDS list, not the last.
        session.Handle(Key(ConsoleKey.Tab));

        await Assert.That(session.Focus().Pane).IsEqualTo(WorldsScreenRenderer.SecurityPane);
        await Assert.That(session.Handle(Key(ConsoleKey.Delete))).IsEqualTo(ScreenAction.None);
        await Assert.That(session.Edits.HasDeletions).IsFalse();
    }

    /// <summary>
    /// The other half: the trigger-set pane <em>does</em> answer Delete now, and deleting a set takes
    /// every character's opt-in with it rather than leaving assignments pointing at a set that no longer
    /// exists. Esc puts both back.
    /// </summary>
    [Test]
    public async Task Delete_RemovesTheSelectedTriggerSetAndItsAssignments()
    {
        var worlds = Worlds();
        var sets = Sets();
        var character = worlds[0].Characters[0];
        character.TriggerSets.Add("Comms");
        var session = new SettingsSession(selection => WorldsScreenRenderer.Model(
            worlds, sets, selection.SelectionIn(0), selection.SelectionIn(1), selection.SelectionIn(2)));

        session.Handle(Key(ConsoleKey.Tab));
        session.Handle(Key(ConsoleKey.Tab));
        session.Handle(Key(ConsoleKey.Tab)); // security → characters → the trigger-set pane

        await Assert.That(session.Focus().Pane).IsEqualTo(WorldsScreenRenderer.TriggerSetsPane);
        await Assert.That(session.Handle(Key(ConsoleKey.Delete))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(sets).IsEmpty();
        await Assert.That(character.TriggerSets).IsEmpty();

        session.Edits.Revert();

        await Assert.That(sets.Select(s => s.Name)).IsEquivalentTo(new[] { "Comms" });
        await Assert.That(character.TriggerSets).IsEquivalentTo(new[] { "Comms" });
    }

    /// <summary>
    /// Mid-edit Delete belongs to the buffer, not to the list. It would be a spectacular way to lose a
    /// row: backspacing past the end of a name and deleting the thing you were naming.
    /// </summary>
    [Test]
    public async Task Delete_TakesACharacterOutOfAnOpenBufferRatherThanTheRow()
    {
        var sets = Sets();
        var session = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));

        session.Handle(Key(ConsoleKey.Enter));
        session.Handle(Key(ConsoleKey.Home));
        session.Handle(Key(ConsoleKey.Delete));

        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("ell");
        await Assert.That(sets[0].Triggers).Count().IsEqualTo(2);
    }

    /// <summary>
    /// The honesty rule, in both directions: a screen advertises <c>Del remove</c> <b>if and only
    /// if</b> its model actually offers a way to remove a row. Both cases have to be represented, or an
    /// "if and only if" passes vacuously.
    /// </summary>
    [Test]
    public async Task HeaderHints_ClaimDeleteExactlyWhenTheModelOffersIt()
    {
        var sets = Sets();
        var worlds = Worlds();
        var empty = new List<TriggerSet>();

        var screens = new List<(string Name, string Header, ScreenModel Model)>();

        void Add(string name, ScreenModel model, Func<ScreenModel, string> header) =>
            screens.Add((name, header(model), model));

        Add("F2", TriggersScreenRenderer.Model(sets, 0), m => TriggersScreenRenderer.HeaderLine(120, m));
        Add("F3", AliasesScreenRenderer.Model(sets, 0), m => AliasesScreenRenderer.HeaderLine(120, m));
        Add("F4", KeypadScreenRenderer.Model(sets[0].Macros, sets, 0), m => KeypadScreenRenderer.HeaderLine(120, m));
        Add("F5", WorldsScreenRenderer.Model(worlds, sets, 0, 0), m => WorldsScreenRenderer.HeaderLine(120, m));
        Add("F6", TimersScreenRenderer.Model(sets, 0), m => TimersScreenRenderer.HeaderLine(120, m));
        Add("F2 empty", TriggersScreenRenderer.Model(empty, -1), m => TriggersScreenRenderer.HeaderLine(120, m));
        Add("F3 empty", AliasesScreenRenderer.Model(empty, -1), m => AliasesScreenRenderer.HeaderLine(120, m));
        Add("F4 no sets", KeypadScreenRenderer.Model(Array.Empty<Macro>()), m => KeypadScreenRenderer.HeaderLine(120, m));
        Add("F6 empty", TimersScreenRenderer.Model(empty, -1), m => TimersScreenRenderer.HeaderLine(120, m));
        Add(
            "F7 options",
            OptionsScreenRenderer.Model(OptionsScreenRenderer.TextAnsiScreen()),
            m => OptionsScreenRenderer.HeaderLine("Text & ANSI", "F7", 120, m));

        await Assert.That(screens.Any(s => s.Model.HasRemovableRow)).IsTrue();
        await Assert.That(screens.Any(s => !s.Model.HasRemovableRow)).IsTrue();

        foreach (var (name, header, model) in screens)
        {
            await Assert.That(header.Contains(ScreenChrome.DeleteHint, StringComparison.Ordinal))
                .IsEqualTo(model.HasRemovableRow)
                .Because(name);
        }
    }

    /// <summary>
    /// End still reaches a pane's buttons without re-anchoring — that is what keeps <c>[⧉ duplicate]</c>
    /// pointed at the rule the user chose — but it now stops at the last <em>building</em> button. Delete
    /// is the only way to run a removal, so ⏎ at the end of the pane duplicates rather than deletes.
    /// <para>
    /// It used to land on <c>[- del]</c> (index 4: 2 rules + add + duplicate + del) and ⏎ there deleted
    /// the rule. That was reachable only by walking the cursor over the whole list, which dragged the
    /// selection to its last row — the bug.
    /// </para>
    /// </summary>
    [Test]
    public async Task End_StillReachesAPanesButtonsWithoutMovingItsSelection()
    {
        var sets = Sets();
        var session = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));

        await Assert.That(session.Selection.SelectionIn(0)).IsEqualTo(0);
        session.Handle(Key(ConsoleKey.End));

        await Assert.That(session.Focus().Index).IsEqualTo(3); // 2 rules + add + duplicate
        await Assert.That(session.Selection.SelectionIn(0)).IsEqualTo(0);

        session.Handle(Key(ConsoleKey.Enter));

        // The first rule duplicated — the row the selection never left — and nothing deleted.
        await Assert.That(sets[0].Triggers.Select(t => t.Name))
            .IsEquivalentTo(new[] { "Tell", "Spam", "Tell copy" });
        await Assert.That(session.Edits.HasDeletions).IsFalse();
    }
}
