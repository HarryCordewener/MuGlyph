using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The add/remove buttons on F2, F3, F4 and F6 — the four screens that could show you a trigger, an
/// alias, a binding or a timer and give you no way to make or unmake one. They follow F5's shape
/// exactly (<see cref="ScreenButtonTests"/> pins that one): a <see cref="ScreenButton"/> performs the
/// change and hands back its own undo, a removal restores at its original index rather than on the
/// end, a button that would act on nothing is not drawn, and a destructive button names its target.
/// <para>
/// What is different here, and is most of what these assert, is that all four panes are
/// <em>flattened</em> across every <see cref="TriggerSet"/>. The pane is one column; the thing lives
/// in one particular set's list. So a button has to find the owning list and translate between an
/// index into it and a row of the pane — get that wrong and <c>[[+ trigger]]</c> leaves the cursor on
/// somebody else's rule.
/// </para>
/// </summary>
public class ScreenListButtonTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Triggers = new List<Trigger>
            {
                new() { Name = "Tell", Pattern = "tells you", Actions = new TriggerActions { Gag = true } },
                new() { Name = "Spam", Pattern = "guild", Actions = new TriggerActions() },
            },
            Aliases = new List<Alias> { new() { Name = "gr", Pattern = "^gr$", Substitution = "greet\nwave" } },
            Macros = new List<Macro> { new() { Name = "Look", Key = "Num5", Command = "look" } },
            Timers = new List<TimerDefinition> { new() { Name = "ping", IntervalSeconds = 30, Command = "look" } },
        },
        new TriggerSet
        {
            Name = "Trade",
            Triggers = new List<Trigger> { new() { Name = "Offer", Pattern = "offers", Actions = new TriggerActions() } },
            Aliases = new List<Alias> { new() { Name = "b", Pattern = "^b$", Substitution = "buy" } },
            Timers = new List<TimerDefinition> { new() { Name = "restock", IntervalSeconds = 120, Command = "list" } },
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

    [Test]
    public async Task EveryListPaneEndsInItsOwnButtons()
    {
        var sets = Sets();

        var triggers = TriggersScreenRenderer.Model(sets, 0);
        await Assert.That(triggers.ListSizes[0]).IsEqualTo(3);
        await Assert.That(triggers.ButtonAt(0, 3)!.Value.Label).IsEqualTo(TriggersScreenRenderer.AddTriggerLabel);
        await Assert.That(triggers.ButtonAt(0, 4)!.Value.Label)
            .IsEqualTo(TriggersScreenRenderer.DuplicateTriggerLabel);
        await Assert.That(triggers.ButtonAt(0, 5)!.Value.Label).IsEqualTo(TriggersScreenRenderer.RemoveTriggerLabel);

        var aliases = AliasesScreenRenderer.Model(sets, 0);
        await Assert.That(aliases.ListSizes[0]).IsEqualTo(2);
        await Assert.That(aliases.ButtonAt(0, 2)!.Value.Label).IsEqualTo(AliasesScreenRenderer.AddAliasLabel);
        await Assert.That(aliases.ButtonAt(0, 3)!.Value.Label).IsEqualTo(AliasesScreenRenderer.DuplicateAliasLabel);
        await Assert.That(aliases.ButtonAt(0, 4)!.Value.Label).IsEqualTo(AliasesScreenRenderer.RemoveAliasLabel);

        // F6 has no duplicate: a timer is an interval and a command, and both are what you would change
        // in the copy — so the button would save nobody anything while still being a cursor stop.
        var timers = TimersScreenRenderer.Model(sets, 0);
        await Assert.That(timers.ListSizes[0]).IsEqualTo(2);
        await Assert.That(timers.ButtonAt(0, 2)!.Value.Label).IsEqualTo(TimersScreenRenderer.AddTimerLabel);
        await Assert.That(timers.ButtonAt(0, 3)!.Value.Label).IsEqualTo(TimersScreenRenderer.RemoveTimerLabel);
        await Assert.That(timers.ButtonAt(0, 4)).IsNull();

        // Nor does F4: a copied binding would land on the key its original already holds, and the
        // second macro on a key never fires.
        var keypad = KeypadScreenRenderer.Model(sets[0].Macros, sets, 0);
        await Assert.That(keypad.ListSizes[0]).IsEqualTo(1);
        await Assert.That(keypad.ButtonAt(0, 1)!.Value.Label).IsEqualTo(KeypadScreenRenderer.AddBindingLabel);
        await Assert.That(keypad.ButtonAt(0, 2)!.Value.Label).IsEqualTo(KeypadScreenRenderer.RemoveBindingLabel);
        await Assert.That(keypad.ButtonAt(0, 3)).IsNull();
    }

    /// <summary>
    /// A pane's rows still mean what they meant. The buttons are appended after the list, so the field
    /// ordinals and row indices every other test addresses are untouched by their arrival.
    /// </summary>
    [Test]
    public async Task GivingAPaneButtonsDoesNotRenumberTheRowsAboveThem()
    {
        var model = TriggersScreenRenderer.Model(Sets(), 0);

        await Assert.That(model.ButtonAt(0, 0)).IsNull();
        await Assert.That(model.FieldAt(0, 0, TriggersScreenRenderer.NameField)!.Value.Get()).IsEqualTo("Tell");
        await Assert.That(model.FieldAt(0, 2, TriggersScreenRenderer.NameField)!.Value.Get()).IsEqualTo("Offer");
    }

    /// <summary>
    /// A button that would act on nothing is not drawn, so ⏎ can never land on a silent no-op. With
    /// nothing selected there is nothing to duplicate or delete; with no sets at all there is nowhere
    /// to put a new rule either, so even the add button goes.
    /// </summary>
    [Test]
    public async Task APaneWithNothingSelectedOffersOnlyItsAddButton()
    {
        var sets = Sets();

        var triggers = TriggersScreenRenderer.Model(sets, -1);
        await Assert.That(triggers.Sizes[0]).IsEqualTo(4);
        await Assert.That(triggers.ButtonAt(0, 3)!.Value.Label).IsEqualTo(TriggersScreenRenderer.AddTriggerLabel);
        await Assert.That(triggers.ButtonAt(0, 4)).IsNull();

        var empty = new List<TriggerSet>();
        await Assert.That(TriggersScreenRenderer.Model(empty, -1).Sizes[0]).IsEqualTo(0);
        await Assert.That(AliasesScreenRenderer.Model(empty, -1).Sizes[0]).IsEqualTo(0);
        await Assert.That(TimersScreenRenderer.Model(empty, -1).Sizes[0]).IsEqualTo(0);
        await Assert.That(KeypadScreenRenderer.Model(Array.Empty<Macro>(), empty, -1).Sizes[0]).IsEqualTo(0);
    }

    /// <summary>
    /// The flattening, which is the whole difficulty of these four screens. A rule is added to the set
    /// that owns the selection — not to a fixed one, which would make rules appear in a set the user
    /// wasn't looking at — and the cursor is asked for the *pane row* the new rule now occupies, which
    /// is not its index in the list it went into.
    /// </summary>
    [Test]
    public async Task AddingGoesIntoTheSelectionsOwnSetAndLandsTheCursorOnThePaneRowItTookUp()
    {
        var sets = Sets();
        var edits = new ScreenEdits();

        // Selection is the second set's only rule (flattened row 2).
        var select = edits.Apply(ButtonNamed(
            TriggersScreenRenderer.Model(sets, 2), 0, TriggersScreenRenderer.AddTriggerLabel));

        await Assert.That(sets[1].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Offer", "New Trigger" });
        await Assert.That(sets[0].Triggers).Count().IsEqualTo(2);
        await Assert.That(select).IsEqualTo(3);

        edits.Revert();
        await Assert.That(sets[1].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Offer" });

        // And with the first set's rule selected it goes there, taking pane row 2 — after that set's
        // own rules and before the second set's.
        var first = new ScreenEdits();
        await Assert.That(first.Apply(ButtonNamed(
            TriggersScreenRenderer.Model(sets, 0), 0, TriggersScreenRenderer.AddTriggerLabel))).IsEqualTo(2);
        await Assert.That(sets[0].Triggers.Select(t => t.Name))
            .IsEquivalentTo(new[] { "Tell", "Spam", "New Trigger" });
    }

    /// <summary>
    /// A new rule has to be usable as it lands: <see cref="ScreenField.Pattern"/> refuses an empty
    /// regex, so a blank one could never be committed. It is left enabled because a rule with no
    /// actions does nothing to the stream whatever it matches — unlike a timer, below.
    /// </summary>
    [Test]
    public async Task ANewTriggerIsAWorkingPlaceholder()
    {
        var sets = Sets();
        new ScreenEdits().Apply(ButtonNamed(
            TriggersScreenRenderer.Model(sets, 0), 0, TriggersScreenRenderer.AddTriggerLabel));

        var added = sets[0].Triggers[^1];
        var model = TriggersScreenRenderer.Model(sets, 2);

        await Assert.That(added.Enabled).IsTrue();
        await Assert.That(model.FieldAt(0, 2, TriggersScreenRenderer.NameField)!.Value.Validate(added.Name)).IsNull();
        await Assert.That(model.FieldAt(0, 2, TriggersScreenRenderer.PatternField)!.Value.Validate(added.Pattern))
            .IsNull();
    }

    /// <summary>
    /// The one asymmetry among the four, and it is deliberate: a timer is the only thing here that acts
    /// without being provoked. A new one left running would start sending a placeholder command at the
    /// server a minute later, which nobody asked for.
    /// </summary>
    [Test]
    public async Task ANewTimerArrivesDisabled_UnlikeANewTriggerOrBinding()
    {
        var sets = Sets();
        new ScreenEdits().Apply(ButtonNamed(TimersScreenRenderer.Model(sets, 0), 0, TimersScreenRenderer.AddTimerLabel));
        new ScreenEdits().Apply(
            ButtonNamed(AliasesScreenRenderer.Model(sets, 0), 0, AliasesScreenRenderer.AddAliasLabel));
        new ScreenEdits().Apply(ButtonNamed(
            KeypadScreenRenderer.Model(sets[0].Macros, sets, 0), 0, KeypadScreenRenderer.AddBindingLabel));

        await Assert.That(sets[0].Timers[^1].Enabled).IsFalse();
        await Assert.That(sets[0].Aliases[^1].Enabled).IsTrue();
        await Assert.That(sets[0].Macros[^1].Enabled).IsTrue();
    }

    /// <summary>
    /// A deletion's undo restores the row's *place*, not merely its existence: the pane's order is what
    /// the screen navigates by, and putting a cancelled deletion back on the end of its set would be a
    /// second, invisible edit riding along with the first.
    /// </summary>
    [Test]
    public async Task RemovingUndoesBackIntoItsOwnIndexOnEveryScreen()
    {
        var sets = Sets();
        var edits = new ScreenEdits();

        // Flattened row 0 is the first set's first rule.
        edits.Apply(ButtonNamed(TriggersScreenRenderer.Model(sets, 0), 0, TriggersScreenRenderer.RemoveTriggerLabel));
        await Assert.That(sets[0].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Spam" });

        edits.Apply(ButtonNamed(AliasesScreenRenderer.Model(sets, 1), 0, AliasesScreenRenderer.RemoveAliasLabel));
        await Assert.That(sets[1].Aliases).IsEmpty();

        edits.Apply(ButtonNamed(TimersScreenRenderer.Model(sets, 0), 0, TimersScreenRenderer.RemoveTimerLabel));
        await Assert.That(sets[0].Timers).IsEmpty();

        edits.Apply(ButtonNamed(
            KeypadScreenRenderer.Model(sets[0].Macros, sets, 0), 0, KeypadScreenRenderer.RemoveBindingLabel));
        await Assert.That(sets[0].Macros).IsEmpty();

        edits.Revert();

        await Assert.That(sets[0].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Tell", "Spam" });
        await Assert.That(sets[1].Aliases.Select(a => a.Name)).IsEquivalentTo(new[] { "b" });
        await Assert.That(sets[0].Timers.Select(t => t.Name)).IsEquivalentTo(new[] { "ping" });
        await Assert.That(sets[0].Macros.Select(m => m.Key)).IsEquivalentTo(new[] { "Num5" });
    }

    /// <summary>
    /// Removing from the middle of a flattened pane puts the row back where it was inside its own set,
    /// and leaves the cursor on the pane row the deleted one occupied — which is now whatever followed
    /// it, the same place the eye is.
    /// </summary>
    [Test]
    public async Task RemovingFromTheMiddleKeepsThePaneRowAndRestoresTheSetsOrder()
    {
        var sets = Sets();
        var edits = new ScreenEdits();

        var select = edits.Apply(ButtonNamed(
            TriggersScreenRenderer.Model(sets, 1), 0, TriggersScreenRenderer.RemoveTriggerLabel));

        await Assert.That(select).IsEqualTo(1);
        await Assert.That(sets.SelectMany(s => s.Triggers).Select(t => t.Name))
            .IsEquivalentTo(new[] { "Tell", "Offer" });

        edits.Revert();

        await Assert.That(sets[0].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Tell", "Spam" });
    }

    /// <summary>
    /// The point of <c>duplicate</c>: the copy must be a copy. An aliased one would look right and then
    /// follow every later edit of its original around — for a trigger that means its actions, which is
    /// where the real damage would be.
    /// </summary>
    [Test]
    public async Task DuplicatingARuleIsADeepCopyWithItsOwnName()
    {
        var sets = Sets();
        var original = sets[0].Triggers[0];
        var edits = new ScreenEdits();

        var select = edits.Apply(ButtonNamed(
            TriggersScreenRenderer.Model(sets, 0), 0, TriggersScreenRenderer.DuplicateTriggerLabel));

        await Assert.That(select).IsEqualTo(2);
        var copy = sets[0].Triggers[2];
        await Assert.That(copy.Name).IsEqualTo("Tell copy");
        await Assert.That(ReferenceEquals(copy.Actions, original.Actions)).IsFalse();

        copy.Actions.Gag = false;
        await Assert.That(original.Actions.Gag).IsTrue();

        edits.Revert();
        await Assert.That(sets[0].Triggers).Count().IsEqualTo(2);
    }

    [Test]
    public async Task DuplicatingAnAliasIsADeepCopyWithItsOwnName()
    {
        var sets = Sets();
        var original = sets[0].Aliases[0];
        var edits = new ScreenEdits();

        edits.Apply(ButtonNamed(AliasesScreenRenderer.Model(sets, 0), 0, AliasesScreenRenderer.DuplicateAliasLabel));

        var copy = sets[0].Aliases[1];
        await Assert.That(copy.Name).IsEqualTo("gr copy");
        await Assert.That(copy.Substitution).IsEqualTo("greet\nwave");
        await Assert.That(ReferenceEquals(copy, original)).IsFalse();

        copy.Pattern = "^gg$";
        await Assert.That(original.Pattern).IsEqualTo("^gr$");

        edits.Revert();
        await Assert.That(sets[0].Aliases).Count().IsEqualTo(1);
    }

    /// <summary>
    /// Duplicating twice keeps finding a free name, so two copies of one rule can still be told apart
    /// in the list they land in.
    /// </summary>
    [Test]
    public async Task DuplicatingTwiceGivesEachCopyAFreeName()
    {
        var sets = Sets();
        var edits = new ScreenEdits();

        edits.Apply(ButtonNamed(
            TriggersScreenRenderer.Model(sets, 0), 0, TriggersScreenRenderer.DuplicateTriggerLabel));
        edits.Apply(ButtonNamed(
            TriggersScreenRenderer.Model(sets, 0), 0, TriggersScreenRenderer.DuplicateTriggerLabel));

        await Assert.That(sets[0].Triggers.Select(t => t.Name))
            .IsEquivalentTo(new[] { "Tell", "Spam", "Tell copy", "Tell copy 2" });
    }

    /// <summary>
    /// F4's add button is the one that cannot simply append. A <see cref="Macro"/> is identified by its
    /// <see cref="Macro.Key"/>, so the button claims a free one and says which on its own row. It claims
    /// from <see cref="MacroKeys.Bindable"/> rather than the numpad: no numpad key ever reaches this
    /// client, so a binding born on one could never fire, and a button whose whole job is to name the
    /// key it takes must not name a dead one.
    /// </summary>
    [Test]
    public async Task AddingABindingClaimsAFreeKeyThatCanActuallyFire_AndNamesIt()
    {
        var sets = Sets();
        var button = ButtonNamed(
            KeypadScreenRenderer.Model(sets[0].Macros, sets, 0), 0, KeypadScreenRenderer.AddBindingLabel);

        var first = MacroKeys.Bindable[0];
        await Assert.That(button.Target).IsEqualTo(first);
        await Assert.That(MacroKeys.Verdict(first).Fires).IsTrue();

        new ScreenEdits().Apply(button);
        await Assert.That(sets[0].Macros[^1].Key).IsEqualTo(first);

        // The next one takes the next free key, not the same one again.
        var again = ButtonNamed(
            KeypadScreenRenderer.Model(sets.SelectMany(s => s.Macros).ToList(), sets, 0),
            0,
            KeypadScreenRenderer.AddBindingLabel);
        await Assert.That(again.Target).IsEqualTo(MacroKeys.Bindable[1]);
    }

    /// <summary>
    /// With every bindable chord spoken for there is no free key to claim, so the add button isn't
    /// drawn — the same rule that keeps <c>[[- del]]</c> off a pane with nothing selected. Delete still
    /// works, which is how you make room.
    /// </summary>
    [Test]
    public async Task NoBindingCanBeAddedOnceEveryBindableKeyIsTaken()
    {
        var sets = new List<TriggerSet> { new() { Name = "All" } };
        foreach (var key in MacroKeys.Bindable)
        {
            sets[0].Macros.Add(new Macro { Name = key, Key = key, Command = "look" });
        }

        var model = KeypadScreenRenderer.Model(sets[0].Macros, sets, 0);
        var bindings = MacroKeys.Bindable.Count;

        await Assert.That(model.Sizes[0]).IsEqualTo(bindings + 1); // the bindings + [- del], nothing else
        await Assert.That(model.ButtonAt(0, bindings)!.Value.Label)
            .IsEqualTo(KeypadScreenRenderer.RemoveBindingLabel);
        await Assert.That(model.ButtonAt(0, bindings + 1)).IsNull();
    }

    /// <summary>
    /// A destructive button names the row it would act on, because the cursor has to leave the list to
    /// reach it and a delete whose target is off-screen is exactly the surprise these screens must not
    /// spring. An add button names no victim — except F4's, which names the key it will claim.
    /// </summary>
    [Test]
    public async Task ATargetedButtonNamesTheRowItWouldActOn()
    {
        var sets = Sets();

        var rules = TriggersScreenRenderer.RulesColumn(sets, 1);
        await Assert.That(rules.Any(l => l.Contains("[[- del]]") && l.Contains("Spam"))).IsTrue();
        await Assert.That(rules.Any(l => l.Contains("[[⧉ duplicate]]") && l.Contains("Spam"))).IsTrue();
        await Assert.That(rules.Any(l => l.Contains("[[+ trigger]]") && l.Contains("Spam"))).IsFalse();

        var aliases = AliasesScreenRenderer.ListColumn(sets, 0);
        await Assert.That(aliases.Any(l => l.Contains("[[- del]]") && l.Contains("gr"))).IsTrue();

        var timers = TimersScreenRenderer.ListColumn(sets, 0);
        await Assert.That(timers.Any(l => l.Contains("[[- del]]") && l.Contains("ping"))).IsTrue();

        var bindings = KeypadScreenRenderer.HotkeysColumn(sets[0].Macros, null, sets, 0);
        await Assert.That(bindings.Any(l => l.Contains("[[- del]]") && l.Contains("Look"))).IsTrue();
        await Assert.That(bindings.Any(l => l.Contains("[[+ binding]]") && l.Contains(MacroKeys.Bindable[0])))
            .IsTrue();
    }

    /// <summary>
    /// End to end through the keyboard, which is the whole reason a new row is worth adding: ⏎ on the
    /// add button runs it and leaves the cursor on the new row, where the next ⏎ opens that row's name.
    /// </summary>
    [Test]
    public async Task Enter_OnAddLeavesTheCursorOnTheNewRowReadyToNameIt()
    {
        var sets = Sets();
        var session = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));

        // Rows 0-2 are the three rules; row 3 is [+ trigger]. Walking onto it leaves the selection on
        // row 2 — the "Trade" set's rule — so that is the set the new one joins.
        for (var i = 0; i < 3; i++)
        {
            session.Handle(Key(ConsoleKey.DownArrow));
        }

        await Assert.That(session.Handle(Key(ConsoleKey.Enter))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(sets[1].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Offer", "New Trigger" });

        // The cursor is on the row the new rule took up, not on the button it was added from.
        await Assert.That(session.Focus().Index).IsEqualTo(3);
        await Assert.That(session.IsEditing).IsFalse();

        session.Handle(Key(ConsoleKey.Enter));
        await Assert.That(session.IsEditing).IsTrue();
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("New Trigger");

        session.Handle(Key(ConsoleKey.Escape));
        await Assert.That(session.Handle(Key(ConsoleKey.Escape))).IsEqualTo(ScreenAction.Cancel);
        session.Edits.Revert();
        await Assert.That(sets[1].Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Offer" });
    }
}
