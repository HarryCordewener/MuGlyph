using MuClient.Core.Text;
using MuClient.Web;

namespace MuClient.Web.Tests;

public class HtmlStyledRendererTests
{
    private static IReadOnlyList<StyledLine> Render(string html, int width = 80) =>
        new HtmlStyledRenderer().Render(html, width);

    private static string AllText(IReadOnlyList<StyledLine> lines) =>
        string.Join("\n", lines.Select(l => l.Text));

    [Test]
    public async Task PlainParagraph_RendersText()
    {
        var lines = Render("<p>Hello world</p>");
        await Assert.That(AllText(lines)).Contains("Hello world");
    }

    [Test]
    public async Task Bold_SetsBoldAttribute()
    {
        var lines = Render("<p>a <b>strong</b> word</p>");
        var strong = lines.SelectMany(l => l.Spans).First(s => s.Text.Contains("strong"));
        await Assert.That(strong.Style.HasAttribute(TextAttributes.Bold)).IsTrue();
    }

    [Test]
    public async Task Anchor_BecomesHyperlinkInteraction()
    {
        var lines = Render("<a href=\"https://example.org\">site</a>");
        var span = lines.SelectMany(l => l.Spans).First(s => s.Text.Contains("site"));
        await Assert.That(span.IsInteractive).IsTrue();
        await Assert.That(span.Interaction!.Kind).IsEqualTo(InteractionKind.Hyperlink);
        await Assert.That(span.Interaction!.Target).IsEqualTo("https://example.org");
    }

    [Test]
    public async Task RelativeAnchor_IsResolvedAgainstBaseUrl()
    {
        var renderer = new HtmlStyledRenderer("https://example.org/dir/page.html");
        var lines = renderer.Render("<a href=\"other.html\">next</a>");
        var span = lines.SelectMany(l => l.Spans).First(s => s.Text.Contains("next"));
        await Assert.That(span.Interaction!.Target).IsEqualTo("https://example.org/dir/other.html");
    }

    [Test]
    public async Task ScriptAndStyle_AreStripped()
    {
        var lines = Render("<p>visible</p><script>var x=1;</script><style>.a{}</style>");
        var text = AllText(lines);
        await Assert.That(text).Contains("visible");
        await Assert.That(text).DoesNotContain("var x");
        await Assert.That(text).DoesNotContain(".a{}");
    }

    [Test]
    public async Task Heading_IsBoldAndSeparated()
    {
        var lines = Render("<h1>Title</h1><p>body</p>");
        var title = lines.SelectMany(l => l.Spans).First(s => s.Text.Contains("Title"));
        await Assert.That(title.Style.HasAttribute(TextAttributes.Bold)).IsTrue();
    }

    [Test]
    public async Task ListItems_GetBullets()
    {
        var lines = Render("<ul><li>one</li><li>two</li></ul>");
        var text = AllText(lines);
        await Assert.That(text).Contains("• one");
        await Assert.That(text).Contains("• two");
    }

    [Test]
    public async Task Break_ProducesNewLine()
    {
        var lines = Render("first<br>second");
        await Assert.That(lines.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(lines.Any(l => l.Text == "first")).IsTrue();
        await Assert.That(lines.Any(l => l.Text == "second")).IsTrue();
    }

    [Test]
    public async Task WhitespaceIsCollapsed_AcrossInlineElements()
    {
        var lines = Render("<p>a   <b>b</b>   c</p>");
        await Assert.That(AllText(lines)).Contains("a b c");
    }

    [Test]
    public async Task Wrapping_RespectsWidth()
    {
        var lines = Render("<p>" + string.Join(' ', Enumerable.Repeat("word", 40)) + "</p>", width: 20);
        await Assert.That(lines.All(l => l.Text.Length <= 20)).IsTrue();
        await Assert.That(lines.Count).IsGreaterThan(1);
    }

    [Test]
    public async Task Image_BecomesLabelledLink()
    {
        var lines = Render("<img src=\"pic.png\" alt=\"a cat\">");
        var span = lines.SelectMany(l => l.Spans).First(s => s.Text.Contains("image"));
        await Assert.That(span.Text).Contains("a cat");
        await Assert.That(span.IsInteractive).IsTrue();
        await Assert.That(span.Interaction!.Kind).IsEqualTo(InteractionKind.Hyperlink);
        await Assert.That(span.Interaction!.Target).IsEqualTo("pic.png");
    }

    [Test]
    public async Task ManySameStyleSegments_CoalesceCorrectly()
    {
        // Exercises the StringBuilder coalescing path with many adjacent same-style inline nodes.
        var html = "<p>" + string.Concat(Enumerable.Repeat("<i>a</i>", 500)) + "</p>";
        var lines = Render(html, width: 10_000);
        var text = string.Concat(lines.Select(l => l.Text));
        await Assert.That(text).IsEqualTo(new string('a', 500));
    }

    [Test]
    public async Task Preformatted_PreservesLineBreaks()
    {
        var lines = Render("<pre>line1\nline2</pre>");
        await Assert.That(lines.Any(l => l.Text == "line1")).IsTrue();
        await Assert.That(lines.Any(l => l.Text == "line2")).IsTrue();
    }

    [Test]
    public async Task GetTitle_ReturnsDocumentTitle()
    {
        await Assert.That(HtmlStyledRenderer.GetTitle("<html><head><title>My Page</title></head></html>"))
            .IsEqualTo("My Page");
    }

    [Test]
    public async Task FontColor_IsApplied()
    {
        var lines = Render("<font color=\"red\">danger</font>");
        var span = lines.SelectMany(l => l.Spans).First(s => s.Text.Contains("danger"));
        await Assert.That(span.Style.Foreground).IsEqualTo(TerminalColor.FromRgb(0xff, 0, 0));
    }
}
