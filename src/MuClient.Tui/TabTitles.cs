using MuClient.Core.Workspaces;

namespace MuClient.Tui;

/// <summary>
/// Formats a window's tab-strip label from its <see cref="WorkspaceWindow"/> state: the title, an
/// unread badge like <c>(3)</c> while the tab is in the background, and a <c>✎</c> pen when it holds
/// an unsent input draft. Pure so it can be unit-tested without a terminal.
/// </summary>
internal static class TabTitles
{
    public static string For(WorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var unread = window.Unread > 0 ? $" ({window.Unread})" : string.Empty;
        var pen = window.HasUnsentInput ? " ✎" : string.Empty;
        return window.Title + unread + pen;
    }
}
