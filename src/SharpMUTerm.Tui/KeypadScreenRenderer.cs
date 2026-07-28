using SharpMUTerm.Core.Automation;

namespace SharpMUTerm.Tui;

/// <summary>
/// Renders the F4 keypad &amp; hotkeys screen: a 3×3 numpad grid (Num7..Num9 top row through
/// Num1..Num3 bottom row, numpad order) showing the command bound to each digit, followed by a
/// binding list of every macro with its enabled state, key, and command. Pure so the panel is
/// unit-testable.
/// </summary>
internal static class KeypadScreenRenderer
{
    private const string Accent = "#00f5b7";
    private const int KeyColumnWidth = 12;

    private static readonly int[][] NumpadRows =
    {
        new[] { 7, 8, 9 },
        new[] { 4, 5, 6 },
        new[] { 1, 2, 3 },
    };

    public static List<string> Render(IReadOnlyList<Macro> macros)
    {
        ArgumentNullException.ThrowIfNull(macros);

        var lines = new List<string>
        {
            "[dim]‹ back[/]   [bold]Keypad & hotkeys[/]   [dim]F4[/]",
            string.Empty,
            "[dim]├ NUMPAD[/]",
        };

        foreach (var row in NumpadRows)
        {
            lines.Add(NumpadRow(row, macros));
        }

        lines.Add(string.Empty);
        lines.Add("[dim]├ HOTKEYS[/]");

        if (macros.Count == 0)
        {
            lines.Add("  [dim]no hotkeys[/]");
        }
        else
        {
            foreach (var macro in macros)
            {
                lines.Add(Hotkey(macro));
            }
        }

        lines.Add(string.Empty);
        lines.Add("[dim][[Cancel]]   [[Save]][/]");
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

    private static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");
}
