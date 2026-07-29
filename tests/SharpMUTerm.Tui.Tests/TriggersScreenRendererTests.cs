using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

public class TriggersScreenRendererTests
{
    private static IReadOnlyList<TriggerSet> Scene() => new[]
    {
        new TriggerSet
        {
            Name = "Comms",
            Triggers = new List<Trigger>
            {
                new()
                {
                    Name = "Tell",
                    Pattern = @"^(\w+) tells you",
                    Enabled = true,
                    Actions = new TriggerActions
                    {
                        HighlightForeground = TerminalColor.FromRgb(0xff, 0xd7, 0x00),
                        Gag = false,
                        SpawnTarget = "Chat",
                    },
                },
                new()
                {
                    Name = "Spam",
                    Pattern = @"^\[guild\]",
                    Enabled = false,
                    Actions = new TriggerActions { Gag = true },
                },
            },
        },
        new TriggerSet
        {
            Name = "Combat",
            Triggers = new List<Trigger>
            {
                new()
                {
                    Name = "LowHp",
                    Pattern = @"hp: (\d+)",
                    Enabled = true,
                    Actions = new TriggerActions { SendResponse = "quaff potion", ScriptCallback = "onLowHp" },
                },
            },
        },
    };

    [Test]
    public async Task Render_RuleListShowsNamePatternOwningSetAndRoute()
    {
        var lines = TriggersScreenRenderer.Render(Scene(), selectedTrigger: 0, spawnTargets: new[] { "Chat", "Combat log" });

        var rowIndex = lines.FindIndex(l => l.Contains("Tell") && l.Contains(@"^(\w+) tells you"));
        await Assert.That(lines[rowIndex]).Contains("→ Chat");

        var sub = lines[rowIndex + 1];
        await Assert.That(sub).Contains("Comms");
    }

    [Test]
    public async Task Render_FlagsSummariseGagHighlightAndSpawn()
    {
        var lines = TriggersScreenRenderer.Render(Scene(), selectedTrigger: 0, spawnTargets: new[] { "Chat" });

        var tellRowIndex = lines.FindIndex(l => l.Contains("Tell") && l.Contains(@"^(\w+) tells you"));
        var tellSub = lines[tellRowIndex + 1];
        await Assert.That(tellSub).Contains("Comms");
        await Assert.That(tellSub).Contains('H'); // highlight foreground set
        await Assert.That(tellSub).Contains(Glyphs.Capture); // spawn target set

        var spamRowIndex = lines.FindIndex(l => l.Contains("[bold]Spam[/]"));
        var spamSub = lines[spamRowIndex + 1];
        await Assert.That(spamSub).Contains('G'); // gag set
    }

