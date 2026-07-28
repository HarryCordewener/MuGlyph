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

    /// <summary>Display title (rail entry, tab label).</summary>
    public string Title { get; set; }

    /// <summary>What the window hosts.</summary>
    public WindowKind Kind { get; }

    /// <summary>The <c>world.character</c> session this window belongs to, or null if unowned.</summary>
    public string? SessionKey { get; }

    /// <summary>Unread lines accumulated while the window was not visible.</summary>
    public int Unread { get; internal set; }

    /// <summary>Whether the window holds a typed-but-unsent input draft (the <c>✎</c> marker).</summary>
    public bool HasUnsentInput { get; internal set; }

    /// <summary>
    /// For a <see cref="WindowKind.Spawn"/> window, the trigger pattern that routes lines here — shown
    /// as a dim <c>⇱ capture …</c> line under the tab strip. Null for windows with no capture rule.
    /// </summary>
    public string? CapturePattern { get; set; }

    /// <summary>
    /// The display name of the character this window belongs to (typically its main window's title),
    /// used to prefix a child window's tab as <c>Owner: Name</c> so a spawn window scattered into another
    /// pane stays visibly tied to its character. Null for a character's own main window.
    /// </summary>
    public string? OwnerLabel { get; set; }
}
