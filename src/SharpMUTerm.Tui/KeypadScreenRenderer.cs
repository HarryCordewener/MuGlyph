using System.Globalization;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using static SharpMUTerm.Tui.MarkupText;
using static SharpMUTerm.Tui.ScreenPalette;

namespace SharpMUTerm.Tui;

/// <summary>
/// Produces the markup sub-blocks for the F4 Keypad &amp; hotkeys screen — the header band, the 3×3
/// numpad grid (Num7..Num9 top row through Num1..Num3 bottom row, numpad order) showing the command
/// bound to each digit, the binding list of every macro with its enabled state, key, and command,
/// and the footer action bar. <see cref="KeypadScreenView"/> composes these into real panels (grids)
/// for the live/snapshot view; <see cref="Render"/> merges the same blocks into a single line list
/// for the unit tests. Pure so every block is testable.
/// <para>
/// Every drawn key carries <see cref="MacroKeys.Verdict"/>: a binding whose chord can never reach the
/// app is marked on its own row and explained in the footer, and the numpad caption says the same for
/// the grid as a whole. That is the screen's one hard rule — a list of bindings may not contain a row
/// that quietly does nothing, and the only way to keep that promise is to ask what the keyboard can
/// actually deliver rather than assuming a stored descriptor means something.
/// </para>
/// </summary>
internal static class KeypadScreenRenderer
{
    private const int KeyColumnWidth = 12;
    private const int ColumnWidth = 48;

    /// <summary>Visible width the binding list's name column is padded to, so the arrows line up.</summary>
    private const int NameColumnWidth = 14;

    /// <summary>
    /// The binding row's field ordinals, in the order ⇥ steps through them. The name leads, as it does
    /// on every list screen.
    /// </summary>
    internal const int NameField = 0;

    internal const int CommandField = 1;

    /// <summary>
    /// The binding's key, as a <see cref="ScreenField.Key"/> capture: ⏎ ⇥ ⇥ arms it and the next
    /// keystroke becomes the binding. It is <em>appended</em> rather than slotted in beside the key the
    /// row draws first, because an ordinal is an address the renderer, the model, the tests and the
    /// snapshot key scripts all use, and inserting one silently moves every caret after it — the same
    /// rule that put F5's log fields and F2's action fields at the end of their rows.
    /// </summary>
    internal const int KeyField = 2;

    /// <summary>The label the binding list's add button carries; it names the key it will claim.</summary>
    internal const string AddBindingLabel = "+ binding";

    internal const string RemoveBindingLabel = "- del";

    /// <summary>What a brand-new binding is called and sends, before it is edited.</summary>
    private const string NewBindingName = "New Binding";

    private const string NewBindingCommand = "look";

    /// <summary>Longest command shown inside a numpad cell before it is ellipsised.</summary>
    private const int NumpadCommandWidth = 10;

    /// <summary>Visible width of one numpad cell: "[N] " (4) plus <see cref="NumpadCommandWidth"/>.</summary>
    private const int NumpadCellWidth = 4 + NumpadCommandWidth;

    private const string NumpadCellGap = "   ";

    private static readonly int[][] NumpadRows =
    {
        new[] { 7, 8, 9 },
        new[] { 4, 5, 6 },
        new[] { 1, 2, 3 },
    };

    /// <summary>
    /// Merges every sub-block into one line list (header, numpad | hotkeys, footer). Used by the
    /// unit tests and as a width-agnostic fallback; the live view composes the same blocks into
    /// panels instead.
    /// </summary>
    public static List<string> Render(
        IReadOnlyList<Macro> macros, IReadOnlyList<TriggerSet>? sets = null, int selected = -1)
    {
        ArgumentNullException.ThrowIfNull(macros);

        var left = NumpadColumn(macros);
        var right = HotkeysColumn(macros, null, sets, selected);

        var lines = new List<string> { HeaderLine(0, Model(macros, sets, selected)), string.Empty };

        var rowCount = Math.Max(left.Count, right.Count);
        for (var i = 0; i < rowCount; i++)
        {
            var leftLine = i < left.Count ? left[i] : string.Empty;
            var rightLine = i < right.Count ? right[i] : string.Empty;
            lines.Add($"{PadVisible(leftLine, ColumnWidth)} │ {rightLine}");
        }

        lines.Add(string.Empty);
        lines.Add(FooterLine(macros, 0, null, selected));

        return lines;
    }

