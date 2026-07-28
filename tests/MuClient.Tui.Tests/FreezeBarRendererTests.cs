using MuClient.Tui;

namespace MuClient.Tui.Tests;

public class FreezeBarRendererTests
{
    [Test]
    public async Task Bar_CarriesFrozenLabelInTheAccentAndADimHint()
    {
        var bar = FreezeBarRenderer.Bar("#c678dd");

        await Assert.That(bar).Contains("[#c678dd]▲ FROZEN[/]");
        await Assert.That(bar).Contains("⌃F");
        await Assert.That(bar).Contains("[dim]");
        await Assert.That(bar).Contains("resume");
    }

    [Test]
    public void Bar_RejectsAnEmptyAccent()
    {
        Assert.Throws<ArgumentException>(() => FreezeBarRenderer.Bar(string.Empty));
    }
}
