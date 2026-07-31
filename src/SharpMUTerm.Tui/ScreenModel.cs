namespace SharpMUTerm.Tui;

/// <summary>
/// A checkbox row on a settings screen, bound to the config it shows: how to read the flag, and how to
/// flip it.
/// <para>
/// There is nothing here about putting the old value back, and deliberately so. A flipped checkbox is a
/// <em>committed</em> edit the moment Space presses it — see <see cref="ScreenEdits"/> for the scope rule
/// — so nothing ever asks a toggle to undo itself, and a snapshot nothing replayed would be a mechanism
/// with no driver.
/// </para>
/// </summary>
/// <param name="Get">Reads the flag as the renderer draws it.</param>
/// <param name="Flip">Inverts the flag.</param>
internal readonly record struct ScreenToggle(Func<bool> Get, Action Flip)
{
    /// <summary>Binds a plain boolean property.</summary>
    internal static ScreenToggle Bind(Func<bool> get, Action<bool> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenToggle(get, () => set(!get()));
    }
}

/// <summary>
/// What running a button left behind: how to undo it if it destroyed something, and where the cursor
/// should be afterwards.
/// </summary>
/// <param name="Undo">
/// Puts the list back exactly as it was, position included — or null when the press destroyed nothing.
/// A button that <em>built</em> a row returns null: an addition loses no work by being kept, so it is
/// never reviewed and never replayed, and an undo action nothing could run would be dead weight
/// carried by every press. Only the destructive buttons hand one back, which is what makes
/// <see cref="ScreenEdits"/>' log a log of deletions rather than of everything.
/// </param>
/// <param name="Select">
/// The row of the button's own pane the cursor should move to — the row just added, so a new world
/// opens ready to be named. Null leaves the cursor where it was.
/// </param>
internal readonly record struct ScreenPress(Action? Undo, int? Select = null);

