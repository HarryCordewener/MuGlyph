using MuClient.Core.Configuration;

namespace MuClient.Tui;

/// <summary>
/// Renders the shared options-list body used by the F7 (Text &amp; ANSI), F8 (Input &amp;
/// spellcheck), and F9 (Logging) screens: a header, toggle/value rows grouped under dim section
/// headers, and a footer. Pure so the panel is unit-testable; the modal host just displays what
/// this produces.
/// </summary>
internal static class OptionsScreenRenderer
{
    private const string Accent = "#00f5b7";
    private const int LabelWidth = 28;

    /// <summary>A single options-list row: a toggle, a value row, a section header, or a spacer.</summary>
    public readonly record struct OptionRow(string Label, string? Value, bool? Toggle, string? Hint = null);

    public static List<string> Render(string title, string fkey, IReadOnlyList<OptionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var lines = new List<string>
        {
            $"[dim]‹ back[/]   [bold]{Escape(title)}[/]   [dim]{Escape(fkey)}[/]",
            string.Empty,
        };

        foreach (var row in rows)
        {
            lines.Add(RenderRow(row));
        }

        lines.Add(string.Empty);
        lines.Add("[dim][[Cancel]]   [[Save]][/]");
        return lines;
    }

    private static string RenderRow(OptionRow row)
    {
        if (row.Label.Length == 0 && row.Value is null && row.Toggle is null)
        {
            return string.Empty;
        }

        if (row.Label.StartsWith("├ ", StringComparison.Ordinal))
        {
            return $"[dim]{Escape(row.Label)}[/]";
        }

        var hint = row.Hint is null ? string.Empty : $"[dim] — {Escape(row.Hint)}[/]";

        if (row.Toggle is { } toggle)
        {
            var box = toggle ? $"[{Accent}][x][/]" : "[dim][ ][/]";
            return $"{box} {Escape(row.Label)}{hint}";
        }

        var label = Escape(row.Label).PadRight(LabelWidth);
        return $"[dim]{label}[/] {Escape(row.Value ?? string.Empty)}{hint}";
    }

    /// <summary>The F7 "Text &amp; ANSI" screen body.</summary>
    public static List<string> TextAnsi()
    {
        var rows = new List<OptionRow>
        {
            new("├ COLOUR", null, null),
            new("strip incoming ANSI colour", null, false),
            new("allow blink", null, false),
            new("underline hyperlinks", null, true),
            new(string.Empty, null, null),
            new("├ UNICODE", null, null),
            new("emoji substitution", null, true),
            new("ambiguous width", "narrow", null),
        };

        return Render("Text & ANSI", "F7", rows);
    }

    /// <summary>The F8 "Input &amp; spellcheck" screen body.</summary>
    public static List<string> InputSpellcheck()
    {
        var rows = new List<OptionRow>
        {
            new("├ INPUT", null, null),
            new("local echo", null, true),
            new("keep per-tab drafts", null, true),
            new("newline key", "Shift+Enter", null),
            new(string.Empty, null, null),
            new("├ SPELLCHECK", null, null),
            new("check spelling", null, true),
            new("dictionary", "en_US", null),
        };

        return Render("Input & spellcheck", "F8", rows);
    }

    /// <summary>The F9 "Logging" screen body, reflecting a character's <see cref="LoggingSettings"/>.</summary>
    public static List<string> Logging(LoggingSettings logging)
    {
        ArgumentNullException.ThrowIfNull(logging);

        var rows = new List<OptionRow>
        {
            new("├ SESSION LOG", null, null),
            new("format", logging.Format.ToString(), null),
            new("directory", logging.Directory ?? "(default)", null),
            new("auto-start on connect", null, logging.Format != LogFormat.None),
        };

        return Render("Logging", "F9", rows);
    }

    private static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");
}
