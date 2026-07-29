using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Theming;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

public class MarkupFormatterTests
{
    private static readonly MarkupFormatter Formatter = new(ThemeLibrary.Dark());

    [Test]
    public async Task PlainText_IsWrappedInAResolvedForegroundColour()
    {
        var line = StyledLine.FromText("hello", TextStyle.Default);

        var markup = Formatter.ToMarkup(line);

        // Default resolves to the theme foreground, so text is coloured but carries no background.
        await Assert.That(markup).Contains("hello");
        await Assert.That(markup).StartsWith("[#");
        await Assert.That(markup).EndsWith("[/]");
        await Assert.That(markup).DoesNotContain(" on #");
    }

    [Test]
    public async Task RuleColor_PrependsLeftRuleGlyph()
    {
        var line = new StyledLine(
            new[] { new StyledSpan("channel", TextStyle.Default) },
            TerminalColor.FromRgb(0x00, 0xf5, 0xb7));

        var markup = Formatter.ToMarkup(line);

        // A 2-col left rule in the trigger colour precedes the content.
        await Assert.That(markup).StartsWith("[#00f5b7]▌[/] ");
        await Assert.That(markup).Contains("channel");
    }

    [Test]
    public async Task Timestamp_PrependsADimGutterAheadOfContent()
    {
        var line = StyledLine.FromText("hello", TextStyle.Default);

        var markup = Formatter.ToMarkup(line, "09:24");

        await Assert.That(markup).StartsWith("[dim]09:24[/] ");
        await Assert.That(markup).Contains("hello");
    }

    [Test]
    public async Task Timestamp_PrecedesTheTriggerLeftRule()
    {
        var line = new StyledLine(
            new[] { new StyledSpan("channel", TextStyle.Default) },
            TerminalColor.FromRgb(0x00, 0xf5, 0xb7));

        var markup = Formatter.ToMarkup(line, "09:24");

        // Timestamp gutter first, then the coloured left rule.
        await Assert.That(markup).StartsWith("[dim]09:24[/] [#00f5b7]▌[/] ");
    }

    [Test]
    public async Task NullOrEmptyTimestamp_AddsNoGutter()
    {
        var line = StyledLine.FromText("hello", TextStyle.Default);

        await Assert.That(Formatter.ToMarkup(line, null)).StartsWith("[#");
        await Assert.That(Formatter.ToMarkup(line, "")).StartsWith("[#");
    }

    [Test]
    public async Task BoldItalic_EmitsAttributeTokens()
    {
        var style = new TextStyle(
            TerminalColor.FromRgb(255, 0, 0),
            TerminalColor.Default,
            TextAttributes.Bold | TextAttributes.Italic);
        var line = StyledLine.FromText("x", style);

        var markup = Formatter.ToMarkup(line);

        await Assert.That(markup).Contains("bold");
        await Assert.That(markup).Contains("italic");
        await Assert.That(markup).Contains("#ff0000");
    }

    [Test]
    public async Task Background_IsEmittedOnlyWhenSet()
    {
        var withBg = new TextStyle(TerminalColor.Default, TerminalColor.FromRgb(16, 32, 48), TextAttributes.None);
        var markup = Formatter.ToMarkup(StyledLine.FromText("x", withBg));

        await Assert.That(markup).Contains("on #102030");
    }

    [Test]
    public async Task SendCommandInteraction_BecomesAnEscapedSendLink()
    {
        var span = new StyledSpan("go north", TextStyle.Default, SpanInteraction.Command("go north"));
        var line = new StyledLine(new[] { span });

        var markup = Formatter.ToMarkup(line);

        await Assert.That(markup).Contains("[link=mux:send:go%20north]");
        await Assert.That(markup).EndsWith("[/][/]"); // closes style then link
    }

    [Test]
    public async Task PromptOnlyInteraction_UsesThePromptScheme()
    {
        var span = new StyledSpan("look", TextStyle.Default, SpanInteraction.Command("look", promptOnly: true));
        var markup = Formatter.ToMarkup(new StyledLine(new[] { span }));

        await Assert.That(markup).Contains("[link=mux:prompt:look]");
    }

    [Test]
    public async Task Hyperlink_UsesTheRawUrl()
    {
        var span = new StyledSpan("site", TextStyle.Default, SpanInteraction.Link("https://example.org"));
        var markup = Formatter.ToMarkup(new StyledLine(new[] { span }));

        await Assert.That(markup).Contains("[link=https://example.org]");
    }

