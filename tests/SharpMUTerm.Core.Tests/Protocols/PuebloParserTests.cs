using SharpMUTerm.Core.Protocols;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Protocols;

public class PuebloParserTests
{
    private static StyledLine ParseSingleLine(string input)
    {
        var parser = new PuebloParser();
        var lines = parser.Feed(input);
        if (lines.Count != 1)
        {
            throw new InvalidOperationException($"Expected 1 line, got {lines.Count}.");
        }

        return lines[0];
    }

    [Test]
    public async Task PlainText_ProducesSingleDefaultSpan()
    {
        var line = ParseSingleLine("hello world\n");
        await Assert.That(line.Spans).HasSingleItem();
        await Assert.That(line.Spans[0].Text).IsEqualTo("hello world");
        await Assert.That(line.Spans[0].Style).IsEqualTo(TextStyle.Default);
        await Assert.That(line.Spans[0].IsInteractive).IsFalse();
    }

    [Test]
    public async Task Feed_SplitsOnNewlines()
    {
        var parser = new PuebloParser();
        var lines = parser.Feed("one\ntwo\nthree\n");
        await Assert.That(lines).Count().IsEqualTo(3);
        await Assert.That(lines[0].Text).IsEqualTo("one");
        await Assert.That(lines[1].Text).IsEqualTo("two");
        await Assert.That(lines[2].Text).IsEqualTo("three");
    }

    [Test]
    public async Task Flush_ReturnsPartialLine()
    {
        var parser = new PuebloParser();
        var lines = parser.Feed("prompt> ");
        await Assert.That(lines).Count().IsEqualTo(0);
        await Assert.That(parser.HasPendingContent).IsTrue();

        var flushed = parser.Flush();
        await Assert.That(flushed).IsNotNull();
        await Assert.That(flushed!.Text).IsEqualTo("prompt> ");
        await Assert.That(parser.Flush()).IsNull();
    }

    [Test]
    public async Task Bold_SetsBoldAttribute()
    {
        var line = ParseSingleLine("<B>hi</B>\n");
        await Assert.That(line.Spans).HasSingleItem();
        await Assert.That(line.Spans[0].Text).IsEqualTo("hi");
        await Assert.That(line.Spans[0].Style.HasAttribute(TextAttributes.Bold)).IsTrue();
    }

    [Test]
    public async Task Strong_IsBold_AndClosesAsB()
    {
        // Alias closer: <STRONG> opened, </B> closes it.
        var line = ParseSingleLine("<STRONG>a</B>b\n");
        await Assert.That(line.Spans).Count().IsEqualTo(2);
        await Assert.That(line.Spans[0].Text).IsEqualTo("a");
        await Assert.That(line.Spans[0].Style.HasAttribute(TextAttributes.Bold)).IsTrue();
        await Assert.That(line.Spans[1].Text).IsEqualTo("b");
        await Assert.That(line.Spans[1].Style.HasAttribute(TextAttributes.Bold)).IsFalse();
    }

    [Test]
    public async Task ItalicAndUnderlineAndStrike_SetAttributes()
    {
        var i = ParseSingleLine("<I>x</I>\n");
        await Assert.That(i.Spans[0].Style.HasAttribute(TextAttributes.Italic)).IsTrue();

        var u = ParseSingleLine("<U>x</U>\n");
        await Assert.That(u.Spans[0].Style.HasAttribute(TextAttributes.Underline)).IsTrue();

        var s = ParseSingleLine("<STRIKE>x</STRIKE>\n");
        await Assert.That(s.Spans[0].Style.HasAttribute(TextAttributes.Strikethrough)).IsTrue();
    }

    [Test]
    public async Task NestedFormatting_TogglesCorrectly()
    {
        var line = ParseSingleLine("<B><I>x</I></B>\n");
        await Assert.That(line.Spans).HasSingleItem();
        var style = line.Spans[0].Style;
        await Assert.That(style.HasAttribute(TextAttributes.Bold)).IsTrue();
        await Assert.That(style.HasAttribute(TextAttributes.Italic)).IsTrue();
    }

