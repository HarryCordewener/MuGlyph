using MuClient.Core.Configuration;
using MuClient.Tui;

namespace MuClient.Tui.Tests;

public class OptionsScreenRendererTests
{
    [Test]
    public async Task Render_ToggleRow_TrueRendersCheckedBox()
    {
        var rows = new[] { new OptionsScreenRenderer.OptionRow("underline hyperlinks", null, true) };
        var lines = OptionsScreenRenderer.Render("Text & ANSI", "F7", rows);
        var row = lines.Single(l => l.Contains("underline hyperlinks"));
        await Assert.That(row).Contains("[x]");
    }

    [Test]
    public async Task Render_ToggleRow_FalseRendersUncheckedBox()
    {
        var rows = new[] { new OptionsScreenRenderer.OptionRow("strip incoming ANSI colour", null, false) };
        var lines = OptionsScreenRenderer.Render("Text & ANSI", "F7", rows);
        var row = lines.Single(l => l.Contains("strip incoming ANSI colour"));
        await Assert.That(row).Contains("[ ]");
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

    [Test]
    public async Task Render_HeaderAndFooter_MatchPattern()
    {
        var lines = OptionsScreenRenderer.Render("Logging", "F9", Array.Empty<OptionsScreenRenderer.OptionRow>());
        await Assert.That(lines[0]).Contains("‹ back");
        await Assert.That(lines[0]).Contains("Logging");
        await Assert.That(lines[0]).Contains("F9");
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

    [Test]
    public async Task Logging_ReflectsPassedSettings()
    {
        var logging = new LoggingSettings { Format = LogFormat.Html, Directory = "/var/log/mu" };
        var lines = OptionsScreenRenderer.Logging(logging);
        await Assert.That(lines.Any(l => l.Contains("format") && l.Contains("Html"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("directory") && l.Contains("/var/log/mu"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("auto-start on connect") && l.Contains("[x]"))).IsTrue();
    }

    [Test]
    public async Task Logging_NoneFormat_AutoStartUnchecked_AndUsesDefaultDirectoryText()
    {
        var logging = new LoggingSettings { Format = LogFormat.None, Directory = null };
        var lines = OptionsScreenRenderer.Logging(logging);
        await Assert.That(lines.Any(l => l.Contains("directory") && l.Contains("(default)"))).IsTrue();
        var autoStart = lines.Single(l => l.Contains("auto-start on connect"));
        await Assert.That(autoStart).Contains("[ ]");
    }
}
