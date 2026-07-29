using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Theming;

namespace SharpMUTerm.Tui;

/// <summary>
/// The three tones the main workspace is drawn on — the output <see cref="Surface"/> a pane paints,
/// the <see cref="Backdrop"/> everything that is not a pane sits on, and the <see cref="Rule"/>
/// hairline between panes.
/// <para>
/// They are <em>derived from the active theme</em> rather than written down as hexes, because the
/// theme is the user's (Dark / Light / Solarized Dark / a hand-written one) and a fixed pair would be
/// right for exactly one of them. What is fixed is the <em>relationship</em>, and it is the one the
/// settings screens already use: <see cref="ScreenPalette.PanelBg"/> sits a little over three quarters
/// of the way from black to <see cref="ScreenPalette.EditBg"/>, so a card reads as raised off its
/// backdrop without either tone leaving the family. The workspace uses that same step, which is why
/// F5 and the pane behind it look like two views of one application.
/// </para>
/// <para>
/// The surface is deliberately <em>not</em> the theme's plain text background: it is that background
/// nudged a quarter of the way toward the theme's own chrome tone, so the output area carries a hint
/// of the colour the header and input bands are already painted in. MU* text is unaffected — a span
/// with the default background emits no background at all (see <see cref="MarkupFormatter"/>), so it
/// takes whatever surface it is drawn on.
/// </para>
/// </summary>
internal static class WorkspacePalette
{
    /// <summary>
    /// How far the surface moves from the theme's text background toward its chrome background. Small
    /// on purpose: this tints the plane the game's own colours are read against, and anything the eye
    /// can name as a colour would be competing with them.
    /// </summary>
    private const double ChromeTint = 0.25;

    /// <summary>
    /// The backdrop as a fraction of the surface, taken from <see cref="ScreenPalette"/>'s own pair —
    /// the mean of <c>PanelBg ÷ EditBg</c> across the three channels. Sharing the settings screens'
    /// step is the whole point: one application, one idea of how far a card floats.
    /// </summary>
    private const double BackdropScale = 0.757;

    /// <summary>
    /// How far the hairline between two panes moves from the <em>surface</em> toward the theme's border
    /// colour. Lifted off the surface rather than off the backdrop, because the rule has both planes
    /// beside it — the backdrop where it runs down the side of the rail, the surface where it separates
    /// two panes — and only the surface end guarantees a step away from each. (On a dark theme that
    /// lands it lighter than both, exactly where <see cref="ScreenPalette.Rule"/> sits on the settings
    /// screens; on a light one it lands between them, which is where a hairline belongs there.) Short of
    /// the border itself: a rule on <see cref="Theme.Border"/> reads fine once and shouts at four panes,
    /// and a divider's job is to be found, not noticed.
    /// </summary>
    private const double RuleLift = 0.45;

    /// <summary>The plane a pane's output is painted on — tab strip, scrollback and empty rows alike.</summary>
    internal static Rgb Surface(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return Mix(theme.Background, theme.StatusBackground, ChromeTint);
    }

    /// <summary>
    /// The plane everything that is not a pane sits on: the connection rail, the status line, and the
    /// gaps a split leaves between panes. Recessed relative to <see cref="Surface"/>, so an empty pane
    /// is still a visible rectangle and a workspace of many panes reads as cards on a desk.
    /// </summary>
    internal static Rgb Backdrop(Theme theme) => Scale(Surface(theme), BackdropScale);

    /// <summary>The one-cell hairline a split draws between two panes, and beside the rail.</summary>
    internal static Rgb Rule(Theme theme) => Mix(Surface(theme), theme.Border, RuleLift);

    /// <summary>Linear blend of two colours, <paramref name="t"/> of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static Rgb Mix(Rgb from, Rgb to, double t) => new(
        Channel(from.R + ((to.R - from.R) * t)),
        Channel(from.G + ((to.G - from.G) * t)),
        Channel(from.B + ((to.B - from.B) * t)));

    /// <summary>Scales a colour toward black, keeping its hue.</summary>
    private static Rgb Scale(Rgb rgb, double factor) =>
        new(Channel(rgb.R * factor), Channel(rgb.G * factor), Channel(rgb.B * factor));

    private static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
