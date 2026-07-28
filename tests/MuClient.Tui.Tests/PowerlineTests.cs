using MuClient.Tui;

namespace MuClient.Tui.Tests;

public class PowerlineTests
{
    [Test]
    public async Task LeftBar_BlendsEachSegmentIntoTheNext()
    {
        var bar = Powerline.LeftBar(
            new[]
            {
                new PowerSegment("A", "#000000", "#ff0000"),
                new PowerSegment("B", "#000000", "#00ff00"),
            },
            tailBg: "#111111");

        // Segment A on red, then a separator red→green, then B on green, then a separator green→tail.
        await Assert.That(bar).Contains("[#000000 on #ff0000] A [/]");
        await Assert.That(bar).Contains($"[#ff0000 on #00ff00]{Glyphs.PowerRight}[/]");
        await Assert.That(bar).Contains("[#000000 on #00ff00] B [/]");
        await Assert.That(bar).Contains($"[#00ff00 on #111111]{Glyphs.PowerRight}[/]");
    }

    [Test]
    public async Task RightBar_PrecedesEachSegmentWithABlendingSeparator()
    {
        var bar = Powerline.RightBar(
            new[]
            {
                new PowerSegment("X", "#ffffff", "#222222"),
                new PowerSegment("Y", "#ffffff", "#333333"),
            },
            headBg: "#000000");

        // First a left separator head→X.bg, then X; then a separator X.bg→Y.bg, then Y.
        await Assert.That(bar).StartsWith($"[#222222 on #000000]{Glyphs.PowerLeft}[/]");
        await Assert.That(bar).Contains($"[#333333 on #222222]{Glyphs.PowerLeft}[/]");
        await Assert.That(bar).Contains("[#ffffff on #333333] Y [/]");
    }

    [Test]
    public async Task Segments_EscapeMarkupBrackets()
    {
        var bar = Powerline.LeftBar(new[] { new PowerSegment("[x]", "#fff", "#000") }, "#111");
        await Assert.That(bar).Contains(" [[x]] ");
    }
}
