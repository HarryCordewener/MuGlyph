using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What each kind of editable value will and won't accept, and what it puts back on undo. Validation
/// is the guarantee that an invalid value never reaches config, so it is asserted per field kind
/// rather than only through the screens that happen to use one.
/// </summary>
public class ScreenFieldTests
{
    /// <summary>Applies a value the way a screen does, returning the rejection reason (null = taken).</summary>
    private static string? Apply(ScreenField field, string value) => new ScreenEdits().Apply(field, value);

    [Test]
    public async Task Text_RefusesBlankAndTrimsWhatItTakes()
    {
        var host = "aardmud.org";
        var field = ScreenField.Text("host", () => host, v => host = v);

        await Assert.That(Apply(field, "   ")).IsNotNull();
        await Assert.That(host).IsEqualTo("aardmud.org");

        await Assert.That(Apply(field, "  example.net  ")).IsNull();
        await Assert.That(host).IsEqualTo("example.net");
    }

    [Test]
    public async Task Optional_TakesBlankAsUnset()
    {
        string? directory = "/logs";
        var field = ScreenField.Optional("directory", () => directory, v => directory = v);

        await Assert.That(field.Get()).IsEqualTo("/logs");
        await Assert.That(Apply(field, "   ")).IsNull();
        await Assert.That(directory).IsNull();
        await Assert.That(field.Get()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Integer_RefusesNonNumbersAndAnythingOutOfRange()
    {
        var port = 4000;
        var field = ScreenField.Integer("port", () => port, v => port = v, 1, 65535);

        await Assert.That(Apply(field, "http")).IsNotNull();
        await Assert.That(Apply(field, "0")).IsNotNull();
        await Assert.That(Apply(field, "65536")).IsNotNull();
        await Assert.That(Apply(field, "4000.5")).IsNotNull();
        await Assert.That(port).IsEqualTo(4000);

        await Assert.That(Apply(field, " 4201 ")).IsNull();
        await Assert.That(port).IsEqualTo(4201);
    }

    [Test]
    public async Task Integer_SaysWhatItWanted()
    {
        var port = 4000;
        var field = ScreenField.Integer("port", () => port, v => port = v, 1, 65535);

        await Assert.That(Apply(field, "-1")).IsEqualTo("port must be a whole number 1-65535");
    }

    [Test]
    public async Task Number_TakesFractionsInsideItsRangeAndNothingAtOrBelowZero()
    {
        var seconds = 30d;
        var field = ScreenField.Number("interval", () => seconds, v => seconds = v, 0.1, 86400);

        await Assert.That(Apply(field, "0")).IsNotNull();
        await Assert.That(Apply(field, "-5")).IsNotNull();
        await Assert.That(Apply(field, "soon")).IsNotNull();
        await Assert.That(seconds).IsEqualTo(30d);

        await Assert.That(Apply(field, "5.5")).IsNull();
        await Assert.That(seconds).IsEqualTo(5.5);
        await Assert.That(field.Get()).IsEqualTo("5.5");
    }

    [Test]
    public async Task Pattern_RefusesARegexThatWouldNotCompile()
    {
        var pattern = "tells you";
        var field = ScreenField.Pattern("match pattern", () => pattern, v => pattern = v);

        var rejected = Apply(field, "(unclosed");
        await Assert.That(rejected).IsNotNull();
        await Assert.That(rejected!).Contains("not a valid regex");
        await Assert.That(pattern).IsEqualTo("tells you");

        await Assert.That(Apply(field, @"^(\w+) tells you")).IsNull();
        await Assert.That(pattern).IsEqualTo(@"^(\w+) tells you");
    }

    [Test]
    public async Task Choice_MatchesCaseInsensitivelyAndStoresTheCanonicalName()
    {
        var width = "narrow";
        var field = ScreenField.Choice("ambiguous width", () => width, v => width = v, new[] { "narrow", "wide" });

        await Assert.That(Apply(field, "double")).IsNotNull();
        await Assert.That(width).IsEqualTo("narrow");

        await Assert.That(Apply(field, "WIDE")).IsNull();
        await Assert.That(width).IsEqualTo("wide");
    }

    [Test]
    public async Task Enumeration_ParsesByNameAndCyclesWithBothDirections()
    {
        var format = LogFormat.Plain;
        var field = ScreenField.Enumeration("format", () => format, v => format = v);

        await Assert.That(Apply(field, "Verbose")).IsNotNull();
        await Assert.That(format).IsEqualTo(LogFormat.Plain);

        await Assert.That(Apply(field, "html")).IsNull();
        await Assert.That(format).IsEqualTo(LogFormat.Html);

        await Assert.That(field.Cycle("Html", 1)).IsEqualTo("Both");
        await Assert.That(field.Cycle("None", -1)).IsEqualTo("Both"); // wraps at the start

        // A half-typed buffer used to start the cycle over at the first choice, from before there was a
        // list on screen to start over *in*. ↑↓ now walk exactly the entries the dropdown is showing, and
        // "Ht" is showing only "Html" — so ↓ takes the one match rather than jumping past it to "None".
        await Assert.That(ScreenField.Matching(field.Choices, "Ht")).IsEquivalentTo(new[] { "Html" });
        await Assert.That(field.Cycle("Ht", 1)).IsEqualTo("Html");
        await Assert.That(field.Cycle("Ht", -1)).IsEqualTo("Html");

        // And a buffer matching nothing has nowhere to step, so the keystroke leaves it alone rather
        // than overwriting what is being typed.
        await Assert.That(field.Cycle("Verbose", 1)).IsNull();
    }

    [Test]
    public async Task PlainFields_HaveNothingToCycle()
    {
        var host = "aardmud.org";
        var field = ScreenField.Text("host", () => host, v => host = v);

        await Assert.That(field.Cycle(host, 1)).IsNull();
    }

    [Test]
    public async Task Lines_EditsAMultiLineValueOnOneRowAndPutsTheBreaksBack()
    {
        var substitution = "kill $1\nsay done";
        var field = ScreenField.Lines("expansion", () => substitution, v => substitution = v);

        await Assert.That(field.Get()).IsEqualTo(@"kill $1\nsay done");

        await Assert.That(Apply(field, @"look\nsay hi")).IsNull();
        await Assert.That(substitution).IsEqualTo("look\nsay hi");
    }

    [Test]
    public async Task Lines_RoundTripsALiteralBackslash()
    {
        var substitution = @"say a\b";
        var field = ScreenField.Lines("expansion", () => substitution, v => substitution = v);

        var shown = field.Get();
        await Assert.That(shown).IsEqualTo(@"say a\\b");

        await Assert.That(Apply(field, shown)).IsNull();
        await Assert.That(substitution).IsEqualTo(@"say a\b");
    }

    /// <summary>
    /// A committed value is the typed value, not the text it was edited as: a port becomes an
    /// <c>int</c> and a log format becomes the enum. It is kept — a field has no undo, because it needs
    /// none: the buffer is the escape hatch, and Esc drops it before it ever reaches config (see
    /// <see cref="ScreenEdits"/>). This test replaced one asserting that Revert put both back, which was
    /// the behaviour behind "I changed the address and it went back to the old setting".
    /// </summary>
    [Test]
    public async Task ACommittedValueIsTheTypedValueAndSurvivesTheScreen()
    {
        var port = 4000;
        var format = LogFormat.Html;
        var edits = new ScreenEdits();

        edits.Apply(ScreenField.Integer("port", () => port, v => port = v, 1, 65535), "4201");
        edits.Apply(ScreenField.Enumeration("format", () => format, v => format = v), "None");
        await Assert.That(port).IsEqualTo(4201);
        await Assert.That(format).IsEqualTo(LogFormat.None);

        edits.Revert(); // whatever the screen does on the way out, a committed field is not undone

        await Assert.That(port).IsEqualTo(4201);
        await Assert.That(format).IsEqualTo(LogFormat.None);
    }

    [Test]
    public async Task ARejectedValueChangesNothing()
    {
        var port = 4000;
        var saves = 0;
        var edits = new ScreenEdits(() => saves++);

        edits.Apply(ScreenField.Integer("port", () => port, v => port = v, 1, 65535), "nope");

        await Assert.That(port).IsEqualTo(4000);
        await Assert.That(saves).IsEqualTo(0);
        await Assert.That(edits.HasDeletions).IsFalse();
    }
}
