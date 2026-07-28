using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The cursor bar the screens draw under the keyboard. A screen's model can be perfectly navigable
/// and still be unusable if nothing shows where the cursor is, so each screen's list is asserted to
/// paint the focused row — and only that row, in the focused pane.
/// </summary>
public class ScreenCursorTests
{
    /// <summary>The background the focused row is painted with (see <see cref="ScreenPalette"/>).</summary>
    private const string Bar = "[on #2e3950]";

    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Triggers = new List<Trigger>
            {
                new() { Name = "Tell", Pattern = "tells you", Actions = new TriggerActions() },
                new() { Name = "Spam", Pattern = "guild", Actions = new TriggerActions() },
            },
            Aliases = new List<Alias>
            {
                new() { Name = "k", Pattern = "^k$", Substitution = "kill" },
                new() { Name = "s", Pattern = "^s$", Substitution = "say" },
            },
            Macros = new List<Macro>
            {
                new() { Name = "look", Key = "Num5", Command = "look" },
                new() { Name = "flee", Key = "Num1", Command = "flee" },
            },
            Timers = new List<TimerDefinition>
            {
                new() { Name = "ping", IntervalSeconds = 30, Command = "look" },
                new() { Name = "tick", IntervalSeconds = 60, Command = "score" },
            },
        },
    };

    private static int Barred(IEnumerable<string> lines) => lines.Count(l => l.Contains(Bar, StringComparison.Ordinal));

    [Test]
    public async Task NoFocus_DrawsNoCursorBarAtAll()
    {
        var sets = Sets();

        await Assert.That(Barred(TriggersScreenRenderer.RulesColumn(sets, 0))).IsEqualTo(0);
        await Assert.That(Barred(AliasesScreenRenderer.ListColumn(sets, 0))).IsEqualTo(0);
        await Assert.That(Barred(TimersScreenRenderer.ListColumn(sets, 0))).IsEqualTo(0);
        await Assert.That(Barred(KeypadScreenRenderer.HotkeysColumn(sets[0].Macros))).IsEqualTo(0);
    }

    [Test]
    public async Task Triggers_BarsTheFocusedRuleAndNothingElse()
    {
        var lines = TriggersScreenRenderer.RulesColumn(Sets(), 1, new ScreenFocus(0, 1));

        await Assert.That(Barred(lines)).IsEqualTo(1);
        await Assert.That(lines.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("Spam");
    }

    [Test]
    public async Task Triggers_EditorPaneBarsItsOwnRow_AndTheRuleListStaysUnbarred()
    {
        var sets = Sets();
        var editor = TriggersScreenRenderer.EditorColumn(sets, 0, Array.Empty<string>(), new ScreenFocus(1, 1));
        var rules = TriggersScreenRenderer.RulesColumn(sets, 0, new ScreenFocus(1, 1));

        await Assert.That(Barred(rules)).IsEqualTo(0);
        await Assert.That(Barred(editor)).IsEqualTo(1);
        await Assert.That(editor.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("stop processing");
    }

    [Test]
    public async Task Aliases_BarsTheFocusedAlias_AndTheEditorsCaseRow()
    {
        var sets = Sets();

        var list = AliasesScreenRenderer.ListColumn(sets, 1, new ScreenFocus(0, 1));
        await Assert.That(list.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("[bold]s[/]");

        var editor = AliasesScreenRenderer.EditorColumn(sets, 1, new ScreenFocus(1, 0));
        await Assert.That(editor.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("case sensitive");
    }

    [Test]
    public async Task Timers_BarsTheFocusedTimer_AndTheEditorsOneShotRow()
    {
        var sets = Sets();

        var list = TimersScreenRenderer.ListColumn(sets, 0, new ScreenFocus(0, 0));
        await Assert.That(list.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("ping");

        var editor = TimersScreenRenderer.EditorColumn(sets, 0, new ScreenFocus(1, 0));
        await Assert.That(editor.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("one-shot");
    }

    [Test]
    public async Task Keypad_BarsTheFocusedBinding()
    {
        var lines = KeypadScreenRenderer.HotkeysColumn(Sets()[0].Macros, new ScreenFocus(0, 1));

        await Assert.That(Barred(lines)).IsEqualTo(1);
        await Assert.That(lines.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("Num1");
    }

    [Test]
    public async Task Options_BarsTheFocusedOptionAndNeverASectionHeader()
    {
        // Navigable row 3 is "emoji substitution" — the two section headers and the spacer are skipped.
        var screen = OptionsScreenRenderer.TextAnsiScreen();
        var lines = OptionsScreenRenderer.BodyColumn(screen.Rows, new ScreenFocus(0, 3));

        await Assert.That(Barred(lines)).IsEqualTo(1);
        await Assert.That(lines.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("emoji substitution");
    }

    [Test]
    public async Task Worlds_BarsTheFocusedWorld_Character_AndTriggerSetInTheirOwnPanes()
    {
        var worlds = new List<WorldDefinition>
        {
            new()
            {
                Name = "Aardwolf",
                Host = "aardmud.org",
                Characters = new List<CharacterDefinition> { new() { Name = "Kaz" }, new() { Name = "Mira" } },
            },
            new() { Name = "Second", Host = "example.org" },
        };
        var sets = Sets();
        var accent = WorldsScreenRenderer.AccentFor(worlds, 0);

        var onWorld = WorldsScreenRenderer.WorldsColumn(worlds, 0, new ScreenFocus(0, 1));
        await Assert.That(onWorld.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("Second");

        var onCharacter = WorldsScreenRenderer.DetailColumn(worlds, sets, 0, 1, accent, new ScreenFocus(1, 1));
        await Assert.That(onCharacter.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("Mira");

        var onSet = WorldsScreenRenderer.TriggersColumn(worlds[0].Characters[0], sets, accent, new ScreenFocus(2, 0));
        await Assert.That(onSet.Single(l => l.Contains(Bar, StringComparison.Ordinal))).Contains("Comms");
    }

    [Test]
    public async Task Worlds_TriggerSetBarsAreAllTheSameWidthSoTheColumnDoesNotShift()
    {
        var character = new CharacterDefinition { Name = "Kaz" };
        var sets = new List<TriggerSet>
        {
            new() { Name = "A", Description = "short" },
            new() { Name = "Longer name", Description = "a much longer description" },
        };
        var widths = new List<int>();
        for (var i = 0; i < sets.Count; i++)
        {
            var row = WorldsScreenRenderer.TriggersColumn(character, sets, "#00f5b7", new ScreenFocus(2, i))
                .Single(l => l.Contains(Bar, StringComparison.Ordinal));
            widths.Add(MarkupText.VisibleLength(row));
        }

        await Assert.That(widths[0]).IsEqualTo(widths[1]);
    }

    [Test]
    public async Task HeaderHints_AdvertiseOnlyTheKeysTheScreensImplement()
    {
        await Assert.That(TriggersScreenRenderer.HeaderLine(0)).Contains(ScreenChrome.ListHints);
        await Assert.That(AliasesScreenRenderer.HeaderLine(0)).Contains(ScreenChrome.ListHints);
        await Assert.That(TimersScreenRenderer.HeaderLine(0)).Contains(ScreenChrome.ListHints);
        await Assert.That(WorldsScreenRenderer.HeaderLine(0)).Contains(ScreenChrome.ListHints);
        await Assert.That(KeypadScreenRenderer.HeaderLine(0)).Contains(ScreenChrome.SingleListHints);
        await Assert.That(OptionsScreenRenderer.HeaderLine("Logging", "F9", 0))
            .Contains(ScreenChrome.SingleListHints);

        // Nothing edits a field yet, so no screen may claim ⏎ opens an editor.
        foreach (var header in new[]
        {
            TriggersScreenRenderer.HeaderLine(0),
            AliasesScreenRenderer.HeaderLine(0),
            TimersScreenRenderer.HeaderLine(0),
            WorldsScreenRenderer.HeaderLine(0),
            KeypadScreenRenderer.HeaderLine(0),
            OptionsScreenRenderer.HeaderLine("Logging", "F9", 0),
        })
        {
            await Assert.That(header).DoesNotContain("⏎ edit");
            await Assert.That(header).DoesNotContain("⏎ rebind");
            await Assert.That(header).DoesNotContain("⏎ change");
        }
    }
}
