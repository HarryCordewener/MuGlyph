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
/// (pattern, route-to list, highlight swatches and attributes, the three action templates, and
/// the toggles), and the footer action bar.
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

    /// <summary>
    /// The rule row's field ordinals, in the order ⇥ steps through them. The name leads, as it does on
    /// every list screen: it is what the row is *called*, so ⏎ on a rule — and on a rule that was just
    /// created — opens the one value that tells it apart from its neighbours. They are named constants
    /// rather than literals because the renderer, the model and the tests all address the same
    /// ordinals, and a screen that grew a field would otherwise silently draw the caret on the wrong row.
    /// </summary>
    internal const int NameField = 0;

    internal const int PatternField = 1;

    internal const int RouteField = 2;

    internal const int ForegroundField = 3;

    internal const int BackgroundField = 4;

    internal const int AttributesField = 5;

    internal const int RewriteField = 6;

    internal const int ResponseField = 7;

    internal const int ScriptField = 8;

    /// <summary>
    /// How wide the <c>fg</c> / <c>bg</c> / <c>attrs</c> and <c>rewrite</c> / <c>respond</c> /
    /// <c>script</c> labels are padded, so the field wells in each section start in the same column.
    /// </summary>
    private const int HighlightLabelWidth = 5;

    private const int ActionLabelWidth = 7;

    /// <summary>How many attributes a legend row lists before wrapping to the next.</summary>
    private const int AttributesPerRow = 4;

    /// <summary>
    /// What an unconfigured action reads as. An empty well would say nothing at all, and the whole
    /// point of these three being null-when-blank is that "this rule does not rewrite" is a state worth
    /// naming — not the same thing as "this rule rewrites to the empty string".
    /// </summary>
    private const string OffLabel = "(off)";

    /// <summary>The vocabulary the attributes field accepts, in the enum's own declaration order.</summary>
    private static readonly IReadOnlyList<string> AttributeNames = ScreenField.FlagNames<TextAttributes>();

    /// <summary>The labels the rule list's buttons carry, in the order they are drawn.</summary>
    internal const string AddTriggerLabel = "+ trigger";

    internal const string DuplicateTriggerLabel = "⧉ duplicate";

    internal const string RemoveTriggerLabel = "- del";

    /// <summary>
    /// What a brand-new rule is called and matches. The pattern is a placeholder rather than blank
    /// because <see cref="ScreenField.Pattern"/> refuses an empty regex, so a blank one could not be
    /// committed — and a rule created with no actions at all does nothing to the stream whatever it
    /// matches, which is why a new trigger is left enabled while a new timer is not (a timer fires
    /// without being provoked; a trigger with nothing to do does not).
    /// </summary>
    private const string NewTriggerName = "New Trigger";

    private const string NewTriggerPattern = "text to match";

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
    /// The script callbacks offered as ↑↓ suggestions on a rule's <c>script</c> field: every callback
    /// named anywhere in the configuration — by a trigger, an alias, a timer or a macro — plus the one
    /// this rule already names, so a rule pointing at a function nothing else calls still shows its own
    /// value.
    /// <para>
    /// They are suggestions, not the permitted set, and they deliberately do <em>not</em> come from the
    /// scripting host. A <see cref="SharpMUTerm.Scripting.ScriptHost"/> cannot enumerate anything
    /// useful here: the callbacks it holds are keyed by ids it generates itself
    /// (<c>trigger#1</c>), created by <c>trigger.add(pattern, function ... end)</c> whose function is
    /// anonymous, and written into <em>runtime</em> triggers rather than into the configuration this
    /// screen edits. What a user can actually type is a name their own Lua defines, so the honest
    /// suggestion list is the vocabulary their configuration already uses.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> Callbacks(Trigger trigger, IReadOnlyList<string>? known)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        var callbacks = new List<string>();
        foreach (var callback in (known ?? Array.Empty<string>()).Append(trigger.Actions.ScriptCallback))
        {
            if (!string.IsNullOrWhiteSpace(callback) && !callbacks.Contains(callback, StringComparer.Ordinal))
            {
                callbacks.Add(callback);
            }
        }

        return callbacks;
    }

    /// <summary>
    /// Every script callback the configuration names, swept once per projection rather than once per
    /// rule — the model is rebuilt on every keystroke, and this is the only part of a row that has to
    /// look at every other set to build itself.
    /// </summary>
    private static List<string> NamedCallbacks(IReadOnlyList<TriggerSet> sets)
    {
        var named = new List<string>();
        foreach (var set in sets)
        {
            var all = set.Triggers.Select(t => t.Actions.ScriptCallback)
                .Concat(set.Aliases.Select(a => a.ScriptCallback))
                .Concat(set.Timers.Select(t => t.ScriptCallback))
                .Concat(set.Macros.Select(m => m.ScriptCallback));

            foreach (var callback in all)
            {
                if (!string.IsNullOrWhiteSpace(callback) && !named.Contains(callback, StringComparer.Ordinal))
                {
                    named.Add(callback);
                }
            }
        }

        return named;
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
            ScreenChrome.ListHints, "F2", model?.HasEditableRow ?? false, focus, model?.HasRemovableRow ?? false);
        return SpreadLR(" " + title, hints, width);
    }

    /// <summary>
    /// The screen's navigable panes: the rule list (Space enables/disables a trigger, ⏎ edits its name
    /// and then ⇥ steps through its pattern, route, two highlight colours, attributes, and its rewrite,
    /// response and script templates) and the selected rule's checkbox rows, in the order
    /// <see cref="EditorColumn"/> draws them.
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
        var callbacks = NamedCallbacks(sets);
        var rules = ScreenModel.Rows(flattened, entry => ScreenRow.Of(
            ScreenToggle.Bind(() => entry.Trigger.Enabled, v => entry.Trigger.Enabled = v),
            ScreenField.Name("name", () => entry.Trigger.Name, v => entry.Trigger.Name = v),
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
                v => entry.Trigger.Actions.HighlightBackground = v),
            ScreenField.Flags<TextAttributes>(
                "attributes",
                () => entry.Trigger.Actions.AddAttributes,
                v => entry.Trigger.Actions.AddAttributes = v),
            ScreenField.Template(
                "rewrite", () => entry.Trigger.Actions.Rewrite, v => entry.Trigger.Actions.Rewrite = v),
            ScreenField.Template(
                "respond",
                () => entry.Trigger.Actions.SendResponse,
                v => entry.Trigger.Actions.SendResponse = v),
            ScreenField.Template(
                "script",
                () => entry.Trigger.Actions.ScriptCallback,
                v => entry.Trigger.Actions.ScriptCallback = v,
                Callbacks(entry.Trigger, callbacks))))
            .Concat(Buttons(sets, selectedTrigger))
            .ToArray();

        if (selectedTrigger < 0 || selectedTrigger >= flattened.Count)
        {
            return new ScreenModel(rules, Array.Empty<ScreenRow>());
        }

        var trigger = flattened[selectedTrigger].Trigger;
        // Appended rather than inserted: these are the pane's cursor stops, and putting the new one
        // above the two that were here would renumber rows the screen (and its tests) already address.
        var editor = new[]
        {
            ScreenRow.Of(ScreenToggle.Bind(() => trigger.Actions.Gag, v => trigger.Actions.Gag = v)),
            ScreenRow.Of(ScreenToggle.Bind(() => trigger.StopProcessing, v => trigger.StopProcessing = v)),
            ScreenRow.Of(ScreenToggle.Bind(() => trigger.CaseSensitive, v => trigger.CaseSensitive = v)),
        };

        return new ScreenModel(rules, editor);
    }

    /// <summary>
    /// The rule list's buttons. A rule is added to the set that owns the selection rather than to some
    /// fixed one, because the list is flattened across every set and a rule appearing in a set the user
    /// wasn't looking at would be a change they never asked for; with nothing selected it goes to the
    /// first set, and with no sets at all there is nowhere to put one, so the button isn't drawn.
    /// <para>
    /// <c>duplicate</c> earns its place here more than anywhere else on these screens: a rule is a
    /// regex, a route, two colours and three flags, and the way people actually build a rule set is to
    /// get one right and then copy it per channel. It deep-copies through
    /// <see cref="Trigger.Clone"/> — an aliased copy would share its actions with the original, so
    /// gagging one would gag both — and the copy is renamed so it can be told from its source in the
    /// list it lands in.
    /// </para>
    /// </summary>
    private static List<ScreenRow> Buttons(IReadOnlyList<TriggerSet> sets, int selected)
    {
        var rows = new List<ScreenRow>();
        if (ScreenLists.Target(sets, s => s.Triggers, selected) is not { } target)
        {
            return rows;
        }

        rows.Add(ScreenRow.Of(ScreenButton.Add(
            AddTriggerLabel,
            target.Items,
            () => new Trigger { Name = NewTriggerName, Pattern = NewTriggerPattern },
            target.Offset)));

        if (ScreenLists.Locate(sets, s => s.Triggers, selected) is not { } slot)
        {
            return rows;
        }

        var source = slot.Items[slot.Index];
        rows.Add(ScreenRow.Of(ScreenButton.Add(
            DuplicateTriggerLabel,
            slot.Items,
            () =>
            {
                var copy = source.Clone();
                copy.Name = ScreenLists.Unique(sets.SelectMany(s => s.Triggers).Select(t => t.Name), source.Name);
                return copy;
            },
            slot.Offset,
            source.Name)));
        rows.Add(ScreenRow.Of(ScreenButton.Remove(
            RemoveTriggerLabel, slot.Items, slot.Index, slot.Offset, source.Name)));

        return rows;
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
        }

        for (var i = 0; i < flattened.Count; i++)
        {
            var (trigger, setName) = flattened[i];
            left.Add(ScreenChrome.Cursor(RuleRow(i, selectedTrigger, trigger), cursor.IsOn(0, i), ColumnWidth));
            left.Add(RuleSub(setName, trigger.Actions));
        }

        left.Add(string.Empty);
        left.AddRange(ScreenChrome.Buttons(
            Buttons(sets, selectedTrigger), cursor, 0, flattened.Count, ColumnWidth));
        return left;
    }

    /// <summary>
    /// The editor for the selected rule — pattern, route-to list, highlight swatches and attributes,
    /// the rewrite/respond/script templates, and the toggles. Empty when nothing is selected.
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

        // Attributes count as highlighting because that is literally what they are: TriggerEngine
        // restyles the matched region when a colour *or* an attribute is set, through one code path.
        if (actions.HighlightForeground is not null
            || actions.HighlightBackground is not null
            || actions.AddAttributes != TextAttributes.None)
        {
            flags.Add("H");
        }

        if (actions.Gag)
        {
            flags.Add("G");
        }

        if (!string.IsNullOrEmpty(actions.Rewrite))
        {
            flags.Add("✎");
        }

        if (!string.IsNullOrEmpty(actions.SendResponse))
        {
            flags.Add("R");
        }

        if (actions.SpawnTarget is not null)
        {
            flags.Add(Glyphs.Capture);
        }

        if (!string.IsNullOrEmpty(actions.ScriptCallback))
        {
            flags.Add("ƒ");
        }

        return flags.Count == 0 ? "—" : string.Join(" ", flags);
    }

    private static List<string> BuildEditor(
        Trigger trigger, IReadOnlyList<string> spawnTargets, ScreenFocus cursor, int index)
    {
        var name = cursor.EditOn(0, index, NameField);
        var pattern = cursor.EditOn(0, index, PatternField);
        var route = cursor.EditOn(0, index, RouteField);
        var foreground = cursor.EditOn(0, index, ForegroundField);
        var background = cursor.EditOn(0, index, BackgroundField);
        var attributes = cursor.EditOn(0, index, AttributesField);
        var rewrite = cursor.EditOn(0, index, RewriteField);
        var response = cursor.EditOn(0, index, ResponseField);
        var script = cursor.EditOn(0, index, ScriptField);

        // While the route field is open the radios follow the *buffer*, not config, so ↑↓ visibly move
        // the dot before anything is committed — the buffer is what ⏎ would write.
        var currentRoute = route?.Text ?? Route(trigger);

        // The name leads the editor because it leads the row's fields: ⏎ on a rule opens it here, which
        // is also where a rule created by [+ trigger] lands ready to be called something.
        var lines = new List<string>
        {
            "[dim]name[/]",
            $"  {ScreenChrome.Field(Escape(trigger.Name), name)}",
            string.Empty,
            "[dim]match pattern (regex)[/]",
            $"  {ScreenChrome.Field(Escape(trigger.Pattern), pattern)}",
            string.Empty,
            Heading("route to", route),

            // The window is a name you type, not one of a fixed set — the spawn windows that exist
            // are defined by what routes to them, so a closed list could only ever re-use one. It is
            // therefore drawn as an editable value like every other row in this pane rather than as a
            // radio group: the group cost five rows, was the only control here shaped differently
            // from its neighbours, and could not show a name being typed for the first time without a
            // row invented for the purpose. The windows already in use remain ↑↓ suggestions while
            // the field is open, which is what a radio group was really offering.
            $"  {ScreenChrome.Field(Escape(currentRoute), route)}",
        };

        var fg = trigger.Actions.HighlightForeground;
        var bg = trigger.Actions.HighlightBackground;
        var attrs = trigger.Actions.AddAttributes;

        // What the swatch rows add up to is a caption on the section that owns them, not a fourth
        // checkbox under it. It was drawn as one, which made it look like a fourth thing to press: the
        // cursor cannot land on it, Space does nothing to it, and it sat *below* the two rows it is
        // derived from, so it read as a cause rather than the effect it is. The caption says the same
        // thing, above the rows that decide it, in a shape nothing offers to press.
        //
        // Attributes belong to this section rather than one of their own because TriggerEngine restyles
        // a matched region through a single path for colours and attributes alike — and while the
        // caption only knew about colours it flatly lied about a bold-only rule.
        lines.Add(string.Empty);
        lines.Add(Heading("highlight", foreground ?? background ?? attributes, HighlightCaption(fg, bg, attrs)));
        lines.Add(HighlightRow("fg", fg, foreground));
        lines.Add(HighlightRow("bg", bg, background));
        lines.Add(AttributeRow(attrs, attributes));
        lines.AddRange(AttributeLegend(attributes?.Text ?? ScreenField.FormatFlags(attrs)));

        // The three action templates. They are one section because they are the three things a rule can
        // *do* to a line beyond recolouring it, and they are not exclusive — a rule may gag a line,
        // answer it and call a script off the same match.
        lines.Add(string.Empty);
        lines.Add(Heading("actions", null, ScreenChrome.ReadOnly("· $1..$9 insert captures")));
        lines.Add(ActionRow("rewrite", trigger.Actions.Rewrite, rewrite));
        lines.Add(ActionRow("respond", trigger.Actions.SendResponse, response));
        lines.Add(ActionRow("script", trigger.Actions.ScriptCallback, script));
        lines.Add(string.Empty);

        // The three rows below are real booleans on the trigger, and are the editor pane's navigable
        // rows in this order.
        lines.Add(ScreenChrome.Cursor(Checkbox("gag line", trigger.Actions.Gag), cursor.IsOn(1, 0), ColumnWidth));
        lines.Add(ScreenChrome.Cursor(
            Checkbox("stop processing", trigger.StopProcessing), cursor.IsOn(1, 1), ColumnWidth));
        lines.Add(ScreenChrome.Cursor(
            Checkbox("case sensitive", trigger.CaseSensitive), cursor.IsOn(1, 2), ColumnWidth));

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
    /// What the section's rows amount to, read out beside its heading: whether a matching line gets
    /// restyled at all. Muted rather than accented, because it reports the state of the rows below it
    /// and cannot itself be changed — the same treatment every other readout on these screens gets (see
    /// <see cref="ScreenChrome.ReadOnly"/>).
    /// <para>
    /// Attributes are read here as well as the colours, because the engine restyles on either. Left to
    /// the colours alone, a rule that only bolds its match would have been captioned "left alone" while
    /// visibly bolding every line it matched.
    /// </para>
    /// </summary>
    private static string HighlightCaption(
        TerminalColor? foreground, TerminalColor? background, TextAttributes attributes)
    {
        if (foreground is not null || background is not null)
        {
            return ScreenChrome.ReadOnly("· matching lines are recoloured");
        }

        return attributes != TextAttributes.None
            ? ScreenChrome.ReadOnly("· matching text is restyled")
            : "[dim]· matching lines are left alone[/]";
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
        return $"  {swatch} {PadVisible(label, HighlightLabelWidth)} {ScreenChrome.Field(display, edit)}";
    }

    /// <summary>
    /// The attributes value, drawn in the same <c>swatch label value</c> shape as the two colours it
    /// sits under — one field well, because it is one setting, however many booleans it packs. Its
    /// swatch is filled or hollow on the same rule as theirs: something is added to the match, or
    /// nothing is.
    /// </summary>
    private static string AttributeRow(TextAttributes attributes, ScreenFieldEdit? edit)
    {
        var swatch = attributes == TextAttributes.None ? $"[{Rule}]░░░░[/]" : $"[{Accent}]▚▚▚▚[/]";
        var spec = ScreenField.FormatFlags(attributes);
        var display = attributes == TextAttributes.None
            ? $"[dim]{Escape(spec)}[/]"
            : $"[{Value}]{Escape(spec)}[/]";
        return $"  {swatch} {PadVisible("attrs", HighlightLabelWidth)} {ScreenChrome.Field(display, edit)}";
    }

    /// <summary>
    /// The whole attribute vocabulary, wrapped over as many rows as it takes, each name lit when it is
    /// in <paramref name="spec"/> and muted when it isn't.
    /// <para>
    /// This is the "row of checkboxes" the setting really is, and it is drawn rather than navigated
    /// deliberately. Eight <see cref="ScreenToggle"/> rows would be eight cursor stops for a setting
    /// most rules never touch, burying the three flags below them; and a <see cref="ScreenRow"/> holds
    /// at most one checkbox, so a *horizontal* row of them could never be one navigable row anyway. As
    /// a legend it does the one job the field cannot: it says what the legal words are, so nobody has
    /// to type into <c>attrs</c> blind.
    /// </para>
    /// <para>
    /// It follows the <em>buffer</em> while the field is open, exactly as the route radios do — a
    /// legend that only moved on ⏎ would look inert for precisely as long as it was being used.
    /// </para>
    /// </summary>
    private static IEnumerable<string> AttributeLegend(string spec)
    {
        // Column widths, not one width for all: "strikethrough" is thirteen cells and padding every
        // name to it would push the legend past the editor column at the widths this screen is used at.
        var widths = new int[AttributesPerRow];
        for (var i = 0; i < AttributeNames.Count; i++)
        {
            widths[i % AttributesPerRow] = Math.Max(widths[i % AttributesPerRow], AttributeNames[i].Length);
        }

        for (var start = 0; start < AttributeNames.Count; start += AttributesPerRow)
        {
            var cells = new List<string>(AttributesPerRow);
            for (var i = start; i < Math.Min(start + AttributesPerRow, AttributeNames.Count); i++)
            {
                var name = AttributeNames[i];
                var padded = name.PadRight(widths[i % AttributesPerRow]);
                cells.Add(ScreenField.FlagIsListed(spec, name)
                    ? $"[{Accent}]✓{padded}[/]"
                    : $"[dim]·{padded}[/]");
            }

            yield return "       " + string.Join(" ", cells).TrimEnd();
        }
    }

    /// <summary>
    /// One of the three action templates: its label, then its value in a field well — or a muted
    /// <c>(off)</c> in that same well when nothing is set. The well stays either way, because the row is
    /// still a place a value goes; what changes is that the screen says, in words, that this rule does
    /// not rewrite / does not answer / calls nothing, which is a different state from a template that
    /// happens to be empty (see <see cref="ScreenField.Template"/>, which stores blank as null).
    /// </summary>
    private static string ActionRow(string label, string? value, ScreenFieldEdit? edit)
    {
        var display = string.IsNullOrEmpty(value)
            ? $"[dim]{OffLabel}[/]"
            : $"[{Value}]{Escape(value)}[/]";
        return $"  {PadVisible(label, ActionLabelWidth)} {ScreenChrome.Field(display, edit)}";
    }
}