    [Test]
    public async Task Render_SelectedTriggerEditorShowsPatternAndRoute()
    {
        var lines = TriggersScreenRenderer.Render(Scene(), selectedTrigger: 0, spawnTargets: new[] { "Chat", "Combat log" });

        await Assert.That(lines.Any(l => l.Contains("match pattern"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains(@"^(\w+) tells you"))).IsTrue();

        // The route is the window's name, drawn as an editable value like every other row here — the
        // windows already in use are ↑↓ suggestions while it is open, not a fixed set of rows.
        await Assert.That(lines.Any(l => l.Contains("route to"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("Chat"))).IsTrue();

        // The windows this rule does not point at are suggestions, so they are not drawn at rest.
        await Assert.That(lines.Any(l => l.Contains("Combat log"))).IsFalse();
    }

    [Test]
    public async Task Render_GagToggleReflectsActionsGag()
    {
        var gagged = TriggersScreenRenderer.Render(Scene(), selectedTrigger: 1, spawnTargets: Array.Empty<string>());
        await Assert.That(gagged.Any(l => l.Contains("[[x]] gag line") || l.Contains("#00f5b7][[x]][/] gag line"))).IsTrue();

        var notGagged = TriggersScreenRenderer.Render(Scene(), selectedTrigger: 0, spawnTargets: Array.Empty<string>());
        await Assert.That(notGagged.Any(l => l.Contains("[dim][[ ]] gag line[/]"))).IsTrue();
    }

    /// <summary>
    /// The swatches show the colours, and the section heading above them says what they add up to. That
    /// summary used to be drawn as <c>[[x]] highlight line</c> — a checkbox the cursor cannot reach and
    /// Space does nothing to, sitting *below* the two rows it was derived from. The assertion is kept
    /// pointed the other way so it cannot come back as a checkbox.
    /// </summary>
    [Test]
    public async Task Render_HighlightCaptionAndSwatchAppearWhenColourSet()
    {
        var lines = TriggersScreenRenderer.Render(Scene(), selectedTrigger: 0, spawnTargets: Array.Empty<string>());

        var heading = lines.Single(l => l.Contains("highlight") && !l.Contains("fg") && !l.Contains("bg"));
        await Assert.That(heading).Contains("recoloured");
        await Assert.That(heading).DoesNotContain("[[x]]");
        await Assert.That(heading).DoesNotContain("[[ ]]");

        await Assert.That(lines.Any(l => l.Contains("████") && l.Contains("fg"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("#ffd700"))).IsTrue();
    }

    [Test]
    public async Task Render_EmptySetsShowsNoTriggers()
    {
        var lines = TriggersScreenRenderer.Render(Array.Empty<TriggerSet>(), selectedTrigger: -1, spawnTargets: Array.Empty<string>());

        await Assert.That(lines.Any(l => l.Contains("no triggers"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("Triggers & spawn routing"))).IsTrue();
    }

    [Test]
    public async Task Render_EscapesMarkupBracketsInNamesAndPatterns()
    {
        var sets = new[]
        {
            new TriggerSet
            {
                Name = "Weird[Set]",
                Triggers = new List<Trigger>
                {
                    new() { Name = "Br[acket]", Pattern = "x[1]", Actions = new TriggerActions() },
                },
            },
        };

        var lines = TriggersScreenRenderer.Render(sets, selectedTrigger: 0, spawnTargets: Array.Empty<string>());
        await Assert.That(lines.Any(l => l.Contains("Br[[acket]]"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("x[[1]]"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("Weird[[Set]]"))).IsTrue();
    }

    /// <summary>
    /// The rule list's key, at the foot of the column. The sub-row compresses a rule to single cells —
    /// <c>▪ Comms · H ✎ ⇥ ƒ</c> is four facts in five glyphs — and until this block existed nothing on
    /// the screen said what any of them meant. Every mark a row can carry has to be named, for the same
    /// reason the attribute legend names every attribute: a key with gaps in it is a key you cannot
    /// trust to be complete.
    /// </summary>
    [Test]
    public async Task TheRuleListKeyNamesEveryMarkARowCanCarry()
    {
        var column = TriggersScreenRenderer.RulesColumn(Scene(), selectedTrigger: 0);
        var key = string.Join("\n", column.SkipWhile(l => !l.Contains("key")));

        foreach (var meaning in new[]
                 {
                     "enabled", "set", "highlight", "gag", "rewrite", "respond", "routed", "script",
                 })
        {
            await Assert.That(key).Contains(meaning);
        }

        foreach (var glyph in new[] { "✓", "▪", "H", "G", "✎", "R", "ƒ" })
        {
            await Assert.That(key).Contains(glyph);
        }
    }

    /// <summary>
    /// It also reads the row the cursor is on: the marks that rule carries are lit and the rest muted,
    /// which is what turns a key into an answer. The same trick the editor pane's attribute legend
    /// plays on the open buffer.
    /// </summary>
    [Test]
    public async Task TheRuleListKeyLightsTheMarksTheSelectedRuleCarries()
    {
        var sets = new[]
        {
            new TriggerSet
            {
                Name = "Comms",
                Triggers = new List<Trigger>
                {
                    new()
                    {
                        Name = "gagged",
                        Pattern = "^x$",
                        Enabled = true,
                        Actions = new TriggerActions { Gag = true },
                    },
                    new()
                    {
                        Name = "plain",
                        Pattern = "^y$",
                        Enabled = false,
                        Actions = new TriggerActions(),
                    },
                },
            },
        };

        var gagged = Key(TriggersScreenRenderer.RulesColumn(sets, selectedTrigger: 0));
        var plain = Key(TriggersScreenRenderer.RulesColumn(sets, selectedTrigger: 1));

        await Assert.That(Lit(gagged, "gag")).IsTrue();
        await Assert.That(Lit(gagged, "enabled")).IsTrue();

        // The second rule is disabled and does nothing at all, so nothing in its reading is lit but the
        // set every drawn row has.
        await Assert.That(Lit(plain, "gag")).IsFalse();
        await Assert.That(Lit(plain, "enabled")).IsFalse();
        await Assert.That(Lit(plain, "set")).IsTrue();
    }

    /// <summary>The key block, as one string — everything from the row that names it onward.</summary>
    private static string Key(IEnumerable<string> column) =>
        string.Join("\n", column.SkipWhile(l => !l.Contains("key")));

    /// <summary>Whether a key entry is drawn lit (accent glyph, primary ink) rather than muted.</summary>
    private static bool Lit(string key, string meaning) =>
        key.Contains($"[{ScreenPalette.Value}]{meaning}[/]", StringComparison.Ordinal);
}
