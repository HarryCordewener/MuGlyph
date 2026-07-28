using System.Globalization;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;
using static SharpMUTerm.Tui.MarkupText;
using static SharpMUTerm.Tui.ScreenPalette;

namespace SharpMUTerm.Tui;

/// <summary>
/// Produces the markup sub-blocks for the F2 Triggers &amp; spawn routing screen — the header band,
/// the rule list (flattened across every <see cref="TriggerSet"/>, each row carrying its enabled
/// state, name/pattern, owning set, action flags, and route), the editor for the selected trigger
/// (pattern, route-to list, highlight swatches, and toggles), and the footer action bar.
/// <see cref="TriggersScreenView"/> composes these into real panels (grids) for the live/snapshot
/// view; <see cref="Render"/> merges the same blocks into a single line list for the unit tests.
/// Pure so every block is testable.
/// </summary>
internal static class TriggersScreenRenderer
{
    private const int ColumnWidth = 54;

    /// <summary>
    /// Merges every sub-block into one line list (header, rule list | editor, footer). Used by the
    /// unit tests and as a width-agnostic fallback; the live view composes the same blocks into
    /// panels instead.
    /// </summary>
    public static List<string> Render(
        IReadOnlyList<TriggerSet> sets,
        int selectedTrigger,
        IReadOnlyList<string> spawnTargets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(spawnTargets);

        var left = RulesColumn(sets, selectedTrigger);
        var right = EditorColumn(sets, selectedTrigger, spawnTargets);

        var lines = new List<string> { HeaderLine(0), string.Empty };

        var rowCount = Math.Max(left.Count, right.Count);
        for (var i = 0; i < rowCount; i++)
        {
            var leftLine = i < left.Count ? left[i] : string.Empty;
            var rightLine = i < right.Count ? right[i] : string.Empty;
            lines.Add($"{PadVisible(leftLine, ColumnWidth)} │ {rightLine}");
        }

        lines.Add(string.Empty);
        lines.Add(FooterLine(sets, selectedTrigger, 0));

        return lines;
    }

    /// <summary>The screen title on the left, the keyboard hints right-aligned to <paramref name="width"/>.</summary>
    internal static string HeaderLine(int width)
    {
        var title = $"[bold {Value}] Triggers & spawn routing[/]";
        var hints = ScreenChrome.Hints("↑↓ select · ⇥ switch pane · ⏎ edit", "F2");
        return SpreadLR(" " + title, hints, width);
    }

    /// <summary>The action bar: which rule is selected on the left, cancel/save on the right.</summary>
    internal static string FooterLine(IReadOnlyList<TriggerSet> sets, int selectedTrigger, int width)
    {
        var flattened = Flatten(sets);
        var context = string.Empty;
        if (flattened.Count > 0 && selectedTrigger >= 0 && selectedTrigger < flattened.Count)
        {
            var count = flattened.Count.ToString(CultureInfo.InvariantCulture);
            context = $"[{Label}]trigger {(selectedTrigger + 1).ToString(CultureInfo.InvariantCulture)}/{count}[/]"
                + $"[{Label}]  ·  set {Escape(flattened[selectedTrigger].SetName)}[/]";
        }

        var actions = ScreenChrome.Actions();
        return SpreadLR(" " + context, actions, width);
    }

    /// <summary>The rule list — every trigger of every set, each over a set/flags sub-row.</summary>
    internal static List<string> RulesColumn(IReadOnlyList<TriggerSet> sets, int selectedTrigger)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var flattened = Flatten(sets);
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

    /// <summary>
    /// The editor for the selected rule — pattern, route-to list, highlight swatches, and toggles.
    /// Empty when nothing is selected.
    /// </summary>
    internal static List<string> EditorColumn(
        IReadOnlyList<TriggerSet> sets,
        int selectedTrigger,
        IReadOnlyList<string> spawnTargets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(spawnTargets);

        var flattened = Flatten(sets);
        return selectedTrigger >= 0 && selectedTrigger < flattened.Count
            ? BuildEditor(flattened[selectedTrigger].Trigger, spawnTargets)
            : new List<string>();
    }

    /// <summary>Flattens every set's triggers into one list, each paired with its owning set's name.</summary>
    private static List<(Trigger Trigger, string SetName)> Flatten(IReadOnlyList<TriggerSet> sets)
    {
        var flattened = new List<(Trigger Trigger, string SetName)>();
        foreach (var set in sets)
        {
            foreach (var trigger in set.Triggers)
            {
                flattened.Add((trigger, set.Name));
            }
        }

        return flattened;
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
}
