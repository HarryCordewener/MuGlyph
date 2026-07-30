using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// Moving around a settings screen with the arrows and ⇥ — the 2D half of the cursor, as opposed to
/// <see cref="ScreenSelectionTests"/>, which pins the within-a-pane half it is built on.
/// <para>
/// The rules under test: ⇥ walks the panes in the order they are <em>drawn</em> and wraps; ←→ move to
/// the pane beside this one and deliberately do not; ↑↓ move a row and spill into a pane stacked below
/// (or above) this one in the same column, which only F5 has. All of it is off limits while a field
/// edit is open, where the arrows belong to the buffer — which is asserted here too, because that is
/// the collision this whole feature had to avoid.
/// </para>
/// </summary>
public class ScreenPaneNavigationTests
{

    /// <summary>
    /// Where the cursor is, and whether a field is open on it — the whole of what a navigation assertion
    /// is about. Compared as a tuple rather than as a whole <see cref="ScreenFocus"/> because the focus
    /// also carries a <em>derived</em> reading of what ⏎ would do on the row
    /// (<see cref="ScreenEnter"/>, for the action bar's chip), and a movement test restating that would be
    /// pinning a label from the wrong place.
    /// </summary>
    private static (int Pane, int Index, bool Editing) Cursor(SettingsSession session)
    {
        var focus = session.Focus();
        return (focus.Pane, focus.Index, focus.IsEditing);
    }
    private static ConsoleKeyInfo Key(ConsoleKey key, ConsoleModifiers modifiers = default) =>
        new('\0', key, modifiers.HasFlag(ConsoleModifiers.Shift), false, false);

    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Description = "channel routing",
            Triggers = new List<Trigger>
            {
                new() { Name = "Tell", Pattern = "tells you", Actions = new TriggerActions() },
            },
            Aliases = new List<Alias> { new() { Name = "k", Pattern = "^k$", Substitution = "kill" } },
            Macros = new List<Macro> { new() { Name = "look", Key = "F1", Command = "look" } },
            Timers = new List<TimerDefinition> { new() { Name = "ping", IntervalSeconds = 30, Command = "look" } },
        },
        new TriggerSet { Name = "Trade", Description = "market watch" },
    };

    private static List<WorldDefinition> Worlds() => new()
    {
        new()
        {
            Name = "Aetherfall",
            Host = "aetherfall.mux",
            Characters = new List<CharacterDefinition>
            {
                new() { Name = "Corvid" },
                new() { Name = "Rookery" },
            },
        },
        new() { Name = "Grapevine", Host = "grapevine.haus" },
    };

    /// <summary>F5, wired the way the app wires it: every pane projected from the live selection.</summary>
    private static SettingsSession WorldsSession(
        IList<WorldDefinition> worlds, IList<TriggerSet> sets) =>
        new(selection => WorldsScreenRenderer.Model(
            (IReadOnlyList<WorldDefinition>)worlds,
            (IReadOnlyList<TriggerSet>)sets,
            selection.SelectionIn(WorldsScreenRenderer.WorldsPane),
            selection.SelectionIn(WorldsScreenRenderer.CharactersPane),
            selection.SelectionIn(WorldsScreenRenderer.TriggerSetsPane)));

    // ---- ⇥ follows the drawing, not the numbering ---------------------------------------------------

    /// <summary>
    /// The awkwardness this replaced, stated as a fact: F5's security pane is numbered last and drawn
    /// first, so index-order ⇥ went left-top → right-middle → bottom-right and then jumped back
    /// <em>up</em> the screen. Reading order — down the rows, then across the columns — is what the eye
    /// was already doing, and ⇧⇥ is exactly its reverse.
    /// </summary>
    [Test]
    public async Task Tab_WalksF5sPanesInTheOrderTheyAreDrawn()
    {
        var session = WorldsSession(Worlds(), Sets());
        var forwards = new List<int> { session.Focus().Pane };

        for (var hop = 0; hop < 4; hop++)
        {
            session.Handle(Key(ConsoleKey.Tab));
            forwards.Add(session.Focus().Pane);
        }

        await Assert.That(forwards).IsEquivalentTo(new[]
        {
            WorldsScreenRenderer.WorldsPane,
            WorldsScreenRenderer.SecurityPane,
            WorldsScreenRenderer.CharactersPane,
            WorldsScreenRenderer.TriggerSetsPane,
            WorldsScreenRenderer.WorldsPane, // ⇥ is the cycle: it wraps where the arrows park
        });

        var backwards = new List<int>();
        for (var hop = 0; hop < 4; hop++)
        {
            session.Handle(Key(ConsoleKey.Tab, ConsoleModifiers.Shift));
            backwards.Add(session.Focus().Pane);
        }

        // ⇧⇥ is the same walk read backwards, which is the only thing it can honestly be.
        forwards.Reverse();
        await Assert.That(backwards).IsEquivalentTo(forwards.Skip(1).ToList());
    }

    /// <summary>
    /// On every screen whose panes are drawn side by side — which is all seven of the others — reading
    /// order and index order are the same walk, so ⇥ steps exactly the panes it always did.
    /// </summary>
    [Test]
    public async Task Tab_OnASideBySideScreenIsUnchangedByTheLayout()
    {
        var sets = Sets();
        var session = new SettingsSession(selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));

        await Assert.That(session.Focus().Pane).IsEqualTo(0);
        session.Handle(Key(ConsoleKey.Tab));
        await Assert.That(session.Focus().Pane).IsEqualTo(1);
        session.Handle(Key(ConsoleKey.Tab));
        await Assert.That(session.Focus().Pane).IsEqualTo(0);
    }

    // ---- ←→ ---------------------------------------------------------------------------------------

    /// <summary>
    /// → crosses to the column beside this one and ← comes back, on the two-column screens where that
    /// is the whole of the geometry.
    /// </summary>
    [Test]
    public async Task LeftAndRight_CrossBetweenATwoColumnScreensPanes()
    {
        var sets = Sets();
        var session = new SettingsSession(selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));

        await Assert.That(session.Handle(Key(ConsoleKey.RightArrow))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(session.Focus().Pane).IsEqualTo(1);

        await Assert.That(session.Handle(Key(ConsoleKey.LeftArrow))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(session.Focus().Pane).IsEqualTo(0);
    }

    /// <summary>
    /// At the edge of the layout ←→ park rather than wrapping — the same promise ↑ makes at the top of
    /// a list, and what keeps them distinguishable from ⇥, which is the movement that goes everywhere.
    /// A screen with one pane has no sideways at all and swallows them.
    /// </summary>
    [Test]
    public async Task LeftAndRight_ParkAtTheEdgeInsteadOfWrapping()
    {
        var sets = Sets();
        var session = new SettingsSession(selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));

        await Assert.That(session.Handle(Key(ConsoleKey.LeftArrow))).IsEqualTo(ScreenAction.Consumed);
        await Assert.That(session.Focus().Pane).IsEqualTo(0);

        session.Handle(Key(ConsoleKey.RightArrow));
        await Assert.That(session.Handle(Key(ConsoleKey.RightArrow))).IsEqualTo(ScreenAction.Consumed);
        await Assert.That(session.Focus().Pane).IsEqualTo(1);

        var single = new SettingsSession(_ => OptionsScreenRenderer.Model(OptionsScreenRenderer.InputScreen()));
        await Assert.That(single.Handle(Key(ConsoleKey.RightArrow))).IsEqualTo(ScreenAction.Consumed);
        await Assert.That(single.Focus().Pane).IsEqualTo(0);
    }

    /// <summary>
    /// On F5, → out of the WORLDS list lands on the pane drawn nearest it in the next column — the
    /// world's own security checkboxes, at the top of the detail column — and ← comes back from any of
    /// the three panes stacked there, because the WORLDS list is the whole of the column beside them.
    /// </summary>
    [Test]
    public async Task Right_FromF5sWorldsListLandsOnTheTopOfTheDetailColumn()
    {
        var session = WorldsSession(Worlds(), Sets());

        session.Handle(Key(ConsoleKey.RightArrow));
        await Assert.That(session.Focus().Pane).IsEqualTo(WorldsScreenRenderer.SecurityPane);

        session.Handle(Key(ConsoleKey.LeftArrow));
        await Assert.That(session.Focus().Pane).IsEqualTo(WorldsScreenRenderer.WorldsPane);

        // …and from the bottom of that column too: one ← is always the way back out of it.
        for (var hop = 0; hop < 3; hop++)
        {
            session.Handle(Key(ConsoleKey.Tab));
        }

        await Assert.That(session.Focus().Pane).IsEqualTo(WorldsScreenRenderer.TriggerSetsPane);
        session.Handle(Key(ConsoleKey.LeftArrow));
        await Assert.That(session.Focus().Pane).IsEqualTo(WorldsScreenRenderer.WorldsPane);
    }

    /// <summary>Crossing a column keeps the cursor the pane was left on, the way ⇥ always has.</summary>
    [Test]
    public async Task CrossingAColumnKeepsEachPanesOwnCursor()
    {
        var sets = Sets();
        var session = new SettingsSession(selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));

        session.Handle(Key(ConsoleKey.DownArrow));
        session.Handle(Key(ConsoleKey.RightArrow));
        session.Handle(Key(ConsoleKey.DownArrow));
        await Assert.That(Cursor(session)).IsEqualTo((1, 1, false));

        session.Handle(Key(ConsoleKey.LeftArrow));
        await Assert.That(Cursor(session)).IsEqualTo((0, 1, false));
    }

    // ---- ↑↓ spilling down a stacked column ---------------------------------------------------------

    /// <summary>
    /// The run that makes F5's detail column one thing under the cursor instead of three: ↓ from the
    /// last security checkbox carries on into the CHARACTERS list, and off the end of that pane's
    /// buttons into the trigger sets. It lands on the row nearest the boundary just crossed — the first
    /// going down, the last coming back up — so the cursor keeps moving in the direction the key does.
    /// </summary>
    [Test]
    public async Task Down_RunsFromTheSecurityCheckboxesThroughTheCharactersAndOnIntoTheSets()
    {
        var session = WorldsSession(Worlds(), Sets());
        session.Handle(Key(ConsoleKey.RightArrow)); // onto the first security checkbox

        // Two checkboxes, then the character list.
        session.Handle(Key(ConsoleKey.DownArrow));
        await Assert.That(Cursor(session)).IsEqualTo((WorldsScreenRenderer.SecurityPane, 1, false));

        await Assert.That(session.Handle(Key(ConsoleKey.DownArrow))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(Cursor(session)).IsEqualTo((WorldsScreenRenderer.CharactersPane, 0, false));

        // Two characters and two reachable buttons — [+ add character] and [⧉ duplicate]; the Del row past
        // them is drawn and is not a stop — then the trigger sets at the foot of the screen. It was three
        // buttons before that row left the walk.
        for (var hop = 0; hop < 3; hop++)
        {
            session.Handle(Key(ConsoleKey.DownArrow));
        }

        await Assert.That(session.Focus().Pane).IsEqualTo(WorldsScreenRenderer.CharactersPane);
        session.Handle(Key(ConsoleKey.DownArrow));
        await Assert.That(Cursor(session)).IsEqualTo((WorldsScreenRenderer.TriggerSetsPane, 0, false));

        // ↑ comes back up the same way, onto the row it left from rather than the top of that pane.
        session.Handle(Key(ConsoleKey.UpArrow));
        await Assert.That(session.Focus().Pane).IsEqualTo(WorldsScreenRenderer.CharactersPane);
        await Assert.That(session.Focus().Index).IsEqualTo(3); // [⧉ duplicate]; was 4, the Del row
    }

    /// <summary>
    /// The spill only happens where a screen has said two panes share a column. Nothing stacks on a
    /// two-column screen, so ↑↓ park at the ends of a list exactly as they always did — the promise
    /// that holding ↑ doesn't teleport you somewhere else.
    /// </summary>
    [Test]
    public async Task UpAndDown_DoNotLeaveAPaneOnASideBySideScreen()
    {
        var sets = Sets();
        var session = new SettingsSession(selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));

        await Assert.That(session.Handle(Key(ConsoleKey.UpArrow))).IsEqualTo(ScreenAction.Consumed);
        await Assert.That(Cursor(session)).IsEqualTo((0, 0, false));

        for (var hop = 0; hop < 20; hop++)
        {
            session.Handle(Key(ConsoleKey.DownArrow));
        }

        await Assert.That(session.Focus().Pane).IsEqualTo(0);
        await Assert.That(session.Handle(Key(ConsoleKey.DownArrow))).IsEqualTo(ScreenAction.Consumed);
    }

    /// <summary>
    /// The WORLDS list is alone in its column, so ↓ off the end of it parks — and ⇥ is what reaches the
    /// panes beside it. That is why ⇥ still wraps: it is the one movement that can always get out.
    /// </summary>
    [Test]
    public async Task Down_ParksAtTheFootOfF5sWorldsListBecauseNothingIsStackedUnderIt()
    {
        var session = WorldsSession(Worlds(), Sets());

        for (var hop = 0; hop < 10; hop++)
        {
            session.Handle(Key(ConsoleKey.DownArrow));
        }

        await Assert.That(session.Focus().Pane).IsEqualTo(WorldsScreenRenderer.WorldsPane);
        await Assert.That(session.Handle(Key(ConsoleKey.DownArrow))).IsEqualTo(ScreenAction.Consumed);
    }

    // ---- the collision the arrows had to avoid ------------------------------------------------------

    /// <summary>
    /// Inside an open field the arrows are the buffer's, and nothing here may take them: ←→ move the
    /// caret through the text and ↑↓ walk the drawn candidate list. A pane move triggered from either
    /// would be the worst possible outcome of this feature — the cursor leaving the row being typed
    /// into, mid-word.
    /// </summary>
    [Test]
    public async Task InsideAnOpenField_TheArrowsBelongToTheBufferAndNotToThePanes()
    {
        var sets = Sets();
        var session = new SettingsSession(selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));

        session.Handle(Key(ConsoleKey.Enter)); // opens the rule's name — "Tell"
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("Tell");
        await Assert.That(session.Focus().Edit!.Value.Caret).IsEqualTo(4);

        await Assert.That(session.Handle(Key(ConsoleKey.LeftArrow))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(session.Focus().Edit!.Value.Caret).IsEqualTo(3);
        await Assert.That(session.Focus().Pane).IsEqualTo(0);

        session.Handle(Key(ConsoleKey.RightArrow));
        await Assert.That(session.Focus().Edit!.Value.Caret).IsEqualTo(4);
        await Assert.That(session.Focus().Pane).IsEqualTo(0);

        // Typing into it still works after all that, which is what proves the buffer was never left.
        session.Handle(new ConsoleKeyInfo('s', ConsoleKey.S, false, false, false));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("Tells");
    }

    /// <summary>
    /// The same, one field over, where ↑↓ have a dropdown to walk: they step its candidates and put the
    /// one they land on into the buffer, and the cursor stays on the row the whole time.
    /// </summary>
    [Test]
    public async Task InsideAFieldWithChoices_UpAndDownStillWalkTheDropdown()
    {
        var worlds = Worlds();
        var session = WorldsSession(worlds, Sets());

        // The world's encoding, a closed list of wire encodings, reached by stepping its row's fields.
        session.Handle(Key(ConsoleKey.Enter));
        for (var hop = 0; hop < WorldsScreenRenderer.EncodingField; hop++)
        {
            session.Handle(Key(ConsoleKey.Tab));
        }

        await Assert.That(session.Focus().Edit!.Value.Field).IsEqualTo(WorldsScreenRenderer.EncodingField);
        var before = session.Focus().Edit!.Value.Text;

        await Assert.That(session.Handle(Key(ConsoleKey.DownArrow))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(session.Focus().Edit!.Value.Text).IsNotEqualTo(before);
        await Assert.That(session.Focus().Pane).IsEqualTo(WorldsScreenRenderer.WorldsPane);

        session.Handle(Key(ConsoleKey.UpArrow));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo(before);
        await Assert.That(session.Focus().Pane).IsEqualTo(WorldsScreenRenderer.WorldsPane);
    }

    // ---- every screen, not only the hard one --------------------------------------------------------

    /// <summary>
    /// The arrows have to mean the same thing on all eight screens or the screens disagree with each
    /// other about how navigation works. On every multi-pane screen ←→ reach every pane there is; on
    /// the single-pane ones they reach nothing and are simply swallowed, which is why those screens
    /// do not advertise them (see <see cref="ScreenHintTests"/>).
    /// </summary>
    [Test]
    public async Task TheArrowsReachEveryPaneOfEveryScreen()
    {
        foreach (var (name, session) in EveryScreen())
        {
            var panes = session.Selection.PaneCount;
            var seen = new HashSet<int> { session.Focus().Pane };

            // Right along the row, then down whatever is stacked in the column it ends in, then back.
            for (var hop = 0; hop < panes * 2; hop++)
            {
                session.Handle(Key(ConsoleKey.RightArrow));
                seen.Add(session.Focus().Pane);
                for (var step = 0; step < 40; step++)
                {
                    session.Handle(Key(ConsoleKey.DownArrow));
                    seen.Add(session.Focus().Pane);
                }
            }

            await Assert.That(seen.Count)
                .IsEqualTo(panes)
                .Because($"{name}: the arrows should reach all {panes} of its panes, not {seen.Count}");
        }
    }

    /// <summary>The eight screens, each wired the way the app wires it.</summary>
    private static IEnumerable<(string Name, SettingsSession Session)> EveryScreen()
    {
        var sets = Sets();
        yield return ("F2 triggers", new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0))));
        yield return ("F3 aliases", new SettingsSession(
            selection => AliasesScreenRenderer.Model(sets, selection.SelectionIn(0))));
        yield return ("F4 keypad", new SettingsSession(
            selection => KeypadScreenRenderer.Model(
                sets.SelectMany(s => s.Macros).ToList(), sets, selection.SelectionIn(0))));
        yield return ("F5 worlds", WorldsSession(Worlds(), sets));
        yield return ("F6 timers", new SettingsSession(
            selection => TimersScreenRenderer.Model(sets, selection.SelectionIn(0))));
        yield return ("F7 text & ANSI", new SettingsSession(
            _ => OptionsScreenRenderer.Model(OptionsScreenRenderer.TextAnsiScreen())));
        yield return ("F8 input", new SettingsSession(
            _ => OptionsScreenRenderer.Model(OptionsScreenRenderer.InputScreen())));
        yield return ("F9 character logging", WorldsSession(Worlds(), sets));
    }
}
