using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Text;

/// <summary>
/// Round-trip fidelity for the scrollback spill's wire format. A MU* client meets every awkward
/// string there is — box-drawing, CJK, emoji with zero-width joiners, combining marks, stray control
/// bytes from a badly behaved server — so "it works for ASCII" is not a passing bar.
/// </summary>
public class StyledLineCodecTests
{
    private static async Task AssertRoundTrips(StyledLine line)
    {
        var decoded = StyledLineCodec.Decode(StyledLineCodec.Encode(line));

        await Assert.That(decoded.Text).IsEqualTo(line.Text);
        await Assert.That(decoded.Spans.Count).IsEqualTo(line.Spans.Count);
        for (var i = 0; i < line.Spans.Count; i++)
        {
            await Assert.That(decoded.Spans[i]).IsEqualTo(line.Spans[i]);
        }

        await Assert.That(decoded.RuleColor).IsEqualTo(line.RuleColor);
    }

    [Test]
    public async Task EmptyLine_RoundTrips()
    {
        await AssertRoundTrips(StyledLine.Empty);
        await Assert.That(StyledLineCodec.Decode(StyledLineCodec.Encode(StyledLine.Empty)).IsEmpty).IsTrue();
    }

    [Test]
    public async Task EmptyLineWithRule_KeepsTheRule()
    {
        var line = StyledLine.Empty.WithRule(TerminalColor.FromRgb(1, 2, 3));
        await AssertRoundTrips(line);
    }

    [Test]
    public async Task EveryColourKindAndAttributeCombination_RoundTrips()
    {
        var spans = new List<StyledSpan>
        {
            new("default", TextStyle.Default),
            new("indexed", new TextStyle(TerminalColor.FromIndex(0), TerminalColor.FromIndex(255), TextAttributes.Bold)),
            new("rgb", new TextStyle(TerminalColor.FromRgb(0, 128, 255), TerminalColor.FromRgb(255, 0, 0), TextAttributes.Italic | TextAttributes.Underline)),
            new("mixed", new TextStyle(TerminalColor.Default, TerminalColor.FromIndex(17), TextAttributes.Blink | TextAttributes.Reverse | TextAttributes.Conceal)),
            new(
                "all-attributes",
                new TextStyle(
                    TerminalColor.FromRgb(255, 255, 255),
                    TerminalColor.Default,
                    TextAttributes.Bold | TextAttributes.Faint | TextAttributes.Italic | TextAttributes.Underline
                    | TextAttributes.Blink | TextAttributes.Reverse | TextAttributes.Conceal | TextAttributes.Strikethrough)),
        };

        await AssertRoundTrips(new StyledLine(spans, TerminalColor.FromIndex(214)));
    }

    [Test]
    public async Task Interactions_RoundTripIncludingHintAndPromptOnly()
    {
        var spans = new[]
        {
            new StyledSpan("look", TextStyle.Default, SpanInteraction.Command("look")),
            new StyledSpan("hint", TextStyle.Default, SpanInteraction.Command("say hi", "Greet them", promptOnly: true)),
            new StyledSpan("link", TextStyle.Default, SpanInteraction.Link("https://example.invalid/x?a=1&b=2", "Open")),
            new StyledSpan("plain", TextStyle.Default),
        };

        await AssertRoundTrips(new StyledLine(spans));
    }

    [Test]
    [Arguments("café — naïve")]
    [Arguments("日本語のテキストです")]
    [Arguments("한국어 텍스트")]
    [Arguments("Здравствуй, мир")]
    [Arguments("العربية")]
    [Arguments("👩‍👩‍👧‍👦 family")]
    [Arguments("🇯🇵🇺🇸 flags")]
    [Arguments("🧑🏽‍🚀 skin-tone joiner")]
    [Arguments("é combining acute")]
    [Arguments("┌─┬─┐ box drawing ╚═╝")]
    [Arguments("tab\there and null\0byte")]
    [Arguments("embedded\nnewline")]
    [Arguments("bare [31m escape text")]
    [Arguments("� replacement char")]
    public async Task AwkwardText_RoundTrips(string text)
    {
        await AssertRoundTrips(StyledLine.FromText(text, TextStyle.Default));
    }

    [Test]
    public async Task VeryLongLine_RoundTrips()
    {
        var text = string.Concat(Enumerable.Repeat("漢字abc🙂", 20_000));
        var line = StyledLine.FromText(text, new TextStyle(TerminalColor.FromIndex(9), TerminalColor.Default, TextAttributes.Bold));
        await AssertRoundTrips(line);
        await Assert.That(line.Text.Length).IsGreaterThan(100_000);
    }

    [Test]
    public async Task ManySpans_RoundTrip()
    {
        var spans = Enumerable.Range(0, 2_000)
            .Select(i => new StyledSpan(
                $"s{i}",
                new TextStyle(TerminalColor.FromIndex(i % 256), TerminalColor.Default, (TextAttributes)(i % 256))))
            .ToArray();

        await AssertRoundTrips(new StyledLine(spans));
    }

    [Test]
    public async Task ZeroLengthSpans_AreDroppedByStyledLineNotTheCodec()
    {
        // StyledLine itself discards empty spans; the codec must agree about the count either way.
        var line = new StyledLine(new[] { new StyledSpan(string.Empty, TextStyle.Default), new StyledSpan("x", TextStyle.Default) });
        await Assert.That(line.Spans.Count).IsEqualTo(1);
        await AssertRoundTrips(line);
    }

    [Test]
    public async Task TruncatedPayload_IsRejectedRatherThanDecodedAsGarbage()
    {
        var payload = StyledLineCodec.Encode(StyledLine.FromText("a reasonably long line of output", TextStyle.Default));

        var threw = false;
        try
        {
            StyledLineCodec.Decode(payload, 0, payload.Length / 2);
        }
        catch (Exception)
        {
            threw = true;
        }

        // The store's job is to never surface garbage; the codec's job is to refuse to invent it.
        await Assert.That(threw).IsTrue();
    }
}
