using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Text;

public class StatusLineTests
{
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
