namespace SharpMUTerm.Tui;

/// <summary>
/// A checkbox row on a settings screen, bound to the config it shows: how to read the flag, how to
/// flip it, and how to put back exactly what was there before. The snapshot exists because not every
/// checkbox is a plain <c>bool</c> property — F9's "auto-start on connect" is really a
/// <see cref="SharpMUTerm.Core.Configuration.LogFormat"/>, and Esc has to restore <c>Html</c>, not
/// merely "on".
/// </summary>
/// <param name="Get">Reads the flag as the renderer draws it.</param>
/// <param name="Flip">Inverts the flag.</param>
/// <param name="Snapshot">Captures the current value, returning the action that restores it.</param>
internal readonly record struct ScreenToggle(Func<bool> Get, Action Flip, Func<Action> Snapshot)
{
    /// <summary>Binds a plain boolean property; restoring it is just writing the old value back.</summary>
    internal static ScreenToggle Bind(Func<bool> get, Action<bool> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenToggle(
            get,
            () => set(!get()),
            () =>
            {
                var previous = get();
                return () => set(previous);
            });
    }
}

/// <summary>
/// What running a button left behind: how to undo it, and where the cursor should be afterwards.
/// </summary>
/// <param name="Undo">Puts the list back exactly as it was, position included.</param>
/// <param name="Select">
/// The row of the button's own pane the cursor should move to — the row just added, so a new world
/// opens ready to be named. Null leaves the cursor where it was.
/// </param>
internal readonly record struct ScreenPress(Action Undo, int? Select = null);

/// <summary>
/// A button row on a settings screen: <c>[+ world]</c>, <c>[⧉ duplicate]</c>, <c>[- remove]</c>. ⏎ is
/// already "activate the focused row", so a button is a row whose activation runs a command instead of
/// opening an editor.
/// <para>
/// <see cref="Run"/> performs the change and *returns* how to undo it, rather than being handed a
/// snapshot taken beforehand the way <see cref="ScreenToggle"/> and <see cref="ScreenField"/> are.
/// That is forced by what these buttons do: the undo for an insertion is "remove the thing that was
/// added", which cannot be described until it has been added. Doing it this way also lets a removal
/// capture the item *and its index*, so Esc puts a deleted world back where it was in the list rather
/// than on the end — the list's order is what the screen navigates by, and silently reordering it
/// would be a second, invisible edit.
/// </para>
/// </summary>
/// <param name="Label">What the button is called, for the row the renderer draws.</param>
/// <param name="Run">Performs the change and returns the undo plus where to leave the cursor.</param>
internal readonly record struct ScreenButton(string Label, Func<ScreenPress> Run)
{
    /// <summary>
    /// Appends a new item and leaves the cursor on it — a new row is worth nothing if the next
    /// keystroke has to go and find it.
    /// </summary>
    internal static ScreenButton Add<T>(string label, IList<T> list, Func<T> create)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(create);

        return new ScreenButton(label, () =>
        {
            list.Add(create());
            var at = list.Count - 1;
            return new ScreenPress(() => list.RemoveAt(at), at);
        });
    }

    /// <summary>
    /// Removes the item at <paramref name="index"/>, restoring it *at that index* on undo. The cursor
    /// stays on the same ordinal, which is now whatever followed the deleted row — the same place the
    /// eye is.
    /// </summary>
    internal static ScreenButton Remove<T>(string label, IList<T> list, int index)
    {
        ArgumentNullException.ThrowIfNull(list);

        return new ScreenButton(label, () =>
        {
            if (index < 0 || index >= list.Count)
            {
                return new ScreenPress(() => { });
            }

            var removed = list[index];
            list.RemoveAt(index);
            return new ScreenPress(() => list.Insert(index, removed), index);
        });
    }
}

/// <summary>
/// One row of a settings screen's navigable shape. A row is a plain stop (neither a checkbox nor
/// anything to type into), a checkbox, a row of editable fields, a button, or both a checkbox and
/// fields at once — the keypad's bindings are the last case, where Space enables the macro and ⏎ edits
/// the command it sends.
/// <para>
/// Fields are an ordered list rather than a single value because a row is a *record*, not a cell: one
/// world row carries its name, host, port, encoding, and keepalive. ⏎ opens the first, ⇥ steps to the
/// next, and the renderers draw the open one wherever that field's labelled value already appears. It
/// also keeps a row's identity stable — giving a row fields never renumbers the rows around it, so a
/// pane's cursor indices mean the same thing before and after this feature.
/// </para>
/// </summary>
/// <param name="Toggle">The checkbox Space flips, or null when the row has none.</param>
/// <param name="Fields">The values ⏎ opens for editing, in the order the screen draws them.</param>
/// <param name="Button">The command ⏎ runs, or null when the row is not a button.</param>
internal readonly record struct ScreenRow(
    ScreenToggle? Toggle = null,
    IReadOnlyList<ScreenField>? Fields = null,
    ScreenButton? Button = null)
{
    /// <summary>A selectable row with nothing to press and nothing to type into.</summary>
    internal static ScreenRow Stop => default;

    /// <summary>A row that is only a checkbox.</summary>
    internal static ScreenRow Of(ScreenToggle toggle) => new(toggle);

    /// <summary>A row that is only editable values, in display order.</summary>
    internal static ScreenRow Of(params ScreenField[] fields) => new(null, fields);

    /// <summary>A row that is both — Space flips the checkbox, ⏎ opens the first field.</summary>
    internal static ScreenRow Of(ScreenToggle toggle, params ScreenField[] fields) => new(toggle, fields);

    /// <summary>A row that is a button — ⏎ runs it, and there is nothing to type into.</summary>
    internal static ScreenRow Of(ScreenButton button) => new(null, null, button);

    /// <summary>How many values ⏎/⇥ can step through on this row.</summary>
    internal int FieldCount => Fields?.Count ?? 0;

    /// <summary>
    /// Whether ⏎ has something to do here; a row that is neither a button nor a record of fields lets
    /// ⏎ save instead.
    /// </summary>
    internal bool IsActivatable => FieldCount > 0 || Button is not null;

    /// <summary>The row's field at an ordinal, or null when it has none there.</summary>
    internal ScreenField? FieldAt(int field) =>
        Fields is not null && field >= 0 && field < Fields.Count ? Fields[field] : null;
}

