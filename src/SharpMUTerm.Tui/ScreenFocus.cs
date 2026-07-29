namespace SharpMUTerm.Tui;

/// <summary>
/// What a renderer needs to know about the keyboard: which pane holds it, which row it is on, and —
/// once ⏎ has opened one — the field edit in flight on that row. Renderers take this as an optional
/// argument so a screen rendered without one (the unit tests, and any caller that only wants the
/// projection) draws exactly what it always did — the cursor bar only appears once a live screen says
/// where the cursor is, and the caret only once one is actually being typed into.
/// </summary>
/// <param name="Pane">The pane the keyboard is in, or -1 for "no keyboard".</param>
/// <param name="Index">The row the cursor is on within that pane.</param>
/// <param name="Edit">The open field edit on that row, or null when the screen is navigating.</param>
internal readonly record struct ScreenFocus(int Pane, int Index, ScreenFieldEdit? Edit = null)
{
    /// <summary>No keyboard on this screen — nothing is drawn as the cursor.</summary>
    internal static ScreenFocus None => new(-1, -1);

    /// <summary>Whether the cursor is on a given pane's row.</summary>
    internal bool IsOn(int pane, int index) => Pane == pane && Index == index;

    /// <summary>Whether a pane holds the keyboard at all (its list draws its cursor, others don't).</summary>
    internal bool InPane(int pane) => Pane == pane;

    /// <summary>Whether a field edit is open anywhere on the screen.</summary>
    internal bool IsEditing => Edit is not null;

    /// <summary>
    /// The open edit for one specific field of one specific row, or null. Renderers ask per drawn
    /// value, so the caret lands on the field being typed rather than on every field of the row.
    /// </summary>
    internal ScreenFieldEdit? EditOn(int pane, int index, int field) =>
        Edit is { } edit && IsOn(pane, index) && edit.Field == field ? edit : null;
}
