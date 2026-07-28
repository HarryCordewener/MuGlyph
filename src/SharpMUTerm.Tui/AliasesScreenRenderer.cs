using System.Globalization;
using System.Text.RegularExpressions;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;

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
    private const string Accent = "#00f5b7";
    private const int ColumnWidth = 54;

    // Palette shared with the view (which sets these as control backgrounds).
    internal const string HeaderBg = "#232b3d";
    internal const string FooterBg = "#232b3d";
    private const string Label = "#7c8699";
    private const string Value = "#d7deec";
    private const string Ink = "#0f1620";

    private static readonly Regex TagPattern = new(@"\[[^\[\]]*\]", RegexOptions.Compiled);

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
        var hints = $"[{Label}]↑↓ select · ⇥ switch pane · ⏎ edit · [/][{Accent}]F3[/][{Label}]/[/]"
            + $"[{Accent}]Esc[/][{Label}] close [/]";
        return SpreadLR(" " + title, hints, width);
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

        var actions = $"[{Label}] [[Esc]] Cancel [/]  [{Ink} on {Accent}] [[⏎]] Save [/] ";
        return SpreadLR(" " + context, actions, width);
    }

    /// <summary>The alias list — every alias of every set, with its enabled state and expansion.</summary>
    internal static List<string> ListColumn(IReadOnlyList<TriggerSet> sets, int selected)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var entries = Flatten(sets);
        var lines = new List<string> { "[dim]on  name / pattern → expansion[/]" };

        if (entries.Count == 0)
        {
            lines.Add("[dim]no aliases[/]");
            return lines;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            lines.Add(Row(entries[i].Alias, entries[i].SetName, i == selected));
        }

        return lines;
    }

    /// <summary>
    /// The editor for the selected alias — pattern, expansion lines, and the case-sensitivity
    /// toggle. Empty when nothing is selected.
    /// </summary>
    internal static List<string> EditorColumn(IReadOnlyList<TriggerSet> sets, int selected)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var entries = Flatten(sets);
        return selected >= 0 && selected < entries.Count
            ? BuildEditor(entries[selected].Alias)
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

    private static List<string> BuildEditor(Alias alias)
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
        lines.Add(alias.CaseSensitive
            ? $"[{Accent}][[x]][/] case sensitive"
            : "[dim][[ ]] case sensitive[/]");

        return lines;
    }

    private static string FirstLine(string text)
    {
        var newlineIndex = text.IndexOf('\n');
        return newlineIndex < 0 ? text : text[..newlineIndex];
    }

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
