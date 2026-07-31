namespace SharpMUTerm.Core.Workspaces;

/// <summary>What a window shows, used for rail grouping and routing rules.</summary>
public enum WindowKind
{
    /// <summary>A character's main output window.</summary>
    Main,

    /// <summary>A spawn window fed by a trigger's <c>SpawnTarget</c>.</summary>
    Spawn,

    /// <summary>An arbitrary auxiliary window (web view, map, notes…).</summary>
    Auxiliary,
}

/// <summary>
/// Per-window state independent of where the window sits in the pane tree: its identity, title,
/// owning session, activity badges (unread count, unsent-input marker), and kind. Placement lives
/// in <see cref="WorkspaceLayout"/>; this is the metadata the tab strip and rail render.
/// </summary>
public sealed class WorkspaceWindow
{
    public WorkspaceWindow(string id, string title, WindowKind kind = WindowKind.Main, string? sessionKey = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Kind = kind;
        SessionKey = sessionKey;
    }

    /// <summary>Stable window identity, unique within a workspace and referenced by pane tabs.</summary>
    public string Id { get; }

    /// <summary>
    /// When this window was created, as a per-workspace counter that only ever goes up. Windows are
    /// <em>numbered</em> by this (see <see cref="Workspace.WindowsFor"/>) rather than by where their
    /// tab happens to sit, so a window's ⌥N number is fixed for as long as it is open.
    /// <para>
    /// It is a sort key and not the number itself. The number is the window's position in the sorted
    /// list, which is what keeps ⌥1–⌥N contiguous when a window in the middle closes.
    /// </para>
    /// <para>
    /// <b>Deliberately the same mechanism as <see cref="PaneNode.Sequence"/>, and deliberately not the
    /// registry's own order.</b> <see cref="Workspace.Windows"/> is a dictionary's values: its order is
    /// unspecified after a removal, so a client that closed a channel and opened another could find the
    /// new one dropped into the freed slot — the number moving under a user who had not touched it,
    /// which is the exact defect creation order was given to panes to prevent.
    /// </para>
    /// </summary>
    public int Sequence { get; internal set; } = Unsequenced;

    /// <summary>
    /// The <see cref="Sequence"/> of a window nobody has numbered yet — one restored from a workspace
    /// persisted before windows carried a creation order. <see cref="Workspace"/> replaces it on load,
    /// from the order the windows were saved in, so an old configuration comes back numbered the way it
    /// was left rather than scrambled. Same value and same reasoning as <see cref="PaneNode.Unsequenced"/>.
    /// </summary>
    public const int Unsequenced = 0;

    /// <summary>Display title (rail entry, tab label).</summary>
    public string Title { get; set; }

    /// <summary>What the window hosts.</summary>
    public WindowKind Kind { get; }

    /// <summary>
    /// The <c>world.character</c> session this window belongs to, or null if unowned. Settable through
    /// <see cref="Workspace.SetWindowOwner"/> because a session can adopt a window that already exists
    /// — the main one, which is opened before any session does.
    /// </summary>
    public string? SessionKey { get; internal set; }

    /// <summary>Unread lines accumulated while the window was not showing its newest output.</summary>
    public int Unread { get; internal set; }

    /// <summary>
    /// Whether the viewer has scrolled this window's output back off its live tail, so newly arriving
    /// lines land below what is on screen. A visible window in this state is <em>not</em> caught up:
    /// <see cref="Workspace.NoteActivity"/> badges it exactly as it badges a window on a hidden tab,
    /// because from the reader's point of view the two are the same situation — output has arrived where
    /// they cannot see it. Set by the shell through <see cref="Workspace.SetScrolledBack"/>; scroll
    /// position itself is the view's business and never lives here.
    /// </summary>
    public bool ScrolledBack { get; internal set; }

    /// <summary>Whether the window holds a typed-but-unsent input draft (the <c>✎</c> marker).</summary>
    public bool HasUnsentInput { get; internal set; }

    /// <summary>
    /// The display name of the character this window belongs to (typically its main window's title),
    /// used to prefix a child window's tab as <c>Owner: Name</c> so a spawn window scattered into another
    /// pane stays visibly tied to its character. Null for a character's own main window.
    /// </summary>
    public string? OwnerLabel { get; set; }
}
