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
/// One row of a settings screen's navigable shape. A row is a plain stop (neither a checkbox nor
/// anything to type into), a checkbox, a row of editable fields, or both at once — the keypad's
/// bindings are the last case, where Space enables the macro and ⏎ edits the command it sends.
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
internal readonly record struct ScreenRow(ScreenToggle? Toggle = null, IReadOnlyList<ScreenField>? Fields = null)
{
    /// <summary>A selectable row with nothing to press and nothing to type into.</summary>
    internal static ScreenRow Stop => default;

    /// <summary>A row that is only a checkbox.</summary>
    internal static ScreenRow Of(ScreenToggle toggle) => new(toggle);

    /// <summary>A row that is only editable values, in display order.</summary>
    internal static ScreenRow Of(params ScreenField[] fields) => new(null, fields);

    /// <summary>A row that is both — Space flips the checkbox, ⏎ opens the first field.</summary>
    internal static ScreenRow Of(ScreenToggle toggle, params ScreenField[] fields) => new(toggle, fields);

    /// <summary>How many values ⏎/⇥ can step through on this row.</summary>
    internal int FieldCount => Fields?.Count ?? 0;

    /// <summary>Whether ⏎ has something to open here; a row without fields lets ⏎ save instead.</summary>
    internal bool IsActivatable => FieldCount > 0;

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

    /// <summary>How many panes the screen offers ⇥ between.</summary>
    internal int PaneCount => _panes.Length;

    /// <summary>
    /// Whether anything on this screen can be edited. The header hints are derived from this rather
    /// than written per screen, so a screen physically cannot advertise <c>⏎ edit</c> without offering
    /// a row that ⏎ opens.
    /// </summary>
    internal bool HasEditableRow
    {
        get
        {
            foreach (var pane in _panes)
            {
                foreach (var row in pane)
                {
                    if (row.IsActivatable)
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
