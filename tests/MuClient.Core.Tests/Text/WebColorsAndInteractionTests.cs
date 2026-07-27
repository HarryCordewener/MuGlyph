using MuClient.Core.Text;

namespace MuClient.Core.Tests.Text;

public class WebColorsTests
{
    [Test]
    public async Task NamedColor_Resolves()
    {
        await Assert.That(WebColors.TryParse("red", out var c)).IsTrue();
        await Assert.That(c).IsEqualTo(TerminalColor.FromRgb(0xff, 0x00, 0x00));
    }

    [Test]
    public async Task NamedColor_IsCaseInsensitive()
    {
        await Assert.That(WebColors.TryParse("Blue", out var c)).IsTrue();
        await Assert.That(c).IsEqualTo(TerminalColor.FromRgb(0x00, 0x00, 0xff));
    }

    [Test]
    public async Task HexColor_SixDigits()
    {
        await Assert.That(WebColors.TryParse("#12ab34", out var c)).IsTrue();
        await Assert.That(c).IsEqualTo(TerminalColor.FromRgb(0x12, 0xab, 0x34));
    }

    [Test]
    public async Task HexColor_ShortForm()
    {
        await Assert.That(WebColors.TryParse("#0f0", out var c)).IsTrue();
        await Assert.That(c).IsEqualTo(TerminalColor.FromRgb(0x00, 0xff, 0x00));
    }

    [Test]
    public async Task Unknown_ReturnsFalse()
    {
        await Assert.That(WebColors.TryParse("notacolor", out _)).IsFalse();
        await Assert.That(WebColors.TryParse("", out _)).IsFalse();
        await Assert.That(WebColors.TryParse(null, out _)).IsFalse();
    }
}

public class SpanInteractionTests
{
    [Test]
    public async Task Command_Factory_SetsFields()
    {
        var i = SpanInteraction.Command("look", "examine", promptOnly: true);
        await Assert.That(i.Kind).IsEqualTo(InteractionKind.SendCommand);
        await Assert.That(i.Target).IsEqualTo("look");
        await Assert.That(i.Hint).IsEqualTo("examine");
        await Assert.That(i.PromptOnly).IsTrue();
    }

    [Test]
    public async Task Link_Factory_SetsFields()
    {
        var i = SpanInteraction.Link("https://example.org", "site");
        await Assert.That(i.Kind).IsEqualTo(InteractionKind.Hyperlink);
        await Assert.That(i.Target).IsEqualTo("https://example.org");
    }

    [Test]
    public async Task StyledSpan_CarriesInteraction_AndAffectsEquality()
    {
        var plain = new StyledSpan("x", TextStyle.Default);
        var linked = new StyledSpan("x", TextStyle.Default, SpanInteraction.Command("go"));
        await Assert.That(plain.IsInteractive).IsFalse();
        await Assert.That(linked.IsInteractive).IsTrue();
        await Assert.That(plain).IsNotEqualTo(linked);
    }
}
