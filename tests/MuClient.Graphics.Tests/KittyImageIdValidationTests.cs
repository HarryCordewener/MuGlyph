using MuClient.Graphics;

namespace MuClient.Graphics.Tests;

public class KittyImageIdValidationTests
{
    private readonly KittyGraphicsProtocol _kitty = new();

    [Test]
    public async Task BuildPlaceholder_RejectsImageIdAbove24Bits()
    {
        // 0x1000000 cannot round-trip through the 24-bit foreground-colour carrier.
        await Assert.That(() => _kitty.BuildPlaceholder(0x1000000, 2, 2))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BuildPlaceholder_RejectsNegativeImageId()
    {
        await Assert.That(() => _kitty.BuildPlaceholder(-1, 2, 2))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BuildPlaceholder_AcceptsMaxValidImageId()
    {
        var lines = _kitty.BuildPlaceholder(0xFFFFFF, 2, 2);
        await Assert.That(lines).Count().IsEqualTo(2);
    }
}
