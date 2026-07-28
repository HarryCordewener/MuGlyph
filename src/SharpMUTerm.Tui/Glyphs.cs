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
    public const string Draft = "\uf040"; // nf-fa-pencil — unsent-input marker (was the pen)
    public const string Close = "\uf00d"; // nf-fa-times — active-tab close (was the multiply sign)
    public const string Capture = "\uf090"; // nf-fa-sign_in — spawn capture / route (was the into-corner arrow)
    public const string Log = "\uf0f6"; // nf-fa-file_text_o — log indicator (was the fisheye)
    public const string Heartbeat = "\uf21e"; // nf-fa-heartbeat — keepalive (was the recycle mark)
    public const string World = "\uf0ac"; // nf-fa-globe — world/server accent (paired with the spine)

    // Powerline separators (solid triangles) for flowing segmented bars.
    public const string PowerRight = "\ue0b0"; // 
    public const string PowerLeft = "\ue0b2";  // 
}