    [Test]
    public async Task Font_Color_SetsForegroundThenReverts()
    {
        var line = ParseSingleLine("a<FONT COLOR=\"red\">b</FONT>c\n");
        await Assert.That(line.Spans).Count().IsEqualTo(3);
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.Default);
        await Assert.That(line.Spans[1].Text).IsEqualTo("b");
        await Assert.That(line.Spans[1].Style.Foreground).IsEqualTo(TerminalColor.FromRgb(0xff, 0x00, 0x00));
        await Assert.That(line.Spans[2].Style.Foreground).IsEqualTo(TerminalColor.Default);
    }

    [Test]
    public async Task Font_BgColor_HexSetsBackground()
    {
        var line = ParseSingleLine("<FONT BGCOLOR=\"#0000ff\">x</FONT>\n");
        await Assert.That(line.Spans).HasSingleItem();
        await Assert.That(line.Spans[0].Style.Background).IsEqualTo(TerminalColor.FromRgb(0x00, 0x00, 0xff));
    }

    [Test]
    public async Task Font_UnquotedColor_IsAccepted()
    {
        var line = ParseSingleLine("<FONT COLOR=lime>x</FONT>\n");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromRgb(0x00, 0xff, 0x00));
    }

    [Test]
    public async Task Anchor_XchCmd_ProducesSendCommand()
    {
        var line = ParseSingleLine("<A XCH_CMD=\"look\" XCH_HINT=\"look around\">here</A>\n");
        await Assert.That(line.Spans).HasSingleItem();
        var span = line.Spans[0];
        await Assert.That(span.Text).IsEqualTo("here");
        await Assert.That(span.IsInteractive).IsTrue();
        await Assert.That(span.Interaction!.Kind).IsEqualTo(InteractionKind.SendCommand);
        await Assert.That(span.Interaction!.Target).IsEqualTo("look");
        await Assert.That(span.Interaction!.Hint).IsEqualTo("look around");
        await Assert.That(span.Interaction!.PromptOnly).IsFalse();
    }

    [Test]
    public async Task Anchor_XchCmd_RevertsAfterClose()
    {
        var line = ParseSingleLine("<A XCH_CMD=\"look\">here</A> plain\n");
        await Assert.That(line.Spans).Count().IsEqualTo(2);
        await Assert.That(line.Spans[0].IsInteractive).IsTrue();
        await Assert.That(line.Spans[1].Text).IsEqualTo(" plain");
        await Assert.That(line.Spans[1].IsInteractive).IsFalse();
    }

    [Test]
    public async Task Anchor_XchMode_Prompt_SetsPromptOnly()
    {
        var line = ParseSingleLine("<A XCH_CMD=\"say hi\" XCH_MODE=\"prompt\">x</A>\n");
        await Assert.That(line.Spans[0].Interaction!.PromptOnly).IsTrue();
    }

    [Test]
    public async Task Anchor_Href_ProducesHyperlink()
    {
        var line = ParseSingleLine("<A HREF=\"https://example.com\">site</A>\n");
        await Assert.That(line.Spans).HasSingleItem();
        var span = line.Spans[0];
        await Assert.That(span.Text).IsEqualTo("site");
        await Assert.That(span.Interaction!.Kind).IsEqualTo(InteractionKind.Hyperlink);
        await Assert.That(span.Interaction!.Target).IsEqualTo("https://example.com");
    }

    [Test]
    public async Task Send_Href_ProducesSendCommand()
    {
        var line = ParseSingleLine("<SEND HREF=\"north\">go</SEND>\n");
        await Assert.That(line.Spans[0].Interaction!.Kind).IsEqualTo(InteractionKind.SendCommand);
        await Assert.That(line.Spans[0].Interaction!.Target).IsEqualTo("north");
    }

    [Test]
    public async Task Entities_ResolveToCharacters()
    {
        var line = ParseSingleLine("&lt;&gt;&amp;&nbsp;&#65;\n");
        await Assert.That(line.Text).IsEqualTo("<>& A");
    }

    [Test]
    public async Task Entities_QuotAndApos()
    {
        var line = ParseSingleLine("&quot;&apos;\n");
        await Assert.That(line.Text).IsEqualTo("\"'");
    }

    [Test]
    public async Task Entity_HexNumeric_Resolves()
    {
        var line = ParseSingleLine("&#x41;&#x42;\n");
        await Assert.That(line.Text).IsEqualTo("AB");
    }

    [Test]
    public async Task UnknownEntity_IsEmittedLiterally()
    {
        var line = ParseSingleLine("&bogus;\n");
        await Assert.That(line.Text).IsEqualTo("&bogus;");
    }

    [Test]
    public async Task BareAmpersand_IsEmittedLiterally()
    {
        var line = ParseSingleLine("Tom & Jerry\n");
        await Assert.That(line.Text).IsEqualTo("Tom & Jerry");
    }

    [Test]
    public async Task Br_BreaksLineMidText()
    {
        var parser = new PuebloParser();
        var lines = parser.Feed("a<BR>b\n");
        await Assert.That(lines).Count().IsEqualTo(2);
        await Assert.That(lines[0].Text).IsEqualTo("a");
        await Assert.That(lines[1].Text).IsEqualTo("b");
    }

    [Test]
    public async Task Paragraph_BreaksLine()
    {
        var parser = new PuebloParser();
        var lines = parser.Feed("a<P>b</P>\n");
        await Assert.That(lines).Count().IsEqualTo(2);
        await Assert.That(lines[0].Text).IsEqualTo("a");
        await Assert.That(lines[1].Text).IsEqualTo("b");
    }

    [Test]
    public async Task UnknownTag_IsConsumedNotLeaked()
    {
        var line = ParseSingleLine("x<BLINK>y</BLINK>z\n");
        await Assert.That(line.Text).IsEqualTo("xyz");
    }

    [Test]
    public async Task Img_IsIgnored()
    {
        var line = ParseSingleLine("before<IMG SRC=\"map.png\">after\n");
        await Assert.That(line.Text).IsEqualTo("beforeafter");
    }

    [Test]
    public async Task Pre_TagsAreStrippedContentKept()
    {
        var line = ParseSingleLine("<PRE>  spaced  </PRE>\n");
        await Assert.That(line.Text).IsEqualTo("  spaced  ");
    }

    [Test]
    public async Task Comment_IsIgnored()
    {
        var line = ParseSingleLine("a<!-- hidden -->b\n");
        await Assert.That(line.Text).IsEqualTo("ab");
    }

    [Test]
    public async Task TagSplitAcrossFeeds_IsReassembled()
    {
        var parser = new PuebloParser();
        var first = parser.Feed("<FONT COL");
        await Assert.That(first).Count().IsEqualTo(0);
        var second = parser.Feed("OR=\"red\">x</FONT>\n");
        await Assert.That(second).Count().IsEqualTo(1);
        await Assert.That(second[0].Spans[0].Text).IsEqualTo("x");
        await Assert.That(second[0].Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromRgb(0xff, 0x00, 0x00));
    }

    [Test]
    public async Task EntitySplitAcrossFeeds_IsReassembled()
    {
        var parser = new PuebloParser();
        parser.Feed("&a");
        parser.Feed("mp;");
        var line = parser.Flush();
        await Assert.That(line!.Text).IsEqualTo("&");
    }

    [Test]
    public async Task UnbalancedCloser_DoesNotThrow()
    {
        var line = ParseSingleLine("</B>plain</FONT>text\n");
        await Assert.That(line.Text).IsEqualTo("plaintext");
        await Assert.That(line.Spans[0].Style).IsEqualTo(TextStyle.Default);
    }

    [Test]
    public async Task StrayLessThan_WithoutClose_IsEmittedLiterally()
    {
        // No '>' before the newline: the buffered "<3" is treated as literal text.
        var line = ParseSingleLine("love <3\n");
        await Assert.That(line.Text).IsEqualTo("love <3");
    }

    [Test]
    public async Task Esc_IsPassedThroughUntouched()
    {
        var line = ParseSingleLine("a\x1bb\n");
        await Assert.That(line.Text).IsEqualTo("a\x1bb");
    }

    [Test]
    public async Task Reset_ClearsStyleAndStack()
    {
        var parser = new PuebloParser();
        parser.Feed("<B><FONT COLOR=\"red\">bold");
        await Assert.That(parser.CurrentStyle).IsNotEqualTo(TextStyle.Default);

        parser.Reset();
        await Assert.That(parser.CurrentStyle).IsEqualTo(TextStyle.Default);
        await Assert.That(parser.HasPendingContent).IsFalse();

        var line = ParseSingleLineWith(parser, "plain\n");
        await Assert.That(line.Spans[0].Style).IsEqualTo(TextStyle.Default);
    }

    private static StyledLine ParseSingleLineWith(PuebloParser parser, string input)
    {
        var lines = parser.Feed(input);
        return lines[^1];
    }
}
