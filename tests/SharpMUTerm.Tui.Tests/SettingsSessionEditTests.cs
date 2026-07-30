using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The field-edit state machine: what ⏎ does on each kind of row, what the keyboard means once an
/// edit is open, what a rejected value does, and what Cancel puts back. All of it is pure, so the
/// whole contract is asserted here rather than being discovered in a terminal.
/// </summary>
public class SettingsSessionEditTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key, ConsoleModifiers modifiers = default) =>
        new('\0', key, modifiers.HasFlag(ConsoleModifiers.Shift), false,
            modifiers.HasFlag(ConsoleModifiers.Control));

    private static ConsoleKeyInfo Char(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static void Type(SettingsSession session, string text)
    {
        foreach (var c in text)
        {
            session.Handle(Char(c));
        }
    }

    /// <summary>A one-pane screen: a stop, a checkbox, and a two-field record (a host and a port).</summary>
    private sealed class Scene
    {
        public bool Flag { get; set; }

        public string Host { get; set; } = "aardmud.org";

        public int Port { get; set; } = 4000;

        public SettingsSession Session() => new(_ => new ScreenModel(new[]
        {
            ScreenRow.Stop,
            ScreenRow.Of(ScreenToggle.Bind(() => Flag, v => Flag = v)),
            ScreenRow.Of(
                ScreenField.Text("host", () => Host, v => Host = v),
                ScreenField.Integer("port", () => Port, v => Port = v, 1, 65535)),
        }));
    }

    /// <summary>Puts the cursor on the record row (index 2) and opens its first field.</summary>
    private static SettingsSession Editing(Scene scene)
    {
        var session = scene.Session();
        session.Handle(Key(ConsoleKey.DownArrow));
        session.Handle(Key(ConsoleKey.DownArrow));
        session.Handle(Key(ConsoleKey.Enter));
        return session;
    }

    /// <summary>
    /// ⏎ on a row with nothing to open still closes the screen — it just no longer claims to save
    /// anything on the way, because a committed value was written when it was committed.
    /// </summary>
    [Test]
    public async Task Enter_OnARowWithNoFieldStillClosesTheScreen()
    {
        var session = new Scene().Session();

        await Assert.That(session.Handle(Key(ConsoleKey.Enter))).IsEqualTo(ScreenAction.Close);
        await Assert.That(session.IsEditing).IsFalse();

        session.Handle(Key(ConsoleKey.DownArrow));
        await Assert.That(session.Handle(Key(ConsoleKey.Enter))).IsEqualTo(ScreenAction.Close);
    }

    [Test]
    public async Task Enter_OnAFieldRowOpensAnEditSeededWithTheCurrentValue()
    {
        var session = Editing(new Scene());

        await Assert.That(session.IsEditing).IsTrue();
        await Assert.That(session.Focus().Edit).IsEqualTo(
            new ScreenFieldEdit(0, "aardmud.org", 11, null, RowFields: 2));
    }

    [Test]
    public async Task TypingInsertsAtTheCaretAndBackspaceRemovesBehindIt()
    {
        var scene = new Scene();
        var session = Editing(scene);

        session.Handle(Key(ConsoleKey.Backspace));
        session.Handle(Key(ConsoleKey.Backspace));
        session.Handle(Key(ConsoleKey.Backspace));
        Type(session, "net");

        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("aardmud.net");

        // Nothing is written until it is committed — the buffer is not config.
        await Assert.That(scene.Host).IsEqualTo("aardmud.org");

        await Assert.That(session.Handle(Key(ConsoleKey.Enter))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(scene.Host).IsEqualTo("aardmud.net");
        await Assert.That(session.IsEditing).IsFalse();
    }

    [Test]
    public async Task SpaceTypesASpaceInsteadOfTogglingWhileAnEditIsOpen()
    {
        var scene = new Scene();
        var session = Editing(scene);

        session.Handle(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false));

        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("aardmud.org ");
        await Assert.That(scene.Flag).IsFalse();
    }

    [Test]
    public async Task TheCaretMovesWithTheArrowsHomeAndEnd_AndDeleteTakesTheCharacterUnderIt()
    {
        var session = Editing(new Scene());

        session.Handle(Key(ConsoleKey.Home));
        await Assert.That(session.Focus().Edit!.Value.Caret).IsEqualTo(0);

        session.Handle(Key(ConsoleKey.Delete));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("ardmud.org");

        session.Handle(Key(ConsoleKey.RightArrow));
        Type(session, "-");
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("a-rdmud.org");

        session.Handle(Key(ConsoleKey.End));
        await Assert.That(session.Focus().Edit!.Value.Caret).IsEqualTo(11);

        // Neither end wraps, and a caret that can't move doesn't cost a redraw.
        await Assert.That(session.Handle(Key(ConsoleKey.RightArrow))).IsEqualTo(ScreenAction.Consumed);
        session.Handle(Key(ConsoleKey.Home));
        await Assert.That(session.Handle(Key(ConsoleKey.LeftArrow))).IsEqualTo(ScreenAction.Consumed);
    }

    [Test]
    public async Task Escape_AbandonsTheEditAndLeavesTheScreenOpen()
    {
        var scene = new Scene();
        var session = Editing(scene);
        Type(session, "!!!");

        await Assert.That(session.Handle(Key(ConsoleKey.Escape))).IsEqualTo(ScreenAction.Redraw);

        await Assert.That(session.IsEditing).IsFalse();
        await Assert.That(scene.Host).IsEqualTo("aardmud.org");
        await Assert.That(session.Edits.HasDeletions).IsFalse();

        // Only now does Esc mean the screen — and there it closes rather than cancelling. This is the
        // layering, and the whole of the scope rule in two keystrokes: the first Esc threw away a buffer
        // config never saw, the second leaves and keeps everything that was confirmed.
        await Assert.That(session.Handle(Key(ConsoleKey.Escape))).IsEqualTo(ScreenAction.Close);
    }

    [Test]
    public async Task ARejectedValueKeepsTheEditOpen_MarksTheField_AndNeverReachesConfig()
    {
        var scene = new Scene();
        var session = Editing(scene);

        session.Handle(Key(ConsoleKey.Tab)); // commit the host, step to the port
        for (var i = 0; i < 4; i++)
        {
            session.Handle(Key(ConsoleKey.Backspace));
        }

        Type(session, "99999");
        await Assert.That(session.Handle(Key(ConsoleKey.Enter))).IsEqualTo(ScreenAction.Redraw);

        await Assert.That(session.IsEditing).IsTrue();
        await Assert.That(session.Focus().Edit!.Value.Error).IsEqualTo("port must be a whole number 1-65535");
        await Assert.That(scene.Port).IsEqualTo(4000);

        // Correcting it clears the mark and lets it through.
        session.Handle(Key(ConsoleKey.Backspace));
        await Assert.That(session.Focus().Edit!.Value.Error).IsNull();
        session.Handle(Key(ConsoleKey.Enter));

        await Assert.That(scene.Port).IsEqualTo(9999);
        await Assert.That(session.IsEditing).IsFalse();
    }

    [Test]
    public async Task Tab_CommitsTheFieldAndStepsToTheRowsNext_WrappingBack()
    {
        var scene = new Scene();
        var session = Editing(scene);
        Type(session, ".uk");

        session.Handle(Key(ConsoleKey.Tab));

        await Assert.That(scene.Host).IsEqualTo("aardmud.org.uk");
        await Assert.That(session.Focus().Edit!.Value.Field).IsEqualTo(1);
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("4000");

        session.Handle(Key(ConsoleKey.Tab));
        await Assert.That(session.Focus().Edit!.Value.Field).IsEqualTo(0);

        session.Handle(Key(ConsoleKey.Tab, ConsoleModifiers.Shift));
        await Assert.That(session.Focus().Edit!.Value.Field).IsEqualTo(1);
    }

    [Test]
    public async Task Tab_WillNotStepPastAValueThatDoesNotValidate()
    {
        var scene = new Scene();
        var session = Editing(scene);
        session.Handle(Key(ConsoleKey.Home));
        for (var i = 0; i < 11; i++)
        {
            session.Handle(Key(ConsoleKey.Delete));
        }

        session.Handle(Key(ConsoleKey.Tab));

        await Assert.That(session.Focus().Edit!.Value.Field).IsEqualTo(0);
        await Assert.That(session.Focus().Edit!.Value.Error).IsNotNull();
        await Assert.That(scene.Host).IsEqualTo("aardmud.org");
    }

    /// <summary>
    /// ⌃S is retired, in both states. It used to save from navigation and to commit an open field before
    /// saving; a committed value is now written the instant it is committed, so the chord had nothing left
    /// to do that ⏎ does not already do. Neither state answers it — it goes to the framework — and
    /// crucially it no longer touches the open buffer, which is the state where a half-typed value lives.
    /// </summary>
    [Test]
    public async Task ControlS_IsNotAScreenKeyInEitherState()
    {
        var scene = new Scene();
        var session = scene.Session();

        await Assert.That(session.Handle(Key(ConsoleKey.S, ConsoleModifiers.Control)))
            .IsEqualTo(ScreenAction.None);

        session = Editing(scene);
        Type(session, ".uk");
        await Assert.That(session.Handle(Key(ConsoleKey.S, ConsoleModifiers.Control)))
            .IsEqualTo(ScreenAction.None);
        await Assert.That(session.IsEditing).IsTrue();
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("aardmud.org.uk");
        await Assert.That(scene.Host).IsEqualTo("aardmud.org"); // still uncommitted, as ⏎ is what commits
    }

    [Test]
    public async Task NavigationIsSuspendedWhileAnEditIsOpen()
    {
        var session = Editing(new Scene());

        session.Handle(Key(ConsoleKey.UpArrow));
        session.Handle(Key(ConsoleKey.DownArrow));

        await Assert.That(session.Focus().Index).IsEqualTo(2);
        await Assert.That(session.IsEditing).IsTrue();
    }

    [Test]
    public async Task UpAndDownCycleAnEnumFieldsChoices()
    {
        var format = LogFormat.Plain;
        var session = new SettingsSession(_ => new ScreenModel(new[]
        {
            ScreenRow.Of(ScreenField.Enumeration("format", () => format, v => format = v)),
        }));

        session.Handle(Key(ConsoleKey.Enter));
        session.Handle(Key(ConsoleKey.DownArrow));

        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("Html");
        session.Handle(Key(ConsoleKey.UpArrow));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("Plain");

        session.Handle(Key(ConsoleKey.Enter));
        await Assert.That(format).IsEqualTo(LogFormat.Plain);
    }

    [Test]
    public async Task CancellingTheScreenPutsAnEditedValueBack_JustLikeAToggle()
    {
        var scene = new Scene();
        var session = Editing(scene);
        Type(session, ".uk");
        session.Handle(Key(ConsoleKey.Tab)); // commits the host, opens the port
        for (var i = 0; i < 4; i++)
        {
            session.Handle(Key(ConsoleKey.Backspace));
        }

        Type(session, "4201");
        session.Handle(Key(ConsoleKey.Enter));
        session.Handle(Key(ConsoleKey.UpArrow));
        session.Handle(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false));

        await Assert.That(scene.Host).IsEqualTo("aardmud.org.uk");
        await Assert.That(scene.Port).IsEqualTo(4201);
        await Assert.That(scene.Flag).IsTrue();

        // Closing the screen — and the review's "put them back", which is the strongest thing the way out
        // can do — leaves all three exactly as they were committed. This test asserted the reverse until
        // now, and the reverse *was* the reported bug: an edited address that went back to the old value
        // the moment the user left the screen.
        await Assert.That(session.Handle(Key(ConsoleKey.Escape))).IsEqualTo(ScreenAction.Close);
        session.Edits.Revert();

        await Assert.That(scene.Host).IsEqualTo("aardmud.org.uk");
        await Assert.That(scene.Port).IsEqualTo(4201);
        await Assert.That(scene.Flag).IsTrue();
    }

    [Test]
    public async Task AnEditAbandonsItselfWhenTheRowItWasOpenedOnDisappears()
    {
        var rows = new List<string> { "one", "two" };
        var session = new SettingsSession(_ => new ScreenModel(
            ScreenModel.Rows(rows, r => ScreenRow.Of(
                ScreenField.Text("name", () => r, _ => { })))));

        session.Handle(Key(ConsoleKey.DownArrow));
        session.Handle(Key(ConsoleKey.Enter));
        await Assert.That(session.IsEditing).IsTrue();

        rows.RemoveAt(1);
        await Assert.That(session.Handle(Char('x'))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(session.IsEditing).IsFalse();
    }

    [Test]
    public async Task AnUnrelatedKeyIsStillLeftForTheFrameworkMidEdit()
    {
        var session = Editing(new Scene());

        await Assert.That(session.Handle(Key(ConsoleKey.F5))).IsEqualTo(ScreenAction.None);
        await Assert.That(session.IsEditing).IsTrue();
    }

    /// <summary>
    /// The screens' real bindings, end to end: the field ordinals a renderer draws against are the
    /// ordinals the session opens, and each writes to the property the screen says it does.
    /// </summary>
    [Test]
    public async Task TheRealScreensBindTheirFieldsInTheOrderTheyAreDrawn()
    {
        var world = new WorldDefinition { Name = "Aardwolf", Host = "aardmud.org", Port = 4000 };
        var worlds = new List<WorldDefinition> { world };
        var model = WorldsScreenRenderer.Model(worlds, Array.Empty<TriggerSet>(), 0, -1);

        await Assert.That(model.FieldAt(0, 0, 0)!.Value.Get()).IsEqualTo("Aardwolf");
        await Assert.That(model.FieldAt(0, 0, 1)!.Value.Get()).IsEqualTo("aardmud.org");
        await Assert.That(model.FieldAt(0, 0, 2)!.Value.Get()).IsEqualTo("4000");
        // "auto" rather than "UTF-8": a world's encoding now defaults to following CHARSET negotiation,
        // and naming one is an override. The claim here is unchanged — ordinal 3 is the encoding field
        // and reads the world's own property — it is the property's default that moved.
        await Assert.That(model.FieldAt(0, 0, 3)!.Value.Get()).IsEqualTo("auto");
        await Assert.That(model.FieldAt(0, 0, 4)!.Value.Get()).IsEqualTo("0");

        new ScreenEdits().Apply(model.FieldAt(0, 0, 1)!.Value, "example.net");
        await Assert.That(world.Host).IsEqualTo("example.net");
    }

    /// <summary>
    /// The security pane is reachable with the key the header advertises, and Space presses it — a pane
    /// ⇥ could not get to would be two checkboxes nobody can use. It is the last pane, so ⇥ walks
    /// worlds → characters → trigger sets → security, and Shift+⇥ comes straight back.
    /// </summary>
    [Test]
    public async Task TabReachesTheWorldsSecurityCheckboxes_AndSpacePressesThem()
    {
        var world = new WorldDefinition
        {
            Name = "Aardwolf",
            Host = "aardmud.org",
            Characters = new List<CharacterDefinition> { new() { Name = "Kaz", TriggerSets = { "Comms" } } },
        };
        var worlds = new List<WorldDefinition> { world };
        var sets = new List<TriggerSet> { new() { Name = "Comms" } };
        var session = new SettingsSession(selection => WorldsScreenRenderer.Model(
            worlds,
            sets,
            selection.SelectionIn(WorldsScreenRenderer.WorldsPane),
            selection.SelectionIn(WorldsScreenRenderer.CharactersPane)));

        // One hop, not three: the checkboxes are drawn inside the WORLD block at the top of the detail
        // column, and ⇥ follows the screen as it is drawn rather than as its panes are numbered.
        await Assert.That(session.Handle(Key(ConsoleKey.Tab))).IsEqualTo(ScreenAction.Redraw);

        await Assert.That(session.Selection.Pane).IsEqualTo(WorldsScreenRenderer.SecurityPane);

        await Assert.That(session.Handle(Key(ConsoleKey.Spacebar))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(world.UseTls).IsTrue();

        session.Handle(Key(ConsoleKey.DownArrow));
        session.Handle(Key(ConsoleKey.Spacebar));
        await Assert.That(world.AllowInvalidCertificates).IsTrue();

        // Esc closes the screen and the security changes stay. They were committed by the Space that
        // pressed them — TLS in particular is a setting you would then reconnect to test, and a close
        // that quietly reverted it would leave the user reconnecting against the old one.
        await Assert.That(session.Handle(Key(ConsoleKey.Escape))).IsEqualTo(ScreenAction.Close);
        session.Edits.Revert();
        await Assert.That(world.UseTls).IsTrue();
        await Assert.That(world.AllowInvalidCertificates).IsTrue();
    }

    /// <summary>
    /// The character row's fields, in the order ⇥ steps through them, which is the order the CHARACTER
    /// form draws them: name → password → connect → on connect → log → log folder. ⏎ still opens the
    /// name, and the walk is asserted stop by stop rather than by hopping to the end, because the whole
    /// argument for inserting the two new fields in drawn order instead of appending them is that ⇥ never
    /// jumps back up the form.
    /// </summary>
    [Test]
    public async Task TheCharacterRowStepsFromItsNameThroughToItsLog()
    {
        var character = new CharacterDefinition { Name = "Kaz", OnConnect = "look" };
        var worlds = new List<WorldDefinition>
        {
            new()
            {
                Name = "Aardwolf",
                Host = "aardmud.org",
                Characters = new List<CharacterDefinition> { character },
            },
        };
        var session = new SettingsSession(selection => WorldsScreenRenderer.Model(
            worlds,
            Array.Empty<TriggerSet>(),
            selection.SelectionIn(WorldsScreenRenderer.WorldsPane),
            selection.SelectionIn(WorldsScreenRenderer.CharactersPane)));

        session.Handle(Key(ConsoleKey.Tab));   // the world's security checkboxes, drawn above the list
        session.Handle(Key(ConsoleKey.Tab));   // the character's own row
        session.Handle(Key(ConsoleKey.Enter));
        await Assert.That(session.Focus().Edit!.Value.Field).IsEqualTo(WorldsScreenRenderer.CharacterNameField);

        foreach (var next in new[]
        {
            WorldsScreenRenderer.PasswordField,
            WorldsScreenRenderer.ConnectStringField,
            WorldsScreenRenderer.OnConnectField,
            WorldsScreenRenderer.LogFormatField,
        })
        {
            session.Handle(Key(ConsoleKey.Tab));
            await Assert.That(session.Focus().Edit!.Value.Field).IsEqualTo(next);
        }

        // ↑↓ cycle an enum, which is what makes the log format usable without typing it.
        session.Handle(Key(ConsoleKey.DownArrow));
        session.Handle(Key(ConsoleKey.Enter));
        await Assert.That(character.Logging.Format).IsNotEqualTo(LogFormat.None);
    }

    [Test]
    public async Task EditingATriggersPatternRecompilesItsMatcher()
    {
        var sets = new List<TriggerSet>
        {
            new() { Name = "Comms", Triggers = new List<Trigger> { new() { Name = "Tell", Pattern = "tells you" } } },
        };
        var trigger = sets[0].Triggers[0];
        await Assert.That(trigger.Regex.IsMatch("she tells you hi")).IsTrue();

        new ScreenEdits().Apply(
            TriggersScreenRenderer.Model(sets, 0).FieldAt(0, 0, TriggersScreenRenderer.PatternField)!.Value,
            "pages you");

        await Assert.That(trigger.Regex.IsMatch("she tells you hi")).IsFalse();
        await Assert.That(trigger.Regex.IsMatch("she pages you")).IsTrue();
    }

    [Test]
    public async Task EditingAnAliasPatternRecompilesItsMatcher()
    {
        var sets = new List<TriggerSet>
        {
            new() { Name = "Comms", Aliases = new List<Alias> { new() { Name = "k", Pattern = "^k$" } } },
        };
        var alias = sets[0].Aliases[0];
        await Assert.That(alias.Regex.IsMatch("k")).IsTrue();

        new ScreenEdits().Apply(
            AliasesScreenRenderer.Model(sets, 0).FieldAt(0, 0, AliasesScreenRenderer.PatternField)!.Value, "^kk$");

        await Assert.That(alias.Regex.IsMatch("k")).IsFalse();
        await Assert.That(alias.Regex.IsMatch("kk")).IsTrue();
    }
}
