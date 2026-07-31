using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Text;

/// <summary>
/// <b>A tab in server output is drawn as spaces.</b> It used to travel the whole pipeline as a single
/// character, so everything that measures a line counted it as one cell while the terminal painted it as
/// a jump to the next tab stop — the layout computed against a width the screen does not use, which is
/// the same defect class as chrome that grows on wire data.
/// </summary>
public class TabExpansionTests
{
    private static StyledLine Line(string text) => StyledLine.FromText(text, TextStyle.Default);

    /// <summary>The claim, and the measurement that motivates it.</summary>
    [Test]
    public async Task ATabBecomesSpacesAndTheLineMeasuresWhatItPaints()
    {
        var raw = Line("a\tb");
        await Assert.That(raw.Text.Length).IsEqualTo(3).Because("this is the bug: three cells claimed");

        var expanded = StyledText.ExpandTabs(raw, 4);

        await Assert.That(expanded.Text).IsEqualTo("a    b");
        await Assert.That(expanded.Text).DoesNotContain("\t");
        await Assert.That(expanded.Text.Length).IsEqualTo(6);
    }

    [Test]
    [Arguments(0, "ab")]
    [Arguments(1, "a b")]
    [Arguments(2, "a  b")]
    [Arguments(4, "a    b")]
    [Arguments(8, "a        b")]
    public async Task TheWidthIsWhateverItIsSetTo(int width, string expected)
    {
        await Assert.That(StyledText.ExpandTabs(Line("a\tb"), width).Text).IsEqualTo(expected);
    }

    /// <summary>
    /// Fixed spaces, not tab stops. Real tabbing would align both <c>b</c>s in the same column; this
    /// deliberately does not, and the test says so out loud so nobody "fixes" it into stop-tracking.
    /// </summary>
    [Test]
    public async Task ItIsNotTabStopAlignment()
    {
        var shortRun = StyledText.ExpandTabs(Line("a\tb"), 4).Text;
        var longRun = StyledText.ExpandTabs(Line("aaaa\tb"), 4).Text;

        await Assert.That(shortRun.IndexOf('b', StringComparison.Ordinal)).IsEqualTo(5);
        await Assert.That(longRun.IndexOf('b', StringComparison.Ordinal))
            .IsEqualTo(8)
            .Because("a fixed run does not align columns, and is not meant to");
    }

    /// <summary>Every tab goes, including runs of them and ones at either end.</summary>
    [Test]
    [Arguments("\tlead", "    lead")]
    [Arguments("trail\t", "trail    ")]
    [Arguments("a\t\tb", "a        b")]
    [Arguments("\t", "    ")]
    public async Task EveryTabIsReplaced(string input, string expected)
    {
        await Assert.That(StyledText.ExpandTabs(Line(input), 4).Text).IsEqualTo(expected);
    }

    /// <summary>
    /// Styles and interactions survive. A tab inside a coloured or clickable run must not split it or
    /// drop its link — the span is rewritten, not rebuilt from plain text.
    /// </summary>
    [Test]
    public async Task StyleAndInteractionSurviveTheSubstitution()
    {
        var styled = new TextStyle(TerminalColor.FromIndex(11), TerminalColor.Default, TextAttributes.Bold);
        var line = new StyledLine([new StyledSpan("a\tb", styled)]);

        var expanded = StyledText.ExpandTabs(line, 4);

        await Assert.That(expanded.Spans.Count).IsEqualTo(1);
        await Assert.That(expanded.Spans[0].Text).IsEqualTo("a    b");
        await Assert.That(expanded.Spans[0].Style).IsEqualTo(styled);
    }

    /// <summary>
    /// A line with no tab — almost every line — is returned as it stands rather than rebuilt. This runs
    /// on every line of output from every connected session.
    /// </summary>
    [Test]
    public async Task ALineWithNoTabIsNotRebuilt()
    {
        var line = Line("nothing to do here");

        await Assert.That(ReferenceEquals(StyledText.ExpandTabs(line, 4), line)).IsTrue();
    }

    [Test]
    public async Task ANegativeWidthIsRefusedRatherThanThrowingSomethingUseless()
    {
        await Assert.That(() => StyledText.ExpandTabs(Line("a\tb"), -1))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
<<<<<<< HEAD
    /// The default is four, and the ceiling is stated rather than implied — both asserted through what
    /// they <em>do</em> rather than by comparing a constant to its own literal, which is a claim the
    /// compiler already settles and which TUnit's analyzer refuses (TUnitAssertions0005).
=======
    /// The default is four, and the ceiling is sixteen — both read through the property rather than
    /// off the constants. Comparing a <c>const</c> to a literal is a comparison the compiler folds
    /// away (TUnitAssertions0005): it pins nothing, and it warned. Going through
    /// <see cref="TextSettings"/> pins the two facts that can actually drift — that the property's
    /// default is the constant, and what the two constants are worth.
>>>>>>> origin/main
    /// </summary>
    [Test]
    public async Task TheDefaultIsFourAndTheCeilingIsSixteen()
    {
        await Assert.That(new TextSettings().TabWidth).IsEqualTo(TextSettings.DefaultTabWidth);
<<<<<<< HEAD
        await Assert.That(StyledText.ExpandTabs(Line("a\tb"), new TextSettings().TabWidth).Text)
            .IsEqualTo("a    b")
            .Because("four is the width a fresh settings object paints a tab at");
        await Assert.That(StyledText.ExpandTabs(Line("a\tb"), TextSettings.MaxTabWidth).Text.Length)
            .IsEqualTo(18)
            .Because("sixteen is the widest a tab may be set to, and it is a real width rather than a cap on paper");
=======
        await Assert.That(new TextSettings().TabWidth).IsEqualTo(4);
        await Assert.That(new TextSettings { TabWidth = TextSettings.MaxTabWidth }.TabWidth).IsEqualTo(16);
>>>>>>> origin/main
    }
}
