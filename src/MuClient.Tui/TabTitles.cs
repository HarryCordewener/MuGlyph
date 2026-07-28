using MuClient.Core.Workspaces;

namespace MuClient.Tui;

/// <summary>
/// Formats a window's tab-strip label from its <see cref="WorkspaceWindow"/> state: the title, an
/// unread badge like <c>(3)</c> while the tab is in the background, a <c>✎</c> pen when it holds an
/// unsent input draft, a <c>⌁</c> when the window belongs to a <em>different</em> character than the
/// one currently focused, and a <c>✕</c> close affordance on the active tab only. Pure so it can be
/// unit-tested without a terminal.
/// </summary>
/// <remarks>
/// SharpConsoleUI renders tab titles as plain text, so the design's per-character accent <em>colour</em>
/// dot can't ride on the label — the traceability signal that survives is the <c>⌁</c> cross-character
/// marker, which this emits. Accent colour still shows in the rail and (for the focused pane) its border.
/// </remarks>
internal static class TabTitles
{
    public static string For(WorkspaceWindow window, string? focusedCharacterKey = null, bool isActive = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        var unread = window.Unread > 0 ? $" ({window.Unread})" : string.Empty;
        var pen = window.HasUnsentInput ? " ✎" : string.Empty;

        // ⌁ marks a window owned by a character other than the focused one, so a pane holding
        // borrowed windows stays traceable to their owners.
        var cross = focusedCharacterKey is not null
                    && window.SessionKey is not null
                    && !string.Equals(window.SessionKey, focusedCharacterKey, StringComparison.Ordinal)
            ? " ⌁"
            : string.Empty;

        var close = isActive ? " ✕" : string.Empty;
        return window.Title + unread + pen + cross + close;
    }
}
