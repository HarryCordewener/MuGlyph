namespace SharpMUTerm.Tui;

/// <summary>
/// Centralised Nerd Font (v3) icon glyphs for SharpMUTerm's chrome. The terminals SharpMUTerm targets
/// (Kitty, WezTerm, Ghostty) are routinely run with Nerd Font builds, so these icons render as
/// crisp symbols there; on a plain font they degrade to a "tofu" box. Keeping them in one place
/// means a future MTTS-driven fallback to plain geometric glyphs is a single-file change rather
/// than a hunt through every renderer.
/// </summary>
/// <remarks>
/// Codepoints are the FontAwesome-in-Nerd-Fonts private-use range (expressed as escapes so the
/// source stays ASCII-clean). The plain glyph each replaces is noted for an easy fallback set.
/// </remarks>
internal static class Glyphs
{
    public const string Menu = "\uf0c9"; // nf-fa-bars — header menu (was the box-drawing menu mark)
    public const string Connections = "\uf1e6"; // nf-fa-plug — rail header
    public const string Freeze = "\uf2dc"; // nf-fa-snowflake — freeze bar (was the up-triangle)
    public const string Scrollback = "\uf102"; // nf-fa-angle_double_up — status row: output above the view
    public const string Draft = "\uf040"; // nf-fa-pencil — unsent-input marker (was the pen)
    public const string Close = "\uf00d"; // nf-fa-times (was the multiply sign). NOT drawn by us: a tab's
    // close button is SharpConsoleUI's own TabPage.IsClosable, which the framework renders as × and
    // hit-tests into TabCloseRequested. Kept so the tab-title tests can assert the label never
    // smuggles a decorative close glyph back in.
    public const string Capture = "\uf090"; // nf-fa-sign_in — spawn capture / route (was the into-corner arrow)
    public const string Log = "\uf0f6"; // nf-fa-file_text_o — log indicator (was the fisheye)
    public const string World = "\uf0ac"; // nf-fa-globe — world/server accent (paired with the spine)

    /// <summary>
    /// The focused pane's marker, drawn on the active tab of the pane every workspace key acts on. Box
    /// drawing rather than a Nerd Font icon, deliberately: it is the one glyph here whose job is to be
    /// legible when nothing else is, so it must not be the character that degrades to a tofu box on a
    /// plain font. It is also a left <em>edge</em> — what a focus border would have been, had a pane been
    /// able to afford the cell for one without changing the size it reports over NAWS.
    /// </summary>
    public const string FocusedPane = "▌"; // ▌ left half block

    // Powerline separators (solid triangles) for flowing segmented bars.
    public const string PowerRight = "\ue0b0"; // 
    public const string PowerLeft = "\ue0b2";  // 
}