/// <summary>
/// A command on a settings screen. The building ones are rows the cursor lands on and ⏎ presses —
/// <c>[+ world]</c>, <c>[⧉ duplicate]</c> — since ⏎ is already "activate the focused row". A
/// <em>removal</em> is run by Delete on the row it would take, and its own drawn row is not a cursor stop
/// (see <see cref="ScreenModel.Sizes"/>).
/// <para>
/// <see cref="Run"/> performs the change and *returns* how to undo it, rather than being handed a
/// snapshot taken beforehand. That is forced by what a removal has to capture: the item <em>and its
/// index</em>, so the closing review's "put them back" restores a deleted world where it was in the list
/// rather than on the end — the list's order is what the screen navigates by, and silently reordering it
/// would be a second, invisible edit. A building press returns no undo at all: an addition destroys
/// nothing, so nothing reviews or replays it.
/// </para>
/// </summary>
/// <param name="Label">What the button is called, for the row the renderer draws.</param>
/// <param name="Run">Performs the change and returns the undo plus where to leave the cursor.</param>
/// <param name="Kind">
/// Whether the button builds or destroys, which is what decides how the row is drawn and whether
/// Delete runs it. It is carried here rather than inferred from <paramref name="Label"/>, because a
/// renderer comparing label strings to decide how to paint a row is one rename away from painting a
/// deletion in the "add" accent.
/// </param>
/// <param name="Target">
/// The row this button would act on, named on the button's own row so the screen says what is about to
/// happen. Null for a button that acts on nothing in particular.
/// </param>
/// <param name="Describe">
/// What this press would destroy, in words, asked <em>before</em> <paramref name="Run"/> — which is the
/// only moment the answer can still be counted (a world's characters are gone by the time the press
/// returns). It is what the closing review names, so the question reads <c>Aetherfall and its 2
/// characters</c> rather than <c>1 deletion</c>; see <see cref="ScreenEditReview"/>. Null on a button
/// that destroys nothing.
/// </param>
internal readonly record struct ScreenButton(
    string Label,
    Func<ScreenPress> Run,
    ScreenButtonKind Kind = ScreenButtonKind.Add,
    string? Target = null,
    Func<string>? Describe = null)
{
    /// <summary>
    /// What a destructive button's row is labelled with, which is the <em>key</em> that runs it rather
    /// than a chip you could land on. The row is not a cursor stop (see <see cref="ScreenModel.Sizes"/>),
    /// so a bracketed <c>[[- del]]</c> would have been an affordance for something the keyboard could no
    /// longer reach; naming the key instead makes the row a true reading of what Delete would take.
    /// </summary>
    internal const string RemoveKeyLabel = "Del";

    /// <summary>
    /// Appends a new item and leaves the cursor on it — a new row is worth nothing if the next
    /// keystroke has to go and find it.
    /// <para>
    /// <paramref name="offset"/> is how many of the pane's list rows precede <paramref name="list"/>.
    /// It is zero when the pane <em>is</em> the list (F5's worlds), and non-zero when the pane flattens
    /// several lists into one (F2/F3/F4/F6 show every set's rules in one column, but a rule is added to
    /// one particular set), because the cursor is asked for a row of the pane, not an index into the
    /// list that happens to hold the new item.
    /// </para>
    /// </summary>
    internal static ScreenButton Add<T>(
        string label, IList<T> list, Func<T> create, int offset = 0, string? target = null)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(create);

        return new ScreenButton(
            label,
            () =>
            {
                list.Add(create());
                var at = list.Count - 1;
                return new ScreenPress(null, offset + at);
            },
            ScreenButtonKind.Add,
            target);
    }

    /// <summary>
    /// Removes the item at <paramref name="index"/>, restoring it *at that index* on undo. The cursor
    /// stays on the same ordinal, which is now whatever followed the deleted row — the same place the
    /// eye is. <paramref name="offset"/> means what it does on <see cref="Add"/>.
    /// <para>
    /// It takes no label. A removal is drawn as the key that runs it
    /// (<see cref="ScreenButton.RemoveKeyLabel"/>) rather than as a chip, because its row is no longer
    /// somewhere the cursor can go — see <see cref="ScreenModel.Sizes"/> for why, and
    /// <see cref="ScreenChrome.Buttons"/> for what that row now reads as.
    /// </para>
    /// </summary>
    /// <param name="describe">
    /// What this removal would destroy, in words, for the closing review to name. See
    /// <see cref="ScreenButton.Describe"/>; it defaults to <paramref name="target"/>, which is the honest
    /// answer for every row that takes nothing else with it.
    /// </param>
    /// <summary>
    /// What a read-only detail screen's key is drawn as, for the same reason
    /// <see cref="RemoveKeyLabel"/> is: the row naming it is not a cursor stop, so a chip would be an
    /// affordance for something the keyboard cannot land on.
    /// </summary>
    internal const string DetailKeyLabel = "i";

    /// <summary>
    /// Opens a read-only report about the selected row. It is a <see cref="ScreenButton"/> so that the
    /// row the screen draws and the key that runs it come from one place, exactly as a removal does —
    /// but it changes nothing, so it returns no undo and moves no cursor, and
    /// <see cref="SettingsSession"/> runs it <em>outside</em> <see cref="ScreenEdits"/>. Navigation is
    /// not an edit: routing it through the edit log would persist the configuration and re-periodise
    /// every running timer every time somebody looked at a world.
    /// </summary>
    /// <param name="target">The row this would report on, named on the drawn key-hint row.</param>
    /// <param name="open">Puts the report on the screen.</param>
    internal static ScreenButton Detail(string target, Action open)
    {
        ArgumentNullException.ThrowIfNull(open);

        return new ScreenButton(
            DetailKeyLabel,
            () =>
            {
                open();
                return new ScreenPress(null);
            },
            ScreenButtonKind.Detail,
            target);
    }

    internal static ScreenButton Remove<T>(
        IList<T> list, int index, int offset = 0, string? target = null, Func<string>? describe = null)
    {
        ArgumentNullException.ThrowIfNull(list);

        return new ScreenButton(
            RemoveKeyLabel,
            () =>
            {
                if (index < 0 || index >= list.Count)
                {
                    return new ScreenPress(null);
                }

                var removed = list[index];
                list.RemoveAt(index);
                return new ScreenPress(() => list.Insert(index, removed), offset + index);
            },
            ScreenButtonKind.Remove,
            target,
            describe ?? (target is { Length: > 0 } ? () => target : null));
    }
}

/// <summary>
/// What a <see cref="ScreenButton"/> does to the list it sits under. It decides two things a screen
/// must not get wrong: how the row is painted (a build in the accent, a destruction in the muted ink
/// beside the name of its victim), and which button the Delete key runs.
/// </summary>
internal enum ScreenButtonKind
{
    /// <summary>Puts a row into the list — <c>[+ world]</c>, <c>[⧉ duplicate]</c>.</summary>
    Add,

    /// <summary>
    /// Takes the selected row out of it. Its row is drawn as the key that runs it (<c>Del  removes
    /// Aetherfall</c>) rather than as a chip, because it is not a cursor stop — see <see cref="ScreenModel.Sizes"/>.
    /// </summary>
    Remove,

