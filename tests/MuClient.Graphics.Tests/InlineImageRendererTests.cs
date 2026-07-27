using MuClient.Graphics;

namespace MuClient.Graphics.Tests;

public class InlineImageRendererTests
{
    private readonly InlineImageRenderer _renderer = new();

    private static MemoryImageSource TinyImage() =>
        new(1, 1, new[] { new Rgba32(255, 255, 255) });

    private static TerminalCapabilities Caps(GraphicsProtocol protocol) =>
        new(protocol, supportsTrueColor: true, supportsKittyGraphics: true, supportsSixel: true);

    [Test]
    public async Task Render_Kitty_ReturnsEscapeSequence()
    {
        var output = _renderer.Render(TinyImage(), Caps(GraphicsProtocol.Kitty));
        await Assert.That(output.Kind).IsEqualTo(InlineImageKind.Kitty);
        await Assert.That(output.EscapeSequence).IsNotNull();
        await Assert.That(output.EscapeSequence!).Contains("a=T");
    }

    [Test]
    public async Task Render_Sixel_ReturnsEscapeSequence()
    {
        var output = _renderer.Render(TinyImage(), Caps(GraphicsProtocol.Sixel));
        await Assert.That(output.Kind).IsEqualTo(InlineImageKind.Sixel);
        await Assert.That(output.EscapeSequence!).Contains("Pq");
    }

    [Test]
    public async Task Render_HalfBlock_ReturnsLines()
    {
        var output = _renderer.Render(TinyImage(), Caps(GraphicsProtocol.HalfBlock));
        await Assert.That(output.Kind).IsEqualTo(InlineImageKind.HalfBlock);
        await Assert.That(output.Lines).IsNotNull();
        await Assert.That(output.Lines!).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Render_None_ReturnsTextPlaceholder()
    {
        var output = _renderer.Render(TinyImage(), Caps(GraphicsProtocol.None));
        await Assert.That(output.Kind).IsEqualTo(InlineImageKind.Text);
        await Assert.That(output.PlaceholderText!).Contains("1x1");
    }

    [Test]
    public async Task ToRgbaBytes_ProducesRowMajorRgba()
    {
        var image = new MemoryImageSource(1, 2, new[]
        {
            new Rgba32(1, 2, 3, 4),
            new Rgba32(5, 6, 7, 8),
        });

        var bytes = InlineImageRenderer.ToRgbaBytes(image);
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
    }
}
