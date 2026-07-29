using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// Whether the editors these screens hold can actually be <em>found</em>, as opposed to merely
/// reached. F5's character fields were the case that proved the difference: every one of them was
/// reachable and writable, and a maintainer who uses the screen daily still had to hunt for the way
/// in, because the CHARACTER form draws four field wells — this project's affordance for "the keyboard
/// can change this here" — while the cursor stop that opens them is the character's row in the
/// CHARACTERS list, a column and a band away, drawn as a bare selector with no well of its own.
/// <para>
/// Two answers, both asserted here: the form says where its door is, and making a thing puts you
/// through that door rather than beside it.
/// </para>
/// </summary>
public class ScreenReachabilityTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    /// <summary>Whether the form says a thing anywhere in the block, wherever it chooses to draw it.</summary>
    private static bool Says(IEnumerable<string> block, string text) =>
        block.Any(l => l.Contains(text, StringComparison.Ordinal));

    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Triggers = new List<Trigger>
            {
                new() { Name = "Tell", Pattern = "tells you", Actions = new TriggerActions() },
            },
            Aliases = new List<Alias> { new() { Name = "k", Pattern = "^k$", Substitution = "kill" } },
            Macros = new List<Macro> { new() { Name = "look", Key = "F1", Command = "look" } },
            Timers = new List<TimerDefinition> { new() { Name = "ping", IntervalSeconds = 30, Command = "look" } },
        },
    };

    private static List<WorldDefinition> Worlds() => new()
    {
        new()
        {
            Name = "Aetherfall",
            Host = "aetherfall.mux",
            Characters = new List<CharacterDefinition> { new() { Name = "Corvid" } },
        },
    };

    private static SettingsSession WorldsSession(IList<WorldDefinition> worlds, IList<TriggerSet> sets) =>
        new(selection => WorldsScreenRenderer.Model(
            (IReadOnlyList<WorldDefinition>)worlds,
            (IReadOnlyList<TriggerSet>)sets,
            selection.SelectionIn(WorldsScreenRenderer.WorldsPane),
            selection.SelectionIn(WorldsScreenRenderer.CharactersPane),
            selection.SelectionIn(WorldsScreenRenderer.TriggerSetsPane)));

    // ---- the form says where its door is ------------------------------------------------------------

    /// <summary>
    /// The CHARACTER heading names the key that opens the wells beneath it, and where that key has to
    /// be pressed. Drawn with no cursor at all — a screen nobody has touched yet — it points at the row
    /// above, which is the reader who has just arrived and is looking at four editable-looking values
    /// with no idea how to get at them.
    /// </summary>
    [Test]
    public async Task TheCharacterFormSaysHowToGetIntoIt()
    {
        var character = Worlds()[0].Characters[0];
        var form = WorldsScreenRenderer.FormColumn(character, "#00f5b7");

        await Assert.That(Says(form, WorldsScreenRenderer.FormClosed)).IsTrue();
        await Assert.That(form[0]).Contains("CHARACTER");
    }

    /// <summary>
    /// And it changes what it says once the cursor is on the row that opens it: from where to go, to
    /// what the key under your finger will now do. Both are derived from the focus, so neither can
    /// outlive the state it describes.
    /// </summary>
    [Test]
    public async Task TheCharacterFormsDoorFollowsTheCursor()
    {
        var character = Worlds()[0].Characters[0];

        var elsewhere = WorldsScreenRenderer.FormColumn(
            character, "#00f5b7", new ScreenFocus(WorldsScreenRenderer.WorldsPane, 0), 0);
        await Assert.That(Says(elsewhere, WorldsScreenRenderer.FormClosed)).IsTrue();

        var onTheRow = WorldsScreenRenderer.FormColumn(
            character, "#00f5b7", new ScreenFocus(WorldsScreenRenderer.CharactersPane, 0), 0);
        await Assert.That(Says(onTheRow, WorldsScreenRenderer.FormOpen)).IsTrue();
        await Assert.That(Says(onTheRow, WorldsScreenRenderer.FormClosed)).IsFalse();

        // On the pane's [+ add character] button ⏎ adds rather than opens, so the promise is withdrawn.
        var onAButton = WorldsScreenRenderer.FormColumn(
            character, "#00f5b7", new ScreenFocus(WorldsScreenRenderer.CharactersPane, 1), 0);
        await Assert.That(Says(onAButton, WorldsScreenRenderer.FormClosed)).IsTrue();
    }

    /// <summary>
    /// Mid-edit it says nothing: the header hints have already swapped wholesale to
    /// <c>⏎ commit · Esc revert</c>, and a heading still offering to open what is already open would be
    /// exactly the disagreement those derived hints exist to prevent.
    /// </summary>
    [Test]
    public async Task TheDoorIsSilentWhileTheFieldIsOpen()
    {
        var character = Worlds()[0].Characters[0];
        var editing = new ScreenFocus(
            WorldsScreenRenderer.CharactersPane,
            0,
            new ScreenFieldEdit(WorldsScreenRenderer.CharacterNameField, "Corvid", 6, null, RowFields: 6));

        var form = WorldsScreenRenderer.FormColumn(character, "#00f5b7", editing, 0);

        await Assert.That(Says(form, WorldsScreenRenderer.FormOpen)).IsFalse();
        await Assert.That(Says(form, WorldsScreenRenderer.FormClosed)).IsFalse();
    }

    // ---- making a thing puts you in it --------------------------------------------------------------

    /// <summary>
    /// The maintainer's report, end to end: <c>[[+ add character]]</c> should land the keyboard in the
    /// character setup section, not on the button that made it. It does — the new character's name is
    /// open, and the well that name is drawn in is the CHARACTER form's, so pressing the button *is*
    /// the guided tour of the route nothing else pointed at.
    /// </summary>
    [Test]
    public async Task AddingACharacterOpensTheNewCharactersNameInTheForm()
    {
        var worlds = Worlds();
        var session = WorldsSession(worlds, Sets());

        session.Handle(Key(ConsoleKey.Tab));   // security
        session.Handle(Key(ConsoleKey.Tab));   // the characters pane
        session.Handle(Key(ConsoleKey.End));   // its last row is [- remove]; [+ add character] is above
        session.Handle(Key(ConsoleKey.UpArrow));
        session.Handle(Key(ConsoleKey.UpArrow));

        await Assert.That(session.Handle(Key(ConsoleKey.Enter))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(worlds[0].Characters).Count().IsEqualTo(2);

        var focus = session.Focus();
        await Assert.That(focus.Pane).IsEqualTo(WorldsScreenRenderer.CharactersPane);
        await Assert.That(focus.Index).IsEqualTo(1);
        await Assert.That(focus.Edit!.Value.Field).IsEqualTo(WorldsScreenRenderer.CharacterNameField);

        // …and the caret is drawn in the form, which is the half of this that made it worth doing.
        var form = WorldsScreenRenderer.FormColumn(worlds[0].Characters[1], "#00f5b7", focus, 1);
        await Assert.That(form.Any(l => l.Contains("name", StringComparison.Ordinal))).IsTrue();
        await Assert.That(form.Count(l => l.Contains(Caret, StringComparison.Ordinal))).IsEqualTo(1);
    }

    /// <summary>The accent block the open field paints its caret in (see <see cref="ScreenChrome"/>).</summary>
    private const string Caret = "[" + ScreenPalette.Ink + " on " + ScreenPalette.Accent + "]";

    /// <summary>
    /// The same rule on all five list screens, because a screen that made this one thing easier while
    /// its four siblings did not would be worse than none of them doing it. Every add button creates
    /// something whose first field is its name, and lands the buffer on that name.
    /// </summary>
    [Test]
    public async Task EveryAddButtonLandsInTheNewRowsFirstField()
    {
        foreach (var (name, session, model, label, expected) in EveryAddButton())
        {
            var pane = session.Focus().Pane;
            var pressed = false;
            for (var row = 0; row < 40 && !pressed; row++)
            {
                if (model().ButtonAt(pane, row)?.Label != label)
                {
                    continue;
                }

                session.Selection.Seed(pane, row);
                session.Handle(Key(ConsoleKey.Enter));
                pressed = true;
            }

            await Assert.That(pressed).IsTrue().Because($"{name}: no [{label}] row to press");
            await Assert.That(session.IsEditing).IsTrue().Because($"{name}: [{label}] left no field open");
            await Assert.That(session.Focus().Edit!.Value.Field).IsEqualTo(0)
                .Because($"{name}: an add should open the new row's first field, which is its name");
            await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo(expected).Because(name);
        }
    }

    /// <summary>
    /// And a <em>removal</em> deliberately opens nothing. The row left under the cursor afterwards is
    /// whatever survived, not something anybody asked to rename, and opening it would put a buffer over
    /// a value the user never chose to touch.
    /// </summary>
    [Test]
    public async Task RemovingLeavesNoFieldOpen()
    {
        var sets = Sets();
        var session = new SettingsSession(selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));

        session.Handle(Key(ConsoleKey.Delete));

        await Assert.That(sets[0].Triggers).IsEmpty();
        await Assert.That(session.IsEditing).IsFalse();
    }

    /// <summary>
    /// One add button per list screen, with the name the thing it creates arrives under, and the
    /// screen's own projection so the button's row can be found the way the renderer finds it.
    /// </summary>
    private static IEnumerable<(
        string Name, SettingsSession Session, Func<ScreenModel> Model, string Label, string Expected)>
        EveryAddButton()
    {
        var sets = Sets();
        var triggers = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0)));
        yield return (
            "F2 triggers",
            triggers,
            () => TriggersScreenRenderer.Model(sets, triggers.Selection.SelectionIn(0)),
            TriggersScreenRenderer.AddTriggerLabel,
            "New Trigger");

        var aliasSets = Sets();
        var aliases = new SettingsSession(
            selection => AliasesScreenRenderer.Model(aliasSets, selection.SelectionIn(0)));
        yield return (
            "F3 aliases",
            aliases,
            () => AliasesScreenRenderer.Model(aliasSets, aliases.Selection.SelectionIn(0)),
            AliasesScreenRenderer.AddAliasLabel,
            "New Alias");

        // F4's pane is every set's bindings flattened, so the list has to be re-gathered per projection
        // or the row the add button just created is invisible to the very next model.
        var keypadSets = Sets();
        List<Macro> Macros() => keypadSets.SelectMany(s => s.Macros).ToList();
        var keypad = new SettingsSession(
            selection => KeypadScreenRenderer.Model(Macros(), keypadSets, selection.SelectionIn(0)));
        yield return (
            "F4 keypad",
            keypad,
            () => KeypadScreenRenderer.Model(Macros(), keypadSets, keypad.Selection.SelectionIn(0)),
            KeypadScreenRenderer.AddBindingLabel,
            "New Binding");

        var worlds = Worlds();
        var worldSets = Sets();
        var world = WorldsSession(worlds, worldSets);
        yield return (
            "F5 worlds",
            world,
            () => WorldsScreenRenderer.Model(
                worlds,
                worldSets,
                world.Selection.SelectionIn(WorldsScreenRenderer.WorldsPane),
                world.Selection.SelectionIn(WorldsScreenRenderer.CharactersPane),
                world.Selection.SelectionIn(WorldsScreenRenderer.TriggerSetsPane)),
            WorldsScreenRenderer.AddWorldLabel,
            "New World");

        var timerSets = Sets();
        var timers = new SettingsSession(
            selection => TimersScreenRenderer.Model(timerSets, selection.SelectionIn(0)));
        yield return (
            "F6 timers",
            timers,
            () => TimersScreenRenderer.Model(timerSets, timers.Selection.SelectionIn(0)),
            TimersScreenRenderer.AddTimerLabel,
            "New Timer");
    }
}
