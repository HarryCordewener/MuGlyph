using System.Text.RegularExpressions;
using MuClient.Core.Automation;
using MuClient.Core.Configuration;
using MuClient.Core.Text;

namespace MuClient.Tui;

/// <summary>
/// Renders the F2 "Triggers &amp; spawn routing" screen: a left rule list (flattened across every
/// <see cref="TriggerSet"/>, each row carrying its enabled state, name/pattern, owning set, action
/// flags, and route) merged column-by-column with a right-hand editor for the selected trigger
/// (pattern, route-to list, highlight swatches, and toggles). Pure so the screen is unit-testable;
/// the modal host just displays what this produces.
/// </summary>
internal static class TriggersScreenRenderer
{
    private const string Accent = "#00f5b7";
    private const int ColumnWidth = 54;

    private static readonly Regex TagPattern = new(@"\[[^\[\]]*\]", RegexOptions.Compiled);

    public static List<string> Render(
        IReadOnlyList<TriggerSet> sets,
        int selectedTrigger,
        IReadOnlyList<string> spawnTargets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(spawnTargets);

        var flattened = new List<(Trigger Trigger, string SetName)>();
        foreach (var set in sets)
        {
            foreach (var trigger in set.Triggers)
            {
                flattened.Add((trigger, set.Name));
            }
        }

        var left = BuildLeft(flattened, selectedTrigger);
        var right = selectedTrigger >= 0 && selectedTrigger < flattened.Count
            ? BuildEditor(flattened[selectedTrigger].Trigger, spawnTargets)
            : new List<string>();

        var lines = new List<string>
        {
            "[dim]‹ back[/]   [bold]Triggers & spawn routing[/]   [dim]F2[/]",
            string.Empty,
        };

        var rowCount = Math.Max(left.Count, right.Count);
        for (var i = 0; i < rowCount; i++)
        {
            var leftLine = i < left.Count ? left[i] : string.Empty;
            var rightLine = i < right.Count ? right[i] : string.Empty;
            lines.Add($"{PadVisible(leftLine, ColumnWidth)} │ {rightLine}");
        }

        lines.Add(string.Empty);
        lines.Add("[dim][[Cancel]]   [[Save]][/]");

        return lines;
    }

    private static List<string> BuildLeft(
        IReadOnlyList<(Trigger Trigger, string SetName)> flattened,
        int selectedTrigger)
    {
        var left = new List<string> { "[dim]on  name / pattern → window[/]" };

        if (flattened.Count == 0)
        {
            left.Add("[dim]no triggers[/]");
            return left;
        }

        for (var i = 0; i < flattened.Count; i++)
        {
            var (trigger, setName) = flattened[i];
            left.Add(RuleRow(i, selectedTrigger, trigger));
            left.Add(RuleSub(setName, trigger.Actions));
        }

        return left;
    }

    private static string RuleRow(int index, int selectedTrigger, Trigger trigger)
    {
        var marker = index == selectedTrigger ? "[bold]▸[/]" : " ";
        var box = trigger.Enabled ? $"[{Accent}]✓[/]" : "[dim]·[/]";
        var target = trigger.Actions.SpawnTarget ?? "main";
        return $"{marker} {box} [bold]{Escape(trigger.Name)}[/] [dim]{Escape(trigger.Pattern)}[/] [dim]→ {Escape(target)}[/]";
    }

    private static string RuleSub(string owningSet, TriggerActions actions) =>
        $"  [dim]▪ {Escape(owningSet)} · {Flags(actions)}[/]";

    private static string Flags(TriggerActions actions)
    {
        var flags = new List<string>();
        if (actions.HighlightForeground is not null || actions.HighlightBackground is not null)
        {
            flags.Add("H");
        }

        if (actions.Gag)
        {
            flags.Add("G");
        }

        if (actions.SendResponse is not null)
        {
            flags.Add("R");
        }

        if (actions.SpawnTarget is not null)
        {
            flags.Add(Glyphs.Capture);
        }

        if (actions.ScriptCallback is not null)
        {
            flags.Add("ƒ");
        }

        return flags.Count == 0 ? "—" : string.Join(" ", flags);
    }

    private static List<string> BuildEditor(Trigger trigger, IReadOnlyList<string> spawnTargets)
    {
        var currentRoute = trigger.Actions.SpawnTarget ?? "main";

        var lines = new List<string>
        {
            "[dim]match pattern (regex)[/]",
            $"  {Escape(trigger.Pattern)}",
            string.Empty,
            "[dim]route to[/]",
            RouteRow("main", currentRoute),
        };

        foreach (var target in spawnTargets)
        {
            lines.Add(RouteRow(target, currentRoute));
        }

        lines.Add(string.Empty);

        var fg = trigger.Actions.HighlightForeground;
        var bg = trigger.Actions.HighlightBackground;
        var hasHighlight = fg is not null || bg is not null;
        if (hasHighlight)
        {
            lines.Add("[dim]highlight[/]");
            if (fg is not null)
            {
                lines.Add($"[{Hex(fg.Value)}]████[/] fg");
            }

            if (bg is not null)
            {
                lines.Add($"[{Hex(bg.Value)}]████[/] bg");
            }

            lines.Add(string.Empty);
        }

        lines.Add(hasHighlight ? $"[{Accent}][[x]][/] highlight line" : "[dim][[ ]] highlight line[/]");
        lines.Add("[dim][[ ]] play sound[/]");
        lines.Add(trigger.Actions.Gag ? $"[{Accent}][[x]][/] gag line" : "[dim][[ ]] gag line[/]");

        return lines;
    }

    private static string RouteRow(string label, string currentRoute)
    {
        var marker = label == currentRoute ? $"[{Accent}]●[/]" : "[dim]○[/]";
        return $"  {marker} {Escape(label)}";
    }

    private static string Hex(TerminalColor color) =>
        color.Kind == TerminalColorKind.Rgb ? $"#{color.R:x2}{color.G:x2}{color.B:x2}" : Accent;

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
