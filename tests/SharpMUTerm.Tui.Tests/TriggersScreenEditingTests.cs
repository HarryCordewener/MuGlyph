using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// F2's route-to radio group and its highlight colour picker. Both were read-only indicators; both are
/// now fields on the rule's own list row, so they cycle with ↑↓ like any other choice and are drawn
/// where they are read. These assert the binding (what each ordinal writes), the choice set (what a
/// radio group offers), the colour round trip, and that the drawn radios follow the *buffer* rather
/// than config while an edit is open — otherwise ↑↓ would appear to do nothing until ⏎.
/// </summary>
public class TriggersScreenEditingTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    private static ConsoleKeyInfo Char(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Triggers = new List<Trigger>
            {
                new()
                {
                    Name = "Tell",
                    Pattern = "tells you",
                    Actions = new TriggerActions
                    {
                        SpawnTarget = "Chat",
                        HighlightForeground = TerminalColor.FromRgb(0xff, 0xd7, 0x00),
                    },
                },
                new() { Name = "Spam", Pattern = "guild", Actions = new TriggerActions() },
            },
        },
    };

    private static readonly string[] Targets = { "Chat", "pages", "trade" };

    /// <summary>
    /// The entry the open dropdown has marked — the value ⏎ would commit — or null when it has marked
    /// none. The mark is <c>▸</c> in the accent, drawn only by <see cref="ScreenChrome.Choices"/>;
    /// the rule list's own selection marker is a bare <c>▸</c> in bold, and lives in a different column.
    /// </summary>
    private static string? Marked(IEnumerable<string> lines) => lines
        .Where(l => l.Contains($"[{ScreenPalette.Accent}]▸[/]", StringComparison.Ordinal))
        .Select(l => l.Split($"[{ScreenPalette.Value}]", StringSplitOptions.None)[1].Split("[/]")[0])
        .FirstOrDefault();

    [Test]
    public async Task ARuleRowCarriesItsPatternRouteAndBothHighlightColours()
    {
        var sets = Sets();
        var model = TriggersScreenRenderer.Model(sets, 0, Targets);

        // The name leads, then everything the editor pane draws. The first five ordinals are unchanged
        // — the four new ones are appended, so nothing the screen, the snapshot keys or these tests
        // already address is renumbered.
        await Assert.That(model.RowAt(0, 0).FieldCount).IsEqualTo(9);
        await Assert.That(model.FieldAt(0, 0, TriggersScreenRenderer.NameField)!.Value.Get()).IsEqualTo("Tell");
        await Assert.That(model.FieldAt(0, 0, TriggersScreenRenderer.PatternField)!.Value.Get()).IsEqualTo("tells you");
        await Assert.That(model.FieldAt(0, 0, TriggersScreenRenderer.RouteField)!.Value.Get()).IsEqualTo("Chat");
        await Assert.That(model.FieldAt(0, 0, TriggersScreenRenderer.ForegroundField)!.Value.Get()).IsEqualTo("gold");
        await Assert.That(model.FieldAt(0, 0, TriggersScreenRenderer.BackgroundField)!.Value.Get()).IsEqualTo("none");
        await Assert.That(model.FieldAt(0, 0, TriggersScreenRenderer.AttributesField)!.Value.Get()).IsEqualTo("none");
        await Assert.That(model.FieldAt(0, 0, TriggersScreenRenderer.RewriteField)!.Value.Get()).IsEqualTo(string.Empty);
        await Assert.That(model.FieldAt(0, 0, TriggersScreenRenderer.ResponseField)!.Value.Get()).IsEqualTo(string.Empty);
        await Assert.That(model.FieldAt(0, 0, TriggersScreenRenderer.ScriptField)!.Value.Get()).IsEqualTo(string.Empty);

        // The editor pane holds only checkbox rows — every value it draws is a field on the rule, not a
        // cursor stop of its own. It gained the third checkbox (case sensitivity) and nothing else.
        await Assert.That(model.Sizes[1]).IsEqualTo(3);
    }

    [Test]
    public async Task TheRouteGroupOffersMainEveryKnownWindowAndTheRulesOwnTarget()
    {
        var sets = Sets();

        var known = TriggersScreenRenderer.Model(sets, 0, Targets).FieldAt(0, 0, TriggersScreenRenderer.RouteField)!.Value.Choices;
        await Assert.That(known).IsEquivalentTo(new[] { "main", "Chat", "pages", "trade" });

        // A rule pointed at a window the workspace has no record of still offers — and keeps — its own
        // value, rather than being refused by its own field.
        var unknown = TriggersScreenRenderer.Model(sets, 0).FieldAt(0, 0, TriggersScreenRenderer.RouteField)!.Value;
        await Assert.That(unknown.Choices).IsEquivalentTo(new[] { "main", "Chat" });
        await Assert.That(unknown.Validate("Chat")).IsNull();
    }

    [Test]
    public async Task ChoosingMainClearsTheSpawnTarget_AndUndoPutsItBack()
    {
        var sets = Sets();
        var trigger = sets[0].Triggers[0];
        var edits = new ScreenEdits();

        edits.Apply(TriggersScreenRenderer.Model(sets, 0, Targets).FieldAt(0, 0, TriggersScreenRenderer.RouteField)!.Value, "main");
        await Assert.That(trigger.Actions.SpawnTarget).IsNull();

        edits.Apply(TriggersScreenRenderer.Model(sets, 0, Targets).FieldAt(0, 0, TriggersScreenRenderer.RouteField)!.Value, "trade");
        await Assert.That(trigger.Actions.SpawnTarget).IsEqualTo("trade");

        edits.Revert();
        await Assert.That(trigger.Actions.SpawnTarget).IsEqualTo("Chat");
    }

    /// <summary>
    /// Typing a window nothing routes to yet is how a spawn window is created — the suggestions are
    /// the windows already in use, so refusing anything outside them could only ever re-use a window
    /// that already existed.
    /// </summary>
    [Test]
    public async Task ARouteMayNameAWindowThatDoesNotExistYet()
    {
        var sets = Sets();
        var trigger = sets[0].Triggers[0];
        var field = TriggersScreenRenderer.Model(sets, 0, Targets).FieldAt(0, 0, TriggersScreenRenderer.RouteField)!.Value;

        await Assert.That(field.Validate("nowhere")).IsNull();
        await Assert.That(new ScreenEdits().Apply(field, "nowhere")).IsNull();
        await Assert.That(trigger.Actions.SpawnTarget).IsEqualTo("nowhere");
    }

    /// <summary>A window name is a tab title, so the two things that would corrupt one are refused.</summary>
    [Test]
    public async Task ARouteIsRefusedWhenBlankOrCarryingControlCharacters()
    {
        var field = TriggersScreenRenderer.Model(Sets(), 0, Targets).FieldAt(0, 0, TriggersScreenRenderer.RouteField)!.Value;

        await Assert.That(field.Validate("   ")).IsNotNull();
        await Assert.That(field.Validate("chat\tspam")).IsNotNull();
        await Assert.That(new ScreenEdits().Apply(field, string.Empty)).IsNotNull();
        await Assert.That(Sets()[0].Triggers[0].Actions.SpawnTarget).IsEqualTo("Chat");
    }

    /// <summary>
    /// Typing a window that doesn't exist yet must be visible. The radio rows are the windows already
    /// in use, so a new name matches none of them — without a row of its own the group would sit with
    /// no dot lit while the keyboard was plainly doing something, and the user would type blind.
    /// </summary>
    [Test]
    public async Task TypingARouteThatMatchesNoKnownWindowStillShowsWhatIsBeingTyped()
    {
        var sets = Sets();
        var session = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.CursorIn(0), Targets));

        session.Handle(Key(ConsoleKey.Enter)); // opens the name
        session.Handle(Key(ConsoleKey.Tab));   // commits it, steps to the pattern
        session.Handle(Key(ConsoleKey.Tab));   // commits that, steps to the route

        // Clear the opened value ("Chat") before typing, as anyone renaming the route would.
        for (var i = 0; i < "Chat".Length; i++)
        {
            session.Handle(Key(ConsoleKey.Backspace));
        }

        foreach (var ch in "combat")
        {
            session.Handle(Char(ch));
        }

        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("combat");

        var editor = TriggersScreenRenderer.EditorColumn(sets, 0, Targets, session.Focus());
        await Assert.That(editor.Any(l => l.Contains("combat"))).IsTrue();

        // No known window is lit while the buffer names one that doesn't exist yet.
        await Assert.That(editor.Any(l => l.Contains('●') && l.Contains("Chat"))).IsFalse();
    }

    /// <summary>
    /// ↑↓ step the windows already in use, and the drawn route follows the buffer — a value that only
    /// moved on ⏎ would look inert for exactly as long as the user was using it.
    /// </summary>
    [Test]
    public async Task UpAndDownStepTheKnownWindows_AndTheDrawnRouteFollowsTheBuffer()
    {
        var sets = Sets();
        var trigger = sets[0].Triggers[0];
        var session = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.CursorIn(0), Targets));

        session.Handle(Key(ConsoleKey.Enter)); // opens the name
        session.Handle(Key(ConsoleKey.Tab));   // commits it, steps to the pattern
        session.Handle(Key(ConsoleKey.Tab));   // commits that, steps to the route
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("Chat");
        await Assert.That(session.Focus().Edit!.Value.HasChoices).IsTrue();

        session.Handle(Key(ConsoleKey.DownArrow));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("pages");

        // Nothing is written yet, but the drawn route already shows where ⏎ would land.
        await Assert.That(trigger.Actions.SpawnTarget).IsEqualTo("Chat");
        var editor = TriggersScreenRenderer.EditorColumn(sets, 0, Targets, session.Focus());
        await Assert.That(editor.Any(l => l.Contains("pages"))).IsTrue();

        // "Chat" is still on screen — the dropdown lists every window, which is the whole point of it —
        // but it is no longer the value: the mark has moved off it and onto the buffer's window, and it
        // is the *mark*, not mere presence, that says what ⏎ would write. (It used to be enough to
        // assert Chat had vanished, back when only the value was drawn.)
        await Assert.That(Marked(editor)).IsEqualTo("pages");
        await Assert.That(editor.Any(l => l.Contains("Chat"))).IsTrue();

        session.Handle(Key(ConsoleKey.Enter));
        await Assert.That(trigger.Actions.SpawnTarget).IsEqualTo("pages");

        // Wrapping backwards off "main" lands on the last window, not on nothing.
        session.Handle(Key(ConsoleKey.Enter));
        session.Handle(Key(ConsoleKey.Tab));
        session.Handle(Key(ConsoleKey.Tab));
        session.Handle(Key(ConsoleKey.UpArrow));
        session.Handle(Key(ConsoleKey.UpArrow));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("main");
        session.Handle(Key(ConsoleKey.UpArrow));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("trade");
    }

    [Test]
    public async Task TheHighlightPickerWritesAColour_AndNoneClearsIt()
    {
        var sets = Sets();
        var trigger = sets[0].Triggers[0];
        var edits = new ScreenEdits();

        edits.Apply(TriggersScreenRenderer.Model(sets, 0, Targets).FieldAt(0, 0, TriggersScreenRenderer.ForegroundField)!.Value, "none");
        await Assert.That(trigger.Actions.HighlightForeground).IsNull();

        edits.Apply(TriggersScreenRenderer.Model(sets, 0, Targets).FieldAt(0, 0, TriggersScreenRenderer.BackgroundField)!.Value, "blue");
        await Assert.That(trigger.Actions.HighlightBackground)
            .IsEqualTo(TerminalColor.FromRgb(0x00, 0x00, 0xff));

        edits.Revert();

        await Assert.That(trigger.Actions.HighlightForeground)
            .IsEqualTo(TerminalColor.FromRgb(0xff, 0xd7, 0x00));
        await Assert.That(trigger.Actions.HighlightBackground).IsNull();
    }

    /// <summary>
    /// The palette is a shortlist, not a straitjacket. A colour already in config that the palette
    /// doesn't name has to open on its own value and commit unchanged, or an existing highlight would
    /// be uneditable — the picker would refuse the value it was showing.
    /// </summary>
    [Test]
    public async Task AColourThePaletteDoesNotNameStillRoundTrips()
    {
        var sets = Sets();
        sets[0].Triggers[0].Actions.HighlightForeground = TerminalColor.FromRgb(0x12, 0x34, 0x56);
        var field = TriggersScreenRenderer.Model(sets, 0, Targets).FieldAt(0, 0, TriggersScreenRenderer.ForegroundField)!.Value;

        await Assert.That(field.Get()).IsEqualTo("#123456");
        await Assert.That(field.Validate(field.Get())).IsNull();

        new ScreenEdits().Apply(field, "idx:200");
        await Assert.That(sets[0].Triggers[0].Actions.HighlightForeground)
            .IsEqualTo(TerminalColor.FromIndex(200));

        await Assert.That(field.Validate("chartreuse")).IsNotNull();
    }

    [Test]
    public async Task TheEditorDrawsBothSwatchRowsWhetherOrNotAColourIsSet()
    {
        var sets = Sets();
        var editor = TriggersScreenRenderer.EditorColumn(sets, 1, Targets);

        // Trigger 1 has no highlight at all — the rows are still there, because they are now where a
        // colour is turned on rather than a report that one already is, and the section heading says
        // as much in words rather than in a checkbox nothing can press.
        await Assert.That(editor.Any(l => l.Contains("fg") && l.Contains("none"))).IsTrue();
        await Assert.That(editor.Any(l => l.Contains("bg") && l.Contains("none"))).IsTrue();
        await Assert.That(editor.Any(l => l.Contains("highlight") && l.Contains("left alone"))).IsTrue();
        await Assert.That(editor.Any(l => l.Contains("highlight") && l.Contains("[[ ]]"))).IsFalse();
    }

    [Test]
    public async Task ARejectedColourIsReportedAgainstTheHighlightHeading()
    {
        var sets = Sets();
        var session = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.CursorIn(0), Targets));

        session.Handle(Key(ConsoleKey.Enter));
        session.Handle(Key(ConsoleKey.Tab));
        session.Handle(Key(ConsoleKey.Tab));
        session.Handle(Key(ConsoleKey.Tab)); // name → pattern → route → highlight fg
        for (var i = 0; i < 4; i++)
        {
            session.Handle(Key(ConsoleKey.Backspace));
        }

        foreach (var c in "nope")
        {
            session.Handle(Char(c));
        }

        session.Handle(Key(ConsoleKey.Enter));

        await Assert.That(session.IsEditing).IsTrue();
        var editor = TriggersScreenRenderer.EditorColumn(sets, 0, Targets, session.Focus());
        await Assert.That(editor.Any(l => l.Contains("highlight") && l.Contains('▲'))).IsTrue();
    }

    [Test]
    public async Task ScreenColours_FormatAndParseAgreeOnNamesLiteralsAndNothing()
    {
        await Assert.That(ScreenColours.Format(null)).IsEqualTo("none");
        await Assert.That(ScreenColours.Format(TerminalColor.FromRgb(0xff, 0xd7, 0x00))).IsEqualTo("gold");
        await Assert.That(ScreenColours.Format(TerminalColor.FromRgb(0x12, 0x34, 0x56))).IsEqualTo("#123456");
        // An indexed colour stays indexed, even where the palette happens to resolve to the same RGB:
        // idx:9 is "whatever the terminal calls bright red", which is not the same promise as #ff0000.
        await Assert.That(ScreenColours.Format(TerminalColor.FromIndex(9))).IsEqualTo("idx:9");
        await Assert.That(ScreenColours.Format(TerminalColor.FromIndex(200))).IsEqualTo("idx:200");
        await Assert.That(ScreenColours.Format(TerminalColor.Default)).IsEqualTo("default");

        foreach (var name in ScreenColours.Palette)
        {
            await Assert.That(ScreenColours.TryParse(name, out var parsed)).IsTrue();
            await Assert.That(ScreenColours.Format(parsed)).IsEqualTo(name);
        }

        await Assert.That(ScreenColours.TryParse("chartreuse", out _)).IsFalse();
        await Assert.That(ScreenColours.TryParse("idx:999", out _)).IsFalse();
    }

    /// <summary>
    /// A swatch shows the colour the terminal will actually paint. An indexed colour resolving to the
    /// app accent would draw every palette highlight the same shade of teal.
    /// </summary>
    [Test]
    public async Task AnIndexedSwatchResolvesThroughTheXtermPaletteRatherThanTheAccent()
    {
        await Assert.That(ScreenColours.Hex(TerminalColor.FromIndex(196), "#00f5b7")).IsEqualTo("#ff0000");
        await Assert.That(ScreenColours.Hex(TerminalColor.FromRgb(1, 2, 3), "#00f5b7")).IsEqualTo("#010203");
        await Assert.That(ScreenColours.Hex(TerminalColor.Default, "#00f5b7")).IsEqualTo("#00f5b7");
    }
}