    /// <summary>
    /// The screen title on the left, the keyboard hints right-aligned to <paramref name="width"/>. The
    /// hints are derived from <paramref name="model"/> and <paramref name="focus"/> rather than
    /// written here, so the header cannot advertise an edit the screen doesn't offer.
    /// </summary>
    internal static string HeaderLine(int width, ScreenModel? model = null, ScreenFocus? focus = null)
    {
        var title = $"[bold {Value}] Keypad & hotkeys[/]";
        var hints = ScreenChrome.Hints(
            ScreenChrome.SingleListHints, "F4", model?.HasEditableRow ?? false, focus, model?.HasRemovableRow ?? false);
        return SpreadLR(" " + title, hints, width);
    }

    /// <summary>
    /// The screen's one navigable pane: the binding list, where Space enables or disables a macro and
    /// ⏎ edits its name and then — with ⇥ — the command it sends. The numpad grid is a projection of
    /// the same macros, so it has no cursor of its own — it updates as the list is toggled and edited.
    /// </summary>
    /// <param name="macros">The bindings to draw, flattened across the sets that own them.</param>
    /// <param name="sets">
    /// The sets those bindings live in, needed only to build the add/remove buttons: a macro's home is
    /// a <see cref="TriggerSet"/>, and a flattened list on its own cannot say which one a new binding
    /// belongs to or which one a removal has to come out of. Optional, because a caller that only wants
    /// the navigable shape (the header hints, the tests) need not know the configuration's sets — and
    /// without them the screen simply offers no buttons rather than offering ones that would throw.
    /// </param>
    /// <param name="selected">Which binding the cursor has anchored, or -1 for none.</param>
    internal static ScreenModel Model(
        IReadOnlyList<Macro> macros, IReadOnlyList<TriggerSet>? sets = null, int selected = -1)
    {
        ArgumentNullException.ThrowIfNull(macros);

        return new ScreenModel(ScreenModel.Rows(macros, macro => ScreenRow.Of(
            ScreenToggle.Bind(() => macro.Enabled, v => macro.Enabled = v),
            ScreenField.Name("name", () => macro.Name, v => macro.Name = v),
            ScreenField.Text("command", () => macro.Command, v => macro.Command = v),
            ScreenField.Key("key", () => macro.Key, v => macro.Key = v, key => AlreadyBound(macros, macro, key))))
            .Concat(Buttons(sets, selected))
            .ToArray());
    }

    /// <summary>
    /// Whether another binding already holds a key, and which — the refusal a key capture needs. A
    /// <see cref="MacroEngine"/> resolves one macro per key, so the second binding on a key never runs:
    /// letting a capture create one would mint exactly the dead row the capture mode exists to remove,
    /// and it would be a dead row with no symptom at all, since both rows would look alive.
    /// <para>
    /// The comparison spans every set the screen was handed, not just the one the binding lives in. It is
    /// deliberately the stricter reading: a session flattens the character's sets into one engine, so two
    /// sets that are ever enabled together collide — and a screen that only refused collisions inside one
    /// set would be right about the sets it could see and wrong about the runtime.
    /// </para>
    /// </summary>
    private static string? AlreadyBound(IReadOnlyList<Macro> macros, Macro self, string key)
    {
        var canonical = MacroKey.Canonicalise(key) ?? key.Trim();
        foreach (var macro in macros)
        {
            if (!ReferenceEquals(macro, self)
                && string.Equals(macro.Key, canonical, StringComparison.OrdinalIgnoreCase))
            {
                return $"already bound to {Identify(macro)}";
            }
        }

        return null;
    }