/// <summary>
/// The navigable shape of one settings screen: its panes, each an ordered list of
/// <see cref="ScreenRow"/>s. It carries no markup and no controls: the renderers draw what the cursor
/// is on, this says where the cursor may go and what happens there. Rebuilt from live config on every
/// key, so it never goes stale against a list the last keystroke changed.
/// </summary>
internal sealed class ScreenModel
{
    private readonly IReadOnlyList<ScreenRow>[] _panes;

    internal ScreenModel(params IReadOnlyList<ScreenRow>[] panes)
    {
        ArgumentNullException.ThrowIfNull(panes);
        _panes = panes.Length == 0 ? new IReadOnlyList<ScreenRow>[] { Array.Empty<ScreenRow>() } : panes;
        Sizes = Array.ConvertAll(_panes, p => p.Count);
    }

    /// <summary>Row counts per pane, in pane order — what <see cref="ScreenSelection"/> navigates by.</summary>
    internal IReadOnlyList<int> Sizes { get; }

    /// <summary>
    /// How many rows of each pane are *list* rows rather than the buttons appended after them. A pane
    /// is a list followed by its own buttons, and the two mean different things to the cursor: moving
    /// onto <c>[[+ world]]</c> must not change which world is selected, or <c>[[- del]]</c> could only
    /// ever delete the last one — you would have to walk past every other world to reach the button.
    /// <see cref="ScreenSelection"/> anchors the selection with this.
    /// </summary>
    internal IReadOnlyList<int> ListSizes
    {
        get
        {
            var sizes = new int[_panes.Length];
            for (var pane = 0; pane < _panes.Length; pane++)
            {
                var rows = _panes[pane];
                var count = rows.Count;
                while (count > 0 && rows[count - 1].Button is not null)
                {
                    count--;
                }

                sizes[pane] = count;
            }

            return sizes;
        }
    }

    /// <summary>How many panes the screen offers ⇥ between.</summary>
    internal int PaneCount => _panes.Length;

    /// <summary>
    /// Whether anything on this screen can be edited. The header hints are derived from this rather
    /// than written per screen, so a screen physically cannot advertise <c>⏎ edit</c> without offering
    /// a row that ⏎ opens.
    /// <para>
    /// A button row is deliberately not counted. ⏎ activates it, but it doesn't *edit* anything, and a
    /// screen whose only ⏎ target were a button would be advertising an editor it hasn't got.
    /// </para>
    /// </summary>
    internal bool HasEditableRow
    {
        get
        {
            foreach (var pane in _panes)
            {
                foreach (var row in pane)
                {
                    if (row.FieldCount > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>The row at a cursor position, or a plain stop when that position holds nothing.</summary>
    internal ScreenRow RowAt(int pane, int index) =>
        pane >= 0 && pane < _panes.Length && index >= 0 && index < _panes[pane].Count
            ? _panes[pane][index]
            : ScreenRow.Stop;

    /// <summary>The checkbox at a cursor position, or null when that row isn't pressable.</summary>
    internal ScreenToggle? ToggleAt(int pane, int index) => RowAt(pane, index).Toggle;

    /// <summary>The button at a cursor position, or null when that row isn't one.</summary>
    internal ScreenButton? ButtonAt(int pane, int index) => RowAt(pane, index).Button;

    /// <summary>The editable value at a cursor position and field ordinal, or null when there is none.</summary>
    internal ScreenField? FieldAt(int pane, int index, int field) => RowAt(pane, index).FieldAt(field);

    /// <summary>A pane of rows that are selectable but carry nothing to press (a plain list).</summary>
    internal static IReadOnlyList<ScreenRow> Stops(int count) => new ScreenRow[Math.Max(0, count)];

    /// <summary>A pane built by binding one checkbox per item of <paramref name="items"/>.</summary>
    internal static IReadOnlyList<ScreenRow> Toggles<T>(
        IReadOnlyList<T> items, Func<T, bool> get, Action<T, bool> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return Rows(items, item => ScreenRow.Of(ScreenToggle.Bind(() => get(item), value => set(item, value))));
    }

    /// <summary>A pane built by projecting each item of <paramref name="items"/> into a row.</summary>
    internal static IReadOnlyList<ScreenRow> Rows<T>(IReadOnlyList<T> items, Func<T, ScreenRow> row)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(row);

        var rows = new ScreenRow[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            rows[i] = row(items[i]);
        }

        return rows;
    }
}
