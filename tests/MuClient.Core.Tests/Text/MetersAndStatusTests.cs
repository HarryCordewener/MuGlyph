using MuClient.Core.Text;

namespace MuClient.Core.Tests.Text;

public class MetersAndStatusTests
{
    [Test]
    public async Task Bar_FillsProportionally()
    {
        await Assert.That(Meters.Bar(78, 100, 8)).IsEqualTo("██████░░");
        await Assert.That(Meters.Bar(0, 100, 4)).IsEqualTo("░░░░");
        await Assert.That(Meters.Bar(100, 100, 4)).IsEqualTo("████");
    }

    [Test]
    public async Task Bar_ClampsOutOfRange()
    {
        await Assert.That(Meters.Bar(150, 100, 4)).IsEqualTo("████");
        await Assert.That(Meters.Bar(-5, 100, 4)).IsEqualTo("░░░░");
        await Assert.That(Meters.Bar(1, 0, 4)).IsEqualTo("░░░░"); // max 0 → empty
    }

    [Test]
    public async Task Sparkline_ScalesAcrossSeriesRange()
    {
        var spark = Meters.Sparkline(new[] { 10, 20, 30, 40 });
        await Assert.That(spark).Length().IsEqualTo(4);
        await Assert.That(spark[0]).IsEqualTo('▁'); // min → lowest
        await Assert.That(spark[3]).IsEqualTo('█'); // max → highest
    }

    [Test]
    public async Task Sparkline_FlatSeries_RendersMidAndEmptyIsBlank()
    {
        await Assert.That(Meters.Sparkline(new[] { 5, 5, 5 })).DoesNotContain("█");
        await Assert.That(Meters.Sparkline(Array.Empty<int>())).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task CharacterPrompt_BindsToCharacterAtWorld()
    {
        await Assert.That(StatusFormatter.CharacterPrompt("Corvid", "aetherfall")).IsEqualTo("Corvid@aetherfall › ");
        await Assert.That(StatusFormatter.CharacterPrompt(null, null)).IsEqualTo("› ");
    }

    [Test]
    public async Task InputGutter_ShowsDestinationDraftsAndCount()
    {
        var gutter = StatusFormatter.InputGutter("main", new[] { "pages", "#public" }, 12);
        await Assert.That(gutter).Contains("→ main");
        await Assert.That(gutter).Contains("✎ pages #public");
        await Assert.That(gutter).Contains("12 chars");
    }
}
