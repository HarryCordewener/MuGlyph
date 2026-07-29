using System.Text;
using SharpMUTerm.Core.Logging;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Logging;

public class LogSinkTests
{
    private static StyledLine Colored(string text, TerminalColor fg) =>
        new(new[] { new StyledSpan(text, new TextStyle(fg, TerminalColor.Default, TextAttributes.None)) });

    [Test]
    public async Task PlainText_WritesPlainLines()
    {
        var sb = new StringBuilder();
        using (var sink = new PlainTextLogSink(new StringWriter(sb), ownsWriter: false))
        {
            sink.WriteLine(Colored("hello", TerminalColor.FromIndex(1)));
            sink.WriteSystem("*** system");
        }

        var text = sb.ToString();
        await Assert.That(text).Contains("hello");
        await Assert.That(text).Contains("*** system");
        await Assert.That(text).DoesNotContain("\x1b");
    }

    [Test]
    public async Task Html_EmitsDocumentWithColorSpans()
    {
        var sb = new StringBuilder();
        using (var sink = new HtmlLogSink(new StringWriter(sb), ownsWriter: false))
        {
            sink.WriteLine(Colored("red text", TerminalColor.FromIndex(1)));
        }

        var html = sb.ToString();
        await Assert.That(html).Contains("<!DOCTYPE html>");
        await Assert.That(html).Contains("</body></html>");
        await Assert.That(html).Contains("color:#800000;");
        await Assert.That(html).Contains("red text");
    }

    [Test]
    public async Task Html_EscapesMarkup()
    {
        var sb = new StringBuilder();
        using (var sink = new HtmlLogSink(new StringWriter(sb), ownsWriter: false))
        {
            sink.WriteLine(StyledLine.FromText("<script>&", TextStyle.Default));
        }

        var html = sb.ToString();
        await Assert.That(html).Contains("&lt;script&gt;&amp;");
        await Assert.That(html).DoesNotContain("<script>&<");
    }

    [Test]
    public async Task Html_TruecolorRendersHex()
    {
        var sb = new StringBuilder();
        using (var sink = new HtmlLogSink(new StringWriter(sb), ownsWriter: false))
        {
            sink.WriteLine(Colored("x", TerminalColor.FromRgb(0x12, 0x34, 0x56)));
        }

        await Assert.That(sb.ToString()).Contains("color:#123456;");
    }

    /// <summary>
    /// <see cref="LogFormat.Both"/> is why <see cref="CompositeLogSink"/> exists — a session holds one
    /// sink, so "plain and HTML" has to be one sink that is two.
    /// </summary>
    [Test]
    public async Task Composite_WritesThroughToEverySink()
    {
        var plain = new StringBuilder();
        var html = new StringBuilder();
        using (var sink = new CompositeLogSink(new ILogSink[]
        {
            new PlainTextLogSink(new StringWriter(plain), ownsWriter: false),
            new HtmlLogSink(new StringWriter(html), ownsWriter: false),
        }))
        {
            sink.WriteLine(Colored("hello", TerminalColor.FromIndex(1)));
            sink.WriteSystem("*** system");
        }

        await Assert.That(plain.ToString()).Contains("hello");
        await Assert.That(plain.ToString()).Contains("*** system");
        await Assert.That(html.ToString()).Contains("hello");
        await Assert.That(html.ToString()).Contains("</html>");
    }

    /// <summary>
    /// A sink that throws must not stop the ones after it — a full disk on the plain log is no reason
    /// to lose the HTML one — but the failure is still rethrown once the round is done.
    /// </summary>
    [Test]
    public async Task Composite_KeepsWritingPastAThrowingSinkAndStillReportsIt()
    {
        var reached = new StringBuilder();
        var sink = new CompositeLogSink(new ILogSink[]
        {
            new ThrowingLogSink(),
            new PlainTextLogSink(new StringWriter(reached), ownsWriter: false),
        });

        await Assert.That(() => sink.WriteSystem("*** system")).Throws<IOException>();
        await Assert.That(reached.ToString()).Contains("*** system");
    }

    private sealed class ThrowingLogSink : ILogSink
    {
        public void WriteLine(StyledLine line) => throw new IOException("no room");

        public void WriteSystem(string text) => throw new IOException("no room");

        public void Flush() => throw new IOException("no room");

        public void Dispose()
        {
        }
    }
}
