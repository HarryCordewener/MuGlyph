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

    /// <summary>A single options-list row: a toggle, a value row, a section header, or a spacer.</summary>
    public readonly record struct OptionRow(string Label, string? Value, bool? Toggle, string? Hint = null);

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
        return SpreadLR(" " + heading, ScreenChrome.Hints("↑↓ select · ⏎ change", Escape(fkey)), width);
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

    /// <summary>The options list itself — one line per row, in order.</summary>
    internal static List<string> BodyColumn(IReadOnlyList<OptionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return rows.Select(RenderRow).ToList();
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

    /// <summary>The F7 "Text &amp; ANSI" screen.</summary>
    internal static OptionsScreen TextAnsiScreen() => new("Text & ANSI", "F7", new List<OptionRow>
    {
        new("├ COLOUR", null, null),
        new("strip incoming ANSI colour", null, false),
        new("allow blink", null, false),
        new("underline hyperlinks", null, true),
        new(string.Empty, null, null),
        new("├ UNICODE", null, null),
        new("emoji substitution", null, true),
        new("ambiguous width", "narrow", null),
    });

    /// <summary>The F8 "Input &amp; spellcheck" screen.</summary>
    internal static OptionsScreen InputSpellcheckScreen() => new("Input & spellcheck", "F8", new List<OptionRow>
    {
        new("├ INPUT", null, null),
        new("local echo", null, true),
        new("keep per-tab drafts", null, true),
        new("newline key", "Shift+Enter", null),
        new(string.Empty, null, null),
        new("├ SPELLCHECK", null, null),
        new("check spelling", null, true),
        new("dictionary", "en_US", null),
    });

    /// <summary>The F9 "Logging" screen, reflecting a character's <see cref="LoggingSettings"/>.</summary>
    internal static OptionsScreen LoggingScreen(LoggingSettings logging)
    {
        ArgumentNullException.ThrowIfNull(logging);

        return new OptionsScreen("Logging", "F9", new List<OptionRow>
        {
            new("├ SESSION LOG", null, null),
            new("format", logging.Format.ToString(), null),
            new("directory", logging.Directory ?? "(default)", null),
            new("auto-start on connect", null, logging.Format != LogFormat.None),
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
