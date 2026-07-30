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

    /// <summary>
    /// How far the <em>focused</em> plane is lifted off the unfocused one, as a fraction of it. Like
    /// <see cref="BackdropScale"/> this is not a number somebody liked: it is the mean of
    /// <c>CursorBg ÷ EditBg</c> across the three channels — the step the settings screens already take
    /// to say "the keyboard is here", measured off the card the cursor bar sits on
    /// (<see cref="ScreenPalette.CursorBg"/> is documented as exactly that). Reusing it means the
    /// workspace and F5 do not have two different ideas of what focus looks like, and it is a step of
    /// about three fifths rather than the thirteen points per channel the two input bands used to
    /// differ by — which is the whole complaint.
    /// <para>
    /// It is a <em>scale</em>, not a mix toward a hue, deliberately: multiplying keeps the theme's own
    /// colour and changes only its luminance, so the cue survives a monochrome terminal and a
    /// colour-blind reader, and a light theme lifts the same way a dark one does.
    /// </para>
    /// </summary>
    private const double FocusScale = 1.595;

    /// <summary>
    /// How far the idle input band is recessed from the theme's chrome tone. Like the other constants
    /// here it is measured off what the design already chose: the mean of the old hardcoded
    /// <c>#262b3a</c> over the default theme's <see cref="Theme.StatusBackground"/>, across the three
    /// channels. The idle band therefore lands where it always did — the tone was never the complaint —
    /// while <see cref="ArmedBand"/> moves away from it by a step the eye can actually find.
    /// </summary>
    private const double IdleBandScale = 0.814;

    /// <summary>
    /// How far the armed input band leans toward <see cref="Theme.Prompt"/>. Enough to read as a
    /// different <em>colour</em> and not merely a brighter one, which is the cue that survives a reader
    /// who sees luminance but not hue being given the opposite problem; small enough that the band is
    /// still the theme's chrome rather than a stripe of accent across the bottom of the window.
    /// </summary>
    private const double PromptTint = 0.28;

    /// <summary>The plane a pane's output is painted on — tab strip, scrollback and empty rows alike.</summary>
    internal static Rgb Surface(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return Mix(theme.Background, theme.StatusBackground, ChromeTint);
    }

    /// <summary>
    /// The plane the <em>focused</em> pane's output is painted on, and the band behind the command line
    /// ⏎ sends from. One tone for both on purpose: "this is where you are" should be one thing to learn,
    /// not two, and the two questions a user has — which pane am I acting on, which line am I typing
    /// into — are then answered by the same colour in the two places it can appear.
    /// <para>
    /// It costs no cells. A focused pane is the same rectangle as an unfocused one, repainted; that
    /// matters because per-pane NAWS is derived from the pane rectangle, so a border or a marker column
    /// would re-announce a different terminal size to the server on every focus change and reflow the
    /// game's own output. See <c>SharpMUTermApp.PaneOutputRects</c>.
    /// </para>
    /// </summary>
    internal static Rgb Focus(Theme theme) => Scale(Surface(theme), FocusScale);

    /// <summary>
    /// The chrome band a command line is drawn on when ⏎ will <em>not</em> send from it. It is the theme's
    /// status/chrome tone recessed by <see cref="IdleBandScale"/> — the input area belongs to the chrome
    /// family, not to the pane surface, which is why it is measured off
    /// <see cref="Theme.StatusBackground"/> and not off <see cref="Surface"/>.
    /// <para>
    /// It sits where the design's own idle band sat; the tone was never the complaint. What was wrong was
    /// the <em>distance</em> to the armed one — the two hardcoded hexes were a ratio of about 1.33 apart,
    /// thirteen points per channel, which is genuinely close to invisible. <see cref="ArmedBand"/> now
    /// takes the same focus step everything else does, and picks up the theme's prompt hue on the way.
    /// </para>
    /// </summary>
    internal static Rgb IdleBand(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return Scale(theme.StatusBackground, IdleBandScale);
    }

    /// <summary>
    /// The band behind the command line ⏎ <em>does</em> send from: the idle band lifted by the same
    /// <see cref="FocusScale"/> a focused pane is lifted by, and then pushed a little toward
    /// <see cref="Theme.Prompt"/> — the theme's own colour for a prompt, which is what this band is.
    /// <para>
    /// The hue is affordable here and not on a pane: a pane's plane is what the game's own colours are
    /// read against, so <see cref="Focus"/> stays a pure luminance lift, while the input band is chrome
    /// and was already tinted. Between them the armed and idle bands now differ in luminance <em>and</em>
    /// hue, on top of the bold-versus-dim prompt and the bright-versus-dim ink — four cues, of which
    /// three survive a terminal that cannot render the fourth.
    /// </para>
    /// </summary>
    internal static Rgb ArmedBand(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return Mix(Scale(IdleBand(theme), FocusScale), theme.Prompt, PromptTint);
    }

    /// <summary>
    /// Text on an idle band: the theme's foreground pulled most of the way down to that band. Dimmer
    /// than the armed bar's ink, so the pair still reads apart if a terminal flattens both backgrounds.
    /// </summary>
    internal static Rgb IdleInk(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return Mix(theme.Foreground, IdleBand(theme), 0.55);
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
