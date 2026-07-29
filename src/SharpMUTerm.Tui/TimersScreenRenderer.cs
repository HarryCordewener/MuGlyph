using System.Globalization;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using static SharpMUTerm.Tui.MarkupText;
using static SharpMUTerm.Tui.ScreenPalette;

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
    /// <summary>
    /// Visible width of the left column. The view lays its column out at exactly this width, so
    /// the two must agree -- a cursor bar padded narrower than its column leaves a gap before the
    /// rule. Shared rather than duplicated so they cannot drift apart.
    /// </summary>
    internal const int ColumnWidth = 56;

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

        var lines = new List<string> { HeaderLine(0, Model(sets, selected)), string.Empty };

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

    /// <summary>
    /// The screen title on the left, the keyboard hints right-aligned to <paramref name="width"/>. The
    /// hints are derived from <paramref name="model"/> and <paramref name="focus"/> rather than
    /// written here, so the header cannot advertise an edit the screen doesn't offer.
    /// </summary>
    internal static string HeaderLine(int width, ScreenModel? model = null, ScreenFocus? focus = null)
    {
        var title = $"[bold {Value}] Timers[/]";
        var hints = ScreenChrome.Hints(
            ScreenChrome.ListHints, "F6", model?.HasEditableRow ?? false, focus);
        return SpreadLR(" " + title, hints, width);
    }

    /// <summary>
    /// The screen's navigable panes: the timer list (Space enables/disables one, ⏎ edits its interval
    /// and then — with ⇥ — its command) and the selected timer's checkbox rows, in the order
    /// <see cref="EditorColumn"/> draws them. Both values hang off the list row rather than becoming
    /// rows of their own, so the editor pane's cursor indices keep meaning what they meant.
    /// </summary>
    internal static ScreenModel Model(IReadOnlyList<TriggerSet> sets, int selected)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var entries = Flatten(sets);
        var list = ScreenModel.Rows(entries, entry => ScreenRow.Of(
            ScreenToggle.Bind(() => entry.Timer.Enabled, v => entry.Timer.Enabled = v),
            ScreenField.Number(
                "interval",
                () => entry.Timer.IntervalSeconds,
                v => entry.Timer.IntervalSeconds = v,
                MinIntervalSeconds,
                MaxIntervalSeconds),
            ScreenField.Text("command", () => entry.Timer.Command, v => entry.Timer.Command = v)));

        if (selected < 0 || selected >= entries.Count)
        {
            return new ScreenModel(list, Array.Empty<ScreenRow>());
        }

        var timer = entries[selected].Timer;
        var editor = new[]
        {
            ScreenRow.Of(ScreenToggle.Bind(() => timer.OneShot, v => timer.OneShot = v)),
            ScreenRow.Of(ScreenToggle.Bind(() => timer.Enabled, v => timer.Enabled = v)),
        };

        return new ScreenModel(list, editor);
    }

    /// <summary>
    /// The shortest interval a timer may be given. Zero or less is "disabled" to the scheduler, which
    /// is what the Enabled checkbox is for — typing it into the interval would silently turn the timer
    /// off while it still read as on.
    /// </summary>
    private const double MinIntervalSeconds = 0.1;

    /// <summary>A day; past this the value is far likelier to be a typo than a schedule.</summary>
    private const double MaxIntervalSeconds = 86400;

    /// <summary>The action bar: which timer is selected on the left, cancel/save on the right.</summary>
    internal static string FooterLine(
        IReadOnlyList<TriggerSet> sets, int selected, int width, ScreenFocus? focus = null)
    {
        var entries = Flatten(sets);
        var context = string.Empty;
        if (entries.Count > 0 && selected >= 0 && selected < entries.Count)
        {
            context = ScreenChrome.Context(
                ScreenChrome.Position("timer", selected, entries.Count),
                "set " + Escape(entries[selected].SetName));
        }

        var actions = ScreenChrome.Actions(focus: focus);
        return SpreadLR(" " + context, actions, width);
    }

    /// <summary>The timer list — every timer of every set, with its enabled state and schedule.</summary>
    internal static List<string> ListColumn(
        IReadOnlyList<TriggerSet> sets, int selected, ScreenFocus? focus = null)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var cursor = focus ?? ScreenFocus.None;
        var entries = Flatten(sets);
        var lines = new List<string> { "[dim]on  name  every  → command[/]" };

        if (entries.Count == 0)
        {
            lines.Add("[dim]no timers[/]");
            return lines;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var row = Row(entries[i].Timer, entries[i].SetName, i == selected);
            lines.Add(ScreenChrome.Cursor(row, cursor.IsOn(0, i), ColumnWidth));
        }

        return lines;
    }

    /// <summary>
    /// The editor for the selected timer — interval, command, and the one-shot/enabled toggles.
    /// Empty when nothing is selected.
    /// </summary>
    internal static List<string> EditorColumn(
        IReadOnlyList<TriggerSet> sets, int selected, ScreenFocus? focus = null)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var cursor = focus ?? ScreenFocus.None;
        var entries = Flatten(sets);
        return selected >= 0 && selected < entries.Count
            ? BuildEditor(entries[selected].Timer, cursor, selected)
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

    private static List<string> BuildEditor(TimerDefinition timer, ScreenFocus cursor, int selected) => new()
    {
        "[dim]interval (seconds)[/]",
        $"  {ScreenChrome.Field(Seconds(timer), cursor.EditOn(0, selected, 0))}",
        string.Empty,
        "[dim]command[/]",
        $"  {ScreenChrome.Field(Escape(timer.Command), cursor.EditOn(0, selected, 1))}",
        string.Empty,
        ScreenChrome.Cursor(Checkbox("one-shot", timer.OneShot), cursor.IsOn(1, 0), ColumnWidth),
        ScreenChrome.Cursor(Checkbox("enabled", timer.Enabled), cursor.IsOn(1, 1), ColumnWidth),
    };

    /// <summary>A checkbox row in the editor pane, checked in the accent and unchecked dim.</summary>
    private static string Checkbox(string label, bool value) =>
        value ? $"[{Accent}][[x]][/] {Escape(label)}" : $"[dim][[ ]] {Escape(label)}[/]";

    /// <summary>The row-level schedule summary: <c>every 30s</c>, or <c>once after 5.5s</c>.</summary>
    private static string Schedule(TimerDefinition timer) =>
        timer.OneShot ? $"once after {Seconds(timer)}s" : $"every {Seconds(timer)}s";

    private static string Seconds(TimerDefinition timer) =>
        timer.IntervalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
}
