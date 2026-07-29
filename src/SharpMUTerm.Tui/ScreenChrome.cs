using System.Globalization;
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
    /// <para>
    /// <paramref name="editable"/> comes from the screen's <see cref="ScreenModel"/>, never from the
    /// screen itself: a header may only claim ⏎ opens an editor when a row actually offers one. While
    /// an edit *is* open the hints change wholesale — Esc no longer closes the screen, it abandons the
    /// buffer, and saying otherwise would be the same lie in the other direction.
    /// </para>
    /// </summary>
    internal static string Hints(
        string verbs, string fkey, bool editable = false, ScreenFocus? focus = null, bool removable = false)
    {
        if (focus?.Edit is { } edit)
        {
            var editing = EditingHints
                + (edit.HasChoices ? ChoiceHint : string.Empty)
                + (edit.RowFields > 1 ? NextFieldHint : string.Empty);
            return $"[{ScreenPalette.Label}]{editing} · [/][{ScreenPalette.Accent}]{fkey}[/]"
                + $"[{ScreenPalette.Label}] close [/]";
        }

        var all = (editable ? verbs + EditHint : verbs) + (removable ? DeleteHint : string.Empty);
        return $"[{ScreenPalette.Label}]{all} · [/][{ScreenPalette.Accent}]{fkey}[/][{ScreenPalette.Label}]/[/]"
            + $"[{ScreenPalette.Accent}]Esc[/][{ScreenPalette.Label}] close [/]";
    }

    /// <summary>
    /// The keyboard hints every screen with a list and a checkbox pane shares. Kept in one place so a
    /// screen can't advertise a key its <see cref="ScreenModel"/> doesn't actually offer.
    /// </summary>
    internal const string ListHints = "↑↓ select · ⇥ pane · Space toggle";

    /// <summary>The hints for a screen that is a single list with no second pane to ⇥ into.</summary>
    internal const string SingleListHints = "↑↓ select · Space toggle";

    /// <summary>
    /// What a screen adds to its hints when — and only when — its model offers a row ⏎ can open.
    /// </summary>
    internal const string EditHint = " · ⏎ edit";

    /// <summary>
    /// What a screen adds to its hints when — and only when — a pane offers a way to remove a row. It
    /// exists because reaching a <c>[[- del]]</c> row with ↑↓ means walking the cursor past the whole
    /// list, and the only key that stepped over it (End) was advertised nowhere. Delete acts on the row
    /// the cursor is already on, which is the row the eye is on, and this is where the screen says so.
    /// </summary>
    internal const string DeleteHint = " · Del remove";

    /// <summary>The hints that replace a screen's own while a field edit is open.</summary>
    internal const string EditingHints = "⏎ commit · Esc revert";

    /// <summary>Added to <see cref="EditingHints"/> only when the row has another field to step to.</summary>
    internal const string NextFieldHint = " · ⇥ next field";

    /// <summary>
    /// Added to <see cref="EditingHints"/> only when the open field is one of a fixed set of values —
    /// a route, an encoding, a highlight colour — because only those have anything for ↑↓ to step
    /// through. A free-text field would be claiming a key that does nothing.
    /// </summary>
    internal const string ChoiceHint = " · ↑↓ choose";

    /// <summary>What the footer's Esc chip does while the screen is navigating.</summary>
    internal const string CancelAction = "[[Esc]] Cancel";

    /// <summary>What the footer's ⏎ chip does while the screen is navigating.</summary>
    internal const string SaveAction = "[[⏎]] Save";

    /// <summary>What the footer's Esc chip does while a field edit is open.</summary>
    internal const string RevertAction = "[[Esc]] Revert";

    /// <summary>What the footer's ⏎ chip does while a field edit is open.</summary>
    internal const string CommitAction = "[[⏎]] Commit";

    /// <summary>
    /// The right-hand actions of a footer bar. <paramref name="accent"/> lets a screen with a
    /// context colour (F5's per-world accent) tint the ⏎ chip; it defaults to the app accent.
    /// <para>
    /// <paramref name="focus"/> is read for the same reason <see cref="Hints"/> reads it: while a
    /// field edit is open, ⏎ commits that field and Esc abandons its buffer — neither closes the
    /// screen — so an action bar still offering <c>Save</c> and <c>Cancel</c> names two keys that do
    /// something else at that moment. The footer is the more visible of the two claims, so it has to
    /// change with the header rather than being left behind.
    /// </para>
    /// </summary>
    internal static string Actions(string? accent = null, ScreenFocus? focus = null)
    {
        var editing = focus?.Edit is not null;
        var escape = editing ? RevertAction : CancelAction;
        var enter = editing ? CommitAction : SaveAction;

        return $"[{ScreenPalette.Label}] {escape} [/]  "
            + $"[{ScreenPalette.Ink} on {accent ?? ScreenPalette.Accent}] {enter} [/] ";
    }

    /// <summary>
    /// Draws a row as the keyboard cursor: the row's own markup on a cursor band padded out to
    /// <paramref name="width"/>, so the bar spans its pane instead of hugging the text. A row that
    /// isn't under the cursor comes back untouched.
    /// </summary>
    internal static string Cursor(string row, bool focused, int width) =>
        focused ? $"[on {ScreenPalette.CursorBg}]{MarkupText.PadVisible(row, width)}[/]" : row;

    /// <summary>
    /// Draws a row's editable value: its committed text in a field well when nothing is being typed,
    /// or — when <paramref name="edit"/> is the open edit for that field — the buffer in that same
    /// well, a block caret sitting inside it, and the reason the last commit was refused. Every screen
    /// draws fields, so the affordance lives here rather than being re-invented (and drifting) per
    /// renderer.
    /// <para>
    /// The resting well is the whole point of <see cref="ReadOnly"/>'s existence: until it was drawn,
    /// <c>host  aetherfall.mux</c> and <c>security  TLS on · certs strict</c> were the same row to look
    /// at, and the only way to find out which one the keyboard could change was to walk the cursor into
    /// it. A screen may not advertise a key its model doesn't offer; a row may not advertise an editor
    /// it hasn't got, which is the same rule one level down.
    /// </para>
    /// <para>
    /// <paramref name="display"/> is already markup, because a screen decides for itself how a
    /// committed value reads (a null log directory shows as <c>(default)</c>); the buffer is escaped
    /// here, since what has been typed is raw text.
    /// </para>
    /// </summary>
    internal static string Field(string display, ScreenFieldEdit? edit)
    {
        if (edit is not { } open)
        {
            return Well(display);
        }

        var caret = Math.Clamp(open.Caret, 0, open.Text.Length);
        var before = MarkupText.Escape(open.Text[..caret]);
        var under = caret < open.Text.Length ? MarkupText.Escape(open.Text[caret].ToString()) : " ";
        var after = caret < open.Text.Length ? MarkupText.Escape(open.Text[(caret + 1)..]) : string.Empty;

        var buffer = $"[{ScreenPalette.Value} on {ScreenPalette.FieldBg}]{before}[/]"
            + $"[{ScreenPalette.Ink} on {ScreenPalette.Accent}]{under}[/]"
            + $"[{ScreenPalette.Value} on {ScreenPalette.FieldBg}]{after} [/]";

        return open.Error is { } error
            ? $"{buffer}  [{ScreenPalette.Warn}]▲ {MarkupText.Escape(error)}[/]"
            : buffer;
    }

    /// <summary>
    /// The resting field well: a value's own markup on the recessed input background, with a trailing
    /// cell so the well is a visible box rather than a tint hugging the glyphs. The trailing cell is
    /// where the caret goes the moment ⏎ opens the field, so the well doesn't jump sideways under it.
    /// </summary>
    private static string Well(string display) => $"[on {ScreenPalette.FieldBg}]{display} [/]";

    /// <summary>
    /// Draws a value the keyboard cannot change where it is drawn — a world's TLS/certificate line, a
    /// character's password or session state, a numpad cell mirroring a binding elsewhere. It gets the
    /// muted ink and, decisively, *no* field well, which is what tells it apart from an editable value
    /// at rest and without focus. The pair of them is one rule with one implementation: a well means
    /// "you can change this here", its absence means "you cannot".
    /// <para>
    /// The rule is scoped to rows that read <c>label   value</c>, which is where the ambiguity lives. A
    /// checkbox and a radio group already carry an affordance of their own and are left alone.
    /// </para>
    /// </summary>
    internal static string ReadOnly(string text) => $"[{ScreenPalette.Muted}]{MarkupText.Escape(text)}[/]";

    /// <summary>
    /// Draws a pane's button rows, appended after its list. The rows come *from* the pane's own
    /// <see cref="ScreenButton"/>s rather than being written out again per screen, so the label the
    /// cursor lands on and the command ⏎ runs cannot drift apart — and every screen paints them the
    /// same, which is the whole reason this lives here rather than in five renderers.
    /// <para>
    /// A button that builds gets the accent and stands alone; one that acts on the selected row is drawn
    /// in the label ink and *names its victim* (<c>[[- del]] Aetherfall</c>), because the cursor has to
    /// leave the list to reach the button and a destructive key whose target is off-screen is exactly
    /// the surprise these screens must not spring.
    /// </para>
    /// </summary>
    /// <param name="buttons">The pane's button rows, in the order the model appends them.</param>
    /// <param name="cursor">Where the keyboard is, so the focused button gets the cursor bar.</param>
    /// <param name="pane">Which pane these belong to.</param>
    /// <param name="firstIndex">The pane row index of the first button — i.e. the list's length.</param>
    /// <param name="width">How wide the cursor bar runs, matching the pane's other rows.</param>
    internal static List<string> Buttons(
        IReadOnlyList<ScreenRow> buttons, ScreenFocus cursor, int pane, int firstIndex, int width)
    {
        ArgumentNullException.ThrowIfNull(buttons);

        var lines = new List<string>(buttons.Count);
        for (var i = 0; i < buttons.Count; i++)
        {
            if (buttons[i].Button is not { } button)
            {
                continue;
            }

            var ink = button.Kind == ScreenButtonKind.Add ? ScreenPalette.Accent : ScreenPalette.Label;
            var row = $"[{ink}][[{MarkupText.Escape(button.Label)}]][/]";
            if (button.Target is { } target)
            {
                row += $" [{ScreenPalette.Value}]{MarkupText.Escape(target)}[/]";
            }

            lines.Add(Cursor(row, cursor.IsOn(pane, firstIndex + i), width));
        }

        return lines;
    }

    /// <summary>
    /// Where the cursor is within one of a screen's lists — <c>trigger 1/4</c>, <c>world 2/2</c>. Every
    /// footer's context line opens with one of these, so the eight screens answer the same question in
    /// the same words instead of each reporting whatever its author found interesting (F9 used to count
    /// its own section headers).
    /// </summary>
    internal static string Position(string noun, int index, int count) =>
        $"{noun} {(index + 1).ToString(CultureInfo.InvariantCulture)}"
        + $"/{count.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// A footer's context line: a <see cref="Position"/>, then whatever identifies the thing it points
    /// at (the set a trigger belongs to, the section an option sits under, the name a binding carries).
    /// Null and empty parts are dropped, so a screen with nothing selected renders an empty context
    /// rather than a stranded separator.
    /// </summary>
    internal static string Context(params string?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var present = parts.Where(p => !string.IsNullOrEmpty(p));
        var joined = string.Join("  ·  ", present);
        return joined.Length == 0 ? string.Empty : $"[{ScreenPalette.Label}]{joined}[/]";
    }

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
