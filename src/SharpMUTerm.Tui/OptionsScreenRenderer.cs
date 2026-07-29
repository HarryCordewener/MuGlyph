using SharpMUTerm.Core.Configuration;
using static SharpMUTerm.Tui.MarkupText;
using static SharpMUTerm.Tui.ScreenPalette;

namespace SharpMUTerm.Tui;

/// <summary>
/// Produces the markup sub-blocks shared by the F7 (Text &amp; ANSI), F8 (Input &amp; spellcheck), and
/// F9 (Logging) screens — the header band, the options list (toggle/value rows grouped under dim
/// section headers), and the footer action bar. The three screens differ only in their title, F-key,
/// and rows, so the blocks take an <see cref="OptionsScreen"/> rather than being written per screen.
/// <see cref="OptionsScreenView"/> composes them into a real panel for the live/snapshot view;
/// <see cref="Render(string, string, IReadOnlyList{OptionRow})"/> merges the same blocks into a single
/// line list for the unit tests. Pure so every block is testable.
/// </summary>
internal static class OptionsScreenRenderer
{
    /// <summary>Where a row's value starts, measured from the left edge of the list.</summary>
    private const int LabelWidth = 28;

    /// <summary>
    /// The checkbox column every row reserves, whether or not it has one: <c>"[[x]] "</c>. A value row
    /// indents past it rather than starting at the margin, so the labels of the two kinds of row line
    /// up in one column and the checkboxes read as a column of their own instead of as a ragged edge.
    /// </summary>
    private const int AffordanceWidth = 4;

    /// <summary>
    /// A single options-list row: a toggle, a value row, both at once, a section header, or a spacer.
    /// <paramref name="Bind"/> is the config the checkbox writes to; <paramref name="Edit"/> is the
    /// config the value writes to, which is what makes a value row activatable with ⏎. A row with
    /// neither still takes the cursor but nothing happens there.
    /// <para>
    /// A row carrying both is F9's log format: Space starts and stops logging, ⏎ picks the format it
    /// writes. They were two rows — a <c>format</c> value and an <c>auto-start on connect</c> checkbox
    /// whose state was simply <c>format != None</c> — which is two controls over one stored value, and
    /// nothing on screen said they were the same setting. The keypad's bindings are the same shape
    /// (Space enables the macro, ⏎ edits its command), so <see cref="ScreenRow"/> already supports it.
    /// </para>
    /// </summary>
    public readonly record struct OptionRow(
        string Label,
        string? Value,
        bool? Toggle,
        string? Hint = null,
        ScreenToggle? Bind = null,
        ScreenField? Edit = null);

    /// <summary>One options screen: the title and F-key its chrome shows, plus the rows it lists.</summary>
    internal readonly record struct OptionsScreen(string Title, string FKey, IReadOnlyList<OptionRow> Rows);

    /// <summary>
    /// Merges every sub-block into one line list (header, options, footer). Used by the unit tests and
    /// as a width-agnostic fallback; the live view composes the same blocks into panels instead.
    /// </summary>
    public static List<string> Render(string title, string fkey, IReadOnlyList<OptionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var screen = new OptionsScreen(title, fkey, rows);
        var lines = new List<string> { HeaderLine(title, fkey, 0, Model(screen)), string.Empty };
        lines.AddRange(BodyColumn(rows));
        lines.Add(string.Empty);
        lines.Add(FooterLine(rows, 0));
        return lines;
    }

    /// <summary>Merges a whole screen into one line list.</summary>
    internal static List<string> Render(OptionsScreen screen) => Render(screen.Title, screen.FKey, screen.Rows);

    /// <summary>
    /// The screen title on the left, the keyboard hints right-aligned to <paramref name="width"/>. The
    /// hints are derived from <paramref name="model"/> and <paramref name="focus"/> rather than written
    /// here, so the header cannot advertise an edit the screen doesn't offer; called without them it
    /// describes a screen that only navigates.
    /// <para>
    /// There is deliberately no <c>‹ back</c> affordance. These three screens were the only ones that
    /// drew one, and it pointed nowhere: there is no navigation stack behind a settings screen, Esc
    /// closes it, and the header already says so two columns to the right.
    /// </para>
    /// </summary>
    internal static string HeaderLine(
        string title, string fkey, int width, ScreenModel? model = null, ScreenFocus? focus = null)
    {
        var heading = $"[bold {Value}]{Escape(title)}[/]";
        var hints = ScreenChrome.Hints(
            ScreenChrome.SingleListHints,
            Escape(fkey),
            model?.HasEditableRow ?? false,
            focus);
        return SpreadLR(" " + heading, hints, width);
    }

