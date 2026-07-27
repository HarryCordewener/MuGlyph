using MuClient.Core.Protocols;
using MuClient.Core.Text;

namespace MuClient.Core.Tests.Protocols;

public class MxpParserTests
{
    private static StyledLine ParseSingleLine(string input)
    {
        var parser = new MxpParser();
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
        var parser = new MxpParser();
        var lines = parser.Feed("one\ntwo\nthree\n");
        await Assert.That(lines).Count().IsEqualTo(3);
        await Assert.That(lines[0].Text).IsEqualTo("one");
        await Assert.That(lines[1].Text).IsEqualTo("two");
        await Assert.That(lines[2].Text).IsEqualTo("three");
    }

    [Test]
    public async Task Flush_ReturnsPartialPromptLine()
    {
        var parser = new MxpParser();
        var lines = parser.Feed("Enter password:");
        await Assert.That(lines).Count().IsEqualTo(0);

        var prompt = parser.Flush();
        await Assert.That(prompt).IsNotNull();
        await Assert.That(prompt!.Text).IsEqualTo("Enter password:");
    }

    [Test]
    public async Task Flush_ReturnsNullWhenNothingBuffered()
    {
        var parser = new MxpParser();
        parser.Feed("done\n");
        await Assert.That(parser.Flush()).IsNull();
    }

    [Test]
    public async Task CarriageReturn_IsDropped()
    {
        var line = ParseSingleLine("prompt\r\n");
        await Assert.That(line.Text).IsEqualTo("prompt");
    }

    [Test]
    public async Task BlankLine_IsEmittedAsEmpty()
    {
        var parser = new MxpParser();
        var lines = parser.Feed("a\n\nb\n");
        await Assert.That(lines).Count().IsEqualTo(3);
        await Assert.That(lines[1].IsEmpty).IsTrue();
    }

    [Test]
    public async Task Bold_TogglesBoldAttribute()
    {
        var line = ParseSingleLine("<B>bold</B> plain\n");
        await Assert.That(line.Spans).Count().IsEqualTo(2);
        await Assert.That(line.Spans[0].Text).IsEqualTo("bold");
        await Assert.That(line.Spans[0].Style.HasAttribute(TextAttributes.Bold)).IsTrue();
        await Assert.That(line.Spans[1].Text).IsEqualTo(" plain");
        await Assert.That(line.Spans[1].Style.HasAttribute(TextAttributes.Bold)).IsFalse();
    }

    [Test]
    public async Task BoldAlias_StrongProducesBold()
    {
        var line = ParseSingleLine("<STRONG>x</STRONG>\n");
        await Assert.That(line.Spans[0].Style.HasAttribute(TextAttributes.Bold)).IsTrue();
    }

    [Test]
    public async Task Italic_And_Underline_And_Strikeout()
    {
        var line = ParseSingleLine("<I>i</I><U>u</U><S>s</S>\n");
        await Assert.That(line.Spans[0].Style.HasAttribute(TextAttributes.Italic)).IsTrue();
        await Assert.That(line.Spans[1].Style.HasAttribute(TextAttributes.Underline)).IsTrue();
        await Assert.That(line.Spans[2].Style.HasAttribute(TextAttributes.Strikethrough)).IsTrue();
    }

    [Test]
    public async Task NestedFormatting_CombinesAttributes()
    {
        var line = ParseSingleLine("<B><I>x</I></B>\n");
        await Assert.That(line.Spans).HasSingleItem();
        await Assert.That(line.Spans[0].Style.HasAttribute(TextAttributes.Bold)).IsTrue();
        await Assert.That(line.Spans[0].Style.HasAttribute(TextAttributes.Italic)).IsTrue();
    }

    [Test]
    public async Task NestedFormatting_CloseRevertsInnerOnly()
    {
        var line = ParseSingleLine("<B>a<I>b</I>c</B>\n");
        await Assert.That(line.Spans).Count().IsEqualTo(3);
        await Assert.That(line.Spans[0].Style.HasAttribute(TextAttributes.Italic)).IsFalse();
        await Assert.That(line.Spans[1].Style.HasAttribute(TextAttributes.Italic)).IsTrue();
        await Assert.That(line.Spans[2].Style.HasAttribute(TextAttributes.Bold)).IsTrue();
        await Assert.That(line.Spans[2].Style.HasAttribute(TextAttributes.Italic)).IsFalse();
    }

    [Test]
    public async Task UnbalancedCloser_DoesNotThrow()
    {
        var line = ParseSingleLine("plain</B> more\n");
        await Assert.That(line.Text).IsEqualTo("plain more");
    }

    [Test]
    public async Task StrayOpenBoldWithoutClose_AppliesToRestOfLine()
    {
        var line = ParseSingleLine("<B>rest of line\n");
        await Assert.That(line.Spans[0].Style.HasAttribute(TextAttributes.Bold)).IsTrue();
    }