    /// <summary>
    /// The binding list's buttons. Adding claims the first unbound numpad digit and *says which*
    /// (<c>[[+ binding]] Num3</c>), so a new binding always lands on a key the row can name. When every
    /// numpad digit is spoken for there is no free key to claim, so the button isn't drawn at all — the
    /// same rule that keeps <c>[[- del]]</c> off a pane with nothing selected.
    /// <para>
    /// The numpad is a <em>placeholder</em> now, not a working key: no numpad chord reaches this host
    /// (see <see cref="MacroKeys"/>), so a fresh binding is drawn with the <c>▲</c> caveat every other
    /// dead row gets, and the key capture on its own row (<see cref="KeyField"/>) is how it is made live.
    /// That is a step, and it is visible; it stays this way only because the claimed key is what
    /// <c>ScreenListButtonTests</c> pins, and moving the claim to the first free deliverable chord is a
    /// change to an asserted value rather than to a behaviour nobody wrote down.
    /// </para>
    /// <para>
    /// There is still no <c>duplicate</c>: a copy would land on the key its original already holds, and
    /// two macros on one key means the second never runs — the capture refuses to create that state
    /// (see <see cref="AlreadyBound"/>), so a button whose only possible result is that state would be
    /// contradicting the field beside it.
    /// </para>
    /// </summary>
    private static List<ScreenRow> Buttons(IReadOnlyList<TriggerSet>? sets, int selected)
    {
        var rows = new List<ScreenRow>();
        if (sets is null)
        {
            return rows;
        }

        var bound = sets.SelectMany(s => s.Macros).Select(m => m.Key).ToList();
        if (ScreenLists.Target(sets, s => s.Macros, selected) is { } target
            && FreeBindableKey(bound) is { } key)
        {
            rows.Add(ScreenRow.Of(ScreenButton.Add(
                AddBindingLabel,
                target.Items,
                () => new Macro { Name = NewBindingName, Key = key, Command = NewBindingCommand },
                target.Offset,
                key)));
        }

        if (ScreenLists.Locate(sets, s => s.Macros, selected) is { } slot)
        {
            var source = slot.Items[slot.Index];
            rows.Add(ScreenRow.Of(ScreenButton.Remove(
                RemoveBindingLabel, slot.Items, slot.Index, slot.Offset, Identify(source))));
        }

        return rows;
    }

