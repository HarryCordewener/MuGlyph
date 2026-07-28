namespace SharpMUTerm.Tui;

/// <summary>
/// What a renderer needs to know about the keyboard: which pane holds it and which row it is on.
/// Renderers take this as an optional argument so a screen rendered without one (the unit tests, and
/// any caller that only wants the projection) draws exactly what it always did — the cursor bar only
/// appears once a live screen says where the cursor is.
/// </summary>
/// <param name="Pane">The pane the keyboard is in, or -1 for "no keyboard".</param>
/// <param name="Index">The row the cursor is on within that pane.</param>
internal readonly record struct ScreenFocus(int Pane, int Index)
{
    /// <summary>No keyboard on this screen — nothing is drawn as the cursor.</summary>
    internal static ScreenFocus None => new(-1, -1);

    /// <summary>Whether the cursor is on a given pane's row.</summary>
    internal bool IsOn(int pane, int index) => Pane == pane && Index == index;

    /// <summary>Whether a pane holds the keyboard at all (its list draws its cursor, others don't).</summary>
    internal bool InPane(int pane) => Pane == pane;
}
