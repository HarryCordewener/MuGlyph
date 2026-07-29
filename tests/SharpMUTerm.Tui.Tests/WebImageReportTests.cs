using SharpMUTerm.Core.Text;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;
using SharpMUTerm.Web;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What <c>/graphics</c> says about the page in the web view. This is the diagnostic a user reaches
/// for when pictures "don't load correctly", so each of the failures that phrase can hide has to come
/// out as a distinguishable line: nothing opened, no images on the page, images never fetched because
/// the view draws placeholders, images fetched and drawn, images fetched and rejected.
/// </summary>
public class WebImageReportTests
{
    private static WebPage Page(params string[] sources) =>
        new(
            "https://example.com/room",
            "Room",
            Array.Empty<StyledLine>(),
            sources.Select((s, i) => new WebImage(i, s, null, "[image]")).ToArray());

    private static Dictionary<int, WebImageReport.Decoded> Decoded(params (int Index, int Width, int Height)[] items) =>
        items.ToDictionary(i => i.Index, i => new WebImageReport.Decoded(i.Width, i.Height));

    private static Dictionary<int, WebImageReport.Decoded> None() => new();

    [Test]
    public async Task NoPageOpen_SaysSo()
    {
        var lines = WebImageReport.Describe(null, None(), InlineImagePresentation.Kitty);
        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("/web");
    }

    [Test]
    public async Task PageWithoutImages_SaysSoAndNamesTheUrl()
    {
        var lines = WebImageReport.Describe(Page(), None(), InlineImagePresentation.Kitty);
        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("no <img>");
        await Assert.That(lines[0]).Contains("https://example.com/room");
    }

    [Test]
    public async Task TextPlaceholderView_SaysNothingWasFetchedAtAll()
    {
        // The no-graphics host never fetches, so per-image detail would be noise: one honest line.
        var lines = WebImageReport.Describe(
            Page("a.png", "b.png"), None(), InlineImagePresentation.TextPlaceholder);

        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("none fetched");
        await Assert.That(lines[0]).Contains("2 image(s)");
    }

    [Test]
    public async Task DrawnImage_ReportsItsCellBoxNextToThePixelBufferBehindIt()
    {
        // The pairing is the point: 53 columns drawn from a buffer only 53 pixels wide says the Kitty
        // path is upscaling a thumbnail, which no amount of staring at the terminal would reveal.
        var lines = WebImageReport.Describe(
            Page("map.png"), Decoded((0, 53, 40)), InlineImagePresentation.Kitty);

        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0]).Contains("1 drawn");
        await Assert.That(lines[1]).Contains("53x20 cells");
        await Assert.That(lines[1]).Contains("53x40 px");
        await Assert.That(lines[1]).Contains("map.png");
    }

    [Test]
    public async Task ImageThatDidNotDecode_IsNamedAsAPlaceholder()
    {
        var lines = WebImageReport.Describe(
            Page("good.png", "bad.svg"), Decoded((0, 32, 32)), InlineImagePresentation.HalfBlock);

        await Assert.That(lines[0]).Contains("1 drawn");
        await Assert.That(lines[0]).Contains("1 kept a text placeholder");
        await Assert.That(lines[2]).Contains("placeholder");
        await Assert.That(lines[2]).Contains("bad.svg");
    }

    [Test]
    public async Task EveryImageGetsExactlyOneLine()
    {
        var lines = WebImageReport.Describe(
            Page("a.png", "b.png", "c.png"),
            Decoded((0, 16, 16), (2, 16, 16)),
            InlineImagePresentation.HalfBlock);

        await Assert.That(lines.Count).IsEqualTo(4); // summary + one per image
    }

    [Test]
    public async Task Describe_RejectsANullDecodeMap()
    {
        await Assert.That(() => WebImageReport.Describe(Page(), null!, InlineImagePresentation.Kitty))
            .Throws<ArgumentNullException>();
    }
}
