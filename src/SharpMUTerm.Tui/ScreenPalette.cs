namespace SharpMUTerm.Tui;

/// <summary>
/// The colours the full-screen settings screens (F2–F9) are built from. Renderers interpolate them
/// into markup tags; the views hand the same hexes to <see cref="SharpConsoleUI.Color"/> as control
/// backgrounds, so the bands a renderer writes text for and the panel a view paints agree by
/// construction. Screen files pull these in with
/// <c>using static SharpMUTerm.Tui.ScreenPalette;</c>.
/// </summary>
internal static class ScreenPalette
{
    /// <summary>The app accent — checkmarks, the Save action, and a world's fallback colour.</summary>
    internal const string Accent = "#00f5b7";

    /// <summary>The overlay backdrop the screens float on.</summary>
    internal const string PanelBg = "#171b24";

    /// <summary>Default text on the backdrop.</summary>
    internal const string PanelFg = "#c8d0e0";

    /// <summary>The header band behind the title and keyboard hints.</summary>
    internal const string HeaderBg = "#232b3d";

    /// <summary>The footer band behind the action bar; the same tone as the header.</summary>
    internal const string FooterBg = "#232b3d";

    /// <summary>The elevated card an editing pane or options list sits on.</summary>
    internal const string EditBg = "#1d2333";

    /// <summary>Secondary text: field labels, hints, and footer context.</summary>
    internal const string Label = "#7c8699";

    /// <summary>Primary text: titles and field values.</summary>
    internal const string Value = "#d7deec";

    /// <summary>
    /// A value that is reported rather than offered — a world's TLS line, a character's password, a
    /// session's state. Deliberately between <see cref="Label"/> and <see cref="Value"/>: a readout is
    /// not its own label, but it must not sit at the same weight as something the keyboard can change.
    /// See <see cref="ScreenChrome.ReadOnly"/>, which is the only place it is applied.
    /// </summary>
    internal const string Muted = "#9aa4b8";

    /// <summary>Hairlines — column rules and separators.</summary>
    internal const string Rule = "#3a4257";

    /// <summary>
    /// The bar under the keyboard cursor. Lighter than <see cref="EditBg"/> so it reads as a cursor on
    /// both the backdrop and an elevated card, without competing with the accent the way a filled
    /// accent bar would when it lands on every screen.
    /// </summary>
    internal const string CursorBg = "#2e3950";

    /// <summary>Near-black, for text printed *on* the accent (the Save chip, and the block caret).</summary>
    internal const string Ink = "#0f1620";

    /// <summary>
    /// The well behind an editable value — whether or not it is being typed into. Clearly darker than
    /// every panel it can appear on (and than <see cref="CursorBg"/>), so a field reads as a recessed
    /// input on any of them, at rest and without focus. The well *is* the affordance: a value drawn in
    /// one can be changed, a value drawn without one cannot. An open edit is the same well plus the
    /// accent block caret, so pressing ⏎ deepens the affordance already there instead of conjuring a
    /// new one.
    /// </summary>
    internal const string FieldBg = "#0a0e18";

    /// <summary>A refused value's marker — the one place these screens raise their voice.</summary>
    internal const string Warn = "#ff6b6b";
}
