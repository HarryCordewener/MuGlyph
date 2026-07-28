using SharpMUTerm.Tui;
using SharpMUTerm.Web;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The web view's block split. The degraded path — nothing decoded, so the page is one markup
/// control exactly as before — is the one that has to be exactly right, because it is the path every
/// terminal without graphics takes, including this test host.
/// </summary>
public class WebViewComposerTests
{
    private static readonly WebImageLayout.CellBox Box = new(20, 6);

    private static IReadOnlyList<string> Lines(params string[] lines) => lines;

    private static WebImage Image(int line, string src = "pic.png") =>
        new(line, src, "alt", "[image: alt]");

    private static Dictionary<int, WebImageLayout.CellBox> Boxes(params int[] indexes) =>
        indexes.ToDictionary(i => i, _ => Box);

    // ---- Degraded: no decoded images ---------------------------------------------------------

    [Test]
    public async Task NoImagesAtAll_YieldsTheWholePageAsOneTextBlock()
    {
        var blocks = WebViewComposer.Compose(
            Lines("a", "b", "c"), Array.Empty<WebImage>(), new Dictionary<int, WebImageLayout.CellBox>());

        await Assert.That(blocks.Count).IsEqualTo(1);
        await Assert.That(((WebTextBlock)blocks[0]).Lines).IsEquivalentTo(new[] { "a", "b", "c" });
    }

    [Test]
    public async Task ImagesPresentButNoneDecoded_LeavesEveryPlaceholderInPlace()
    {
        // The no-graphics case: placeholders stay, and the page is still a single control.
        var blocks = WebViewComposer.Compose(
            Lines("a", "[image: alt]", "b"),
            new[] { Image(1) },
            new Dictionary<int, WebImageLayout.CellBox>());

        await Assert.That(blocks.Count).IsEqualTo(1);
        await Assert.That(((WebTextBlock)blocks[0]).Lines).IsEquivalentTo(new[] { "a", "[image: alt]", "b" });
    }

    [Test]
    public async Task EmptyPage_YieldsOneEmptyTextBlock()
    {
        var blocks = WebViewComposer.Compose(
            Array.Empty<string>(), Array.Empty<WebImage>(), new Dictionary<int, WebImageLayout.CellBox>());

        await Assert.That(blocks.Count).IsEqualTo(1);
        await Assert.That(((WebTextBlock)blocks[0]).Lines).IsEmpty();
    }

    // ---- Splitting around decoded images -----------------------------------------------------

    [Test]
    public async Task DecodedImage_SplitsThePageAndConsumesThePlaceholderLine()
    {
        var blocks = WebViewComposer.Compose(
            Lines("a", "[image: alt]", "b"), new[] { Image(1) }, Boxes(0));

        await Assert.That(blocks.Count).IsEqualTo(3);
        await Assert.That(((WebTextBlock)blocks[0]).Lines).IsEquivalentTo(new[] { "a" });
        await Assert.That(((WebImageBlock)blocks[1]).Index).IsEqualTo(0);
        await Assert.That(((WebImageBlock)blocks[1]).Box).IsEqualTo(Box);
        await Assert.That(((WebTextBlock)blocks[2]).Lines).IsEquivalentTo(new[] { "b" });
    }

    [Test]
    public async Task PlaceholderTextNeverSurvivesAlongsideItsPicture()
    {
        var blocks = WebViewComposer.Compose(
            Lines("a", "[image: alt]", "b"), new[] { Image(1) }, Boxes(0));

        var text = blocks.OfType<WebTextBlock>().SelectMany(b => b.Lines);
        await Assert.That(text).DoesNotContain("[image: alt]");
    }

    [Test]
    public async Task ImageAtTheTop_EmitsNoLeadingTextBlock()
    {
        var blocks = WebViewComposer.Compose(Lines("[image: alt]", "b"), new[] { Image(0) }, Boxes(0));

        await Assert.That(blocks.Count).IsEqualTo(2);
        await Assert.That(blocks[0]).IsTypeOf<WebImageBlock>();
        await Assert.That(((WebTextBlock)blocks[1]).Lines).IsEquivalentTo(new[] { "b" });
    }

    [Test]
    public async Task ImageAtTheBottom_EmitsNoTrailingTextBlock()
    {
        var blocks = WebViewComposer.Compose(Lines("a", "[image: alt]"), new[] { Image(1) }, Boxes(0));

        await Assert.That(blocks.Count).IsEqualTo(2);
        await Assert.That(((WebTextBlock)blocks[0]).Lines).IsEquivalentTo(new[] { "a" });
        await Assert.That(blocks[1]).IsTypeOf<WebImageBlock>();
    }

