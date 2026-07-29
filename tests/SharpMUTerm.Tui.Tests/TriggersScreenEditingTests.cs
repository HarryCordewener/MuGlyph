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

    [Test]
    public async Task ARuleRowCarriesItsPatternRouteAndBothHighlightColours()
    {
        var sets = Sets();
        var model = TriggersScreenRenderer.Model(sets, 0, Targets);

        await Assert.That(model.RowAt(0, 0).FieldCount).IsEqualTo(4);
        await Assert.That(model.FieldAt(0, 0, 0)!.Value.Get()).IsEqualTo("tells you");
        await Assert.That(model.FieldAt(0, 0, 1)!.Value.Get()).IsEqualTo("Chat");
        await Assert.That(model.FieldAt(0, 0, 2)!.Value.Get()).IsEqualTo("gold");
        await Assert.That(model.FieldAt(0, 0, 3)!.Value.Get()).IsEqualTo("none");

        // The editor pane keeps exactly the two checkbox rows it had — the route and the colours are
        // one setting each, so they are fields on the rule, not new cursor stops.
        await Assert.That(model.Sizes[1]).IsEqualTo(2);
    }

    [Test]
    public async Task TheRouteGroupOffersMainEveryKnownWindowAndTheRulesOwnTarget()
    {
        var sets = Sets();

        var known = TriggersScreenRenderer.Model(sets, 0, Targets).FieldAt(0, 0, 1)!.Value.Choices;
        await Assert.That(known).IsEquivalentTo(new[] { "main", "Chat", "pages", "trade" });

        // A rule pointed at a window the workspace has no record of still offers — and keeps — its own
        // value, rather than being refused by its own field.
        var unknown = TriggersScreenRenderer.Model(sets, 0).FieldAt(0, 0, 1)!.Value;
        await Assert.That(unknown.Choices).IsEquivalentTo(new[] { "main", "Chat" });
        await Assert.That(unknown.Validate("Chat")).IsNull();
    }

    [Test]
    public async Task ChoosingMainClearsTheSpawnTarget_AndUndoPutsItBack()
    {
        var sets = Sets();
        var trigger = sets[0].Triggers[0];
        var edits = new ScreenEdits();

        edits.Apply(TriggersScreenRenderer.Model(sets, 0, Targets).FieldAt(0, 0, 1)!.Value, "main");
        await Assert.That(trigger.Actions.SpawnTarget).IsNull();

        edits.Apply(TriggersScreenRenderer.Model(sets, 0, Targets).FieldAt(0, 0, 1)!.Value, "trade");
        await Assert.That(trigger.Actions.SpawnTarget).IsEqualTo("trade");

        edits.Revert();
        await Assert.That(trigger.Actions.SpawnTarget).IsEqualTo("Chat");
    }

    [Test]
    public async Task ARouteThatNamesNoWindowIsRefused()
    {
        var field = TriggersScreenRenderer.Model(Sets(), 0, Targets).FieldAt(0, 0, 1)!.Value;

        await Assert.That(field.Validate("nowhere")).IsNotNull();
        await Assert.That(new ScreenEdits().Apply(field, "nowhere")).IsNotNull();
        await Assert.That(Sets()[0].Triggers[0].Actions.SpawnTarget).IsEqualTo("Chat");
    }

    /// <summary>
    /// ↑↓ steps the radio group, and the drawn radios follow the buffer — a group that only moved on
    /// ⏎ would look inert for exactly as long as the user was using it.
    /// </summary>
    [Test]
    public async Task UpAndDownStepTheRouteRadios_AndTheDrawnDotFollowsTheBuffer()
    {
        var sets = Sets();
        var trigger = sets[0].Triggers[0];
        var session = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.CursorIn(0), Targets));

        session.Handle(Key(ConsoleKey.Enter)); // opens the pattern
        session.Handle(Key(ConsoleKey.Tab));   // commits it, steps to the route
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("Chat");
        await Assert.That(session.Focus().Edit!.Value.HasChoices).IsTrue();

        session.Handle(Key(ConsoleKey.DownArrow));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("pages");

        // Nothing is written yet, but the radios already show where ⏎ would land.
        await Assert.That(trigger.Actions.SpawnTarget).IsEqualTo("Chat");
        var editor = TriggersScreenRenderer.EditorColumn(sets, 0, Targets, session.Focus());
        await Assert.That(editor.Any(l => l.Contains('●') && l.Contains("pages"))).IsTrue();
        await Assert.That(editor.Any(l => l.Contains('●') && l.Contains("Chat"))).IsFalse();

        session.Handle(Key(ConsoleKey.Enter));
        await Assert.That(trigger.Actions.SpawnTarget).IsEqualTo("pages");

        // Wrapping backwards off "main" lands on the last window, not on nothing.
        session.Handle(Key(ConsoleKey.Enter));
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

        edits.Apply(TriggersScreenRenderer.Model(sets, 0, Targets).FieldAt(0, 0, 2)!.Value, "none");
        await Assert.That(trigger.Actions.HighlightForeground).IsNull();

        edits.Apply(TriggersScreenRenderer.Model(sets, 0, Targets).FieldAt(0, 0, 3)!.Value, "blue");
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
        var field = TriggersScreenRenderer.Model(sets, 0, Targets).FieldAt(0, 0, 2)!.Value;

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
        // colour is turned on rather than a report that one already is.
        await Assert.That(editor.Any(l => l.Contains("fg") && l.Contains("none"))).IsTrue();
        await Assert.That(editor.Any(l => l.Contains("bg") && l.Contains("none"))).IsTrue();
        await Assert.That(editor.Any(l => l.Contains("[dim][[ ]] highlight line[/]"))).IsTrue();
    }

    [Test]
    public async Task ARejectedColourIsReportedAgainstTheHighlightHeading()
    {
        var sets = Sets();
        var session = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.CursorIn(0), Targets));

        session.Handle(Key(ConsoleKey.Enter));
        session.Handle(Key(ConsoleKey.Tab));
        session.Handle(Key(ConsoleKey.Tab)); // pattern → route → highlight fg
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