    /// <summary>
    /// Opens a read-only report about the selected row (<c>i  info on Aetherfall</c>), changing nothing.
    /// It is drawn and navigated exactly as a removal is, and for the identical reason: the action has a
    /// <em>target</em>, and an action with a target must not steal the cursor from the thing it acts on
    /// (<see cref="ScreenModel.Sizes"/>). A chip walked to with ↑↓ would drag the selection to the last
    /// row of the list, so an INFO chip could only ever report on the last world — the same defect as
    /// "only the last world can be deleted", one feature later.
    /// </summary>
    Detail,
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
    private IReadOnlyList<ScreenPanePlace> _layout;

    internal ScreenModel(params IReadOnlyList<ScreenRow>[] panes)
    {
        ArgumentNullException.ThrowIfNull(panes);
        _panes = panes.Length == 0 ? new IReadOnlyList<ScreenRow>[] { Array.Empty<ScreenRow>() } : panes;
        Sizes = Array.ConvertAll(_panes, Stops);
        _layout = SideBySide(_panes.Length);
    }

    /// <summary>
    /// How many rows of each pane the cursor may occupy, in pane order — what
    /// <see cref="ScreenSelection"/> navigates by. It is <em>not</em> how many rows the pane draws: a
    /// pane's destructive button is drawn and is not a stop.
    /// <para>
    /// That asymmetry is the point, and it is the fix for "only the last world can be deleted". A pane is
    /// a list followed by its buttons, and reaching a button with ↑↓ means walking the cursor over every
    /// row of the list on the way — which drags the selection to the last one
    /// (<see cref="ScreenSelection.Anchor"/>), so a <c>[[- del]]</c> arrived at that way could only ever
    /// delete the final item. It cannot be fixed by remembering where the cursor has been, because the
    /// last list row visited on the way to the buttons is *always* the last row of the list; the button
    /// block structurally cannot know which item was meant.
    /// </para>
    /// <para>
    /// So the rule is: <b>an action with no target needs a cursor stop; an action with a target must not
    /// steal the cursor from the thing it acts on.</b> <c>[[+ world]]</c> stays a stop, because "add" has
    /// nothing to point at and needs somewhere to be pressed from. A removal has a target — the selected
    /// row — and is run by Delete on that very row, so its drawn row stops being a place you go and
    /// becomes a true reading of what Delete would take (<see cref="ScreenChrome.Buttons"/>). Nothing is
    /// lost: <see cref="ScreenChrome.DeleteHint"/> advertises the key, and it is derived from
    /// <see cref="HasRemovableRow"/> so it cannot advertise it where there is none.
    /// </para>
    /// </summary>
    internal IReadOnlyList<int> Sizes { get; }

    /// <summary>
    /// How many of a pane's rows the cursor may occupy: all of them but the <em>targeted</em> buttons at
    /// the end. Those are appended last on every screen (a pane reads list → add → duplicate → info →
    /// remove), so this is a count and not a set of holes — the cursor never has to skip a row in the
    /// middle, and <see cref="ScreenSelection"/> stays a plain clamp. <c>ScreenModelTests</c> pins the
    /// count against the drawn rows, and <c>MsspScreenTests.TheInfoRowIsDrawnAndIsNotSomewhereTheCursorCanGo</c>
    /// pins it for the <see cref="ScreenButtonKind.Detail"/> half.
    /// <para>
    /// <b>Both non-stop kinds must stay trailing.</b> A <see cref="ScreenButtonKind.Detail"/> row put
    /// anywhere but among them gives the cursor a hole, and the clamp becomes a skip list.
    /// </para>
    /// </summary>
    private static int Stops(IReadOnlyList<ScreenRow> rows)
    {
        var count = rows.Count;
        while (count > 0
            && rows[count - 1].Button is { Kind: ScreenButtonKind.Remove or ScreenButtonKind.Detail })
        {
            count--;
        }

        return count;
    }

    /// <summary>
    /// Where each pane is drawn, which is what the arrow keys and ⇥ navigate by. It defaults to
    /// <see cref="SideBySide"/> — one pane per column, in index order — because that is what every
    /// screen but F5 actually is: a list beside an editor, or a single card. Under that default
    /// reading order and index order are the same thing, so ⇥ walks exactly the panes it always did.
    /// <para>
    /// A screen states its own layout only when the two disagree. F5 is the one that does: its
    /// security pane is <em>appended</em> (a pane index is a cursor coordinate, so inserting one would
    /// renumber every stop the screen and its tests navigate by) while being <em>drawn</em> above the
    /// characters list, in the same column. Left as index order, ⇥ went left-top → right-middle →
    /// bottom-right and then jumped back up the screen, which is the awkwardness this coordinate
    /// exists to fix.
    /// </para>
    /// <para>
    /// A layout that doesn't name every pane is ignored rather than half-applied: a missing
    /// coordinate is a pane the arrows could never reach, and silently dropping one stop is worse than
    /// falling back to a rule that reaches them all.
    /// </para>
    /// </summary>
    internal IReadOnlyList<ScreenPanePlace> Layout
    {
        get => _layout;
        init
        {
            if (value is { } places && places.Count == _panes.Length)
            {
                _layout = places;
            }
        }
    }

