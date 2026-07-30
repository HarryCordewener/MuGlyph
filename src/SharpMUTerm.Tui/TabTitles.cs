using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Tui;

/// <summary>
/// Formats a window's tab-strip label from its <see cref="WorkspaceWindow"/> state: the title, an
/// unread badge like <c>(3)</c> while the tab is in the background, a <c>✎</c> pen when it holds an
/// unsent input draft, and a <c>⌁</c> when the window belongs to a <em>different</em> character than
/// the one currently focused. Pure so it can be unit-tested without a terminal.
/// </summary>
/// <remarks>
/// <para><b>A tab title is markup, not plain text.</b> This file used to say the opposite, and the claim
/// had a cost: it is why the unread count went out untinted and why a window title was never escaped.
/// <c>TabControl.Rendering</c> runs each label through <c>MarkupParser.Parse</c>, and every width it is
/// measured by — the header paint, the strip's desired width, and the click hit test that decides which
/// tab and which <c>×</c> a press landed on — is <c>MarkupParser.StripLength</c>. So a colour tag here
/// costs <em>no cells</em> and moves no hit test, which is what makes the activity tint affordable on a
/// surface where a cell may not be spent.</para>
/// <para>It also means configured and world-supplied text has to be escaped on the way in
/// (<see cref="MarkupText.Escape"/>): a window titled <c>[Chat]</c> — or a web view titled from the page
/// it loaded — would otherwise have that eaten as a tag by the parser and by the hit test alike.</para>
/// <para>The focused <em>pane</em> is marked by the <c>▌</c> this emits plus the lit plane it is painted
/// on (<see cref="WorkspacePalette.Focus"/>), because a pane cannot be given a border without changing
/// its rectangle and so the per-pane NAWS size it reports. The <c>▌</c> is deliberately left
/// <em>outside</em> the activity tint: focus and activity are independent, a tab can have both, and a
/// marker that changed colour when a line arrived would be reporting the wrong fact.</para>
/// <para>The close affordance is deliberately <em>not</em> here. A <c>✕</c> written into the label is
/// just text: the framework's tab hit test sees it as part of the title and a click on it merely
/// selects the tab. The real close button is <c>TabPage.IsClosable</c>, which the framework draws
/// itself and hit-tests into <c>TabControl.TabCloseRequested</c> — see
/// <c>SharpMUTermApp.BuildPaneTabs</c>.</para>
/// </remarks>
internal static class TabTitles
{
    /// <param name="window">The window the tab stands for.</param>
    /// <param name="focusedCharacterKey">
    /// The session key of the character in focus, so a window belonging to another one can be marked.
    /// </param>
    /// <param name="focusedPane">
    /// Whether this tab is the <em>active</em> tab of the <em>focused pane</em> — the one pane the
    /// scrollback keys, the ⌃B commands and the Ctrl+arrows all act on. It gets a leading <c>▌</c>: the
    /// pane's own plane is lit as well, and the glyph is what carries the signal on a terminal where the
    /// two planes flatten together. It leads rather than trails because that is the edge of the strip a
    /// reader's eye starts at, and it is on the active tab only, so a pane never shows two.
    /// </param>
    public static string For(
        WorkspaceWindow window, string? focusedCharacterKey = null, bool focusedPane = false)
    {
        ArgumentNullException.ThrowIfNull(window);

        // A child window (spawn/aux) carries its owning connection as an "Owner - " prefix so it stays
        // traceable to its character once dragged into another pane. A character's own main window
        // needs no prefix — the focused-character context already identifies it.
        var owner = window.Kind != WindowKind.Main && !string.IsNullOrEmpty(window.OwnerLabel)
            ? MarkupText.Escape(window.OwnerLabel) + " - "
            : string.Empty;

        // Capped the way the sidebar's badge is, from the same formatter. Not for the sidebar's reason —
        // a tab strip is laid out along a row the framework fills to the pane's edge, so a label that grows
        // moves the tabs beside it and never the pane's own rectangle. The cap is here so the two surfaces
        // reading one number cannot print different answers, and so an unbounded count arriving from the
        // wire cannot push a pane's other tabs off the end of a narrow strip.
        var unread = window.Unread > 0 ? $" ({UnreadBadge.Format(window.Unread)})" : string.Empty;
        var pen = window.HasUnsentInput ? $" {Glyphs.Draft}" : string.Empty;

        // ⌁ marks a window owned by a character other than the focused one, so a pane holding
        // borrowed windows stays traceable to their owners.
        var cross = focusedCharacterKey is not null
                    && window.SessionKey is not null
                    && !string.Equals(window.SessionKey, focusedCharacterKey, StringComparison.Ordinal)
            ? " ⌁"
            : string.Empty;

        var focus = focusedPane ? Glyphs.FocusedPane + " " : string.Empty;

        // The activity tint. It covers the window's name and its count and stops there: the ▌ ahead of it
        // is the focus marker and the ✎ / ⌁ behind it are other facts, and a signal that recoloured them
        // would be claiming they had changed too. Zero cells — see the remarks on this class.
        var named = owner + MarkupText.Escape(window.Title) + unread;
        var body = window.Unread > 0 ? $"[{UnreadBadge.Tint}]{named}[/]" : named;

        return focus + body + pen + cross;
    }
}
