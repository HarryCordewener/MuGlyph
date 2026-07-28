using MuClient.Core.Automation;
using MuClient.Core.Configuration;

namespace MuClient.Tui;

/// <summary>
/// Renders the F3 aliases screen: a flattened list of aliases across all trigger sets on the left,
/// merged with an editor for the selected alias on the right, into full-width markup lines. Pure so
/// the screen is unit-testable; the modal host just displays what this produces.
/// </summary>
internal static class AliasesScreenRenderer
{
    private const string Accent = "#00f5b7";

    public static List<string> Render(IReadOnlyList<TriggerSet> sets, int selected)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var lines = new List<string>
        {
            "[dim]‹ back[/]   [bold]Aliases[/]   [dim]F3[/]",
            string.Empty,
        };

        var entries = Flatten(sets);
        if (entries.Count == 0)
        {
            lines.Add("[dim]on  name / pattern → expansion[/]");
            lines.Add("[dim]no aliases[/]");
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
            lines.Add($"{l} │ {r}");
        }

        lines.Add(string.Empty);
        lines.Add("[dim][[Cancel]]   [[Save]][/]");
        return lines;
    }

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

    private static List<string> LeftColumn(List<(Alias Alias, string SetName)> entries, int selected)
    {
        var lines = new List<string> { "[dim]on  name / pattern → expansion[/]" };
        for (var i = 0; i < entries.Count; i++)
        {
            lines.Add(Row(entries[i].Alias, entries[i].SetName, i == selected));
        }

        return lines;
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

    private static List<string> RightColumn(List<(Alias Alias, string SetName)> entries, int selected)
    {
        if (selected < 0 || selected >= entries.Count)
        {
            return new List<string>();
        }

        var alias = entries[selected].Alias;
        var lines = new List<string>
        {
            "[dim]match pattern (regex)[/]",
            Escape(alias.Pattern),
            "[dim]expands to[/]",
        };

        var substitutionLines = alias.Substitution.Split('\n');
        foreach (var line in substitutionLines)
        {
            lines.Add(Escape(line));
        }

        lines.Add(alias.CaseSensitive
            ? $"[{Accent}][x][/] case sensitive"
            : "[dim][ ] case sensitive[/]");

        return lines;
    }

    private static string FirstLine(string text)
    {
        var newlineIndex = text.IndexOf('\n');
        return newlineIndex < 0 ? text : text[..newlineIndex];
    }

    private static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");
}
