using System.Globalization;
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
    private const int LabelWidth = 28;

    /// <summary>
    /// A single options-list row: a toggle, a value row, a section header, or a spacer.
    /// <paramref name="Bind"/> is the config the checkbox writes to; a row without one still takes the
    /// cursor but Space does nothing there (the value rows, until field editing exists).
    /// </summary>
    public readonly record struct OptionRow(
        string Label, string? Value, bool? Toggle, string? Hint = null, ScreenToggle? Bind = null);

    /// <summary>One options screen: the title and F-key its chrome shows, plus the rows it lists.</summary>
    internal readonly record struct OptionsScreen(string Title, string FKey, IReadOnlyList<OptionRow> Rows);

    /// <summary>
    /// Merges every sub-block into one line list (header, options, footer). Used by the unit tests and
    /// as a width-agnostic fallback; the live view composes the same blocks into panels instead.
    /// </summary>
    public static List<string> Render(string title, string fkey, IReadOnlyList<OptionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var lines = new List<string> { HeaderLine(title, fkey, 0), string.Empty };
        lines.AddRange(BodyColumn(rows));
        lines.Add(string.Empty);
        lines.Add(FooterLine(rows, 0));
        return lines;
    }

    /// <summary>Merges a whole screen into one line list.</summary>
    internal static List<string> Render(OptionsScreen screen) => Render(screen.Title, screen.FKey, screen.Rows);

    /// <summary>
    /// The back affordance and screen title on the left, the keyboard hints right-aligned to
    /// <paramref name="width"/>.
    /// </summary>
    internal static string HeaderLine(string title, string fkey, int width)
    {
        var heading = $"[{Label}]‹ back[/]   [bold {Value}]{Escape(title)}[/]";
        return SpreadLR(" " + heading, ScreenChrome.Hints(ScreenChrome.SingleListHints, Escape(fkey)), width);
    }

    /// <summary>The action bar: how much the screen holds on the left, cancel/save on the right.</summary>
    internal static string FooterLine(IReadOnlyList<OptionRow> rows, int width)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var options = rows.Count(r => !IsSpacer(r) && !IsSection(r));
        var sections = rows.Count(IsSection);
        var context = $"[{Label}]{Plural(options, "option")}[/]";
        if (sections > 0)
        {
            context += $"[{Label}]  ·  {Plural(sections, "section")}[/]";
        }

        return SpreadLR(" " + context, ScreenChrome.Actions(), width);
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
            var line = RenderRow(row);
            if (IsSpacer(row) || IsSection(row))
            {
                lines.Add(line);
                continue;
            }

            lines.Add(ScreenChrome.Cursor(line, cursor.IsOn(0, navigable), width));
            navigable++;
        }

        return lines;
    }

    /// <summary>
    /// The screen's one navigable pane: every row that isn't a spacer or a section header, in display
    /// order, each carrying whatever config binding it was built with.
    /// </summary>
    internal static ScreenModel Model(OptionsScreen screen)
    {
        var rows = screen.Rows
            .Where(r => !IsSpacer(r) && !IsSection(r))
            .Select(r => r.Bind)
            .ToArray();
        return new ScreenModel(rows);
    }

    private static string RenderRow(OptionRow row)
    {
        if (IsSpacer(row))
        {
            return string.Empty;
        }

        if (IsSection(row))
        {
            return $"[dim]{Escape(row.Label)}[/]";
        }

        var hint = row.Hint is null ? string.Empty : $"[dim] — {Escape(row.Hint)}[/]";

        if (row.Toggle is { } toggle)
        {
            var box = toggle ? $"[{Accent}][[x]][/]" : "[dim][[ ]][/]";
            return $"{box} {Escape(row.Label)}{hint}";
        }

        var label = Escape(row.Label).PadRight(LabelWidth);
        return $"[dim]{label}[/] {Escape(row.Value ?? string.Empty)}{hint}";
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
            new("ambiguous width", settings.AmbiguousWidth, null),
        });
    }

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
            new("newline key", settings.NewlineKey, null),
            new(string.Empty, null, null),
            new("├ SPELLCHECK", null, null),
            new("check spelling", null, settings.CheckSpelling, null,
                ScreenToggle.Bind(() => settings.CheckSpelling, v => settings.CheckSpelling = v)),
            new("dictionary", settings.Dictionary, null),
        });
    }

    /// <summary>The F9 "Logging" screen, reflecting a character's <see cref="LoggingSettings"/>.</summary>
    internal static OptionsScreen LoggingScreen(LoggingSettings logging)
    {
        ArgumentNullException.ThrowIfNull(logging);

        // "Auto-start" is really the log format: off means None, on means whatever format was last
        // chosen (Plain when there isn't one). The binding's snapshot restores the *format*, not the
        // boolean, so cancelling a toggle-off puts Html back rather than downgrading it to Plain.
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
            new("format", logging.Format.ToString(), null),
            new("directory", logging.Directory ?? "(default)", null),
            new("auto-start on connect", null, logging.Format != LogFormat.None, null, autoStart),
        });
    }

    /// <summary>The F7 "Text &amp; ANSI" screen body.</summary>
    public static List<string> TextAnsi() => Render(TextAnsiScreen());

    /// <summary>The F8 "Input &amp; spellcheck" screen body.</summary>
    public static List<string> InputSpellcheck() => Render(InputSpellcheckScreen());

    /// <summary>The F9 "Logging" screen body, reflecting a character's <see cref="LoggingSettings"/>.</summary>
    public static List<string> Logging(LoggingSettings logging) => Render(LoggingScreen(logging));

    /// <summary>Counts a noun for the footer: <c>1 option</c>, <c>3 options</c>.</summary>
    private static string Plural(int count, string noun)
    {
        var n = count.ToString(CultureInfo.InvariantCulture);
        return count == 1 ? $"{n} {noun}" : $"{n} {noun}s";
    }
}