    /// <summary>
    /// The action bar: where the cursor is in the options list on the left, cancel/save on the right.
    /// It used to be an inventory (<c>5 options · 2 sections</c>); the section count was noise — the
    /// headings are on screen and counting them tells nobody anything — and the option count answered a
    /// different question from every other screen's footer. It now names the cursor's position and the
    /// section it is standing in, which is F2's <c>trigger 1/4 · set Comms</c> in this screen's nouns.
    /// The selected row comes from <paramref name="focus"/>, the only thing that knows it.
    /// </summary>
    internal static string FooterLine(IReadOnlyList<OptionRow> rows, int width, ScreenFocus? focus = null)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var options = rows.Count(r => !IsSpacer(r) && !IsSection(r));
        var context = string.Empty;
        if (options > 0)
        {
            var selected = Math.Clamp(focus?.Pane == 0 ? focus.Value.Index : 0, 0, options - 1);
            context = ScreenChrome.Context(
                ScreenChrome.Position("option", selected, options), SectionOf(rows, selected));
        }

        return SpreadLR(" " + context, ScreenChrome.Actions(focus: focus), width);
    }

    /// <summary>
    /// The section heading the nth navigable row sits under, without the branch glyph that marks it as
    /// one — or null on a screen whose rows aren't grouped.
    /// </summary>
    private static string? SectionOf(IReadOnlyList<OptionRow> rows, int selected)
    {
        string? section = null;
        var navigable = 0;
        foreach (var row in rows)
        {
            if (IsSection(row))
            {
                section = Escape(row.Label[2..]);
                continue;
            }

            if (IsSpacer(row))
            {
                continue;
            }

            if (navigable++ == selected)
            {
                return section;
            }
        }

        return null;
    }

    /// <summary>
    /// The options list itself — one line per row, in order. The row under the keyboard cursor is
    /// drawn on a cursor bar padded to <paramref name="width"/>; spacers and section headers are
    /// skipped when counting, so the cursor index matches <see cref="Model"/>'s row order.
    /// </summary>
    internal static List<string> BodyColumn(
        IReadOnlyList<OptionRow> rows, ScreenFocus? focus = null, int width = 0)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var cursor = focus ?? ScreenFocus.None;
        var lines = new List<string>(rows.Count);
        var navigable = 0;
        foreach (var row in rows)
        {
            if (IsSpacer(row) || IsSection(row))
            {
                lines.Add(RenderRow(row, null));
                continue;
            }

            var line = RenderRow(row, cursor.EditOn(0, navigable, 0));
            lines.Add(ScreenChrome.Cursor(line, cursor.IsOn(0, navigable), width));
            navigable++;
        }

        return lines;
    }

    /// <summary>
    /// The screen's one navigable pane: every row that isn't a spacer or a section header, in display
    /// order, each carrying whatever config bindings it was built with — the checkbox Space flips and
    /// the value ⏎ opens.
    /// </summary>
    internal static ScreenModel Model(OptionsScreen screen)
    {
        var rows = screen.Rows
            .Where(r => !IsSpacer(r) && !IsSection(r))
            .Select(r => r.Edit is { } field ? new ScreenRow(r.Bind, new[] { field }) : new ScreenRow(r.Bind))
            .ToArray();
        return new ScreenModel(rows);
    }

    /// <summary>
    /// One row: its checkbox column (a box, or the blank that keeps the labels in one column), its
    /// label, the value it holds if it holds one, and its hint. A row can carry both a checkbox and a
    /// value, which is how F9 draws one setting as one row.
    /// </summary>
    private static string RenderRow(OptionRow row, ScreenFieldEdit? edit)
    {
        if (IsSpacer(row))
        {
            return string.Empty;
        }

        if (IsSection(row))
        {
            return $"[dim]{Escape(row.Label)}[/]";
        }

        var box = row.Toggle switch
        {
            true => $"[{Accent}][[x]][/] ",
            false => "[dim][[ ]][/] ",
            null => new string(' ', AffordanceWidth),
        };

        var hint = row.Hint is null ? string.Empty : $"[dim] — {Escape(row.Hint)}[/]";
        var hasValue = row.Value is not null || edit is not null;
        var label = Escape(row.Label);
        // A checkbox row's label *is* its content, so it keeps the primary ink; a label/value row's
        // label is the secondary half of a pair, so it dims and lets the value carry the weight.
        if (!hasValue)
        {
            return $"{box}{label}{hint}";
        }

        var value = ScreenChrome.Field(Escape(row.Value ?? string.Empty), edit);
        return $"{box}[dim]{label.PadRight(LabelWidth - AffordanceWidth)}[/] {value}{hint}";
    }

    /// <summary>A blank separator carrying no label, value, or toggle.</summary>
    private static bool IsSpacer(OptionRow row) =>
        row.Label.Length == 0 && row.Value is null && row.Toggle is null;

    /// <summary>A dim group heading, marked by the branch glyph the screens prefix them with.</summary>
    private static bool IsSection(OptionRow row) => row.Label.StartsWith("├ ", StringComparison.Ordinal);

    /// <summary>
    /// The F7 "Text &amp; ANSI" screen, reflecting — and writing back to — the app's
    /// <see cref="TextSettings"/>. Called without arguments it projects the defaults, which is what
    /// the unit tests and the width-agnostic fallback want.
    /// </summary>
    internal static OptionsScreen TextAnsiScreen(TextSettings? text = null)
    {
        var settings = text ?? new TextSettings();

        return new OptionsScreen("Text & ANSI", "F7", new List<OptionRow>
        {
            new("├ COLOUR", null, null),
            new("strip incoming ANSI colour", null, settings.StripIncomingColour, null,
                ScreenToggle.Bind(() => settings.StripIncomingColour, v => settings.StripIncomingColour = v)),
            new("allow blink", null, settings.AllowBlink, null,
                ScreenToggle.Bind(() => settings.AllowBlink, v => settings.AllowBlink = v)),
            new("underline hyperlinks", null, settings.UnderlineHyperlinks, null,
                ScreenToggle.Bind(() => settings.UnderlineHyperlinks, v => settings.UnderlineHyperlinks = v)),
            new(string.Empty, null, null),
            new("├ UNICODE", null, null),
            new("emoji substitution", null, settings.EmojiSubstitution, null,
                ScreenToggle.Bind(() => settings.EmojiSubstitution, v => settings.EmojiSubstitution = v)),
            new("ambiguous width", settings.AmbiguousWidth, null, null, null,
                ScreenField.Choice(
                    "ambiguous width",
                    () => settings.AmbiguousWidth,
                    v => settings.AmbiguousWidth = v,
                    AmbiguousWidths)),
        });
    }

    /// <summary>
    /// How East Asian ambiguous-width characters may be measured. A fixed set rather than free text:
    /// the measurer only knows these two, and a typo would silently fall back to one of them.
    /// </summary>
    private static readonly string[] AmbiguousWidths = { "narrow", "wide" };

    /// <summary>
    /// The F8 "Input &amp; spellcheck" screen, reflecting — and writing back to — the app's
    /// <see cref="InputSettings"/>.
    /// </summary>
    internal static OptionsScreen InputSpellcheckScreen(InputSettings? input = null)
    {
        var settings = input ?? new InputSettings();

        return new OptionsScreen("Input & spellcheck", "F8", new List<OptionRow>
        {
            new("├ INPUT", null, null),
            new("local echo", null, settings.LocalEcho, null,
                ScreenToggle.Bind(() => settings.LocalEcho, v => settings.LocalEcho = v)),
            new("keep per-tab drafts", null, settings.KeepDrafts, null,
                ScreenToggle.Bind(() => settings.KeepDrafts, v => settings.KeepDrafts = v)),
            new("newline key", settings.NewlineKey, null, null, null,
                ScreenField.Text("newline key", () => settings.NewlineKey, v => settings.NewlineKey = v)),
            new(string.Empty, null, null),
            new("├ SPELLCHECK", null, null),
            new("check spelling", null, settings.CheckSpelling, null,
                ScreenToggle.Bind(() => settings.CheckSpelling, v => settings.CheckSpelling = v)),
            new("dictionary", settings.Dictionary, null, null, null,
                ScreenField.Text("dictionary", () => settings.Dictionary, v => settings.Dictionary = v)),
        });
    }

    /// <summary>
    /// The F9 "Logging" screen, reflecting a character's <see cref="LoggingSettings"/>. Its two rows
    /// are its two settings: the log format — whose checkbox starts and stops logging, because
    /// <see cref="LogFormat.None"/> *is* "off" — and where the file goes.
    /// <para>
    /// The format and the auto-start checkbox used to be separate rows over the same stored value, and
    /// nothing said so: setting the format to <c>None</c> silently unchecked a box three lines down.
    /// One row, one value, two keys — Space for on/off, ⏎ for which format — leaves nothing derived to
    /// keep in sync.
    /// </para>
    /// </summary>
    internal static OptionsScreen LoggingScreen(LoggingSettings logging)
    {
        ArgumentNullException.ThrowIfNull(logging);

        // Off means None, on means whatever format was last chosen (Plain when there isn't one). The
        // binding's snapshot restores the *format*, not the boolean, so cancelling a toggle-off puts
        // Html back rather than downgrading it to Plain.
        var chosen = logging.Format == LogFormat.None ? LogFormat.Plain : logging.Format;
        var autoStart = new ScreenToggle(
            () => logging.Format != LogFormat.None,
            () => logging.Format = logging.Format == LogFormat.None ? chosen : LogFormat.None,
            () =>
            {
                var previous = logging.Format;
                return () => logging.Format = previous;
            });

        return new OptionsScreen("Logging", "F9", new List<OptionRow>
        {
            new("├ SESSION LOG", null, null),
            new("format", logging.Format.ToString(), logging.Format != LogFormat.None,
                "auto-start on connect",
                autoStart,
                ScreenField.Enumeration("format", () => logging.Format, v => logging.Format = v)),
            new("directory", logging.Directory ?? "(default)", null, null, null,
                ScreenField.Optional("directory", () => logging.Directory, v => logging.Directory = v)),
        });
    }

    /// <summary>The F7 "Text &amp; ANSI" screen body.</summary>
    public static List<string> TextAnsi() => Render(TextAnsiScreen());

    /// <summary>The F8 "Input &amp; spellcheck" screen body.</summary>
    public static List<string> InputSpellcheck() => Render(InputSpellcheckScreen());

    /// <summary>The F9 "Logging" screen body, reflecting a character's <see cref="LoggingSettings"/>.</summary>
    public static List<string> Logging(LoggingSettings logging) => Render(LoggingScreen(logging));
}
