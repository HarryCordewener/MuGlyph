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
    public async Task Render_SelectedTriggerEditorShowsPatternAndRouteList()
    {
        var lines = TriggersScreenRenderer.Render(Scene(), selectedTrigger: 0, spawnTargets: new[] { "Chat", "Combat log" });

        await Assert.That(lines.Any(l => l.Contains("match pattern"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains(@"^(\w+) tells you"))).IsTrue();

        await Assert.That(lines.Any(l => l.Contains("route to"))).IsTrue();

        // Current route (Chat) is marked with the accent dot; "main" and "Combat log" are hollow.
        var chatRow = lines.Single(l => l.Contains("●") && l.Contains("Chat") && !l.Contains("Combat log"));
        await Assert.That(chatRow).Contains("#00f5b7");

        var mainRow = lines.Single(l => l.EndsWith("main", StringComparison.Ordinal));
        await Assert.That(mainRow).Contains("○");
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
}
