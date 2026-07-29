using SharpMUTerm.Core.Text;
using SharpMUTerm.Web;

namespace SharpMUTerm.Web.Tests;

/// <summary>
/// The image index is what lets a graphics-capable view swap a placeholder line for a real picture,
/// so the indices have to point at exactly the right lines — including after wrapping, blank-line
/// collapsing, and the trailing-blank trim <see cref="HtmlStyledRenderer"/> does on the way out.
/// </summary>
public class WebImageIndexTests
{
    private static HtmlRenderResult Render(string html, int width = 80) =>
        new HtmlStyledRenderer().RenderDocument(html, width);

    [Test]
    public async Task Image_IsIndexedWithItsResolvedSourceAndAlt()
    {
        var result = new HtmlStyledRenderer("https://example.com/dir/page.html")
            .RenderDocument("<p>before</p><img src=\"pic.png\" alt=\"a cat\"><p>after</p>");

        await Assert.That(result.Images.Count).IsEqualTo(1);
        var image = result.Images[0];
        await Assert.That(image.Source).IsEqualTo("https://example.com/dir/pic.png");
        await Assert.That(image.Alt).IsEqualTo("a cat");
        await Assert.That(image.PlaceholderText).IsEqualTo("[image: a cat]");
    }

    [Test]
    public async Task IndexedLine_IsTheOneHoldingThePlaceholder()
    {
        var result = Render("<p>before</p><img src=\"pic.png\" alt=\"a cat\"><p>after</p>");
        var image = result.Images[0];

        await Assert.That(image.LineIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(image.LineIndex).IsLessThan(result.Lines.Count);
        await Assert.That(result.Lines[image.LineIndex].Text).IsEqualTo("[image: a cat]");
    }

    [Test]
    public async Task PlaceholderLine_HoldsNothingButTheImage()
    {
        // Prose must not share the row: the view replaces the whole line with the picture.
        var result = Render("<p>text before <img src=\"pic.png\" alt=\"cat\"> text after</p>");
        var image = result.Images[0];

        await Assert.That(result.Lines[image.LineIndex].Text).IsEqualTo("[image: cat]");
        await Assert.That(string.Join("\n", result.Lines.Select(l => l.Text))).Contains("text before");
        await Assert.That(string.Join("\n", result.Lines.Select(l => l.Text))).Contains("text after");
    }

    [Test]
    public async Task MultipleImages_AreIndexedInDocumentOrderAtDistinctLines()
    {
        var result = Render(
            "<p>one</p><img src=\"a.png\" alt=\"A\"><p>two</p><img src=\"b.png\" alt=\"B\"><p>three</p>");

        await Assert.That(result.Images.Count).IsEqualTo(2);
        await Assert.That(result.Images[0].Source).IsEqualTo("a.png");
        await Assert.That(result.Images[1].Source).IsEqualTo("b.png");
        await Assert.That(result.Images[0].LineIndex).IsLessThan(result.Images[1].LineIndex);
        await Assert.That(result.Lines[result.Images[0].LineIndex].Text).IsEqualTo("[image: A]");
        await Assert.That(result.Lines[result.Images[1].LineIndex].Text).IsEqualTo("[image: B]");
    }

    [Test]
    public async Task IndicesSurviveWrappedProseAbove()
    {
        var prose = string.Join(' ', Enumerable.Repeat("word", 60));
        var result = Render($"<p>{prose}</p><img src=\"pic.png\" alt=\"cat\">", width: 20);

        await Assert.That(result.Lines.Count).IsGreaterThan(4);
        await Assert.That(result.Lines[result.Images[0].LineIndex].Text).IsEqualTo("[image: cat]");
    }

    [Test]
    public async Task ImageAtEndOfDocument_SurvivesTheTrailingBlankTrim()
    {
        var result = Render("<p>text</p><img src=\"pic.png\" alt=\"cat\">");
        var image = result.Images[0];

        await Assert.That(image.LineIndex).IsEqualTo(result.Lines.Count - 1);
        await Assert.That(result.Lines[image.LineIndex].Text).IsEqualTo("[image: cat]");
    }

    [Test]
    public async Task ImageAtStartOfDocument_IsIndexedAtLineZero()
    {
        var result = Render("<img src=\"pic.png\" alt=\"cat\"><p>text</p>");
        await Assert.That(result.Images[0].LineIndex).IsEqualTo(0);
        await Assert.That(result.Lines[0].Text).IsEqualTo("[image: cat]");
    }

    [Test]
    public async Task ImageWithoutSource_StillShowsAPlaceholderButIsNotIndexed()
    {
        var result = Render("<img alt=\"broken\">");
        await Assert.That(result.Images).IsEmpty();
        await Assert.That(string.Join("\n", result.Lines.Select(l => l.Text))).Contains("[image: broken]");
    }

    [Test]
    public async Task ImageWithoutAlt_UsesTheBarePlaceholderAndNullAlt()
    {
        var result = Render("<img src=\"pic.png\">");
        await Assert.That(result.Images[0].Alt).IsNull();
        await Assert.That(result.Images[0].PlaceholderText).IsEqualTo("[image]");
        await Assert.That(result.Lines[result.Images[0].LineIndex].Text).IsEqualTo("[image]");
    }

    [Test]
    public async Task PlaceholderKeepsItsHyperlink_SoNonGraphicalViewsCanStillOpenTheImage()
    {
        var result = Render("<img src=\"pic.png\" alt=\"cat\">");
        var span = result.Lines[result.Images[0].LineIndex].Spans.First();

        await Assert.That(span.IsInteractive).IsTrue();
        await Assert.That(span.Interaction!.Kind).IsEqualTo(InteractionKind.Hyperlink);
        await Assert.That(span.Interaction!.Target).IsEqualTo("pic.png");
    }

    [Test]
    public async Task RenderDocument_IsRepeatableOnTheSameRendererInstance()
    {
        var renderer = new HtmlStyledRenderer();
        renderer.RenderDocument("<img src=\"a.png\">");
        var second = renderer.RenderDocument("<img src=\"b.png\">");

        // The image list is per-render, not cumulative.
        await Assert.That(second.Images.Count).IsEqualTo(1);
        await Assert.That(second.Images[0].Source).IsEqualTo("b.png");
    }

    [Test]
    public async Task PageWithoutImages_HasAnEmptyIndex()
    {
        await Assert.That(Render("<p>just words</p>").Images).IsEmpty();
    }

    [Test]
    public async Task WebPage_DefaultsToNoImages()
    {
        var page = new WebPage("http://x", "t", Array.Empty<StyledLine>());
        await Assert.That(page.Images).IsEmpty();
    }
}
