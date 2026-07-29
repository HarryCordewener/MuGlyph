namespace SharpMUTerm.Tui;

/// <summary>
/// Where the keyboard is on a settings screen: which pane holds focus, and where each pane's cursor
/// sits. A screen's panes change size as the selection moves (picking another world changes how many
/// characters there are), so the sizes are handed in on every move rather than cached here — this
/// object only remembers cursors, which keeps it a pure state machine with no view or config
/// dependency and makes every navigation rule unit-testable.
/// </summary>
internal sealed class ScreenSelection
{
    private readonly int[] _cursors;
    private readonly int[] _anchors;

    /// <summary>Creates a selection over <paramref name="paneCount"/> panes, focused on the first.</summary>
    internal ScreenSelection(int paneCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(paneCount, 1);
        _cursors = new int[paneCount];
        _anchors = new int[paneCount];
    }

    /// <summary>How many panes the screen has.</summary>
    internal int PaneCount => _cursors.Length;

    /// <summary>The pane the keyboard is in.</summary>
    internal int Pane { get; private set; }

    /// <summary>The cursor position within the focused pane.</summary>
    internal int Index => _cursors[Pane];

    /// <summary>The cursor position a pane will return to when focus comes back to it.</summary>
    internal int CursorIn(int pane) =>
        pane >= 0 && pane < _cursors.Length ? _cursors[pane] : -1;

    /// <summary>
    /// Which of a pane's *list* rows is selected — the same thing as the cursor while the cursor is on
    /// one, and the last such row once it has moved on to the pane's buttons. That is what keeps
    /// <c>[[- del]]</c> pointed at the world the screen is showing instead of at whichever one happens
    /// to be last: the cursor has to leave the list to reach the button, and the selection must not
    /// leave with it.
    /// </summary>
    internal int SelectionIn(int pane) =>
        pane >= 0 && pane < _anchors.Length ? _anchors[pane] : -1;

    /// <summary>
    /// Re-reads the selection from the cursors, given how many rows of each pane are list rows rather
    /// than buttons. A cursor on a list row *is* the selection; a cursor on a button leaves it where it
    /// was, clamped in case the button it just ran shortened the list underneath it.
    /// </summary>
    internal void Anchor(IReadOnlyList<int> listSizes)
    {
        ArgumentNullException.ThrowIfNull(listSizes);

        for (var pane = 0; pane < _anchors.Length; pane++)
        {
            var size = SizeOf(pane, listSizes);
            if (_cursors[pane] < size)
            {
                _anchors[pane] = _cursors[pane];
            }

            _anchors[pane] = size == 0 ? 0 : Math.Clamp(_anchors[pane], 0, size - 1);
        }
    }

    /// <summary>
    /// Seeds a pane's cursor without moving focus — used when a screen opens on state the app already
    /// tracks (F5 opens on the connected world and character). Negative indices are ignored.
    /// </summary>
    internal void Seed(int pane, int index)
    {
        if (pane >= 0 && pane < _cursors.Length && index >= 0)
        {
            _cursors[pane] = index;
            _anchors[pane] = index;
        }
    }

    /// <summary>
    /// Puts the keyboard in a pane before the screen first opens, without moving any cursor — used when
    /// the key that opened the screen means "start here" (F9 opens F5 on the character whose log it
    /// used to edit). Out-of-range panes are ignored, and an empty pane is corrected by the first
    /// <see cref="Clamp"/>, so a seeded focus can never park the cursor somewhere with no rows.
    /// </summary>
    internal void FocusPane(int pane)
    {
        if (pane >= 0 && pane < _cursors.Length)
        {
            Pane = pane;
        }
    }

    /// <summary>
    /// Moves the focused pane's cursor by <paramref name="delta"/> rows, clamped to the pane's
    /// current size — deliberately not wrapping, so holding ↑ parks on the first row instead of
    /// teleporting to the last. Returns whether the cursor actually moved.
    /// </summary>
    internal bool Move(int delta, IReadOnlyList<int> paneSizes)
    {
        ArgumentNullException.ThrowIfNull(paneSizes);

        Clamp(paneSizes);
        var size = SizeOf(Pane, paneSizes);
        if (size == 0)
        {
            return false;
        }

        var next = Math.Clamp(_cursors[Pane] + delta, 0, size - 1);
        if (next == _cursors[Pane])
        {
            return false;
        }

        _cursors[Pane] = next;
        return true;
    }

    /// <summary>
    /// Jumps the focused pane's cursor to a row, clamped to the pane's current size —
    /// <see cref="int.MaxValue"/> is End, 0 is Home. It exists for the button rows a list pane ends in:
    /// they can only be reached by ↑↓ after walking past every row of the list, which would drag the
    /// selection to the last one and leave <c>[[- del]]</c> pointed at something the user never chose.
    /// End steps over the list without touching the selection, because a cursor on a button doesn't
    /// re-anchor. Returns whether the cursor actually moved.
    /// </summary>
    internal bool MoveTo(int index, IReadOnlyList<int> paneSizes)
    {
        ArgumentNullException.ThrowIfNull(paneSizes);

        Clamp(paneSizes);
        var size = SizeOf(Pane, paneSizes);
        if (size == 0)
        {
            return false;
        }

        var next = Math.Clamp(index, 0, size - 1);
        if (next == _cursors[Pane])
        {
            return false;
        }

        _cursors[Pane] = next;
        return true;
    }

