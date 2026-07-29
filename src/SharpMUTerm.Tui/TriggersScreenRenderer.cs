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
    /// <summary>
    /// Visible width of the left column. The view lays its column out at exactly this width, so
    /// the two must agree -- a cursor bar padded narrower than its column leaves a gap before the
    /// rule. Shared rather than duplicated so they cannot drift apart.
    /// </summary>
    internal const int ColumnWidth = 56;

    /// <summary>
    /// What the route list calls "no spawn window" — a rule with a null <c>SpawnTarget</c> goes to the
    /// main output. It is a real choice in the radio group, not the absence of one.
    /// </summary>
    internal const string MainWindow = "main";

    /// <summary>The rule row's field ordinals, in the order ⇥ steps through them.</summary>
    private const int PatternField = 0;
    private const int RouteField = 1;
    private const int ForegroundField = 2;
    private const int BackgroundField = 3;

    /// <summary>The window a rule routes to, as the route field reads and writes it.</summary>
    private static string Route(Trigger trigger) => trigger.Actions.SpawnTarget ?? MainWindow;

    /// <summary>
    /// The windows offered as ↑↓ suggestions on the route field: the main output, every spawn window
    /// the workspace knows about, and — always — the one this rule already points at, so a rule
    /// routed somewhere the current workspace has no window for still shows its own value.
    /// <para>
    /// These are suggestions, not the permitted set. Typing a name that isn't here is how a new spawn
    /// window comes into existence: the workspace's spawn windows are defined by what triggers route
    /// to, so a closed list could only ever re-use one that already exists.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> Routes(Trigger trigger, IReadOnlyList<string>? spawnTargets)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        var routes = new List<string> { MainWindow };
        foreach (var target in (spawnTargets ?? Array.Empty<string>()).Append(Route(trigger)))
        {
            if (!string.IsNullOrEmpty(target) && !routes.Contains(target, StringComparer.Ordinal))
            {
                routes.Add(target);
            }
        }

        return routes;
    }

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

        var lines = new List<string> { HeaderLine(0, Model(sets, selectedTrigger, spawnTargets)), string.Empty };

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

    /// <summary>
    /// The screen title on the left, the keyboard hints right-aligned to <paramref name="width"/>. The
    /// hints are derived from <paramref name="model"/> and <paramref name="focus"/> rather than
    /// written here, so the header cannot advertise an edit the screen doesn't offer.
    /// </summary>
    internal static string HeaderLine(int width, ScreenModel? model = null, ScreenFocus? focus = null)
    {
        var title = $"[bold {Value}] Triggers & spawn routing[/]";
        var hints = ScreenChrome.Hints(
            ScreenChrome.ListHints, "F2", model?.HasEditableRow ?? false, focus);
        return SpreadLR(" " + title, hints, width);
    }

    /// <summary>
    /// The screen's navigable panes: the rule list (Space enables/disables a trigger, ⏎ edits its
    /// match pattern and then ⇥ steps through its route and its two highlight colours) and the
    /// selected rule's checkbox rows, in the order <see cref="EditorColumn"/> draws them.
    /// <para>
    /// Everything the editor pane *displays* about the selected rule belongs to that rule's own list
    /// row rather than to the editor pane. That is the same reason the pattern does: a rule's route
    /// and its highlight are one setting each, so making them navigable rows of their own would put
    /// N cursor stops (one per route, one per swatch) in front of one value, and renumber the rows the
    /// cursor already navigates by. As fields they cycle with ↑↓ — which is exactly what a radio group
    /// and a palette are — while the editor keeps drawing them where they are read.
    /// </para>
    /// </summary>
    /// <param name="spawnTargets">
    /// The spawn windows a rule may route to, beyond <c>main</c> and its own current target. Optional
    /// so a caller that only wants the navigable shape (the header hints, the tests) need not know the
    /// workspace's windows.
    /// </param>
    internal static ScreenModel Model(
        IReadOnlyList<TriggerSet> sets,
        int selectedTrigger,
        IReadOnlyList<string>? spawnTargets = null)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var flattened = Flatten(sets);
        var rules = ScreenModel.Rows(flattened, entry => ScreenRow.Of(
            ScreenToggle.Bind(() => entry.Trigger.Enabled, v => entry.Trigger.Enabled = v),
            ScreenField.Pattern(
                "match pattern", () => entry.Trigger.Pattern, v => entry.Trigger.Pattern = v),
            ScreenField.WindowName(
                "route",
                () => Route(entry.Trigger),
                v => entry.Trigger.Actions.SpawnTarget = v == MainWindow ? null : v.Trim(),
                Routes(entry.Trigger, spawnTargets)),
            ScreenField.Colour(
                "highlight fg",
                () => entry.Trigger.Actions.HighlightForeground,
                v => entry.Trigger.Actions.HighlightForeground = v),
            ScreenField.Colour(
                "highlight bg",
                () => entry.Trigger.Actions.HighlightBackground,
                v => entry.Trigger.Actions.HighlightBackground = v)));

        if (selectedTrigger < 0 || selectedTrigger >= flattened.Count)
        {
            return new ScreenModel(rules, Array.Empty<ScreenRow>());
        }

        var trigger = flattened[selectedTrigger].Trigger;
        var editor = new[]
        {
            ScreenRow.Of(ScreenToggle.Bind(() => trigger.Actions.Gag, v => trigger.Actions.Gag = v)),
            ScreenRow.Of(ScreenToggle.Bind(() => trigger.StopProcessing, v => trigger.StopProcessing = v)),
        };

        return new ScreenModel(rules, editor);
    }

    /// <summary>The action bar: which rule is selected on the left, cancel/save on the right.</summary>
    internal static string FooterLine(
        IReadOnlyList<TriggerSet> sets, int selectedTrigger, int width, ScreenFocus? focus = null)
    {
        var flattened = Flatten(sets);
        var context = string.Empty;
        if (flattened.Count > 0 && selectedTrigger >= 0 && selectedTrigger < flattened.Count)
        {
            context = ScreenChrome.Context(
                ScreenChrome.Position("trigger", selectedTrigger, flattened.Count),
                "set " + Escape(flattened[selectedTrigger].SetName));
        }

        var actions = ScreenChrome.Actions(focus: focus);
        return SpreadLR(" " + context, actions, width);
    }

    /// <summary>The rule list — every trigger of every set, each over a set/flags sub-row.</summary>
    internal static List<string> RulesColumn(
        IReadOnlyList<TriggerSet> sets, int selectedTrigger, ScreenFocus? focus = null)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var cursor = focus ?? ScreenFocus.None;
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
            left.Add(ScreenChrome.Cursor(RuleRow(i, selectedTrigger, trigger), cursor.IsOn(0, i), ColumnWidth));
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
        IReadOnlyList<string> spawnTargets,
        ScreenFocus? focus = null)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(spawnTargets);

        var cursor = focus ?? ScreenFocus.None;
        var flattened = Flatten(sets);
        return selectedTrigger >= 0 && selectedTrigger < flattened.Count
            ? BuildEditor(flattened[selectedTrigger].Trigger, spawnTargets, cursor, selectedTrigger)
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

    private static List<string> BuildEditor(
        Trigger trigger, IReadOnlyList<string> spawnTargets, ScreenFocus cursor, int index)
    {
        var pattern = cursor.EditOn(0, index, PatternField);
        var route = cursor.EditOn(0, index, RouteField);
        var foreground = cursor.EditOn(0, index, ForegroundField);
        var background = cursor.EditOn(0, index, BackgroundField);

        // While the route field is open the radios follow the *buffer*, not config, so ↑↓ visibly move
        // the dot before anything is committed — the buffer is what ⏎ would write.
        var currentRoute = route?.Text ?? Route(trigger);

        var lines = new List<string>
        {
            "[dim]match pattern (regex)[/]",
            $"  {ScreenChrome.Field(Escape(trigger.Pattern), pattern)}",
            string.Empty,
            Heading("route to", route),
        };

        var known = Routes(trigger, spawnTargets);
        foreach (var target in known)
        {
            lines.Add(RouteRow(target, currentRoute, route is not null));
        }

        // A route may name a window that doesn't exist yet — that is how a spawn window is created.
        // The rows above are the windows already in use, so a name being typed for the first time
        // matches none of them and would otherwise be invisible: the group would sit with no dot lit
        // while the keyboard was plainly doing something. Give the new name its own row, carrying the
        // caret, so what is being typed is on screen where the committed value will be.
        if (route is { } typing && !known.Contains(currentRoute, StringComparer.Ordinal))
        {
            lines.Add($"  [{Accent}]●[/] {ScreenChrome.Field(Escape(currentRoute), typing)}");
        }

        var fg = trigger.Actions.HighlightForeground;
        var bg = trigger.Actions.HighlightBackground;

        // What the two swatch rows add up to is a caption on the section that owns them, not a fourth
        // checkbox under it. It was drawn as one, which made it look like a fourth thing to press: the
        // cursor cannot land on it, Space does nothing to it, and it sat *below* the two rows it is
        // derived from, so it read as a cause rather than the effect it is. The caption says the same
        // thing, above the rows that decide it, in a shape nothing offers to press.
        lines.Add(string.Empty);
        lines.Add(Heading("highlight", foreground ?? background, HighlightCaption(fg, bg)));
        lines.Add(HighlightRow("fg", fg, foreground));
        lines.Add(HighlightRow("bg", bg, background));
        lines.Add(string.Empty);

        // The two rows below are real booleans on the trigger, and are the editor pane's navigable rows
        // in this order.
        lines.Add(ScreenChrome.Cursor(Checkbox("gag line", trigger.Actions.Gag), cursor.IsOn(1, 0), ColumnWidth));
        lines.Add(ScreenChrome.Cursor(
            Checkbox("stop processing", trigger.StopProcessing), cursor.IsOn(1, 1), ColumnWidth));

        return lines;
    }

    /// <summary>A checkbox row in the editor pane, checked in the accent and unchecked dim.</summary>
    private static string Checkbox(string label, bool value) =>
        value ? $"[{Accent}][[x]][/] {Escape(label)}" : $"[dim][[ ]] {Escape(label)}[/]";

    /// <summary>
    /// A section label, carrying the open field's rejection message when there is one — and otherwise
    /// an optional caption summarising what the rows beneath it come to. A radio group and a pair of
    /// swatches have nowhere sensible to put an error inline, so it hangs off the heading the group
    /// belongs to; an error displaces the caption, because a refused value is the more urgent of the
    /// two and they occupy the same cells.
    /// </summary>
    private static string Heading(string label, ScreenFieldEdit? edit, string? caption = null)
    {
        if (edit?.Error is { } error)
        {
            return $"[dim]{label}[/]  [{Warn}]▲ {Escape(error)}[/]";
        }

        return caption is null ? $"[dim]{label}[/]" : $"[dim]{label}[/]  {caption}";
    }

    /// <summary>
    /// What the two swatch rows amount to, read out beside the section heading: whether a matching line
    /// gets recoloured at all. Muted rather than accented, because it reports the state of the two rows
    /// below it and cannot itself be changed — the same treatment every other readout on these screens
    /// gets (see <see cref="ScreenChrome.ReadOnly"/>).
    /// </summary>
    private static string HighlightCaption(TerminalColor? foreground, TerminalColor? background) =>
        foreground is not null || background is not null
            ? ScreenChrome.ReadOnly("· matching lines are recoloured")
            : "[dim]· matching lines are left alone[/]";

    /// <summary>
    /// One radio of the route group. <paramref name="editing"/> wells the whole group so it reads as
    /// live rather than as the report it is the rest of the time — the selected radio moves with ↑↓,
    /// and a group that looked identical either way would give no sign the keyboard had it.
    /// </summary>
    private static string RouteRow(string label, string currentRoute, bool editing)
    {
        var selected = string.Equals(label, currentRoute, StringComparison.Ordinal);
        if (!selected)
        {
            return $"  [dim]○[/] {Escape(label)}";
        }

        return editing
            ? $"  [{Accent}]●[/] [{Value} on {FieldBg}]{Escape(label)} [/]"
            : $"  [{Accent}]●[/] {Escape(label)}";
    }

    /// <summary>
    /// A highlight swatch and the colour it is set to, drawn as a field so an open picker shows its
    /// buffer and caret here. An unset colour gets a hollow swatch rather than none at all, so the row
    /// is visibly a place a colour goes.
    /// </summary>
    private static string HighlightRow(string label, TerminalColor? colour, ScreenFieldEdit? edit)
    {
        var swatch = colour is { } set ? $"[{ScreenColours.Hex(set, Accent)}]████[/]" : $"[{Rule}]░░░░[/]";
        var name = ScreenColours.Format(colour);
        var display = colour is null ? $"[dim]{name}[/]" : $"[{Value}]{Escape(name)}[/]";
        return $"  {swatch} {label}  {ScreenChrome.Field(display, edit)}";
    }
}
