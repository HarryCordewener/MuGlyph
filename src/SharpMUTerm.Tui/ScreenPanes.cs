namespace SharpMUTerm.Tui;

/// <summary>
/// Where one of a settings screen's panes sits on the screen, as a coarse grid coordinate rather than
/// a rectangle: which column of the layout it is drawn in, and how far down that column it comes. It
/// is deliberately coarse — the arrow keys need to know that F5's security checkboxes are drawn
/// <em>above</em> its characters list and in the <em>same</em> column as it, and nothing finer than
/// that. Cell geometry belongs to the view, which is rebuilt on every keystroke and cannot be
/// consulted from a pure navigation rule.
/// </summary>
/// <param name="Column">The body column the pane is drawn in, left to right from zero.</param>
/// <param name="Row">How far down that column the pane comes, top to bottom from zero.</param>
internal readonly record struct ScreenPanePlace(int Column, int Row);

/// <summary>
/// The pure rules for moving between a screen's panes, given where those panes are drawn. They are
/// separate from <see cref="ScreenSelection"/> for the same reason pane sizes are passed in rather
/// than cached: a keystroke can change how many rows a pane holds, and every one of these is a
/// function of the layout and the sizes it is handed and of nothing else.
/// <para>
/// Every rule skips panes with no rows, because a pane with nothing in it has nowhere for the cursor
/// to land — a world with no characters, an editor with no toggles. All of them return -1 when there
/// is nowhere to go, which is what lets the caller swallow the key instead of pretending it moved.
/// </para>
/// </summary>
internal static class ScreenPanes
{
    /// <summary>
    /// The screen's panes in <b>reading order</b> — down the rows, then across the columns — which is
    /// the order ⇥ walks. Index order was the obvious rule and is the wrong one: F5 appends its
    /// security pane last (a pane index is a cursor coordinate, so it could not be slotted in beside
    /// the world it describes) while drawing it *above* the characters list, so a linear ⇥ went
    /// left-top → right-middle → bottom-right and then jumped back up the screen. Reading order is
    /// what the eye already did.
    /// </summary>
    internal static IReadOnlyList<int> ReadingOrder(IReadOnlyList<ScreenPanePlace> layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var order = new int[layout.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            var row = layout[a].Row.CompareTo(layout[b].Row);
            if (row != 0)
            {
                return row;
            }

            var column = layout[a].Column.CompareTo(layout[b].Column);
            return column != 0 ? column : a.CompareTo(b);
        });

        return order;
    }

    /// <summary>
    /// The pane ⇥ (<paramref name="direction"/> 1) or ⇧⇥ (-1) reaches from <paramref name="from"/>:
    /// the next non-empty one in reading order, wrapping past the end. ⇥ is the screen's <em>cycle</em>
    /// — it is the only key guaranteed to reach every pane, including one the arrows cannot get to
    /// because it is alone in its column — so it wraps where the arrows deliberately do not.
    /// </summary>
    internal static int Step(
        IReadOnlyList<ScreenPanePlace> layout, IReadOnlyList<int> sizes, int from, int direction)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(sizes);

        var order = ReadingOrder(layout);
        var at = IndexOf(order, from);
        if (at < 0 || order.Count == 0)
        {
            return -1;
        }

        for (var hop = 1; hop < order.Count; hop++)
        {
            var candidate = order[((at + (direction * hop)) % order.Count + order.Count) % order.Count];
            if (SizeOf(candidate, sizes) > 0)
            {
                return candidate;
            }
        }

        return -1;
    }

    /// <summary>
    /// The pane → (<paramref name="direction"/> 1) or ← (-1) reaches: the nearest column on that side
    /// that holds a pane with rows, and within it the pane drawn nearest the current one's row. It does
    /// <b>not</b> wrap, so ← at the left edge parks rather than teleporting to the far side — the same
    /// promise ↑ already makes at the top of a list, and the reason ⇥ is still worth having.
    /// </summary>
    internal static int Beside(
        IReadOnlyList<ScreenPanePlace> layout, IReadOnlyList<int> sizes, int from, int direction)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(sizes);

        if (from < 0 || from >= layout.Count)
        {
            return -1;
        }

        var here = layout[from];
        var best = -1;
        for (var pane = 0; pane < layout.Count; pane++)
        {
            if (pane == from || SizeOf(pane, sizes) == 0)
            {
                continue;
            }

            var there = layout[pane];
            if (Math.Sign(there.Column - here.Column) != direction)
            {
                continue;
            }

            if (best < 0 || Closer(layout[best], there, here))
            {
                best = pane;
            }
        }

        return best;
    }

    /// <summary>
    /// The pane stacked directly below (<paramref name="direction"/> 1) or above (-1) this one in the
    /// same column, or -1 when it is alone there. It is what lets ↓ walk off the end of one pane into
    /// the top of the next — on F5 that is security → characters → trigger sets, one continuous run
    /// down the detail column — without ↑↓ ever wrapping or leaving the column the eye is following.
    /// On every screen whose panes are side by side there is no such pane, so ↑↓ mean exactly what
    /// they always did.
    /// </summary>
    internal static int Stacked(
        IReadOnlyList<ScreenPanePlace> layout, IReadOnlyList<int> sizes, int from, int direction)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(sizes);

        if (from < 0 || from >= layout.Count)
        {
            return -1;
        }

        var here = layout[from];
        var best = -1;
        for (var pane = 0; pane < layout.Count; pane++)
        {
            if (pane == from || SizeOf(pane, sizes) == 0)
            {
                continue;
            }

            var there = layout[pane];
            if (there.Column != here.Column || Math.Sign(there.Row - here.Row) != direction)
            {
                continue;
            }

            if (best < 0 || Math.Abs(there.Row - here.Row) < Math.Abs(layout[best].Row - here.Row))
            {
                best = pane;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> beats <paramref name="best"/> as the pane beside
    /// <paramref name="here"/>: the nearer column first, then the nearer row, then the one drawn
    /// higher — so a wide gap in the layout never lets a distant column win on row alone.
    /// </summary>
    private static bool Closer(ScreenPanePlace best, ScreenPanePlace candidate, ScreenPanePlace here)
    {
        var bestColumn = Math.Abs(best.Column - here.Column);
        var thisColumn = Math.Abs(candidate.Column - here.Column);
        if (thisColumn != bestColumn)
        {
            return thisColumn < bestColumn;
        }

        var bestRow = Math.Abs(best.Row - here.Row);
        var thisRow = Math.Abs(candidate.Row - here.Row);
        return thisRow != bestRow ? thisRow < bestRow : candidate.Row < best.Row;
    }

    private static int IndexOf(IReadOnlyList<int> order, int pane)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == pane)
            {
                return i;
            }
        }

        return -1;
    }

    private static int SizeOf(int pane, IReadOnlyList<int> sizes) =>
        pane >= 0 && pane < sizes.Count ? Math.Max(0, sizes[pane]) : 0;
}
