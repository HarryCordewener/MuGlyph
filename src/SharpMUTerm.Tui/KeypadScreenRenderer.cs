using System.Globalization;
using SharpMUTerm.Core.Automation;
using static SharpMUTerm.Tui.MarkupText;
using static SharpMUTerm.Tui.ScreenPalette;

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
    private const int KeyColumnWidth = 12;
    private const int ColumnWidth = 48;

    /// <summary>Longest command shown inside a numpad cell before it is ellipsised.</summary>
    private const int NumpadCommandWidth = 10;

    /// <summary>Visible width of one numpad cell: "[N] " (4) plus <see cref="NumpadCommandWidth"/>.</summary>
    private const int NumpadCellWidth = 4 + NumpadCommandWidth;

    private const string NumpadCellGap = "   ";

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
        var hints = ScreenChrome.Hints(ScreenChrome.SingleListHints, "F4");
        return SpreadLR(" " + title, hints, width);
    }

    /// <summary>
    /// The screen's one navigable pane: the binding list, where Space enables or disables a macro.
    /// The numpad grid is a projection of the same macros, so it has no cursor of its own — it
    /// updates as the list is toggled.
    /// </summary>
    internal static ScreenModel Model(IReadOnlyList<Macro> macros)
    {
        ArgumentNullException.ThrowIfNull(macros);
        return new ScreenModel(ScreenModel.Toggles(macros, m => m.Enabled, (m, v) => m.Enabled = v));
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

        var actions = ScreenChrome.Actions();
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
    internal static List<string> HotkeysColumn(IReadOnlyList<Macro> macros, ScreenFocus? focus = null)
    {
        ArgumentNullException.ThrowIfNull(macros);

        var cursor = focus ?? ScreenFocus.None;
        var lines = new List<string> { "[dim]HOTKEYS[/]" };
        if (macros.Count == 0)
        {
            lines.Add("  [dim]no hotkeys[/]");
            return lines;
        }

        for (var i = 0; i < macros.Count; i++)
        {
            lines.Add(ScreenChrome.Cursor(Hotkey(macros[i]), cursor.IsOn(0, i), ColumnWidth));
        }

        return lines;
    }

    private static string NumpadRow(int[] digits, IReadOnlyList<Macro> macros)
    {
        var cells = new string[digits.Length];
        for (var i = 0; i < digits.Length; i++)
        {
            var cell = NumpadCell(digits[i], macros);

            // Every cell is padded to the same visible width so the three columns line up whatever
            // is bound: a cell is as wide as "[N] " plus the longest command it can hold. The last
            // cell in a row is left unpadded to avoid trailing whitespace.
            cells[i] = i == digits.Length - 1 ? cell : PadVisible(cell, NumpadCellWidth);
        }

        return string.Join(NumpadCellGap, cells);
    }

    private static string NumpadCell(int digit, IReadOnlyList<Macro> macros)
    {
        var macro = FindByKey(macros, $"Num{digit}");
        var command = macro is null ? "[dim]—[/]" : Escape(Truncate(macro.Command, NumpadCommandWidth));
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
}
