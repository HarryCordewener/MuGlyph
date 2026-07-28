using MuClient.Tui;

namespace MuClient.Tui.Tests;

public class CaptureLineRendererTests
{
    [Test]
    public async Task Line_IsADimCaptureRow()
    {
        var line = CaptureLineRenderer.Line("^chat:");

        await Assert.That(line).IsEqualTo($"[dim]{Glyphs.Capture} capture ^chat:[/]");
    }

    [Test]
    public async Task Line_EscapesMarkupBracketsInThePattern()
    {
        // A regex like ^\[public\] must not be parsed as markup tags.
        var line = CaptureLineRenderer.Line(@"^\[public\]");

        await Assert.That(line).IsEqualTo($@"[dim]{Glyphs.Capture} capture ^\[[public\]][/]");
    }
}