    [Test]
    public async Task PageThatIsNothingButAnImage_YieldsJustTheImageBlock()
    {
        var blocks = WebViewComposer.Compose(Lines("[image: alt]"), new[] { Image(0) }, Boxes(0));

        await Assert.That(blocks.Count).IsEqualTo(1);
        await Assert.That(blocks[0]).IsTypeOf<WebImageBlock>();
    }

    [Test]
    public async Task AdjacentImages_ProduceNoEmptyTextBlockBetweenThem()
    {
        var blocks = WebViewComposer.Compose(
            Lines("[image: alt]", "[image: alt]"),
            new[] { Image(0, "a.png"), Image(1, "b.png") },
            Boxes(0, 1));

        await Assert.That(blocks.Count).IsEqualTo(2);
        await Assert.That(blocks.All(b => b is WebImageBlock)).IsTrue();
    }

    [Test]
    public async Task MixedDecodeResults_DrawOnlyTheOnesThatDecoded()
    {
        // Image 0 decoded; image 1 did not, so its placeholder rides along in the trailing text.
        var blocks = WebViewComposer.Compose(
            Lines("a", "[image: alt]", "b", "[image: alt]", "c"),
            new[] { Image(1, "a.png"), Image(3, "b.png") },
            Boxes(0));

        await Assert.That(blocks.Count).IsEqualTo(3);
        await Assert.That(((WebImageBlock)blocks[1]).Image.Source).IsEqualTo("a.png");
        await Assert.That(((WebTextBlock)blocks[2]).Lines).IsEquivalentTo(new[] { "b", "[image: alt]", "c" });
    }

    [Test]
    public async Task BlocksCoverEveryLineExactlyOnce()
    {
        var lines = Lines("0", "[image: alt]", "2", "3", "[image: alt]", "5");
        var blocks = WebViewComposer.Compose(
            lines, new[] { Image(1, "a.png"), Image(4, "b.png") }, Boxes(0, 1));

        var text = blocks.OfType<WebTextBlock>().SelectMany(b => b.Lines).ToList();
        await Assert.That(text).IsEquivalentTo(new[] { "0", "2", "3", "5" });
        await Assert.That(blocks.OfType<WebImageBlock>().Count()).IsEqualTo(2);
    }

    // ---- Defensive cases ----------------------------------------------------------------------

    [Test]
    public async Task ImageIndexPastTheEndOfThePage_IsIgnored()
    {
        // A stale index must never crash or slice out of range.
        var blocks = WebViewComposer.Compose(Lines("a", "b"), new[] { Image(99) }, Boxes(0));

        await Assert.That(blocks.Count).IsEqualTo(1);
        await Assert.That(((WebTextBlock)blocks[0]).Lines).IsEquivalentTo(new[] { "a", "b" });
    }

    [Test]
    public async Task NegativeImageIndex_IsIgnored()
    {
        var blocks = WebViewComposer.Compose(Lines("a"), new[] { Image(-1) }, Boxes(0));

        await Assert.That(blocks.Count).IsEqualTo(1);
        await Assert.That(((WebTextBlock)blocks[0]).Lines).IsEquivalentTo(new[] { "a" });
    }

    [Test]
    public async Task TwoImagesClaimingOneLine_OnlyTheFirstWins()
    {
        var blocks = WebViewComposer.Compose(
            Lines("a", "[image: alt]", "b"),
            new[] { Image(1, "a.png"), Image(1, "b.png") },
            Boxes(0, 1));

        await Assert.That(blocks.OfType<WebImageBlock>().Count()).IsEqualTo(1);
        await Assert.That(blocks.OfType<WebImageBlock>().Single().Image.Source).IsEqualTo("a.png");
    }

    [Test]
    public async Task OutOfOrderImageIndices_AreStillPlacedInLineOrder()
    {
        var blocks = WebViewComposer.Compose(
            Lines("[image: alt]", "x", "[image: alt]"),
            new[] { Image(2, "second.png"), Image(0, "first.png") },
            Boxes(0, 1));

        var images = blocks.OfType<WebImageBlock>().ToList();
        await Assert.That(images[0].Image.Source).IsEqualTo("first.png");
        await Assert.That(images[1].Image.Source).IsEqualTo("second.png");
    }

    [Test]
    public async Task Compose_RejectsNullArguments()
    {
        var empty = new Dictionary<int, WebImageLayout.CellBox>();
        await Assert.That(() => WebViewComposer.Compose(null!, Array.Empty<WebImage>(), empty))
            .Throws<ArgumentNullException>();
        await Assert.That(() => WebViewComposer.Compose(Array.Empty<string>(), null!, empty))
            .Throws<ArgumentNullException>();
        await Assert.That(() => WebViewComposer.Compose(Array.Empty<string>(), Array.Empty<WebImage>(), null!))
            .Throws<ArgumentNullException>();
    }
}
