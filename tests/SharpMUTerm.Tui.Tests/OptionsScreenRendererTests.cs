using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

public class OptionsScreenRendererTests
{
    [Test]
    public async Task Render_ToggleRow_TrueRendersCheckedBox()
    {
        var rows = new[] { new OptionsScreenRenderer.OptionRow("underline hyperlinks", null, true) };
        var lines = OptionsScreenRenderer.Render("Text & ANSI", "F7", rows);
        var row = lines.Single(l => l.Contains("underline hyperlinks"));
        await Assert.That(row).Contains("[[x]]");
    }

    [Test]
    public async Task Render_ToggleRow_FalseRendersUncheckedBox()
    {
        var rows = new[] { new OptionsScreenRenderer.OptionRow("strip incoming ANSI colour", null, false) };
        var lines = OptionsScreenRenderer.Render("Text & ANSI", "F7", rows);
        var row = lines.Single(l => l.Contains("strip incoming ANSI colour"));
        await Assert.That(row).Contains("[[ ]]");
    }

    [Test]
    public async Task Render_ValueRow_ShowsLabelAndValue()
    {
        var rows = new[] { new OptionsScreenRenderer.OptionRow("dictionary", "en_US", null) };
        var lines = OptionsScreenRenderer.Render("Input & spellcheck", "F8", rows);
        var row = lines.Single(l => l.Contains("dictionary"));
        await Assert.That(row).Contains("en_US");
    }

    [Test]
    public async Task Render_SectionHeaderRow_IsDim()
    {
        var rows = new[] { new OptionsScreenRenderer.OptionRow("├ COLOUR", null, null) };
        var lines = OptionsScreenRenderer.Render("Text & ANSI", "F7", rows);
        await Assert.That(lines.Any(l => l == "[dim]├ COLOUR[/]")).IsTrue();
    }

    [Test]
    public async Task Render_SpacerRow_IsBlankLine()
    {
        var rows = new[]
        {
            new OptionsScreenRenderer.OptionRow("a", null, true),
            new OptionsScreenRenderer.OptionRow(string.Empty, null, null),
            new OptionsScreenRenderer.OptionRow("b", null, true),
        };
        var lines = OptionsScreenRenderer.Render("Text & ANSI", "F7", rows);
        // header + blank + a + spacer + b + blank + footer
        await Assert.That(lines[3]).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// The header names the screen and how to leave it, and nothing else. It used to open with a
    /// <c>‹ back</c> affordance — on the options screens only, pointing at a navigation stack that does
    /// not exist. The assertion is kept pointed the other way rather than dropped, so it cannot come
    /// back by accident.
    /// </summary>
    [Test]
    public async Task Render_HeaderAndFooter_MatchPattern()
    {
        var lines = OptionsScreenRenderer.Render(
            "Input & spellcheck", "F8", Array.Empty<OptionsScreenRenderer.OptionRow>());
        await Assert.That(lines[0]).DoesNotContain("‹ back");
        await Assert.That(lines[0]).Contains("Input & spellcheck");
        await Assert.That(lines[0]).Contains("F8");
        await Assert.That(lines[^1]).Contains("Cancel");
        await Assert.That(lines[^1]).Contains("Save");
    }

    [Test]
    public async Task Render_EscapesMarkupBrackets()
    {
        var rows = new[] { new OptionsScreenRenderer.OptionRow("weird [label]", "va[l]ue", null) };
        var lines = OptionsScreenRenderer.Render("Text & ANSI", "F7", rows);
        await Assert.That(lines.Any(l => l.Contains("weird [[label]]") && l.Contains("va[[l]]ue"))).IsTrue();
    }

    [Test]
    public async Task TextAnsi_ContainsExpectedLabelsAndSections()
    {
        var lines = OptionsScreenRenderer.TextAnsi();
        await Assert.That(lines.Any(l => l.Contains("COLOUR"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("UNICODE"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("strip incoming ANSI colour"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("allow blink"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("underline hyperlinks"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("emoji substitution"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("ambiguous width") && l.Contains("narrow"))).IsTrue();
    }

    [Test]
    public async Task InputSpellcheck_ContainsExpectedLabelsAndSections()
    {
        var lines = OptionsScreenRenderer.InputSpellcheck();
        await Assert.That(lines.Any(l => l.Contains("INPUT"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("SPELLCHECK"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("local echo"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("keep per-tab drafts"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("newline key") && l.Contains("Shift+Enter"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("check spelling"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("dictionary") && l.Contains("en_US"))).IsTrue();
    }
}
