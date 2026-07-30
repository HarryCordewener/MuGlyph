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
    /// Visible width of the left column when the screen can afford it. The view lays its column out at
    /// exactly the width it passes back in, so the two must agree -- a cursor bar padded narrower than
    /// its column leaves a gap before the rule. Shared rather than duplicated so they cannot drift apart.
    /// </summary>
    internal const int ColumnWidth = 56;

    /// <summary>The fewest cells the timer list is still worth drawing in.</summary>
    internal const int MinColumnWidth = 40;

    /// <summary>The fewest cells the editor pane can be read in — its widest heading and then some.</summary>
    internal const int MinEditorWidth = 32;

    /// <summary>
    /// What the timer list's key is called, and the two marks it glosses. A row reads
    /// <c>✓ ▸ keepalive every 60s ▪ Comms → @@idle</c>: the tick is the enabled state <c>on</c> heads
    /// without really explaining, and the square is the owning set. Named constants because the row
    /// draws the same glyphs and the key would otherwise be a second, drifting copy of them.
    /// </summary>
    private const string LegendLabel = "key";

    private const string EnabledGlyph = "✓";

    private const string SetGlyph = "▪";

    /// <summary>
    /// The timer row's field ordinals, in the order ⇥ steps through them. The name leads, as it does on
    /// every list screen: ⏎ on a timer — including one just created — opens the value that tells it
    /// apart from its neighbours. Named rather than written as literals because the renderer, the model
    /// and the tests all address the same ordinals.
    /// </summary>
    internal const int NameField = 0;

    internal const int IntervalField = 1;

    internal const int CommandField = 2;

    /// <summary>
    /// Which <see cref="TriggerSet"/> the timer lives in, appended last so the ordinals above keep
    /// meaning what they meant. Committing it moves the timer — see <see cref="ScreenLists.Owner{T}"/>.
    /// </summary>
    internal const int SetField = 3;

    /// <summary>The labels the timer list's buttons carry, in the order they are drawn.</summary>
    internal const string AddTimerLabel = "+ timer";

    /// <summary>
    /// What a brand-new timer is called, waits and sends. It is created <b>disabled</b>, alone among
    /// the four list screens: a timer is the only thing here that acts without being provoked, so a new
    /// one left running would start sending a placeholder command at the server a minute later, which
    /// nobody asked for. A new trigger only reacts to output and a new binding only to a keypress, so
    /// those stay live.
    /// </summary>
    private const string NewTimerName = "New Timer";

    private const double NewTimerIntervalSeconds = 60;

    private const string NewTimerCommand = "look";

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
            ScreenChrome.ListHints, "F6", model?.HasEditableRow ?? false, focus, model?.HasRemovableRow ?? false);
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
            ScreenField.Name("name", () => entry.Timer.Name, v => entry.Timer.Name = v),
            ScreenField.Number(
                "interval",
                () => entry.Timer.IntervalSeconds,
                v => entry.Timer.IntervalSeconds = v,
                MinIntervalSeconds,
                MaxIntervalSeconds),
            ScreenField.Text("command", () => entry.Timer.Command, v => entry.Timer.Command = v),
            ScreenLists.Owner(sets, s => s.Timers, entry.Timer)))
            .Concat(Buttons(sets, selected))
            .ToArray();

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

    /// <summary>
    /// The timer list's buttons. A timer is added to the set that owns the selection, so a new one
    /// appears in the set the user is looking at rather than wherever the configuration ends.
    /// <para>
    /// There is deliberately no <c>duplicate</c> here, unlike F2 and F3. A timer is three values
    /// (interval, command, one-shot), and two of them are exactly what you would change in the copy —
    /// so <c>[[+ timer]]</c> and typing is no slower than duplicating and retyping, and the screen is
    /// one row shorter for it. A button that saves nobody anything is still a cursor stop.
    /// </para>
    /// </summary>
    private static List<ScreenRow> Buttons(IReadOnlyList<TriggerSet> sets, int selected)
    {
        var rows = new List<ScreenRow>();
        if (ScreenLists.Target(sets, s => s.Timers, selected) is not { } target)
        {
            return rows;
        }

        rows.Add(ScreenRow.Of(ScreenButton.Add(
            AddTimerLabel,
            target.Items,
            () => new TimerDefinition
            {
                Name = NewTimerName,
                IntervalSeconds = NewTimerIntervalSeconds,
                Command = NewTimerCommand,
                Enabled = false,
            },
            target.Offset)));

        if (ScreenLists.Locate(sets, s => s.Timers, selected) is { } slot)
        {
            var timer = slot.Items[slot.Index];
            rows.Add(ScreenRow.Of(ScreenButton.Remove(
                slot.Items, slot.Index, slot.Offset, timer.Name, () => $"timer {timer.Name}")));
        }

        return rows;
    }

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
    /// <param name="width">
    /// How wide the column actually runs, which the cursor bars are padded to — the view gives cells
    /// back to the editor on a narrow screen (see <see cref="ScreenChrome.SplitWidth"/>).
    /// </param>
    internal static List<string> ListColumn(
        IReadOnlyList<TriggerSet> sets, int selected, ScreenFocus? focus = null, int width = ColumnWidth)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var cursor = focus ?? ScreenFocus.None;
        var entries = Flatten(sets);
        var lines = new List<string> { "[dim]on  name  every  → command[/]" };

        if (entries.Count == 0)
        {
            lines.Add("[dim]no timers[/]");
        }

        // Walked per set rather than down the flattened list, so a set holding no timers can still say
        // so — it owns none of these rows and would otherwise be drawn nowhere at all. The row indices
        // are the flattened ones either way: the placeholder is markup, not a cursor stop.
        var index = 0;
        foreach (var set in sets)
        {
            if (set.Timers.Count == 0)
            {
                lines.Add(ScreenChrome.EmptySet(set.Name, "timers"));
                continue;
            }

            foreach (var timer in set.Timers)
            {
                lines.Add(ScreenChrome.Cursor(
                    Row(timer, set.Name, index == selected), cursor.IsOn(0, index), width));
                index++;
            }
        }

        lines.Add(string.Empty);
        lines.AddRange(ScreenChrome.Buttons(Buttons(sets, selected), cursor, 0, entries.Count, width));
        lines.Add(string.Empty);
        var picked = selected >= 0 && selected < entries.Count ? entries[selected].Timer : null;
        lines.AddRange(ScreenChrome.Legend(
            LegendLabel,
            new[]
            {
                ScreenChrome.LegendEntry(EnabledGlyph, "enabled", picked?.Enabled ?? false),
                ScreenChrome.LegendEntry(SetGlyph, "set", picked is not null),
            },
            width));
        return lines;
    }

    /// <summary>
    /// The editor for the selected timer — interval, command, and the one-shot/enabled toggles.
    /// Empty when nothing is selected.
    /// </summary>
    /// <param name="width">How wide the pane runs, which its cursor bars and dropdowns are sized to.</param>
    /// <param name="height">How many rows the pane has, or 0 when the caller has none.</param>
    internal static List<string> EditorColumn(
        IReadOnlyList<TriggerSet> sets,
        int selected,
        ScreenFocus? focus = null,
        int width = ColumnWidth,
        int height = 0)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var cursor = focus ?? ScreenFocus.None;
        var entries = Flatten(sets);
        if (selected < 0 || selected >= entries.Count)
        {
            return new List<string>();
        }

        // Compacted before the dropdown is laid over it: the overlay replaces rows by index, so dropping
        // a blank underneath it afterwards would slide the list off the field it belongs to.
        var lines = ScreenChrome.Compact(
            BuildEditor(entries[selected].Timer, entries[selected].SetName, cursor, selected, width), height);
        return ScreenChrome.Choices(lines, cursor.Edit, width);
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

    /// <summary>
    /// The editor rows for one timer. The name leads because it leads the row's fields: ⏎ on a timer
    /// opens it here, which is also where one created by <c>[[+ timer]]</c> lands ready to be named. The
    /// set follows it, because the list is flattened across every set and the two together are what
    /// identify the timer; committing it <em>moves</em> the timer (<see cref="ScreenLists.Owner{T}"/>).
    /// </summary>
    private static List<string> BuildEditor(
        TimerDefinition timer,
        string setName,
        ScreenFocus cursor,
        int selected,
        int width = ColumnWidth) => new()
    {
        "[dim]name[/]",
        $"  {ScreenChrome.Field(Escape(timer.Name), cursor.EditOn(0, selected, NameField))}",
        string.Empty,
        "[dim]set[/]",
        $"  {ScreenChrome.Field($"[{Value}]{Escape(setName)}[/]", cursor.EditOn(0, selected, SetField))}",
        string.Empty,
        "[dim]interval (seconds)[/]",
        $"  {ScreenChrome.Field(Seconds(timer), cursor.EditOn(0, selected, IntervalField))}",
        string.Empty,
        "[dim]command[/]",
        $"  {ScreenChrome.Field(Escape(timer.Command), cursor.EditOn(0, selected, CommandField))}",
        string.Empty,
        ScreenChrome.Cursor(Checkbox("one-shot", timer.OneShot), cursor.IsOn(1, 0), width),
        ScreenChrome.Cursor(Checkbox("enabled", timer.Enabled), cursor.IsOn(1, 1), width),
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
