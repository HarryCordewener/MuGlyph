using System.Globalization;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using static SharpMUTerm.Tui.MarkupText;
using static SharpMUTerm.Tui.ScreenPalette;

namespace SharpMUTerm.Tui;

/// <summary>
/// Produces the markup sub-blocks for the F3 Aliases screen — the header band, the alias list
/// (flattened across every <see cref="TriggerSet"/>, each row carrying its enabled state,
/// name/pattern, owning set, and expansion), the editor for the selected alias (pattern, expansion
/// lines, and the case-sensitivity toggle), and the footer action bar.
/// <see cref="AliasesScreenView"/> composes these into real panels (grids) for the live/snapshot
/// view; <see cref="Render"/> merges the same blocks into a single line list for the unit tests.
/// Pure so every block is testable.
/// </summary>
internal static class AliasesScreenRenderer
{
    /// <summary>
    /// Visible width of the left column. The view lays its column out at exactly this width, so
    /// the two must agree -- a cursor bar padded narrower than its column leaves a gap before the
    /// rule. Shared rather than duplicated so they cannot drift apart.
    /// </summary>
    internal const int ColumnWidth = 56;

    /// <summary>
    /// Merges every sub-block into one line list (header, alias list | editor, footer). Used by the
    /// unit tests and as a width-agnostic fallback; the live view composes the same blocks into
    /// panels instead.
    /// </summary>
    public static List<string> Render(IReadOnlyList<TriggerSet> sets, int selected)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var left = ListColumn(sets, selected);
        var right = EditorColumn(sets, selected);

        var lines = new List<string> { HeaderLine(0), string.Empty };

        var rowCount = Math.Max(left.Count, right.Count);
        for (var i = 0; i < rowCount; i++)
        {
            var leftLine = i < left.Count ? left[i] : string.Empty;
            var rightLine = i < right.Count ? right[i] : string.Empty;
            lines.Add($"{PadVisible(leftLine, ColumnWidth)} │ {rightLine}");
        }

        lines.Add(string.Empty);
        lines.Add(FooterLine(sets, selected, 0));

        return lines;
    }

    /// <summary>The screen title on the left, the keyboard hints right-aligned to <paramref name="width"/>.</summary>
    internal static string HeaderLine(int width)
    {
        var title = $"[bold {Value}] Aliases[/]";
        var hints = ScreenChrome.Hints(ScreenChrome.ListHints, "F3");
        return SpreadLR(" " + title, hints, width);
    }

    /// <summary>
    /// The screen's navigable panes: the alias list (Space enables/disables one) and the selected
    /// alias's checkbox rows, in the order <see cref="EditorColumn"/> draws them.
    /// </summary>
    internal static ScreenModel Model(IReadOnlyList<TriggerSet> sets, int selected)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var entries = Flatten(sets);
        var list = ScreenModel.Toggles(entries, e => e.Alias.Enabled, (e, v) => e.Alias.Enabled = v);

        if (selected < 0 || selected >= entries.Count)
        {
            return new ScreenModel(list, Array.Empty<ScreenToggle?>());
        }

        var alias = entries[selected].Alias;
        var editor = new ScreenToggle?[]
        {
            ScreenToggle.Bind(() => alias.CaseSensitive, v => alias.CaseSensitive = v),
        };

        return new ScreenModel(list, editor);
    }

    /// <summary>The action bar: which alias is selected on the left, cancel/save on the right.</summary>
    internal static string FooterLine(IReadOnlyList<TriggerSet> sets, int selected, int width)
    {
        var entries = Flatten(sets);
        var context = string.Empty;
        if (entries.Count > 0 && selected >= 0 && selected < entries.Count)
        {
            var count = entries.Count.ToString(CultureInfo.InvariantCulture);
            context = $"[{Label}]alias {(selected + 1).ToString(CultureInfo.InvariantCulture)}/{count}[/]"
                + $"[{Label}]  ·  set {Escape(entries[selected].SetName)}[/]";
        }

        var actions = ScreenChrome.Actions();
        return SpreadLR(" " + context, actions, width);
    }

    /// <summary>The alias list — every alias of every set, with its enabled state and expansion.</summary>
    internal static List<string> ListColumn(
        IReadOnlyList<TriggerSet> sets, int selected, ScreenFocus? focus = null)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var cursor = focus ?? ScreenFocus.None;
        var entries = Flatten(sets);
        var lines = new List<string> { "[dim]on  name / pattern → expansion[/]" };

        if (entries.Count == 0)
        {
            lines.Add("[dim]no aliases[/]");
            return lines;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var row = Row(entries[i].Alias, entries[i].SetName, i == selected);
            lines.Add(ScreenChrome.Cursor(row, cursor.IsOn(0, i), ColumnWidth));
        }

        return lines;
    }

    /// <summary>
    /// The editor for the selected alias — pattern, expansion lines, and the case-sensitivity
    /// toggle. Empty when nothing is selected.
    /// </summary>
    internal static List<string> EditorColumn(
        IReadOnlyList<TriggerSet> sets, int selected, ScreenFocus? focus = null)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var entries = Flatten(sets);
        return selected >= 0 && selected < entries.Count
            ? BuildEditor(entries[selected].Alias, focus ?? ScreenFocus.None)
            : new List<string>();
    }

    /// <summary>Flattens every set's aliases into one list, each paired with its owning set's name.</summary>
    private static List<(Alias Alias, string SetName)> Flatten(IReadOnlyList<TriggerSet> sets)
    {
        var entries = new List<(Alias, string)>();
        foreach (var set in sets)
        {
            foreach (var alias in set.Aliases)
            {
                entries.Add((alias, set.Name));
            }
        }

        return entries;
    }

    private static string Row(Alias alias, string setName, bool selected)
    {
        var check = alias.Enabled ? $"[{Accent}]✓[/]" : "[dim]·[/]";
        var marker = selected ? "▸" : " ";
        var name = Escape(alias.Name);
        var pattern = Escape(alias.Pattern);
        var expansion = Escape(FirstLine(alias.Substitution));
        return $"{check} {marker} [bold]{name}[/] [dim]{pattern}[/] [dim]▪ {Escape(setName)}[/] → {expansion}";
    }

    private static List<string> BuildEditor(Alias alias, ScreenFocus cursor)
    {
        var lines = new List<string>
        {
            "[dim]match pattern (regex)[/]",
            $"  {Escape(alias.Pattern)}",
            string.Empty,
            "[dim]expands to[/]",
        };

        foreach (var line in alias.Substitution.Split('\n'))
        {
            lines.Add($"  {Escape(line)}");
        }

        lines.Add(string.Empty);
        var caseRow = alias.CaseSensitive
            ? $"[{Accent}][[x]][/] case sensitive"
            : "[dim][[ ]] case sensitive[/]";
        lines.Add(ScreenChrome.Cursor(caseRow, cursor.IsOn(1, 0), ColumnWidth));

        return lines;
    }

    private static string FirstLine(string text)
    {
        var newlineIndex = text.IndexOf('\n');
        return newlineIndex < 0 ? text : text[..newlineIndex];
    }
}
