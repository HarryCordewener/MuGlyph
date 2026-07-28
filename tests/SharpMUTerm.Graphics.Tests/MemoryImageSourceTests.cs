using SharpMUTerm.Graphics;

namespace SharpMUTerm.Graphics.Tests;

public class MemoryImageSourceTests
{
    private static MemoryImageSource Make2x2() => new(2, 2, new[]
    {
        new Rgba32(1, 0, 0), new Rgba32(2, 0, 0),
        new Rgba32(3, 0, 0), new Rgba32(4, 0, 0),
    });

    [Test]
    public async Task GetPixel_ReturnsRowMajorPixel()
    {
        var image = Make2x2();
        await Assert.That(image.GetPixel(0, 0)).IsEqualTo(new Rgba32(1, 0, 0));
        await Assert.That(image.GetPixel(1, 0)).IsEqualTo(new Rgba32(2, 0, 0));
        await Assert.That(image.GetPixel(0, 1)).IsEqualTo(new Rgba32(3, 0, 0));
        await Assert.That(image.GetPixel(1, 1)).IsEqualTo(new Rgba32(4, 0, 0));
    }

    [Test]
    public async Task Constructor_MismatchedLength_Throws()
    {
        await Assert.That(() => new MemoryImageSource(2, 2, new[] { new Rgba32(0, 0, 0) }))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments(0, 4)]
    [Arguments(-1, 4)]
    public async Task Constructor_NonPositiveWidth_Throws(int width, int height)
    {
        await Assert.That(() => new MemoryImageSource(width, height, new Rgba32[Math.Max(0, width * height)]))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task GetPixel_OutOfBoundsX_Throws()
    {
        var image = Make2x2();
        await Assert.That(() => image.GetPixel(2, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => image.GetPixel(-1, 0)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task GetPixel_OutOfBoundsY_Throws()
    {
        var image = Make2x2();
        await Assert.That(() => image.GetPixel(0, 2)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => image.GetPixel(0, -1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Rgba32_Equality_Works()
    {
        await Assert.That(new Rgba32(1, 2, 3)).IsEqualTo(new Rgba32(1, 2, 3, 255));
        await Assert.That(new Rgba32(1, 2, 3, 4) == new Rgba32(1, 2, 3, 4)).IsTrue();
        await Assert.That(new Rgba32(1, 2, 3, 4) != new Rgba32(1, 2, 3, 5)).IsTrue();
    }
}
