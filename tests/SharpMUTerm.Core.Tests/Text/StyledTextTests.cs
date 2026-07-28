using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Text;

public class StyledTextTests
{
    [Test]
    public async Task Restyle_RecoloursSubrange_AndSplitsSpans()
    {
        var line = StyledLine.FromText("hello world", TextStyle.Default);
        var restyled = StyledText.Restyle(line, 6, 5, s => s.WithForeground(TerminalColor.FromIndex(2)));

        await Assert.That(restyled.Text).IsEqualTo("hello world");
        var worldSpan = restyled.Spans.First(s => s.Text.Contains("world"));
        await Assert.That(worldSpan.Style.Foreground).IsEqualTo(TerminalColor.FromIndex(2));
        var helloSpan = restyled.Spans.First(s => s.Text.Contains("hello"));
        await Assert.That(helloSpan.Style.Foreground).IsEqualTo(TerminalColor.Default);
    }

    [Test]
    public async Task Restyle_ClampsRangePastEnd()
    {
        var line = StyledLine.FromText("abc", TextStyle.Default);
        var restyled = StyledText.Restyle(line, 1, 100, s => s.AddAttribute(TextAttributes.Bold));
        await Assert.That(restyled.Text).IsEqualTo("abc");
        await Assert.That(restyled.Spans.Last().Style.HasAttribute(TextAttributes.Bold)).IsTrue();
    }

    [Test]
    public async Task Restyle_EmptyRange_ReturnsSameText()
    {
        var line = StyledLine.FromText("abc", TextStyle.Default);
        var restyled = StyledText.Restyle(line, 1, 0, s => s.AddAttribute(TextAttributes.Bold));
        await Assert.That(restyled.Text).IsEqualTo("abc");
    }

    [Test]
    public async Task Coalesce_MergesAdjacentEqualStyles()
    {
        var styles = new[] { TextStyle.Default, TextStyle.Default, TextStyle.Default };
        var line = StyledText.Coalesce("abc", styles);
        await Assert.That(line.Spans).HasSingleItem();
    }
}
