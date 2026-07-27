using MuClient.Core.Text;

namespace MuClient.Core.Tests.Text;

public class AnsiParserTests
{
    private static StyledLine ParseSingleLine(string input)
    {
        var parser = new AnsiParser();
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
    }

    [Test]
    public async Task Feed_SplitsOnNewlines()
    {
        var parser = new AnsiParser();
        var lines = parser.Feed("one\ntwo\nthree\n");
        await Assert.That(lines).Count().IsEqualTo(3);
        await Assert.That(lines[0].Text).IsEqualTo("one");
        await Assert.That(lines[1].Text).IsEqualTo("two");
        await Assert.That(lines[2].Text).IsEqualTo("three");
    }

    [Test]
    public async Task CarriageReturns_AreDropped()
    {
        var line = ParseSingleLine("prompt\r\n");
        await Assert.That(line.Text).IsEqualTo("prompt");
    }

    [Test]
    public async Task BlankLine_IsEmittedAsEmpty()
    {
        var parser = new AnsiParser();
        var lines = parser.Feed("a\n\nb\n");
        await Assert.That(lines).Count().IsEqualTo(3);
        await Assert.That(lines[1].IsEmpty).IsTrue();
    }

    [Test]
    public async Task Sgr_16Color_SetsForegroundIndex()
    {
        var line = ParseSingleLine("\x1b[31mred\n");
        await Assert.That(line.Spans).HasSingleItem();
        await Assert.That(line.Spans[0].Text).IsEqualTo("red");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromIndex(1));
    }

    [Test]
    public async Task Sgr_BrightForeground_MapsToHighPaletteIndex()
    {
        var line = ParseSingleLine("\x1b[92mbright\n");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromIndex(10));
    }

    [Test]
    public async Task Sgr_BackgroundColor_SetsBackgroundIndex()
    {
        var line = ParseSingleLine("\x1b[44mblue-bg\n");
        await Assert.That(line.Spans[0].Style.Background).IsEqualTo(TerminalColor.FromIndex(4));
    }

    [Test]
    public async Task Sgr_256Color_Foreground()
    {
        var line = ParseSingleLine("\x1b[38;5;196mrose\n");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromIndex(196));
    }

    [Test]
    public async Task Sgr_Truecolor_Foreground()
    {
        var line = ParseSingleLine("\x1b[38;2;12;34;56mtrue\n");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromRgb(12, 34, 56));
    }

    [Test]
    public async Task Sgr_Truecolor_ColonForm()
    {
        var line = ParseSingleLine("\x1b[38:2:255:128:0mcolon\n");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromRgb(255, 128, 0));
    }

    [Test]
    public async Task Sgr_Truecolor_ColonForm_WithColorSpaceId()
    {
        var line = ParseSingleLine("\x1b[38:2::255:128:0mcolon\n");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromRgb(255, 128, 0));
    }

    [Test]
    public async Task Sgr_Attributes_BoldItalicUnderline()
    {
        var line = ParseSingleLine("\x1b[1;3;4mfancy\n");
        var style = line.Spans[0].Style;
        await Assert.That(style.HasAttribute(TextAttributes.Bold)).IsTrue();
        await Assert.That(style.HasAttribute(TextAttributes.Italic)).IsTrue();
        await Assert.That(style.HasAttribute(TextAttributes.Underline)).IsTrue();
    }

    [Test]
    public async Task Sgr_Reset_ClearsEverything()
    {
        var parser = new AnsiParser();
        parser.Feed("\x1b[1;31mred");
        var lines = parser.Feed("\x1b[0mplain\n");
        await Assert.That(lines).HasSingleItem();
        await Assert.That(lines[0].Spans).Count().IsEqualTo(2);
        await Assert.That(lines[0].Spans[1].Style).IsEqualTo(TextStyle.Default);
    }

    [Test]
    public async Task Sgr_CombinedForegroundAndBackground()
    {
        var line = ParseSingleLine("\x1b[31;42mmix\n");
        var style = line.Spans[0].Style;
        await Assert.That(style.Foreground).IsEqualTo(TerminalColor.FromIndex(1));
        await Assert.That(style.Background).IsEqualTo(TerminalColor.FromIndex(2));
    }

    [Test]
    public async Task Sgr_EmptyParameter_IsReset()
    {
        var parser = new AnsiParser();
        parser.Feed("\x1b[31mred");
        var lines = parser.Feed("\x1b[mback\n");
        await Assert.That(lines[0].Spans[1].Style).IsEqualTo(TextStyle.Default);
    }

    [Test]
    public async Task StyleChange_SplitsSpans()
    {
        var line = ParseSingleLine("\x1b[31mA\x1b[32mB\n");
        await Assert.That(line.Spans).Count().IsEqualTo(2);
        await Assert.That(line.Spans[0].Text).IsEqualTo("A");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromIndex(1));
        await Assert.That(line.Spans[1].Text).IsEqualTo("B");
        await Assert.That(line.Spans[1].Style.Foreground).IsEqualTo(TerminalColor.FromIndex(2));
    }

    [Test]
    public async Task StylePersists_AcrossFeedCalls()
    {
        var parser = new AnsiParser();
        parser.Feed("\x1b[33m");
        var lines = parser.Feed("yellow\n");
        await Assert.That(lines[0].Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromIndex(3));
    }

    [Test]
    public async Task EscapeSequence_SplitAcrossFeeds_IsReassembled()
    {
        var parser = new AnsiParser();
        await Assert.That(parser.Feed("\x1b[3")).IsEmpty();
        await Assert.That(parser.Feed("1m")).IsEmpty();
        var lines = parser.Feed("red\n");
        await Assert.That(lines[0].Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromIndex(1));
    }

    [Test]
    public async Task NonSgrCsiSequence_IsDiscarded()
    {
        var line = ParseSingleLine("\x1b[2J\x1b[H\x1b[Kclean\n");
        await Assert.That(line.Text).IsEqualTo("clean");
    }

    [Test]
    public async Task OscSequence_IsDiscarded_BelTerminated()
    {
        var line = ParseSingleLine("\x1b]0;window title\x07visible\n");
        await Assert.That(line.Text).IsEqualTo("visible");
    }

    [Test]
    public async Task OscSequence_IsDiscarded_StTerminated()
    {
        var line = ParseSingleLine("\x1b]0;title\x1b\\visible\n");
        await Assert.That(line.Text).IsEqualTo("visible");
    }

    [Test]
    public async Task CharsetDesignation_IsDiscarded()
    {
        var line = ParseSingleLine("\x1b(Btext\n");
        await Assert.That(line.Text).IsEqualTo("text");
    }

    [Test]
    public async Task DcsSequence_IsConsumedThroughSt()
    {
        // A DCS (ESC P ... ST) payload must not leak into the output as text.
        var line = ParseSingleLine("\x1bP1;2;3|payload\x1b\\visible\n");
        await Assert.That(line.Text).IsEqualTo("visible");
    }

    [Test]
    public async Task ApcSequence_IsConsumedThroughSt()
    {
        // APC (ESC _ ... ST) — as used by the Kitty graphics protocol — must be discarded.
        var line = ParseSingleLine("\x1b_Gf=100,a=T;base64data\x1b\\shown\n");
        await Assert.That(line.Text).IsEqualTo("shown");
    }

    [Test]
    public async Task Flush_ReturnsPendingPartialLine()
    {
        var parser = new AnsiParser();
        await Assert.That(parser.Feed("\x1b[31mprompt> ")).IsEmpty();
        var line = parser.Flush();
        await Assert.That(line).IsNotNull();
        await Assert.That(line!.Text).IsEqualTo("prompt> ");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromIndex(1));
    }

    [Test]
    public async Task Flush_ReturnsNull_WhenNothingBuffered()
    {
        var parser = new AnsiParser();
        parser.Feed("done\n");
        await Assert.That(parser.Flush()).IsNull();
    }

    [Test]
    public async Task Reset_ClearsStyleAndBuffers()
    {
        var parser = new AnsiParser();
        parser.Feed("\x1b[31mpartial");
        parser.Reset();
        await Assert.That(parser.CurrentStyle).IsEqualTo(TextStyle.Default);
        await Assert.That(parser.HasPendingContent).IsFalse();
        await Assert.That(parser.Flush()).IsNull();
    }

    [Test]
    public async Task DefaultForeground_ResetsColorButKeepsAttributes()
    {
        var parser = new AnsiParser();
        parser.Feed("\x1b[1;31mA");
        var lines = parser.Feed("\x1b[39mB\n");
        var second = lines[0].Spans[1].Style;
        await Assert.That(second.Foreground).IsEqualTo(TerminalColor.Default);
        await Assert.That(second.HasAttribute(TextAttributes.Bold)).IsTrue();
    }
}