    [Test]
    public async Task LiteralBrackets_AreEscaped()
    {
        var markup = Formatter.ToMarkup(StyledLine.FromText("[chat] hi", TextStyle.Default));

        await Assert.That(markup).Contains("[[chat]] hi");
    }

    [Test]
    public async Task EmptyLine_ProducesEmptyMarkup()
    {
        await Assert.That(Formatter.ToMarkup(StyledLine.Empty)).IsEqualTo(string.Empty);
    }

    // ---- F7 preferences that are decisions about markup ----

    private static StyledLine Blinking() => new(new[]
    {
        new StyledSpan("alert", new TextStyle(TerminalColor.Default, TerminalColor.Default, TextAttributes.Blink)),
    });

    /// <summary>
    /// SGR 5 is parsed but dropped by default: a blinking line is the one rendition a server can
    /// impose that the reader cannot stop looking at. F7's <c>allow blink</c> is what lets it through.
    /// </summary>
    [Test]
    public async Task AllowBlink_Off_DropsTheBlinkAttribute()
    {
        var formatter = new MarkupFormatter(ThemeLibrary.Dark(), new TextSettings { AllowBlink = false });

        await Assert.That(formatter.ToMarkup(Blinking())).DoesNotContain("blink");
    }

    [Test]
    public async Task AllowBlink_On_EmitsTheBlinkToken()
    {
        var formatter = new MarkupFormatter(ThemeLibrary.Dark(), new TextSettings { AllowBlink = true });

        await Assert.That(formatter.ToMarkup(Blinking())).Contains("blink");
    }

    /// <summary>The setting is read per span, so flipping it changes the very next line rendered.</summary>
    [Test]
    public async Task AllowBlink_FlippingIt_ChangesTheNextLine()
    {
        var text = new TextSettings { AllowBlink = false };
        var formatter = new MarkupFormatter(ThemeLibrary.Dark(), text);

        var before = formatter.ToMarkup(Blinking());
        text.AllowBlink = true;
        var after = formatter.ToMarkup(Blinking());

        await Assert.That(before).DoesNotContain("blink");
        await Assert.That(after).Contains("blink");
    }

    private static StyledLine LinkLine() => new(new[]
    {
        new StyledSpan("site", TextStyle.Default, SpanInteraction.Link("https://example.org")),
    });

    [Test]
    public async Task UnderlineHyperlinks_On_UnderlinesAClickableSpan()
    {
        var formatter = new MarkupFormatter(ThemeLibrary.Dark(), new TextSettings { UnderlineHyperlinks = true });

        await Assert.That(formatter.ToMarkup(LinkLine())).Contains("underline");
    }

    [Test]
    public async Task UnderlineHyperlinks_Off_LeavesAnUnstyledLinkUnstyled()
    {
        var formatter = new MarkupFormatter(ThemeLibrary.Dark(), new TextSettings { UnderlineHyperlinks = false });
        var markup = formatter.ToMarkup(LinkLine());

        await Assert.That(markup).DoesNotContain("underline");
        await Assert.That(markup).Contains("[link=https://example.org]");
    }

    /// <summary>It underlines links, not everything — plain text is untouched either way.</summary>
    [Test]
    public async Task UnderlineHyperlinks_DoesNotTouchPlainText()
    {
        var formatter = new MarkupFormatter(ThemeLibrary.Dark(), new TextSettings { UnderlineHyperlinks = true });

        await Assert.That(formatter.ToMarkup(StyledLine.FromText("hello", TextStyle.Default)))
            .DoesNotContain("underline");
    }

    /// <summary>
    /// A link the server already underlined gets one token, not two: the preference is folded into the
    /// span's own attributes rather than emitted alongside them.
    /// </summary>
    [Test]
    public async Task UnderlineHyperlinks_OnAnAlreadyUnderlinedLink_EmitsOneToken()
    {
        var formatter = new MarkupFormatter(ThemeLibrary.Dark(), new TextSettings { UnderlineHyperlinks = true });
        var line = new StyledLine(new[]
        {
            new StyledSpan(
                "site",
                new TextStyle(TerminalColor.Default, TerminalColor.Default, TextAttributes.Underline),
                SpanInteraction.Link("https://example.org")),
        });

        var markup = formatter.ToMarkup(line);

        await Assert.That(markup.Split("underline").Length - 1).IsEqualTo(1);
    }
}
