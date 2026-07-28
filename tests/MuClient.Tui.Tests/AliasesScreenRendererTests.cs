using MuClient.Core.Automation;
using MuClient.Core.Configuration;
using MuClient.Tui;

namespace MuClient.Tui.Tests;

public class AliasesScreenRendererTests
{
    private static List<TriggerSet> Scene() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Aliases = new List<Alias>
            {
                new()
                {
                    Name = "gr",
                    Pattern = "^gr (.*)$",
                    Enabled = true,
                    CaseSensitive = false,
                    Substitution = "greet $1\nwave $1",
                },
                new()
                {
                    Name = "afk",
                    Pattern = "^afk$",
                    Enabled = false,
                    CaseSensitive = true,
                    Substitution = "pose is away from keyboard.",
                },
            },
        },
        new TriggerSet
        {
            Name = "Trade",
            Aliases = new List<Alias>
            {
                new()
                {
                    Name = "buy",
                    Pattern = "^b (.*)$",
                    Enabled = true,
                    Substitution = "buy $1",
                },
            },
        },
    };

    [Test]
    public async Task Render_TitleAndHeaderPresent()
    {
        var lines = AliasesScreenRenderer.Render(Scene(), 0);
        await Assert.That(lines[0]).Contains("Aliases");
        await Assert.That(lines[0]).Contains("F3");
        await Assert.That(lines.Any(l => l.Contains("on  name / pattern → expansion"))).IsTrue();
    }

    [Test]
    public async Task Render_FlattensAcrossSetsAndShowsOwningSet()
    {
        var lines = AliasesScreenRenderer.Render(Scene(), 0);
        await Assert.That(lines.Any(l => l.Contains("gr") && l.Contains("▪ Comms"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("buy") && l.Contains("▪ Trade"))).IsTrue();
    }

    [Test]
    public async Task Render_EnabledAndDisabledMarkersDiffer()
    {
        var lines = AliasesScreenRenderer.Render(Scene(), 0);
        var grLine = lines.Single(l => l.Contains("[bold]gr[/]"));
        var afkLine = lines.Single(l => l.Contains("[bold]afk[/]"));
        await Assert.That(grLine).Contains("✓");
        await Assert.That(afkLine).Contains("[dim]·[/]");
    }

    [Test]
    public async Task Render_SelectedAliasShowsMarkerAndEditorFields()
    {
        var lines = AliasesScreenRenderer.Render(Scene(), 0);
        var grLine = lines.Single(l => l.Contains("[bold]gr[/]"));
        await Assert.That(grLine).Contains("▸");
        await Assert.That(lines.Any(l => l.Contains("match pattern (regex)"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("expands to"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("greet $1"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("wave $1"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("case sensitive"))).IsTrue();
    }

    [Test]
    public async Task Render_CaseSensitiveCheckboxReflectsFlag()
    {
        var lines = AliasesScreenRenderer.Render(Scene(), 1);
        await Assert.That(lines.Any(l => l.Contains("[[x]][/] case sensitive"))).IsTrue();
    }

    [Test]
    public async Task Render_FooterPresent()
    {
        var lines = AliasesScreenRenderer.Render(Scene(), 0);
        await Assert.That(lines[^1]).Contains("Cancel");
        await Assert.That(lines[^1]).Contains("Save");
    }

    [Test]
    public async Task Render_EmptyShowsNoAliases()
    {
        var lines = AliasesScreenRenderer.Render(Array.Empty<TriggerSet>(), 0);
        await Assert.That(lines.Any(l => l.Contains("no aliases"))).IsTrue();
    }

    [Test]
    public async Task Render_EscapesMarkupBrackets()
    {
        var sets = new List<TriggerSet>
        {
            new()
            {
                Name = "Comms",
                Aliases = new List<Alias>
                {
                    new()
                    {
                        Name = "wei[rd]",
                        Pattern = "^x$",
                        Substitution = "y",
                    },
                },
            },
        };

        var lines = AliasesScreenRenderer.Render(sets, 0);
        await Assert.That(lines.Any(l => l.Contains("wei[[rd]]"))).IsTrue();
    }
}
