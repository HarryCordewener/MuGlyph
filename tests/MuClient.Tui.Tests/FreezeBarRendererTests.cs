using MuClient.Tui;

namespace MuClient.Tui.Tests;

public class FreezeBarRendererTests
{
    [Test]
    public async Task Bar_CarriesFrozenLabelInTheAccentAndADimHint()
    {
        var bar = FreezeBarRenderer.Bar("#c678dd");

        // A single line: accented "❄ FROZEN ⌃F" label, then a dim rule serving as the border.
        await Assert.That(bar).Contains($"[#c678dd]{Glyphs.Freeze} FROZEN ⌃F[/]");
        await Assert.That(bar).Contains("[dim]");
        await Assert.That(bar).Contains("─");
    }

    [Test]
    public void Bar_RejectsAnEmptyAccent()
    {
        Assert.Throws<ArgumentException>(() => FreezeBarRenderer.Bar(string.Empty));
    }
}
