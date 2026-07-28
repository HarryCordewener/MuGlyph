namespace MuClient.Core.Workspaces;

/// <summary>
/// The full workspace state the TUI shell drives: the <see cref="WorkspaceLayout"/> pane tree plus
/// the registry of <see cref="WorkspaceWindow"/>s the panes host. It keeps the two consistent —
/// opening a window places it in a pane, closing one removes its tab, spawn routing finds-or-creates
/// the destination window — and tracks activity badges (unread, unsent-input) against visibility.
/// UI-agnostic and fully testable; the Terminal.Gui layer renders from it and calls its operations.
/// </summary>
public sealed class Workspace
{
    private readonly Dictionary<string, WorkspaceWindow> _windows = new(StringComparer.Ordinal);

    /// <summary>Creates a workspace with a single main window in one pane.</summary>
    public Workspace(string mainWindowId = "main", string mainTitle = "Main", string? sessionKey = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(mainWindowId);
        Layout = new WorkspaceLayout(new[] { mainWindowId });
        var main = new WorkspaceWindow(mainWindowId, mainTitle, WindowKind.Main, sessionKey);
        _windows[main.Id] = main;
    }

    /// <summary>The pane tree.</summary>
    public WorkspaceLayout Layout { get; }

    /// <summary>Every known window, in insertion order.</summary>
    public IReadOnlyCollection<WorkspaceWindow> Windows => _windows.Values;

    /// <summary>Looks up a window by id, or null.</summary>
    public WorkspaceWindow? FindWindow(string id) => _windows.GetValueOrDefault(id);

    /// <summary>
    /// Opens a window: registers it (if new) and places it as a tab. An existing id updates nothing
    /// but is returned so callers can treat open as idempotent. Placement defaults to the focused
    /// pane. Returns the window.
    /// </summary>
    public WorkspaceWindow OpenWindow(
        string id,
        string title,
        WindowKind kind = WindowKind.Auxiliary,
        string? sessionKey = null,
        string? paneId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (_windows.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var window = new WorkspaceWindow(id, title, kind, sessionKey);
        _windows[id] = window;
        Layout.AddWindow(id, paneId);
        return window;
    }

    /// <summary>
    /// Routes trigger-spawned output to a spawn window named <paramref name="target"/>, creating and
    /// placing the window on first use, and counts the line as unread unless the window is currently
    /// visible. Returns the destination window.
    /// </summary>
    public WorkspaceWindow RouteSpawn(string target, string? sessionKey = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        var id = SpawnWindowId(target);
        if (!_windows.TryGetValue(id, out var window))
        {
            window = new WorkspaceWindow(id, target, WindowKind.Spawn, sessionKey);
            _windows[id] = window;
            Layout.AddWindow(id, activate: false); // spawns open in the background and accrue unread
        }

        NoteActivity(id);
        return window;
    }

    /// <summary>The window id a spawn <paramref name="target"/> routes to.</summary>
    public static string SpawnWindowId(string target) => $"spawn:{target}";

    /// <summary>
    /// Records a line arriving in a window: increments its unread badge unless the window is visible
    /// (the active tab of its host pane). Unknown ids are ignored.
    /// </summary>
    public void NoteActivity(string windowId)
    {
        if (_windows.TryGetValue(windowId, out var window) && !IsVisible(windowId))
        {
            window.Unread++;
        }
    }

    /// <summary>
    /// Makes a window the active tab of its pane, focuses that pane, and clears its unread badge.
    /// Returns false if the window is not placed in any pane.
    /// </summary>
    public bool ActivateWindow(string windowId)
    {
        var pane = Layout.FindWindow(windowId);
        if (pane is null || !_windows.ContainsKey(windowId))
        {
            return false;
        }

        Layout.SetActiveTab(pane.Id, windowId);
        Layout.Focus(pane.Id);
        _windows[windowId].Unread = 0;
        return true;
    }

    /// <summary>Sets the unsent-input marker for a window. Unknown ids are ignored.</summary>
    public void SetUnsentInput(string windowId, bool hasUnsent)
    {
        if (_windows.TryGetValue(windowId, out var window))
        {
            window.HasUnsentInput = hasUnsent;
        }
    }

    /// <summary>Closes a window: removes its tab (pruning empty panes) and forgets its state.</summary>
    public bool CloseWindow(string windowId)
    {
        if (!_windows.Remove(windowId))
        {
            return false;
        }

        Layout.RemoveWindow(windowId);
        return true;
    }

    /// <summary>True when the window is the active tab of the pane that hosts it.</summary>
    public bool IsVisible(string windowId) =>
        Layout.FindWindow(windowId) is { } pane && pane.ActiveTab == windowId;
}
