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
}
