using System.Globalization;

namespace SharpMUTerm.Core.Workspaces;

/// <summary>
/// The full workspace state the TUI shell drives: the <see cref="WorkspaceLayout"/> pane tree plus
/// the registry of <see cref="WorkspaceWindow"/>s the panes host. It keeps the two consistent —
/// opening a window places it in a pane, closing one removes its tab, spawn routing finds-or-creates
/// the destination window — and tracks activity badges (unread, unsent-input) against visibility.
/// UI-agnostic and fully testable; the SharpConsoleUI layer renders from it and calls its operations.
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

    /// <summary>
    /// Rebuilds a workspace from a restored set of windows and a pre-built layout (session resume).
    /// The two are assumed consistent — every window id referenced by a pane tab should have a window.
    /// </summary>
    public Workspace(IEnumerable<WorkspaceWindow> windows, WorkspaceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(windows);
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        foreach (var window in windows)
        {
            _windows[window.Id] = window;
        }
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
    /// Routes trigger-spawned output to <paramref name="sessionKey"/>'s spawn window named
    /// <paramref name="target"/>, creating and placing the window on first use, and counts the line as
    /// unread unless the window is currently visible. Returns the destination window.
    /// <para>
    /// <b>The destination is per session, not per workspace.</b> Two connected characters running the
    /// same capture rule each get a window of their own; the id carries the owner, so the second
    /// session to match cannot land in the first's window. It used to: the id was the target alone, so
    /// whoever matched first created the window <em>with their own session key on it</em> and everybody
    /// else's lines were appended to somebody else's pane. That was not merely a mixed-up channel — the
    /// rail draws window rows for the active character only, so the second character's own channel was
    /// filed under the first and was invisible from the character it belonged to.
    /// </para>
    /// </summary>
    public WorkspaceWindow RouteSpawn(string target, string? sessionKey = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        var id = SpawnWindowId(sessionKey, target);
        if (!_windows.TryGetValue(id, out var window))
        {
            window = new WorkspaceWindow(id, target, WindowKind.Spawn, sessionKey);
            _windows[id] = window;
            Layout.AddWindow(id, activate: false); // spawns open in the background and accrue unread
        }

        NoteActivity(id);
        return window;
    }

    /// <summary>Every spawn window id starts with this.</summary>
    public const string SpawnPrefix = "spawn:";

    /// <summary>
    /// The owner field of a spawn window that belongs to nobody. A single <c>-</c>, which is not a
    /// decimal length and so can never be mistaken for one — that is the whole reason it is not the
    /// empty string.
    /// </summary>
    private const string Unowned = "-";

    /// <summary>
    /// The window id the spawn <paramref name="target"/> of the session <paramref name="sessionKey"/>
    /// routes to. Unique per <c>(owner, target)</c> and stable for ever, so a reconnect or a restart
    /// comes back to the pane it left.
    /// <para>
    /// <b>Why the length prefix.</b> A session key and a target are both user-controlled strings that may
    /// hold any character, colons included — a world or character can be called <c>a:b</c> and a
    /// trigger's <c>SpawnTarget</c> is free text. Joining them with a separator is therefore <em>not</em>
    /// injective: <c>(a, b:c)</c> and <c>(a:b, c)</c> would produce one id and collapse two characters'
    /// panes into one, which is this defect again in a rarer shape. Writing the owner's length in front
    /// of it makes the encoding total and reversible: the digits up to the first colon give the length,
    /// exactly that many characters are the owner, one more colon is consumed, and everything left is
    /// the target — so distinct pairs cannot produce equal ids, whatever is in them.
    /// </para>
    /// <para>
    /// It is legible on purpose rather than hashed. This id is a dictionary key, a value in
    /// <c>config.json</c>, and the stem of a <c>RestoreLog</c> file name; a digest would be unambiguous
    /// too and would make every one of those unreadable to whoever has to look at them, for no property
    /// a reversible encoding does not already have. (The file name's own collision handling is
    /// unchanged and unaffected: <c>RestoreLog</c> stores the full id in each file's header and refuses
    /// a file whose header names a different window, so a CRC-32 clash on the stem costs one window's
    /// log rather than mixing two.)
    /// </para>
    /// </summary>
    /// <param name="sessionKey">The owning <c>world.character</c> session, or null for a window nobody owns.</param>
    /// <param name="target">The capture target, which is also the window's title.</param>
    public static string SpawnWindowId(string? sessionKey, string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        return sessionKey is null
            ? $"{SpawnPrefix}{Unowned}:{target}"
            : $"{SpawnPrefix}{sessionKey.Length}:{sessionKey}:{target}";
    }

    /// <summary>
    /// Reads a spawn window id back into the pair that made it. False when <paramref name="id"/> is not
    /// one this build writes — including a spawn id from before the owner was in it, which is what
    /// <c>ConfigurationMigrator</c>'s v4→v5 step upgrades.
    /// </summary>
    public static bool TryReadSpawnWindowId(string id, out string? sessionKey, out string target)
    {
        sessionKey = null;
        target = string.Empty;
        if (id is null || !id.StartsWith(SpawnPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = id.AsSpan(SpawnPrefix.Length);
        if (rest.StartsWith(Unowned + ":", StringComparison.Ordinal))
        {
            target = rest[(Unowned.Length + 1)..].ToString();
            return target.Length > 0;
        }

        // NumberStyles.None: bare digits only, so a sign or surrounding space is not silently accepted
        // into a field whose whole job is to say how many characters to take.
        var separator = rest.IndexOf(':');
        if (separator <= 0
            || !int.TryParse(rest[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            return false;
        }

        // The owner's exact length, then the colon that closes it: anything shorter is not this
        // encoding. Written as `<=` rather than `< length + 1` so a declared length of int.MaxValue
        // cannot overflow the comparison into passing and then index off the end.
        var owner = rest[(separator + 1)..];
        if (owner.Length <= length || owner[length] != ':')
        {
            return false;
        }

        sessionKey = owner[..length].ToString();
        target = owner[(length + 1)..].ToString();
        return target.Length > 0;
    }

    /// <summary>
    /// Records a line arriving in a window: increments its unread badge unless the window is
    /// <see cref="IsCaughtUp"/> — visible <em>and</em> showing its live tail. Unknown ids are ignored.
    /// </summary>
    public void NoteActivity(string windowId)
    {
        if (_windows.TryGetValue(windowId, out var window) && !IsCaughtUp(windowId))
        {
            window.Unread++;
        }
    }

    /// <summary>
    /// Makes a window the active tab of its pane, focuses that pane, and clears its unread badge — but
    /// only if the window is showing its live tail. A window the viewer had scrolled back keeps its
    /// badge when its tab is picked, because the unread lines are still below the viewport; returning to
    /// the bottom (<see cref="SetScrolledBack"/> with <c>false</c>) is what clears it.
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
        if (!_windows[windowId].ScrolledBack)
        {
            _windows[windowId].Unread = 0;
        }

        return true;
    }

    /// <summary>
    /// Records whether the shell has this window's output scrolled back off its live tail. Returning to
    /// the bottom of a <em>visible</em> window is the reader catching up, so it clears the unread badge
    /// the same way picking the tab does. Unknown ids are ignored.
    /// </summary>
    /// <returns>True when the flag changed, so a caller can skip repainting badges that cannot have moved.</returns>
    public bool SetScrolledBack(string windowId, bool scrolledBack)
    {
        if (!_windows.TryGetValue(windowId, out var window) || window.ScrolledBack == scrolledBack)
        {
            return false;
        }

        window.ScrolledBack = scrolledBack;
        if (!scrolledBack && IsVisible(windowId))
        {
            window.Unread = 0;
        }

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

    /// <summary>
    /// Records who owns a window, for the case where a session takes over one that already exists.
    /// Unknown ids are ignored.
    /// <para>
    /// A window's owner is otherwise fixed at creation, which was fine while the first session always
    /// created its own: the main window is opened before any session exists and the first session
    /// simply adopts it, so without this its <see cref="WorkspaceWindow.SessionKey"/> keeps naming
    /// whoever held it before — and anything that reads ownership (the connection rail listing a
    /// character's windows, the command surface subtitling them with their owner) is then reading a
    /// stale answer.
    /// </para>
    /// </summary>
    public void SetWindowOwner(string windowId, string? sessionKey)
    {
        if (_windows.TryGetValue(windowId, out var window))
        {
            window.SessionKey = sessionKey;
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

    /// <summary>
    /// True when a new line arriving in this window would land somewhere the reader can see it: the
    /// window is the visible tab of its pane <em>and</em> its output is not scrolled back. This is the
    /// condition unread badging turns on — <see cref="IsVisible"/> alone is not it, which is why a client
    /// that only asked that question badged nothing while the reader sat in their scrollback.
    /// </summary>
    public bool IsCaughtUp(string windowId) =>
        IsVisible(windowId) && _windows.TryGetValue(windowId, out var window) && !window.ScrolledBack;
}