    /// <summary>
    /// Moves focus to the next pane that has rows, wrapping past the last. Empty panes are skipped so
    /// ⇥ never lands somewhere with no cursor (a world with no characters, an editor with no
    /// toggles). Returns false when no other pane can take focus.
    /// <para>
    /// With no layout the panes are walked in index order, which is what a caller holding nothing but
    /// row counts can honestly do. The overload taking one walks them in <em>reading</em> order
    /// instead; on every screen whose panes are drawn side by side the two are the same walk.
    /// </para>
    /// </summary>
    internal bool NextPane(IReadOnlyList<int> paneSizes) => Step(1, paneSizes);

    /// <summary>The mirror of <see cref="NextPane"/>, for Shift+⇥.</summary>
    internal bool PreviousPane(IReadOnlyList<int> paneSizes) => Step(-1, paneSizes);

    /// <summary>⇥ through the panes in the order they are drawn (see <see cref="ScreenPanes.Step"/>).</summary>
    internal bool NextPane(IReadOnlyList<int> paneSizes, IReadOnlyList<ScreenPanePlace> layout) =>
        Land(ScreenPanes.Step(layout, paneSizes, Pane, 1), paneSizes);

    /// <summary>⇧⇥ through them backwards — the exact reverse of <see cref="NextPane"/>.</summary>
    internal bool PreviousPane(IReadOnlyList<int> paneSizes, IReadOnlyList<ScreenPanePlace> layout) =>
        Land(ScreenPanes.Step(layout, paneSizes, Pane, -1), paneSizes);

    /// <summary>
    /// ←/→: focus the pane drawn beside this one, keeping the cursor that pane was last left on. It is
    /// a <em>move</em> and not a cycle — at the edge of the layout it parks, exactly as ↑ parks on the
    /// first row of a list — because a key that wrapped the screen sideways would make the two ways of
    /// changing pane indistinguishable, and ⇥ is already the one that goes everywhere.
    /// </summary>
    internal bool MoveBeside(int direction, IReadOnlyList<int> paneSizes, IReadOnlyList<ScreenPanePlace> layout)
    {
        Clamp(paneSizes);
        return Land(ScreenPanes.Beside(layout, paneSizes, Pane, direction), paneSizes);
    }

    /// <summary>
    /// ↑/↓: move a row within the focused pane, and — only when the pane runs out and another is drawn
    /// directly under (or over) it in the same column — carry on into that one, landing on the row
    /// nearest the boundary just crossed. That is what makes F5's detail column one continuous run
    /// under the cursor rather than three lists that happen to be stacked.
    /// <para>
    /// The spill is the whole of the difference from <see cref="Move"/>, and it can only happen where a
    /// screen has said two panes share a column. Under the default side-by-side layout no pane has a
    /// neighbour above or below, so this is <see cref="Move"/> exactly — including its refusal to wrap
    /// at the ends of a list.
    /// </para>
    /// </summary>
    internal bool MoveVertically(int delta, IReadOnlyList<int> paneSizes, IReadOnlyList<ScreenPanePlace> layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (Move(delta, paneSizes))
        {
            return true;
        }

        var direction = Math.Sign(delta);
        var next = direction == 0 ? -1 : ScreenPanes.Stacked(layout, paneSizes, Pane, direction);
        if (next < 0)
        {
            return false;
        }

        Pane = next;
        var size = SizeOf(next, paneSizes);
        _cursors[next] = direction > 0 ? 0 : Math.Max(0, size - 1);
        return true;
    }

    /// <summary>
    /// Puts the keyboard in <paramref name="pane"/>, pulling that pane's remembered cursor back inside
    /// it. -1 means "there was nowhere to go", which is how every layout rule spells a refusal.
    /// </summary>
    private bool Land(int pane, IReadOnlyList<int> paneSizes)
    {
        ArgumentNullException.ThrowIfNull(paneSizes);

        if (pane < 0 || pane >= _cursors.Length || pane == Pane)
        {
            return false;
        }

        Pane = pane;
        _cursors[pane] = Math.Max(0, Math.Min(_cursors[pane], SizeOf(pane, paneSizes) - 1));
        return true;
    }

    /// <summary>
    /// Pulls every cursor back inside its pane and moves focus off a pane that has emptied, so a
    /// selection that outlived the rows it pointed at still renders something sane.
    /// </summary>
    internal void Clamp(IReadOnlyList<int> paneSizes)
    {
        ArgumentNullException.ThrowIfNull(paneSizes);

        for (var pane = 0; pane < _cursors.Length; pane++)
        {
            var size = SizeOf(pane, paneSizes);
            _cursors[pane] = size == 0 ? 0 : Math.Clamp(_cursors[pane], 0, size - 1);
        }

        if (SizeOf(Pane, paneSizes) == 0)
        {
            Step(1, paneSizes);
        }
    }

    /// <summary>Whether the focused pane has a row under the cursor at all.</summary>
    internal bool HasSelection(IReadOnlyList<int> paneSizes)
    {
        ArgumentNullException.ThrowIfNull(paneSizes);
        return SizeOf(Pane, paneSizes) > 0;
    }

    private bool Step(int direction, IReadOnlyList<int> paneSizes)
    {
        for (var hop = 1; hop < _cursors.Length; hop++)
        {
            var candidate = ((Pane + (direction * hop)) % _cursors.Length + _cursors.Length) % _cursors.Length;
            if (SizeOf(candidate, paneSizes) > 0)
            {
                Pane = candidate;
                _cursors[Pane] = Math.Clamp(_cursors[Pane], 0, SizeOf(Pane, paneSizes) - 1);
                return true;
            }
        }

        return false;
    }

    private static int SizeOf(int pane, IReadOnlyList<int> paneSizes) =>
        pane >= 0 && pane < paneSizes.Count ? Math.Max(0, paneSizes[pane]) : 0;
}
