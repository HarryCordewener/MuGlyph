using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace SharpMUTerm.Tui;

/// <summary>
/// The chrome every full-screen settings screen (F2–F9) shares: the keyboard-hint and action-bar
/// fragments its renderer writes, and the band / rule / inset panels its view composes. The screens
/// differ in their body, not their frame, so the frame lives here — a header band, a Cancel/Save
/// action bar, and the hairlines between columns look and behave the same on all of them.
/// </summary>
internal static class ScreenChrome
{
    /// <summary>
    /// The right-hand keyboard hints of a header band: the screen's verbs, then how to close it.
    /// <paramref name="fkey"/> is the F-key that also toggles the screen (<c>F6/Esc close</c>).
    /// </summary>
    internal static string Hints(string verbs, string fkey) =>
        $"[{ScreenPalette.Label}]{verbs} · [/][{ScreenPalette.Accent}]{fkey}[/][{ScreenPalette.Label}]/[/]"
        + $"[{ScreenPalette.Accent}]Esc[/][{ScreenPalette.Label}] close [/]";

    /// <summary>
    /// The keyboard hints every screen with a list and a checkbox pane shares. Kept in one place so a
    /// screen can't advertise a key its <see cref="ScreenModel"/> doesn't actually offer.
    /// </summary>
    internal const string ListHints = "↑↓ select · ⇥ pane · Space toggle";

    /// <summary>The hints for a screen that is a single list with no second pane to ⇥ into.</summary>
    internal const string SingleListHints = "↑↓ select · Space toggle";

    /// <summary>
    /// The right-hand actions of a footer bar. <paramref name="accent"/> lets a screen with a
    /// context colour (F5's per-world accent) tint the Save chip; it defaults to the app accent.
    /// </summary>
    internal static string Actions(string? accent = null) =>
        $"[{ScreenPalette.Label}] [[Esc]] Cancel [/]  "
        + $"[{ScreenPalette.Ink} on {accent ?? ScreenPalette.Accent}] [[⏎]] Save [/] ";

    /// <summary>
    /// Draws a row as the keyboard cursor: the row's own markup on a cursor band padded out to
    /// <paramref name="width"/>, so the bar spans its pane instead of hugging the text. A row that
    /// isn't under the cursor comes back untouched.
    /// </summary>
    internal static string Cursor(string row, bool focused, int width) =>
        focused ? $"[on {ScreenPalette.CursorBg}]{MarkupText.PadVisible(row, width)}[/]" : row;

    /// <summary>A full-width one-row band — the header or the footer.</summary>
    internal static MarkupControl Band(string line, string bg) => new(new List<string> { line })
    {
        BackgroundColor = new Color(bg),
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    /// <summary>Widens a column panel to its arranged width so its content isn't hugged.</summary>
    internal static MarkupControl Stretch(MarkupControl control)
    {
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        return control;
    }

    /// <summary>
    /// The one-cell rule between two body columns. A <see cref="MarkupControl"/> with no lines measures
    /// to nothing and never paints its background, so the rule is an empty grid instead — a grid's
    /// background covers its whole arranged area, giving a full-height hairline.
    /// </summary>
    internal static IWindowControl VerticalRule()
    {
        var rule = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Column(c => c.Flex(1).Add(Filler()))
            .Build();
        rule.BackgroundColor = new Color(ScreenPalette.Rule);
        return rule;
    }

    /// <summary>An empty panel, used to hold a spacer or flex column open.</summary>
    internal static MarkupControl Filler() => new(new List<string>());

    /// <summary>Prefixes each line with a space so a column doesn't sit flush against the rule.</summary>
    internal static List<string> Indent(IEnumerable<string> lines) => lines.Select(l => " " + l).ToList();
}