    [Test]
    public async Task Color_ForeNamed_SetsForeground()
    {
        WebColors.TryParse("red", out var red);
        var line = ParseSingleLine("<COLOR FORE=red>hot</COLOR>\n");
        await Assert.That(line.Spans[0].Text).IsEqualTo("hot");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(red);
    }

    [Test]
    public async Task ColorShortForm_C_WithForeAndBack()
    {
        WebColors.TryParse("white", out var white);
        WebColors.TryParse("blue", out var blue);
        var line = ParseSingleLine("<C FORE=white BACK=blue>x</C>\n");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(white);
        await Assert.That(line.Spans[0].Style.Background).IsEqualTo(blue);
    }

    [Test]
    public async Task ColorPositional_FirstIsForeground()
    {
        WebColors.TryParse("green", out var green);
        var line = ParseSingleLine("<COLOR green>go</COLOR>\n");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(green);
    }

    [Test]
    public async Task ColorClose_RevertsForeground()
    {
        var line = ParseSingleLine("<COLOR FORE=red>red</COLOR>back\n");
        await Assert.That(line.Spans).Count().IsEqualTo(2);
        await Assert.That(line.Spans[1].Text).IsEqualTo("back");
        await Assert.That(line.Spans[1].Style.Foreground).IsEqualTo(TerminalColor.Default);
    }

    [Test]
    public async Task Font_ColorAttribute_SetsForegroundHex()
    {
        WebColors.TryParse("#00ff00", out var lime);
        var line = ParseSingleLine("<FONT COLOR=#00ff00>x</FONT>\n");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(lime);
    }

