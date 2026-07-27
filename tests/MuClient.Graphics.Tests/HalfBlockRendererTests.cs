using MuClient.Core.Text;
using MuClient.Graphics;

namespace MuClient.Graphics.Tests;

public class HalfBlockRendererTests
{
    private readonly HalfBlockRenderer _renderer = new();

    [Test]
    public async Task Render_2x2Solid_ProducesOneRowOfBlocks()
    {
        var red = new Rgba32(255, 0, 0);
        var image = new MemoryImageSource(2, 2, new[] { red, red, red, red });

        var lines = _renderer.Render(image, 10, 10);

        // 2 pixel rows -> 1 character row, 2 columns.
        await Assert.That(lines).Count().IsEqualTo(1);
        await Assert.That(lines[0].Spans).Count().IsEqualTo(2);

        var span = lines[0].Spans[0];
        await Assert.That(span.Text).IsEqualTo(HalfBlockRenderer.UpperHalfBlock);
        await Assert.That(span.Style.Foreground).IsEqualTo(TerminalColor.FromRgb(255, 0, 0));
        await Assert.That(span.Style.Background).IsEqualTo(TerminalColor.FromRgb(255, 0, 0));
    }

    [Test]
    public async Task Render_TopAndBottomPixelsMapToFgAndBg()
    {
        // 1 column, 2 rows: top red, bottom blue.
        var image = new MemoryImageSource(1, 2, new[]
        {
            new Rgba32(255, 0, 0),
            new Rgba32(0, 0, 255),
        });

        var lines = _renderer.Render(image, 10, 10);
        var span = lines[0].Spans[0];

        await Assert.That(span.Style.Foreground).IsEqualTo(TerminalColor.FromRgb(255, 0, 0));
        await Assert.That(span.Style.Background).IsEqualTo(TerminalColor.FromRgb(0, 0, 255));
    }

    [Test]
    public async Task Render_OddHeight_LastCellBottomIsDefault()
    {
        // 1 column, 3 rows -> 2 character rows; the second row's bottom pixel doesn't exist.
        var image = new MemoryImageSource(1, 3, new[]
        {
            new Rgba32(10, 0, 0),
            new Rgba32(20, 0, 0),
            new Rgba32(30, 0, 0),
        });

        var lines = _renderer.Render(image, 10, 10);
        await Assert.That(lines).Count().IsEqualTo(2);

        await Assert.That(lines[0].Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromRgb(10, 0, 0));
        await Assert.That(lines[0].Spans[0].Style.Background).IsEqualTo(TerminalColor.FromRgb(20, 0, 0));

        await Assert.That(lines[1].Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromRgb(30, 0, 0));
        await Assert.That(lines[1].Spans[0].Style.Background).IsEqualTo(TerminalColor.Default);
    }

    [Test]
    public async Task Render_Downscales_WithinBudget()
    {
        // 100x100 image constrained to 10 cols / 5 char rows (= 10 pixel rows).
        var pixels = new Rgba32[100 * 100];
        Array.Fill(pixels, new Rgba32(1, 2, 3));
        var image = new MemoryImageSource(100, 100, pixels);

        var lines = _renderer.Render(image, 10, 5);

        await Assert.That(lines.Count <= 5).IsTrue();
        foreach (var line in lines)
        {
            await Assert.That(line.Spans.Count <= 10).IsTrue();
        }
    }

    [Test]
    [Arguments(0, 5)]
    [Arguments(5, 0)]
    public async Task Render_NonPositiveBudget_Throws(int cols, int rows)
    {
        var image = new MemoryImageSource(1, 1, new[] { new Rgba32(0, 0, 0) });
        await Assert.That(() => _renderer.Render(image, cols, rows)).Throws<ArgumentOutOfRangeException>();
    }
}
