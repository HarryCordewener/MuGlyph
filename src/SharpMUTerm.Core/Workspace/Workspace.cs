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

    /// <summary>
    /// The last <see cref="WorkspaceWindow.Sequence"/> handed out. Never reused, so a number is a
    /// window's for as long as it is open — see <see cref="WindowsFor"/> for why the ordinal is then
    /// taken from the sorted position rather than from this.
    /// </summary>
    private int _sequenceCounter;

    /// <summary>Creates a workspace with a single main window in one pane.</summary>
    public Workspace(string mainWindowId = "main", string mainTitle = "Main", string? sessionKey = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(mainWindowId);
        Layout = new WorkspaceLayout(new[] { mainWindowId });
        Register(new WorkspaceWindow(mainWindowId, mainTitle, WindowKind.Main, sessionKey));
    }

    /// <summary>
    /// Rebuilds a workspace from a restored set of windows and a pre-built layout (session resume).
    /// The two are assumed consistent — every window id referenced by a pane tab should have a window.
    /// <para>
    /// <b>A window restored without a creation sequence is given one here, from the order it arrived
    /// in.</b> Windows are numbered by <see cref="WorkspaceWindow.Sequence"/> and a configuration
    /// written before that field existed carries none, so without this every restored window would sort
    /// equal and the numbering would be whatever the sort happened to do. The saved order is the
    /// numbering such a workspace was saved under, which is why it is the right seed. Any window that
    /// <em>does</em> carry a sequence keeps it, and unsequenced ones are numbered after the highest
    /// already taken, so a half-migrated set cannot produce two windows with one number. Same shape,
    /// and the same reasoning, as <see cref="WorkspaceLayout"/>'s restoring constructor.
    /// </para>
    /// </summary>
    public Workspace(IEnumerable<WorkspaceWindow> windows, WorkspaceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(windows);
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        var restored = windows.ToList();
        foreach (var window in restored)
        {
            _windows[window.Id] = window;
        }

        _sequenceCounter = restored.Select(w => w.Sequence).DefaultIfEmpty(WorkspaceWindow.Unsequenced).Max();
        foreach (var window in restored.Where(w => w.Sequence <= WorkspaceWindow.Unsequenced))
        {
            window.Sequence = ++_sequenceCounter;
        }
    }

    /// <summary>The pane tree.</summary>
    public WorkspaceLayout Layout { get; }

    /// <summary>
    /// Every known window, whether or not a pane still holds it. The order is the registry's, which is a
    /// dictionary's — fine for "what exists", and <b>not</b> what anything numbers windows by; see
    /// <see cref="WindowsFor"/>.
    /// </summary>
    public IReadOnlyCollection<WorkspaceWindow> Windows => _windows.Values;

    /// <summary>
    /// <b>The windows <paramref name="sessionKey"/> can reach, in creation order — the one order this
    /// client numbers windows in.</b> The Nth entry is the window ⌥N goes to while that character is
    /// active, the <c>⌥N</c> the connection rail prints on that window's row, and the chord the ⌃P
    /// <c>Go to …</c> entry for it names. Those are three spellings of this index and there is
    /// deliberately no second ordering for any of them to drift onto.
    /// <para>
    /// <b>Scoped to the active character, and re-based from 1 for each.</b> It was global — every window
    /// in the workspace in one sequence — and that failed on a real client the first day it was used:
    /// with three characters sharing pane 1 as tabs, every character's row read <c>⌥1</c> because their
    /// windows happened to be numbered from the same run. Nine digits also do not stretch across
    /// everybody's windows; six windows over three characters already crowds them. Scoped, ⌥1 is
    /// <em>this</em> character's main window whoever you are, ⌥2 their first capture, and the digits mean
    /// the same thing wherever you stand.
    /// </para>
    /// <para>
    /// <b>An unowned window is in everybody's list.</b> The web view belongs to no session and is
    /// reachable from wherever you are, so it takes a digit under each character — a different one under
    /// each, since it sits after that character's own windows. That is not a second numbering: this
    /// method is exactly the set the rail draws window rows for (its owner filter admits a character's
    /// own windows plus the unowned ones), so the digit on the screen and the digit in the chord are the
    /// same list read twice.
    /// </para>
    /// <para>
    /// <b>Creation order, for the reason panes are in creation order.</b> Any ordering that is a function
    /// of <em>where</em> a window sits — its tab index, its pane's position — moves when something is
    /// inserted before it, so dragging a channel one slot left would renumber every window after it and
    /// ⌥4 would stop meaning what it meant while the user was doing something else entirely. A window's
    /// number is fixed for as long as it is open, and a new one always appears at the end.
    /// </para>
    /// <para>
    /// <b>The number is the index, not the sequence.</b> Sequences are never reused, so reading them
    /// directly would leave holes — close the second of three windows and the survivors would be 1 and
    /// 3, with ⌥2 doing nothing while two windows sat on the screen.
    /// </para>
    /// <para>
    /// <b>Placed, because ⌥N has to land somewhere.</b> A window the registry still knows and no pane
    /// holds is drawn in the rail as <c>closed</c>; giving it a number would spend a digit on a place
    /// there is no way to go, and would shift every window after it for a row that names nothing.
    /// </para>
    /// </summary>
    /// <param name="sessionKey">
    /// The active character, or null when nothing is active — which leaves the unowned windows, the only
    /// ones there is anywhere to go to.
    /// </param>
    public IReadOnlyList<WorkspaceWindow> WindowsFor(string? sessionKey) =>
        _windows.Values
            .Where(w => Layout.FindWindow(w.Id) is not null)
            .Where(w => w.SessionKey is null || string.Equals(w.SessionKey, sessionKey, StringComparison.Ordinal))
            .OrderBy(w => w.Sequence)
            .ToList();

    /// <summary>Files a window in the registry, giving it the next creation sequence.</summary>
    private WorkspaceWindow Register(WorkspaceWindow window)
    {
        window.Sequence = ++_sequenceCounter;
        _windows[window.Id] = window;
        return window;
    }

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

        var window = Register(new WorkspaceWindow(id, title, kind, sessionKey));
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
            window = Register(new WorkspaceWindow(id, target, WindowKind.Spawn, sessionKey));
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
