namespace SharpMUTerm.Core.Commands;

/// <summary>
/// The command ids every surface in the client names its two navigation actions by: switch to a
/// character, and go to a window. They were three separate string literals — <c>CommandCatalog</c>
/// spelt them, the shell's dispatcher re-spelt them in its own <c>const</c>, and the connection rail
/// was about to spell them a third time. That is exactly the drift the dispatcher's own comment
/// warns about (<c>char:</c> was offered by the surface and implemented nowhere for as long as the
/// surface existed), so the spelling lives in one place and every surface reads it from here.
/// </summary>
public static class CommandIds
{
    /// <summary>Prefix of a "switch to this character" id; the remainder is a <c>world.character</c> session key.</summary>
    public const string CharacterPrefix = "char:";

    /// <summary>Prefix of a "go to this window" id; the remainder is a workspace window id.</summary>
    public const string WindowPrefix = "win:";

    /// <summary>The id that switches to the character named by <paramref name="sessionKey"/>.</summary>
    public static string Character(string sessionKey) => CharacterPrefix + sessionKey;

    /// <summary>The id that activates the window named by <paramref name="windowId"/>.</summary>
    public static string Window(string windowId) => WindowPrefix + windowId;

    /// <summary>
    /// How many windows have a keyboard chord of their own: ⌥1–⌥9, counting
    /// <see cref="SharpMUTerm.Core.Workspaces.Workspace.WindowsFor"/>. Nine because nine is what the
    /// digit row spells with one modifier, what the terminal's Alt encoding covers (<c>ESC</c> + a
    /// printable digit), and what the framework's own Alt+1–9 window selector claims — leaving one of
    /// those digits unclaimed would hand it back to that selector, so all nine are claimed whether or
    /// not there is a window behind them. Windows past the ninth are still reachable by ⌃N, the tab
    /// strip, the rail and the ⌃P surface; they simply have no chord, and no surface claims otherwise.
    /// <para>
    /// The chord counts <em>windows</em> because that is what was asked for: "switch not just
    /// characters, but captures, etc." — a capture window only ever shared a pane's number when it
    /// happened to be that pane's active tab, so under a pane-numbered chord most of them were
    /// unreachable.
    /// </para>
    /// </summary>
    public const int WindowJumpDigits = 9;

    /// <summary>Prefix of a "go to this numbered pane" id; the remainder is the pane's 1-based number.</summary>
    public const string PanePrefix = "layout:pane-";

    /// <summary>
    /// How many panes have a keyboard chord of their own: ⌃B 1–⌃B 9. Nine to match
    /// <see cref="WindowJumpDigits"/>, and on the <em>prefix</em> rather than on Alt because ⌥N now
    /// names a window and one chord cannot mean two things. ⌃B is where every other pane command already
    /// lives (split, zoom, close, cycle, move), so the ordinal one joining them costs a reader nothing
    /// new to learn. Panes past the ninth are reachable by ⌃O, the arrows, the rail and the ⌃P entry.
    /// </summary>
    public const int PaneJumpDigits = 9;

    /// <summary>
    /// The id that focuses the <paramref name="number"/>th pane, counting the way every surface in this
    /// client counts panes: <see cref="SharpMUTerm.Core.Workspaces.WorkspaceLayout.Panes"/> order, which is
    /// <b>creation</b> order. The chord (⌃B N), the move and drag overlays' <c>pane N</c> label and this
    /// id are three spellings of one number.
    /// <para>
    /// It was tree order — left-to-right then top-to-bottom — and that renumbered panes that already
    /// existed whenever one was inserted before them, so a number a user had learnt moved without being
    /// touched. Creation order is stable while a pane is open, and closing one compacts the rest so the
    /// range stays contiguous.
    /// </para>
    /// </summary>
    public static string Pane(int number) => PanePrefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
