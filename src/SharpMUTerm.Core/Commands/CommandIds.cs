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

    /// <summary>Prefix of a "go to this numbered pane" id; the remainder is the pane's 1-based number.</summary>
    public const string PanePrefix = "layout:pane-";

    /// <summary>
    /// How many panes have a keyboard chord of their own: ⌥1–⌥9. Nine rather than the five that were
    /// asked for because nine is what the digit row spells with one modifier, what the terminal's Alt
    /// encoding covers (<c>ESC</c> + a printable digit), and what the framework's own Alt+1–9 window
    /// selector claims — leaving one of those digits unclaimed would hand it back to that selector.
    /// Panes past the ninth are still reachable by ⌃O, the arrows and the rail; they simply have no
    /// chord, and no surface claims otherwise.
    /// </summary>
    public const int PaneJumpDigits = 9;

    /// <summary>
    /// The id that focuses the <paramref name="number"/>th pane, counting the way every surface in this
    /// client counts panes: <see cref="SharpMUTerm.Core.Workspace.WorkspaceLayout.Panes"/> order, which is
    /// left-to-right then top-to-bottom, which is the order the connection rail's <c>pane N</c> column
    /// numbers them in. The chord (⌥N), the rail's label and this id are three spellings of one number.
    /// </summary>
    public static string Pane(int number) => PanePrefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