    [Test]
    public async Task Color_UnknownName_IsIgnored()
    {
        var line = ParseSingleLine("<COLOR FORE=notacolour>x</COLOR>\n");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.Default);
    }

    [Test]
    public async Task Send_WithHref_ProducesSendCommand()
    {
        var line = ParseSingleLine("<SEND HREF=\"look\">here</SEND>\n");
        await Assert.That(line.Spans).HasSingleItem();
        var span = line.Spans[0];
        await Assert.That(span.Text).IsEqualTo("here");
        await Assert.That(span.IsInteractive).IsTrue();
        await Assert.That(span.Interaction!.Kind).IsEqualTo(InteractionKind.SendCommand);
        await Assert.That(span.Interaction!.Target).IsEqualTo("look");
    }

    [Test]
    public async Task Send_WithoutHref_UsesEnclosedTextAsCommand()
    {
        var line = ParseSingleLine("<SEND>north</SEND>\n");
        var span = line.Spans[0];
        await Assert.That(span.Text).IsEqualTo("north");
        await Assert.That(span.Interaction!.Kind).IsEqualTo(InteractionKind.SendCommand);
        await Assert.That(span.Interaction!.Target).IsEqualTo("north");
    }

    [Test]
    public async Task Send_CapturesHint()
    {
        var line = ParseSingleLine("<SEND HREF=\"look\" HINT=\"examine\">x</SEND>\n");
        await Assert.That(line.Spans[0].Interaction!.Hint).IsEqualTo("examine");
    }

    [Test]
    public async Task Send_MultiCommandHref_UsesFirstAsPrimary()
    {
        var line = ParseSingleLine("<SEND HREF=\"a|b|c\">x</SEND>\n");
        await Assert.That(line.Spans[0].Interaction!.Target).IsEqualTo("a");
    }

    [Test]
    public async Task Send_PromptFlag_SetsPromptOnly()
    {
        var line = ParseSingleLine("<SEND HREF=\"cast spell\" PROMPT>x</SEND>\n");
        await Assert.That(line.Spans[0].Interaction!.PromptOnly).IsTrue();
    }

    [Test]
    public async Task Send_BareWithoutClose_ClosesAtEndOfLine()
    {
        var parser = new MxpParser();
        var lines = parser.Feed("<SEND HREF=\"go\">walk\nnext line\n");
        await Assert.That(lines).Count().IsEqualTo(2);
        await Assert.That(lines[0].Spans[0].Interaction!.Target).IsEqualTo("go");
        await Assert.That(lines[1].Spans[0].IsInteractive).IsFalse();
    }

    [Test]
    public async Task Anchor_WithHref_ProducesHyperlink()
    {
        var line = ParseSingleLine("<A HREF=\"https://example.com\">site</A>\n");
        var span = line.Spans[0];
        await Assert.That(span.Text).IsEqualTo("site");
        await Assert.That(span.Interaction!.Kind).IsEqualTo(InteractionKind.Hyperlink);
        await Assert.That(span.Interaction!.Target).IsEqualTo("https://example.com");
    }

    [Test]
    public async Task Entities_AreDecoded()
    {
        var line = ParseSingleLine("&lt;&gt;&amp;&nbsp;&#65;\n");
        await Assert.That(line.Text).IsEqualTo("<>& A");
    }

    [Test]
    public async Task Entities_QuoteAndApos()
    {
        var line = ParseSingleLine("&quot;&apos;\n");
        await Assert.That(line.Text).IsEqualTo("\"'");
    }

    [Test]
    public async Task Entity_HexNumeric_IsDecoded()
    {
        var line = ParseSingleLine("&#x41;&#x42;\n");
        await Assert.That(line.Text).IsEqualTo("AB");
    }

    [Test]
    public async Task Entity_Unknown_IsEmittedLiterally()
    {
        var line = ParseSingleLine("&unknown;\n");
        await Assert.That(line.Text).IsEqualTo("&unknown;");
    }

    [Test]
    public async Task StrayAmpersand_IsEmittedLiterally()
    {
        var line = ParseSingleLine("Tom & Jerry\n");
        await Assert.That(line.Text).IsEqualTo("Tom & Jerry");
    }

    [Test]
    public async Task UnknownTag_IsConsumedNotLeaked()
    {
        var line = ParseSingleLine("a<VAR NAME=hp>b\n");
        await Assert.That(line.Text).IsEqualTo("ab");
    }

    [Test]
    public async Task UnsupportedTags_AreStripped()
    {
        var line = ParseSingleLine("<H1>Title</H1><P>text<IMG SRC=\"x.png\">end\n");
        await Assert.That(line.Text).IsEqualTo("Titletextend");
    }

    [Test]
    public async Task Br_ProducesLineBreak()
    {
        var parser = new MxpParser();
        var lines = parser.Feed("first<BR>second\n");
        await Assert.That(lines).Count().IsEqualTo(2);
        await Assert.That(lines[0].Text).IsEqualTo("first");
        await Assert.That(lines[1].Text).IsEqualTo("second");
    }

    [Test]
    public async Task StrayLessThan_IsEmittedLiterally()
    {
        var line = ParseSingleLine("5 < 3 is false\n");
        await Assert.That(line.Text).IsEqualTo("5 < 3 is false");
    }

    [Test]
    public async Task TagSplitAcrossFeeds_IsReassembled()
    {
        var parser = new MxpParser();
        var first = parser.Feed("<COL");
        await Assert.That(first).Count().IsEqualTo(0);
        var second = parser.Feed("OR FORE=red>hot</COLOR>\n");
        WebColors.TryParse("red", out var red);
        await Assert.That(second).HasSingleItem();
        await Assert.That(second[0].Spans[0].Text).IsEqualTo("hot");
        await Assert.That(second[0].Spans[0].Style.Foreground).IsEqualTo(red);
    }

    [Test]
    public async Task EntitySplitAcrossFeeds_IsReassembled()
    {
        var parser = new MxpParser();
        parser.Feed("x&a");
        var lines = parser.Feed("mp;y\n");
        await Assert.That(lines[0].Text).IsEqualTo("x&y");
    }

    [Test]
    public async Task StyleState_PersistsAcrossFeeds()
    {
        var parser = new MxpParser();
        parser.Feed("<B>bold ");
        await Assert.That(parser.CurrentStyle.HasAttribute(TextAttributes.Bold)).IsTrue();
        var lines = parser.Feed("still bold</B>\n");
        await Assert.That(lines[0].Spans[0].Style.HasAttribute(TextAttributes.Bold)).IsTrue();
    }

    [Test]
    public async Task SelfClosingBr_IsHandled()
    {
        var parser = new MxpParser();
        var lines = parser.Feed("a<BR/>b\n");
        await Assert.That(lines).Count().IsEqualTo(2);
        await Assert.That(lines[0].Text).IsEqualTo("a");
    }

    [Test]
    public async Task EscapeByte_IsPassedThrough()
    {
        var line = ParseSingleLine("a\x1bb\n");
        await Assert.That(line.Text).IsEqualTo("a\x1bb");
    }

    [Test]
    public async Task Send_WithFormattingInside_KeepsInteractionOnAllSpans()
    {
        var line = ParseSingleLine("<SEND HREF=\"go\"><B>walk</B> now</SEND>\n");
        await Assert.That(line.Spans).Count().IsEqualTo(2);
        await Assert.That(line.Spans[0].Interaction!.Target).IsEqualTo("go");
        await Assert.That(line.Spans[0].Style.HasAttribute(TextAttributes.Bold)).IsTrue();
        await Assert.That(line.Spans[1].Interaction!.Target).IsEqualTo("go");
    }

    [Test]
    public async Task Reset_ClearsStyleAndStack()
    {
        var parser = new MxpParser();
        parser.Feed("<B>bold");
        parser.Reset();
        await Assert.That(parser.CurrentStyle).IsEqualTo(TextStyle.Default);
        await Assert.That(parser.HasPendingContent).IsFalse();
        var lines = parser.Feed("plain\n");
        await Assert.That(lines[0].Spans[0].Style.HasAttribute(TextAttributes.Bold)).IsFalse();
    }

    [Test]
    public async Task HasPendingContent_TrueWithBufferedText()
    {
        var parser = new MxpParser();
        parser.Feed("partial");
        await Assert.That(parser.HasPendingContent).IsTrue();
    }
}
