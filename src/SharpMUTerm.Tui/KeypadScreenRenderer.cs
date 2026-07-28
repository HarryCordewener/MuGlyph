using System.Globalization;
using System.Text.RegularExpressions;
using SharpMUTerm.Core.Automation;

namespace SharpMUTerm.Tui;

/// <summary>
/// Produces the markup sub-blocks for the F4 Keypad &amp; hotkeys screen — the header band, the 3×3
/// numpad grid (Num7..Num9 top row through Num1..Num3 bottom row, numpad order) showing the command
/// bound to each digit, the binding list of every macro with its enabled state, key, and command,
/// and the footer action bar. <see cref="KeypadScreenView"/> composes these into real panels (grids)
/// for the live/snapshot view; <see cref="Render"/> merges the same blocks into a single line list
/// for the unit tests. Pure so every block is testable.
/// </summary>
internal static class KeypadScreenRenderer
{
    private const string Accent = "#00f5b7";
    private const int KeyColumnWidth = 12;
    private const int ColumnWidth = 48;

    // Palette shared with the view (which sets these as control backgrounds).
    internal const string HeaderBg = "#232b3d";
    internal const string FooterBg = "#232b3d";
    private const string Label = "#7c8699";
    private const string Value = "#d7deec";
    private const string Ink = "#0f1620";

    private static readonly Regex TagPattern = new(@"\[[^\[\]]*\]", RegexOptions.Compiled);

    private static readonly int[][] NumpadRows =
    {
        new[] { 7, 8, 9 },
        new[] { 4, 5, 6 },
        new[] { 1, 2, 3 },
    };

    /// <summary>
    /// Merges every sub-block into one line list (header, numpad | hotkeys, footer). Used by the
    /// unit tests and as a width-agnostic fallback; the live view composes the same blocks into
    /// panels instead.
    /// </summary>
    public static List<string> Render(IReadOnlyList<Macro> macros)
    {
        ArgumentNullException.ThrowIfNull(macros);

        var left = NumpadColumn(macros);
        var right = HotkeysColumn(macros);

        var lines = new List<string> { HeaderLine(0), string.Empty };

        var rowCount = Math.Max(left.Count, right.Count);
        for (var i = 0; i < rowCount; i++)
        {
            var leftLine = i < left.Count ? left[i] : string.Empty;
            var rightLine = i < right.Count ? right[i] : string.Empty;
            lines.Add($"{PadVisible(leftLine, ColumnWidth)} │ {rightLine}");
        }

        lines.Add(string.Empty);
        lines.Add(FooterLine(macros, 0));

        return lines;
    }

    /// <summary>The screen title on the left, the keyboard hints right-aligned to <paramref name="width"/>.</summary>
    internal static string HeaderLine(int width)
    {
        var title = $"[bold {Value}] Keypad & hotkeys[/]";
        var hints = $"[{Label}]↑↓ select · ⇥ switch pane · ⏎ rebind · [/][{Accent}]F4[/][{Label}]/[/]"
            + $"[{Accent}]Esc[/][{Label}] close [/]";
        return SpreadLR(" " + title, hints, width);
    }

    /// <summary>The action bar: how much of the keypad is bound on the left, cancel/save on the right.</summary>
    internal static string FooterLine(IReadOnlyList<Macro> macros, int width)
    {
        ArgumentNullException.ThrowIfNull(macros);

        var bound = 0;
        foreach (var row in NumpadRows)
        {
            foreach (var digit in row)
            {
                if (FindByKey(macros, $"Num{digit}") is not null)
                {
                    bound++;
                }
            }
        }

        var total = macros.Count.ToString(CultureInfo.InvariantCulture);
        var context = $"[{Label}]{total} bindings[/]"
            + $"[{Label}]  ·  {bound.ToString(CultureInfo.InvariantCulture)} of 9 numpad keys bound[/]";

        var actions = $"[{Label}] [[Esc]] Cancel [/]  [{Ink} on {Accent}] [[⏎]] Save [/] ";
        return SpreadLR(" " + context, actions, width);
    }

    /// <summary>The 3×3 numpad grid, in numpad order (7-8-9 on top), one row per line.</summary>
    internal static List<string> NumpadColumn(IReadOnlyList<Macro> macros)
    {
        ArgumentNullException.ThrowIfNull(macros);

        var lines = new List<string> { "[dim]NUMPAD[/]" };
        foreach (var row in NumpadRows)
        {
            lines.Add(NumpadRow(row, macros));
        }

        return lines;
    }

    /// <summary>The binding list — every macro with its enabled state, key, and command.</summary>
    internal static List<string> HotkeysColumn(IReadOnlyList<Macro> macros)
    {
        ArgumentNullException.ThrowIfNull(macros);

        var lines = new List<string> { "[dim]HOTKEYS[/]" };
        if (macros.Count == 0)
        {
            lines.Add("  [dim]no hotkeys[/]");
            return lines;
        }

        foreach (var macro in macros)
        {
            lines.Add(Hotkey(macro));
        }

        return lines;
    }

    private static string NumpadRow(int[] digits, IReadOnlyList<Macro> macros)
    {
        var cells = new string[digits.Length];
        for (var i = 0; i < digits.Length; i++)
        {
            cells[i] = NumpadCell(digits[i], macros);
        }

        return string.Join("   ", cells);
    }

    private static string NumpadCell(int digit, IReadOnlyList<Macro> macros)
    {
        var macro = FindByKey(macros, $"Num{digit}");
        var command = macro is null ? "[dim]—[/]" : Escape(Truncate(macro.Command, 10));
        return $"[bold {Accent}][[{digit}]][/] {command}";
    }

    private static string Hotkey(Macro macro)
    {
        var tick = macro.Enabled ? $"[{Accent}]✓[/]" : "[dim]·[/]";
        var key = $"[bold]{Escape(macro.Key).PadRight(KeyColumnWidth)}[/]";
        return $"{tick} {key} → {Escape(macro.Command)}";
    }

    private static Macro? FindByKey(IReadOnlyList<Macro> macros, string key)
    {
        foreach (var macro in macros)
        {
            if (string.Equals(macro.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return macro;
            }
        }

        return null;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength - 1), "…");

    /// <summary>Lays a left- and right-hand fragment on one line, right-aligning the right to <paramref name="width"/>.</summary>
    private static string SpreadLR(string left, string right, int width)
    {
        if (width <= 0)
        {
            return $"{left}   {right}";
        }

        var gap = Math.Max(1, width - VisibleLength(left) - VisibleLength(right));
        return left + new string(' ', gap) + right;
    }

    /// <summary>Pads a markup string to a target *visible* column width, ignoring markup tags.</summary>
    private static string PadVisible(string markup, int width)
    {
        var visible = VisibleLength(markup);
        return visible >= width ? markup : markup + new string(' ', width - visible);
    }

    /// <summary>
    /// Counts the printable length of a markup string: escaped brackets (<c>[[</c>/<c>]]</c>) count
    /// as one literal character each, and <c>[tag]</c> wrappers are stripped entirely.
    /// </summary>
    private static int VisibleLength(string markup)
    {
        var protectedText = markup.Replace("[[", "\u0001").Replace("]]", "\u0002");
        return TagPattern.Replace(protectedText, string.Empty).Length;
    }

    private static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");
}
