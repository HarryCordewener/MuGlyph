using System.Globalization;
using System.Text.RegularExpressions;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;

namespace SharpMUTerm.Tui;

/// <summary>
/// Produces the markup sub-blocks for the F6 Timers screen — the header band, the timer list
/// (flattened across every <see cref="TriggerSet"/>, each row carrying its enabled state, name,
/// schedule, owning set, and command), the editor for the selected timer (interval, command, and the
/// one-shot/enabled toggles), and the footer action bar.
/// <see cref="TimersScreenView"/> composes these into real panels (grids) for the live/snapshot
/// view; <see cref="Render"/> merges the same blocks into a single line list for the unit tests.
/// Pure so every block is testable.
/// </summary>
internal static class TimersScreenRenderer
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
    /// Merges every sub-block into one line list (header, timer list | editor, footer). Used by the
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
        var title = $"[bold {Value}] Timers[/]";
        var hints = $"[{Label}]↑↓ select · ⇥ switch pane · ⏎ edit · [/][{Accent}]F6[/][{Label}]/[/]"
            + $"[{Accent}]Esc[/][{Label}] close [/]";
        return SpreadLR(" " + title, hints, width);
    }

    /// <summary>The action bar: which timer is selected on the left, cancel/save on the right.</summary>
    internal static string FooterLine(IReadOnlyList<TriggerSet> sets, int selected, int width)
    {
        var entries = Flatten(sets);
        var context = string.Empty;
        if (entries.Count > 0 && selected >= 0 && selected < entries.Count)
        {
            var count = entries.Count.ToString(CultureInfo.InvariantCulture);
            context = $"[{Label}]timer {(selected + 1).ToString(CultureInfo.InvariantCulture)}/{count}[/]"
                + $"[{Label}]  ·  set {Escape(entries[selected].SetName)}[/]";
        }

        var actions = $"[{Label}] [[Esc]] Cancel [/]  [{Ink} on {Accent}] [[⏎]] Save [/] ";
        return SpreadLR(" " + context, actions, width);
    }

    /// <summary>The timer list — every timer of every set, with its enabled state and schedule.</summary>
    internal static List<string> ListColumn(IReadOnlyList<TriggerSet> sets, int selected)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var entries = Flatten(sets);
        var lines = new List<string> { "[dim]on  name  every  → command[/]" };

        if (entries.Count == 0)
        {
            lines.Add("[dim]no timers[/]");
            return lines;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            lines.Add(Row(entries[i].Timer, entries[i].SetName, i == selected));
        }

        return lines;
    }

    /// <summary>
    /// The editor for the selected timer — interval, command, and the one-shot/enabled toggles.
    /// Empty when nothing is selected.
    /// </summary>
    internal static List<string> EditorColumn(IReadOnlyList<TriggerSet> sets, int selected)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var entries = Flatten(sets);
        return selected >= 0 && selected < entries.Count
            ? BuildEditor(entries[selected].Timer)
            : new List<string>();
    }

    /// <summary>Flattens every set's timers into one list, each paired with its owning set's name.</summary>
    private static List<(TimerDefinition Timer, string SetName)> Flatten(IReadOnlyList<TriggerSet> sets)
    {
        var entries = new List<(TimerDefinition, string)>();
        foreach (var set in sets)
        {
            foreach (var timer in set.Timers)
            {
                entries.Add((timer, set.Name));
            }
        }

        return entries;
    }

    private static string Row(TimerDefinition timer, string setName, bool selected)
    {
        var check = timer.Enabled ? $"[{Accent}]✓[/]" : "[dim]·[/]";
        var marker = selected ? "▸" : " ";
        var name = Escape(timer.Name);
        var schedule = Schedule(timer);
        var command = Escape(timer.Command);
        return $"{check} {marker} [bold]{name}[/] {schedule} [dim]▪ {Escape(setName)}[/] → {command}";
    }

    private static List<string> BuildEditor(TimerDefinition timer) => new()
    {
        "[dim]interval (seconds)[/]",
        $"  {Seconds(timer)}",
        string.Empty,
        "[dim]command[/]",
        $"  {Escape(timer.Command)}",
        string.Empty,
        timer.OneShot ? $"[{Accent}][[x]][/] one-shot" : "[dim][[ ]] one-shot[/]",
        timer.Enabled ? $"[{Accent}][[x]][/] enabled" : "[dim][[ ]] enabled[/]",
    };

    /// <summary>The row-level schedule summary: <c>every 30s</c>, or <c>once after 5.5s</c>.</summary>
    private static string Schedule(TimerDefinition timer) =>
        timer.OneShot ? $"once after {Seconds(timer)}s" : $"every {Seconds(timer)}s";

    private static string Seconds(TimerDefinition timer) =>
        timer.IntervalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

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
