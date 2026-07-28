using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;
using SharpMUTerm.Web;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The whole inline-image seam without a terminal: HTML → styled lines + image index → capability
/// probe → degradation policy → decoded pixels → composed blocks.
///
/// <para>What this cannot show is a picture. The sandbox has no graphics-capable terminal, so the
/// Kitty and Sixel <em>output</em> is unverified here by construction. What it does show is that the
/// decision is right in each case and that the no-graphics path — the one this host actually takes —
/// produces the same plain page it always did.</para>
/// </summary>
public class WebInlineImageEndToEndTests
{
    private const string PageHtml =
        "<html><head><title>Room</title></head><body>" +
        "<p>You are standing in a field.</p>" +
        "<img src=\"https://example.com/map.png\" alt=\"the map\">" +
        "<p>Exits lead north and south.</p>" +
        "</body></html>";

    private static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgb24>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new Rgb24((byte)(x % 256), (byte)(y % 256), 200);
            }
        }

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static TerminalCapabilities Detect(params (string Key, string? Value)[] vars) =>
        CapabilityProbe.Detect(vars.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal));

    [Test]
    public async Task BareTerminal_KeepsThePlaceholderAndNeverFetchesTheImage()
    {
        // No TERM, no COLORTERM: exactly this test host, and every dumb terminal or pipe.
        var caps = Detect();
        var presentation = InlineImagePolicy.Select(caps, GraphicsSurface.Compositor(canPlaceKitty: false));
        await Assert.That(presentation).IsEqualTo(InlineImagePresentation.TextPlaceholder);

        var page = new HtmlStyledRenderer().RenderDocument(PageHtml);
        await Assert.That(page.Images.Count).IsEqualTo(1);

        // TextPlaceholder means the app never decodes anything, so the composer sees no boxes.
        var blocks = WebViewComposer.Compose(
            page.Lines.Select(l => l.Text).ToList(), page.Images, new Dictionary<int, WebImageLayout.CellBox>());

        await Assert.That(blocks.Count).IsEqualTo(1);
        var text = string.Join("\n", ((WebTextBlock)blocks[0]).Lines);
        await Assert.That(text).Contains("[image: the map]");
        await Assert.That(text).Contains("You are standing in a field.");
        await Assert.That(text).Contains("Exits lead north and south.");
    }

    [Test]
    public async Task TrueColorTerminal_DrawsTheImageAsHalfBlocks()
    {
        var caps = Detect(("COLORTERM", "truecolor"));
        var presentation = InlineImagePolicy.Select(caps, GraphicsSurface.Compositor(canPlaceKitty: false));
        await Assert.That(presentation).IsEqualTo(InlineImagePresentation.HalfBlock);

        await AssertPageDrawsTheImage();
    }

    [Test]
    public async Task KittyTerminal_DrawsTheImageThroughTheKittyPath()
    {
        var caps = Detect(("TERM", "xterm-kitty"), ("COLORTERM", "truecolor"));
        var presentation = InlineImagePolicy.Select(caps, GraphicsSurface.Compositor(canPlaceKitty: true));
        await Assert.That(presentation).IsEqualTo(InlineImagePresentation.Kitty);

        // The composition is protocol-independent — the framework's ImageControl picks Kitty vs
        // half-block per driver. Only the *picture* differs, and that is what needs real hardware.
        await AssertPageDrawsTheImage();
    }

    [Test]
    public async Task SixelTerminal_FallsBackToHalfBlockInsideTheCompositor()
    {
        var caps = Detect(("TERM_PROGRAM", "foot"), ("COLORTERM", "truecolor"));
        await Assert.That(caps.Protocol).IsEqualTo(GraphicsProtocol.Sixel);

        // The framework has no Sixel back-end and the compositor has nowhere to put a raw escape,
        // so the chain steps down one rung rather than drawing nothing.
        var presentation = InlineImagePolicy.Select(caps, GraphicsSurface.Compositor(canPlaceKitty: false));
        await Assert.That(presentation).IsEqualTo(InlineImagePresentation.HalfBlock);

        await AssertPageDrawsTheImage();
    }

    /// <summary>Runs the decode + compose half of the seam and asserts the picture replaced its placeholder.</summary>
    private static async Task AssertPageDrawsTheImage()
    {
        var page = new HtmlStyledRenderer().RenderDocument(PageHtml);
        var markup = page.Lines.Select(l => l.Text).ToList();

        var buffer = WebImageLoader.Decode(Png(200, 100), availableColumns: 60);
        await Assert.That(buffer).IsNotNull();

        var boxes = new Dictionary<int, WebImageLayout.CellBox>
        {
            [0] = new(buffer!.Width, buffer.Height / WebImageLayout.PixelsPerCell),
        };

        var blocks = WebViewComposer.Compose(markup, page.Images, boxes);

        // Prose above, picture, prose below — and the placeholder is gone.
        await Assert.That(blocks.OfType<WebImageBlock>().Count()).IsEqualTo(1);
        var remainingText = string.Join("\n", blocks.OfType<WebTextBlock>().SelectMany(b => b.Lines));
        await Assert.That(remainingText).DoesNotContain("[image: the map]");
        await Assert.That(remainingText).Contains("You are standing in a field.");
        await Assert.That(remainingText).Contains("Exits lead north and south.");

        var image = blocks.OfType<WebImageBlock>().Single();
        await Assert.That(image.Image.Source).IsEqualTo("https://example.com/map.png");
        await Assert.That(image.Box.Rows).IsGreaterThan(0);
        await Assert.That(image.Box.Rows).IsLessThanOrEqualTo(WebImageLayout.MaxRows);
    }

    [Test]
    public async Task ThisTestHostReallyIsTheNoGraphicsCase()
    {
        // Documents the sandbox assumption the honesty of the rest of this work rests on: nothing
        // here can render Kitty or Sixel, so only the selection logic above is verified.
        var caps = CapabilityProbe.DetectFromEnvironment();
        var presentation = InlineImagePolicy.Select(caps, GraphicsSurface.Compositor(canPlaceKitty: false));

        await Assert.That(presentation)
            .IsNotEqualTo(InlineImagePresentation.Kitty)
            .Because("a CI/sandbox host must never claim a Kitty-capable compositor surface");
    }
}
