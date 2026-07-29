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

        var lines = new List<string> { HeaderLine(0, Model(macros)), string.Empty };

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

    /// <summary>
    /// The screen title on the left, the keyboard hints right-aligned to <paramref name="width"/>. The
    /// hints are derived from <paramref name="model"/> and <paramref name="focus"/> rather than
    /// written here, so the header cannot advertise an edit the screen doesn't offer.
    /// </summary>
    internal static string HeaderLine(int width, ScreenModel? model = null, ScreenFocus? focus = null)
    {
        var title = $"[bold {Value}] Keypad & hotkeys[/]";
        var hints = ScreenChrome.Hints(
            ScreenChrome.SingleListHints, "F4", model?.HasEditableRow ?? false, focus);
        return SpreadLR(" " + title, hints, width);
    }

    /// <summary>
    /// The screen's one navigable pane: the binding list, where Space enables or disables a macro and
    /// ⏎ edits the command it sends. The numpad grid is a projection of the same macros, so it has no
    /// cursor of its own — it updates as the list is toggled and edited.
    /// </summary>
    internal static ScreenModel Model(IReadOnlyList<Macro> macros)
    {
        ArgumentNullException.ThrowIfNull(macros);

        return new ScreenModel(ScreenModel.Rows(macros, macro => ScreenRow.Of(
            ScreenToggle.Bind(() => macro.Enabled, v => macro.Enabled = v),
            ScreenField.Text("command", () => macro.Command, v => macro.Command = v))));
    }

    /// <summary>
    /// The action bar: where the cursor is in the binding list on the left, cancel/save on the right.
    /// It used to report the screen's inventory instead (<c>9 bindings · 8 of 9 numpad keys bound</c>),
    /// which the numpad grid two columns over already shows cell by cell; every other screen answers
    /// "where am I", so this one does too. The selected binding comes from <paramref name="focus"/>
    /// rather than a parameter of its own, because the cursor is the only thing that decides it.
    /// <para>
    /// The qualifier is the macro's <em>name</em>, which is the one thing about a binding this screen
    /// doesn't otherwise show — its rows are key → command. A macro that has never been named falls
    /// back to its key, because an empty qualifier would leave the footer saying less than it could.
    /// </para>
    /// </summary>
    internal static string FooterLine(IReadOnlyList<Macro> macros, int width, ScreenFocus? focus = null)
    {
        ArgumentNullException.ThrowIfNull(macros);

        var context = string.Empty;
        if (macros.Count > 0)
        {
            var selected = Math.Clamp(focus?.Pane == 0 ? focus.Value.Index : 0, 0, macros.Count - 1);
            var macro = macros[selected];
            var names = string.IsNullOrWhiteSpace(macro.Name) ? macro.Key : macro.Name;
            context = ScreenChrome.Context(
                ScreenChrome.Position("binding", selected, macros.Count), Escape(names));
        }

        var actions = ScreenChrome.Actions(focus: focus);
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
            lines.Add(ScreenChrome.Cursor(
                Hotkey(macros[i], cursor.EditOn(0, i, 0)), cursor.IsOn(0, i), ColumnWidth));
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

    /// <summary>
    /// One cell of the numpad diagram. The command is drawn as a readout, not a field: the grid mirrors
    /// the binding list beside it and has no cursor of its own, so a cell is somewhere a command is
    /// *shown*, never somewhere one is typed. See <see cref="ScreenChrome.ReadOnly"/>.
    /// </summary>
    private static string NumpadCell(int digit, IReadOnlyList<Macro> macros)
    {
        var macro = FindByKey(macros, $"Num{digit}");
        var command = macro is null
            ? "[dim]—[/]"
            : ScreenChrome.ReadOnly(Truncate(macro.Command, NumpadCommandWidth));
        return $"[bold {Accent}][[{digit}]][/] {command}";
    }

    private static string Hotkey(Macro macro, ScreenFieldEdit? edit)
    {
        var tick = macro.Enabled ? $"[{Accent}]✓[/]" : "[dim]·[/]";
        var key = $"[bold]{Escape(macro.Key).PadRight(KeyColumnWidth)}[/]";
        return $"{tick} {key} → {ScreenChrome.Field(Escape(macro.Command), edit)}";
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
