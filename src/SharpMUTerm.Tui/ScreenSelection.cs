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

    /// <summary>Creates a selection over <paramref name="paneCount"/> panes, focused on the first.</summary>
    internal ScreenSelection(int paneCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(paneCount, 1);
        _cursors = new int[paneCount];
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
    /// Seeds a pane's cursor without moving focus — used when a screen opens on state the app already
    /// tracks (F5 opens on the connected world and character). Negative indices are ignored.
    /// </summary>
    internal void Seed(int pane, int index)
    {
        if (pane >= 0 && pane < _cursors.Length && index >= 0)
        {
            _cursors[pane] = index;
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
    /// Moves focus to the next pane that has rows, wrapping past the last. Empty panes are skipped so
    /// ⇥ never lands somewhere with no cursor (a world with no characters, an editor with no
    /// toggles). Returns false when no other pane can take focus.
    /// </summary>
    internal bool NextPane(IReadOnlyList<int> paneSizes) => Step(1, paneSizes);

    /// <summary>The mirror of <see cref="NextPane"/>, for Shift+⇥.</summary>
    internal bool PreviousPane(IReadOnlyList<int> paneSizes) => Step(-1, paneSizes);

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