    /// <summary>Panes drawn as columns, left to right in index order — the shape all but F5 have.</summary>
    internal static IReadOnlyList<ScreenPanePlace> SideBySide(int panes)
    {
        var layout = new ScreenPanePlace[Math.Max(1, panes)];
        for (var i = 0; i < layout.Length; i++)
        {
            layout[i] = new ScreenPanePlace(i, 0);
        }

        return layout;
    }

    /// <summary>
    /// How many rows of each pane are *list* rows rather than the buttons appended after them. A pane
    /// is a list followed by its own buttons, and the two mean different things to the cursor: moving
    /// onto <c>[[+ world]]</c> must not change which world is selected, or the detail column beside it
    /// would blank the moment the cursor reached for the add button.
    /// <see cref="ScreenSelection"/> anchors the selection with this.
    /// <para>
    /// The anchor is <em>not</em> what keeps a removal pointed at the right row — it cannot be, since
    /// walking down to a button leaves it on the last list row. That is what <see cref="Sizes"/> answers,
    /// by keeping the cursor out of the removal's row altogether.
    /// </para>
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
    /// How many rows a pane <em>draws</em>, buttons included — which is more than <see cref="Sizes"/>
    /// wherever a pane offers a removal. Only the tests that check the two apart need it.
    /// </summary>
    internal int RowCount(int pane) => pane >= 0 && pane < _panes.Length ? _panes[pane].Count : 0;

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

    /// <summary>
    /// Whether any pane offers a way to take a row out. The <c>Del remove</c> hint is derived from this
    /// the way <c>⏎ edit</c> is derived from <see cref="HasEditableRow"/>: a screen physically cannot
    /// advertise the key without offering it.
    /// </summary>
    internal bool HasRemovableRow
    {
        get
        {
            for (var pane = 0; pane < _panes.Length; pane++)
            {
                if (RemoveIn(pane) is not null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// A pane's destructive button, or null when it has none — what Delete runs while the cursor is on
    /// one of that pane's list rows. Delete acts through the button rather than around it, so the key
    /// and the drawn <c>Del</c> row are the same command with the same undo and the same conditions: a
    /// pane that doesn't offer the button doesn't answer the key either, and the row cannot be pressed
    /// independently because it is not a cursor stop.
    /// </summary>
    internal ScreenButton? RemoveIn(int pane)
    {
        if (pane < 0 || pane >= _panes.Length)
        {
            return null;
        }

        foreach (var row in _panes[pane])
        {
            if (row.Button is { Kind: ScreenButtonKind.Remove } button)
            {
                return button;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether any pane offers a read-only report on the selected row. The <c>i info</c> hint is derived
    /// from this exactly as <c>Del remove</c> is derived from <see cref="HasRemovableRow"/>: a screen
    /// physically cannot advertise a letter key it does not answer, which matters more here than for
    /// Delete because <c>i</c> is a letter and a screen that swallowed it silently would be indis-
    /// tinguishable from one where the key was simply not wired.
    /// </summary>
    internal bool HasDetailRow
    {
        get
        {
            for (var pane = 0; pane < _panes.Length; pane++)
            {
                if (DetailIn(pane) is not null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// A pane's read-only report button, or null when it has none — what <c>i</c> runs while the cursor
    /// is on one of that pane's list rows. The same shape as <see cref="RemoveIn"/>, and scoped the same
    /// way: a pane that does not offer the button does not answer the key, which is what keeps <c>i</c>
    /// from meaning anything in a pane that has editable fields.
    /// </summary>
    internal ScreenButton? DetailIn(int pane)
    {
        if (pane < 0 || pane >= _panes.Length)
        {
            return null;
        }

        foreach (var row in _panes[pane])
        {
            if (row.Button is { Kind: ScreenButtonKind.Detail } button)
            {
                return button;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a pane position is one of its *list* rows rather than one of the buttons appended after
    /// them. Delete asks this before acting: on a button row the cursor already has ⏎, and deleting the
    /// selection from under a row that isn't it would be the surprise the whole selection anchor exists
    /// to prevent.
    /// </summary>
    internal bool IsListRow(int pane, int index) =>
        pane >= 0 && pane < _panes.Length && index >= 0 && index < ListSizes[pane];

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
