using System.Globalization;
using System.Text.RegularExpressions;
using MuClient.Core.Automation;
using MuClient.Core.Configuration;

namespace MuClient.Tui;

/// <summary>
/// Renders the F6 timers screen: a flattened list of timers across all trigger sets on the left,
/// merged with an editor for the selected timer on the right, into full-width markup lines. Pure so
/// the screen is unit-testable; the modal host just displays what this produces.
/// </summary>
internal static class TimersScreenRenderer
{
    private const string Accent = "#00f5b7";
    private const int ColumnWidth = 54;

    private static readonly Regex TagPattern = new(@"\[[^\[\]]*\]", RegexOptions.Compiled);

    public static List<string> Render(IReadOnlyList<TriggerSet> sets, int selected)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var lines = new List<string>
        {
            "[dim]‹ back[/]   [bold]Timers[/]   [dim]F6[/]",
            string.Empty,
        };

        var entries = Flatten(sets);
        if (entries.Count == 0)
        {
            lines.Add("[dim]on  name  every  → command[/]");
            lines.Add("[dim]no timers[/]");
            lines.Add(string.Empty);
            lines.Add("[dim][[Cancel]]   [[Save]][/]");
            return lines;
        }

        var left = LeftColumn(entries, selected);
        var right = RightColumn(entries, selected);
        var rowCount = Math.Max(left.Count, right.Count);
        for (var i = 0; i < rowCount; i++)
        {
            var l = i < left.Count ? left[i] : string.Empty;
            var r = i < right.Count ? right[i] : string.Empty;
            lines.Add($"{PadVisible(l, ColumnWidth)} │ {r}");
        }

        lines.Add(string.Empty);
        lines.Add("[dim][[Cancel]]   [[Save]][/]");
        return lines;
    }

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

    private static List<string> LeftColumn(List<(TimerDefinition Timer, string SetName)> entries, int selected)
    {
        var lines = new List<string> { "[dim]on  name  every  → command[/]" };
        for (var i = 0; i < entries.Count; i++)
        {
            lines.Add(Row(entries[i].Timer, entries[i].SetName, i == selected));
        }

        return lines;
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

    private static List<string> RightColumn(List<(TimerDefinition Timer, string SetName)> entries, int selected)
    {
        if (selected < 0 || selected >= entries.Count)
        {
            return new List<string>();
        }

        var timer = entries[selected].Timer;
        return new List<string>
        {
            "[dim]interval (seconds)[/]",
            timer.IntervalSeconds.ToString("0.#", CultureInfo.InvariantCulture),
            "[dim]command[/]",
            Escape(timer.Command),
            timer.OneShot ? $"[{Accent}][[x]][/] one-shot" : "[dim][[ ]] one-shot[/]",
            timer.Enabled ? $"[{Accent}][[x]][/] enabled" : "[dim][[ ]] enabled[/]",
        };
    }

    private static string Schedule(TimerDefinition timer)
    {
        var seconds = timer.IntervalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        return timer.OneShot ? $"once after {seconds}s" : $"every {seconds}s";
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
        var protectedText = markup.Replace("[[", "").Replace("]]", "");
        return TagPattern.Replace(protectedText, string.Empty).Length;
    }

    private static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");
}
