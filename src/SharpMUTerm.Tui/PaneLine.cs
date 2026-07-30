namespace SharpMUTerm.Tui;

/// <summary>
/// One line of a window's output buffer: the line's own markup, and — held <em>separately</em> — the
/// wall-clock stamp of when it arrived.
/// <para>
/// The two are apart on purpose. The timestamp gutter used to be baked into the markup by
/// <see cref="MarkupFormatter.ToMarkup(Core.Text.StyledLine, string?)"/> at the moment a line was
/// appended, which made <c>Show timestamps</c> an <b>append-time</b> decision: flipping it changed
/// nothing already on screen, so on a quiet connection the command had no observable effect at all.
/// That was the reported bug — the toggle was wired, and invisible.
/// </para>
/// <para>
/// Keeping the stamp beside the markup makes the gutter a <b>render-time</b> decision
/// (<c>SharpMUTermApp.Compose</c>), so the toggle repaints history in <em>every</em> window. That
/// matters most for the windows the alternative could not have reached: a session window could in
/// principle be rebuilt from its <c>WorldSession.Scrollback</c>, but a spawn window's history exists
/// only as markup in the app's own line buffer and there are no <c>StyledLine</c>s left to re-render.
/// A stamp recorded per line is available to both.
/// </para>
/// <para>
/// The stamp is recorded whether or not the gutter is currently shown, because when a line arrived is
/// a fact about the line and not about the setting: turning the column on has to be able to say when
/// the text already on screen came in.
/// </para>
/// <para>
/// <see cref="Stamp"/> is null for lines that are not a world's output — client replies such as
/// <c>/graphics</c> and <c>/triggers</c>, and the numbered demo scrollback scene. Those carried no
/// gutter before this change and carry none after it; the column has only ever described text a world
/// sent, and giving client chatter a fake arrival time would be inventing data.
/// </para>
/// </summary>
/// <param name="Markup">The line's own Spectre-style markup, with no gutter attached.</param>
/// <param name="Stamp">When the line arrived, pre-formatted for the gutter, or null for no gutter.</param>
internal readonly record struct PaneLine(string Markup, string? Stamp = null);
