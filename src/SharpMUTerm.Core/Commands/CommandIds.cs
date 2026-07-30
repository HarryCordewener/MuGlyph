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
}
