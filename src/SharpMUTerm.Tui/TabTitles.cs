using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Tui;

/// <summary>
/// Formats a window's tab-strip label from its <see cref="WorkspaceWindow"/> state: the title, an
/// unread badge like <c>(3)</c> while the tab is in the background, a <c>✎</c> pen when it holds an
/// unsent input draft, and a <c>⌁</c> when the window belongs to a <em>different</em> character than
/// the one currently focused. Pure so it can be unit-tested without a terminal.
/// </summary>
/// <remarks>
/// <para>SharpConsoleUI renders tab titles as plain text, so the design's per-character accent
/// <em>colour</em> dot can't ride on the label — the traceability signal that survives is the <c>⌁</c>
/// cross-character marker, which this emits. Accent colour still shows in the rail; the focused
/// <em>pane</em> is marked by the <c>▌</c> this emits plus the lit plane it is painted on
/// (<see cref="WorkspacePalette.Focus"/>), because a pane cannot be given a border without changing its
/// rectangle and so the per-pane NAWS size it reports.</para>
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
            ? window.OwnerLabel + " - "
            : string.Empty;

        var unread = window.Unread > 0 ? $" ({window.Unread})" : string.Empty;
        var pen = window.HasUnsentInput ? $" {Glyphs.Draft}" : string.Empty;

        // ⌁ marks a window owned by a character other than the focused one, so a pane holding
        // borrowed windows stays traceable to their owners.
        var cross = focusedCharacterKey is not null
                    && window.SessionKey is not null
                    && !string.Equals(window.SessionKey, focusedCharacterKey, StringComparison.Ordinal)
            ? " ⌁"
            : string.Empty;

        var focus = focusedPane ? Glyphs.FocusedPane + " " : string.Empty;

        return focus + owner + window.Title + unread + pen + cross;
    }
}