    /// <summary>
    /// The first chord a new binding can be born on: unbound, and one this client can actually be
    /// reached by. It deliberately does <em>not</em> hand out a numpad key — no numpad key ever
    /// arrives (see <see cref="MacroKeys"/>), so a binding created on one is dead from birth, and a
    /// button whose whole job is to name the key it claims must not name a key that cannot fire.
    /// Null when every candidate is taken, which is what stops the button being drawn at all.
    /// Comparison is case-insensitive because <see cref="MacroEngine"/>'s lookup is.
    /// </summary>
    private static string? FreeBindableKey(IReadOnlyList<string> bound)
    {
        foreach (var key in MacroKeys.Bindable)
        {
            if (!bound.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// What to call a binding on screen: its name, falling back to its key. A macro that has never been
    /// named would otherwise leave <c>[[- del]]</c> naming nothing at all, which is the one thing a
    /// destructive row may not do.
    /// </summary>
    private static string Identify(Macro macro) =>
        string.IsNullOrWhiteSpace(macro.Name) ? macro.Key : macro.Name;

    /// <summary>
    /// The action bar: where the cursor is in the binding list on the left, cancel/save on the right.
    /// It used to report the screen's inventory instead (<c>9 bindings · 8 of 9 numpad keys bound</c>),
    /// which the numpad grid two columns over already shows cell by cell; every other screen answers
    /// "where am I", so this one does too. The selected binding comes from <paramref name="focus"/>
    /// rather than a parameter of its own, because the cursor is the only thing that decides it.
    /// <para>
    /// The qualifier is the macro's <em>name</em>, which is the one thing about a binding this screen
    /// doesn't otherwise show — its rows are key → command. A macro that has never been named falls
    /// back to its key, because an empty qualifier would leave the footer saying less than it could.
    /// </para>
    /// </summary>
    /// <param name="macros">The bindings the list draws.</param>
    /// <param name="width">How wide the bar runs.</param>
    /// <param name="focus">Where the keyboard is, used when no selection is handed in.</param>
    /// <param name="selected">
    /// The anchored selection, or -1 to fall back to the cursor. The list pane now ends in buttons, so
    /// the cursor can sit past the list while the selection — and the <c>[[- del]]</c> row's target —
    /// stays on the binding the screen is showing; the footer has to report that one, not the last.
    /// </param>
    internal static string FooterLine(
        IReadOnlyList<Macro> macros, int width, ScreenFocus? focus = null, int selected = -1)
    {
        ArgumentNullException.ThrowIfNull(macros);

        var context = string.Empty;
        if (macros.Count > 0)
        {
            var at = selected >= 0 ? selected : focus?.Pane == 0 ? focus.Value.Index : 0;
            at = Math.Clamp(at, 0, macros.Count - 1);

            // The row can only spare a handful of cells for a reason; the footer has the width to give
            // the whole one, for the binding the cursor is actually on.
            var verdict = MacroKeys.Verdict(macros[at].Key);
            context = ScreenChrome.Context(
                ScreenChrome.Position("binding", at, macros.Count),
                Escape(Identify(macros[at])),
                verdict.Fires ? null : "▲ " + Escape(verdict.Reason));
        }

        var actions = ScreenChrome.Actions(focus: focus);
        return SpreadLR(" " + context, actions, width);
    }

    /// <summary>
    /// The 3×3 numpad grid, in numpad order (7-8-9 on top), one row per line.
    /// <para>
    /// Its caption carries the whole grid's verdict, because on this host that verdict is the same for
    /// every cell: nothing sends a numpad key here (see <see cref="MacroKeys"/>). Nine diagrams of keys
    /// that cannot fire, drawn without saying so, is the exact shape of the lie this screen was audited
    /// for — and the caption is <em>derived</em> from the verdict rather than written, so a host that
    /// could one day deliver the numpad would drop the disclaimer without anyone remembering to.
    /// </para>
    /// </summary>
    internal static List<string> NumpadColumn(IReadOnlyList<Macro> macros)
    {
        ArgumentNullException.ThrowIfNull(macros);

        var lines = new List<string> { $"[dim]NUMPAD[/]{Caveat(MacroKeys.Verdict("Num5"))}" };
        foreach (var row in NumpadRows)
        {
            lines.Add(NumpadRow(row, macros));
        }

        return lines;
    }

    /// <summary>The binding list — every macro with its enabled state, key, name, and command.</summary>
    internal static List<string> HotkeysColumn(
        IReadOnlyList<Macro> macros,
        ScreenFocus? focus = null,
        IReadOnlyList<TriggerSet>? sets = null,
        int selected = -1)
    {
        ArgumentNullException.ThrowIfNull(macros);

        var cursor = focus ?? ScreenFocus.None;
        var lines = new List<string> { $"[dim]HOTKEYS[/]{DeadCount(macros)}" };
        if (macros.Count == 0)
        {
            lines.Add("  [dim]no hotkeys[/]");
        }

        for (var i = 0; i < macros.Count; i++)
        {
            lines.Add(ScreenChrome.Cursor(
                Hotkey(
                    macros[i],
                    cursor.EditOn(0, i, NameField),
                    cursor.EditOn(0, i, CommandField),
                    cursor.EditOn(0, i, KeyField)),
                cursor.IsOn(0, i),
                ColumnWidth));
        }

        lines.Add(string.Empty);
        lines.AddRange(ScreenChrome.Buttons(Buttons(sets, selected), cursor, 0, macros.Count, ColumnWidth));
        return ScreenChrome.Choices(lines, cursor.Edit, ColumnWidth);
    }

    private static string NumpadRow(int[] digits, IReadOnlyList<Macro> macros)
    {
        var cells = new string[digits.Length];
        for (var i = 0; i < digits.Length; i++)
        {
            var cell = NumpadCell(digits[i], macros);

            // Every cell is padded to the same visible width so the three columns line up whatever
            // is bound: a cell is as wide as "[N] " plus the longest command it can hold. The last
            // cell in a row is left unpadded to avoid trailing whitespace.
            cells[i] = i == digits.Length - 1 ? cell : PadVisible(cell, NumpadCellWidth);
        }

        return string.Join(NumpadCellGap, cells);
    }

    /// <summary>
    /// One cell of the numpad diagram. The command is drawn as a readout, not a field: the grid mirrors
    /// the binding list beside it and has no cursor of its own, so a cell is somewhere a command is
    /// *shown*, never somewhere one is typed. See <see cref="ScreenChrome.ReadOnly"/>.
    /// </summary>
    private static string NumpadCell(int digit, IReadOnlyList<Macro> macros)
    {
        var macro = FindByKey(macros, $"Num{digit}");
        var command = macro is null
            ? "[dim]—[/]"
            : ScreenChrome.ReadOnly(Truncate(macro.Command, NumpadCommandWidth));
        return $"[bold {Accent}][[{digit}]][/] {command}";
    }

    /// <summary>
    /// One row of the binding list: <c>tick key name → command</c>. All three values are welled, because
    /// all three are edited here — this screen has no editor pane to draw them in, and the well is the
    /// affordance that says which values the keyboard can change (see <see cref="ScreenChrome.ReadOnly"/>,
    /// and the numpad grid, which has none). The key was the one unwelled value on the screen until it
    /// grew a capture mode; it is welled now because the claim the well makes has become true.
    /// <para>
    /// A binding whose chord can never reach the app is drawn in the muted ink behind a <c>▲</c>, with
    /// the reason after the command — the read-only treatment, used for what it means: this row is not
    /// what it looks like. The well stays, because the key genuinely <em>is</em> editable here; the two
    /// marks answer different questions, and the answer to "can I change this" is yes even when the
    /// answer to "does this do anything" is no.
    /// </para>
    /// </summary>
    private static string Hotkey(Macro macro, ScreenFieldEdit? name, ScreenFieldEdit? command, ScreenFieldEdit? key)
    {
        var verdict = MacroKeys.Verdict(macro.Key);
        var tick = macro.Enabled ? $"[{Accent}]✓[/]" : "[dim]·[/]";
        var chord = verdict.Fires
            ? $"[bold]{Escape(macro.Key)}[/]"
            : $"[{Muted}]▲ {Escape(macro.Key)}[/]";
        var bound = ScreenChrome.Field(PadVisible(chord, KeyColumnWidth), key);

        // A binding that has never been named still gets its well — the name is editable whether or not
        // one is set — but with the same em-dash placeholder an unbound numpad cell uses, so the column
        // reads as an empty field rather than as a slab of background.
        var label = string.IsNullOrWhiteSpace(macro.Name) ? "[dim]—[/]" : $"[{Value}]{Escape(macro.Name)}[/]";
        var named = ScreenChrome.Field(PadVisible(label, NameColumnWidth), name);
        return $"{tick} {bound} {named} → {ScreenChrome.Field(Escape(macro.Command), command)}";
    }

    /// <summary>
    /// What a verdict adds to a <em>caption</em>: nothing when it fires, and otherwise the muted
    /// <c>▲ reason</c> in full. One helper, so a caption and the footer cannot phrase the same fact two
    /// ways.
    /// </summary>
    private static string Caveat(MacroKeyVerdict verdict) =>
        verdict.Fires ? string.Empty : $"  [{Muted}]▲ {Escape(verdict.Reason)}[/]";

    /// <summary>
    /// How many of the drawn bindings can never fire, on the list's own caption. The rows carry the
    /// <c>▲</c> and the footer carries the reason for the one the cursor is on; the reason is
    /// deliberately <em>not</em> repeated per row, because the reasons differ, they are longer than the
    /// column has cells, and eight copies of the same sentence is how a warning stops being read. A
    /// count, though, is the one thing neither the rows nor the footer can say: how much of this list is
    /// scenery.
    /// </summary>
    private static string DeadCount(IReadOnlyList<Macro> macros)
    {
        var dead = macros.Count(m => !MacroKeys.Verdict(m.Key).Fires);
        return dead == 0
            ? string.Empty
            : $"  [{Muted}]▲ {dead} of {macros.Count} cannot fire[/]";
    }

    private static Macro? FindByKey(IReadOnlyList<Macro> macros, string key)
    {
        foreach (var macro in macros)
        {
            if (string.Equals(macro.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return macro;
            }
        }

        return null;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength - 1), "…");
}
