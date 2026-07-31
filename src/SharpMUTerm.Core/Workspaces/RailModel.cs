using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Workspaces;

/// <summary>What a <see cref="RailRow"/> represents in the connection rail.</summary>
public enum RailRowKind
{
    /// <summary>The "CONNECTIONS" header.</summary>
    Header,

    /// <summary>A world (server) header, carrying its accent.</summary>
    World,

    /// <summary>A world's host:port line.</summary>
    Host,

    /// <summary>A character under its world.</summary>
    Character,

    /// <summary>A window hosted by the active character.</summary>
    Window,

    /// <summary>A placeholder (e.g. a world with no characters).</summary>
    Empty,
}

/// <summary>
/// One rendered row of the connection rail. The view maps these to markup/glyphs.
/// <para>
/// <see cref="Target"/> is what a click on the row does, named as a command id so the rail reuses the
/// actions the ⌃P surface already dispatches instead of growing a second way to switch character. A
/// row with no <see cref="Target"/> is not clickable — that is the difference between the rail's
/// chrome (its CONNECTIONS header, and the <see cref="RailRowKind.Host"/> line the model does not
/// currently emit) and the things in it you can go to.
/// </para>
/// </summary>
public sealed record RailRow(
    RailRowKind Kind,
    int Indent,
    string Label,
    TerminalColor Accent = default,
    bool Active = false,
    bool Connected = false,
    bool Unsent = false,
    bool Closed = false,
    int Unread = 0,
    string? Chord = null,
    string? Target = null);

/// <summary>A world as projected into the rail: its identity, accent, and characters.</summary>
public sealed record RailWorld(string Name, string Host, int Port, TerminalColor Accent, IReadOnlyList<RailCharacter> Characters);

/// <summary>
/// A character in the rail: connection/active state, unread total, its windows, and the chord that
/// goes to them.
/// <para>
/// <b><paramref name="Chord"/> is what makes the window numbering visible across characters.</b> Only
/// the <em>active</em> character's windows are listed (see <see cref="RailModel.Build"/>), so without
/// this field a reader looking at character A could see A's own <c>⌥N</c>s and nothing else — B's
/// windows were on the screen, numbered, and one keystroke away, and nothing anywhere said which
/// keystroke. The number is useless if it cannot be read off the sidebar, and switching character is
/// the commonest thing these nine chords are for.
/// </para>
/// <para>
/// It is the chord of the character's <em>own</em> window — their session window when they have one —
/// so pressing it goes to that character, and so a character row and the window row beneath it that
/// names the same window carry the same digit. Null when the workspace holds a single window (one
/// window is no information) or the character has none open.
/// </para>
/// </summary>
public sealed record RailCharacter(
    string Name,
    string SessionKey,
    bool Connected,
    bool Active,
    int Unread,
    IReadOnlyList<RailWindow> Windows,
    string? Chord = null);

/// <summary>
/// A window in the rail: its title, the workspace id a click activates, the <c>⌥N</c> chord that goes
/// to it (or none), unread, and unsent marker. The id is required rather than optional because a row
/// the rail draws as a destination and cannot name is a row that silently does nothing when clicked.
/// <para>
/// The second column used to be the <em>hosting pane</em>, back when ⌥N named a pane. It names the
/// window itself now, which is the row it is drawn on — so the number beside a window is the number
/// that goes to that window, rather than to whatever else happens to share its pane.
/// </para>
/// </summary>
public sealed record RailWindow(string Title, string Id, string? Chord, int Unread, bool HasUnsent, bool Closed);

/// <summary>
/// Projects the worlds/characters/windows tree into a flat list of <see cref="RailRow"/>s for the
/// connection rail, matching the design: a CONNECTIONS header, each world carrying its accent,
/// characters indented with a connected dot and active marker, and — under the <b>active</b>
/// character only — its windows with unread/unsent/pane detail. A world with no characters prints
/// "no characters". (A world's <c>host:port</c> is deliberately <em>not</em> a row; the rail shows
/// name and characters only, which <c>RailModelTests</c> pins.) Pure and unit-testable.
/// <para>
/// <b>Every</b> character row carries its own chord (<see cref="RailCharacter.Chord"/>), active or not,
/// and that is the one thing here that is not scoped to the active character. It has to be: the window
/// numbering is a property of the workspace rather than of whoever is in front, ⌥3 is a character switch
/// as much as a window switch, and a number that is only legible once you are already there is a number
/// nobody presses. Listing the other characters' <em>windows</em> would have said the same thing and cost
/// the rail its one unambiguous reading — a window row means "this is yours", which is why the owner
/// filter exists.
/// </para>
/// <para>
/// Every row that names somewhere you can go also carries the <see cref="RailRow.Target"/> a click
/// dispatches. The ids are the shell's own (<see cref="CommandIds"/>), so the rail is a second door
/// onto the ⌃P surface's actions rather than a second implementation of switching.
/// </para>
/// </summary>
public static class RailModel
{
    /// <summary>
    /// Prefix of the rail's own click target for a world that has no characters to switch to. It is
    /// deliberately not a <see cref="CommandIds"/> id: there is no command for "this world is empty",
    /// and the honest answer — saying so — belongs to the rail. The remainder is the world's name.
    /// </summary>
    public const string NoCharactersPrefix = "rail:no-characters:";

    /// <summary>The target a click on a character-less <paramref name="worldName"/> reports through.</summary>
    public static string NoCharactersTarget(string worldName) => NoCharactersPrefix + worldName;

    public static IReadOnlyList<RailRow> Build(IReadOnlyList<RailWorld> worlds)
    {
        ArgumentNullException.ThrowIfNull(worlds);

        var rows = new List<RailRow> { new(RailRowKind.Header, 0, "CONNECTIONS") };

        foreach (var world in worlds)
        {
            rows.Add(new RailRow(RailRowKind.World, 0, world.Name, Accent: world.Accent, Target: WorldTarget(world)));

            if (world.Characters.Count == 0)
            {
                rows.Add(new RailRow(
                    RailRowKind.Empty, 2, "no characters", Target: NoCharactersTarget(world.Name)));
                continue;
            }

            foreach (var character in world.Characters)
            {
                rows.Add(new RailRow(
                    RailRowKind.Character,
                    2,
                    character.Name,
                    Accent: world.Accent,
                    Active: character.Active,
                    Connected: character.Connected,
                    Unread: character.Unread,
                    Chord: character.Chord,
                    Target: CommandIds.Character(character.SessionKey)));

                if (!character.Active)
                {
                    continue;
                }

                foreach (var window in character.Windows)
                {
                    rows.Add(new RailRow(
                        RailRowKind.Window,
                        3,
                        window.Title,
                        Accent: world.Accent,
                        Unsent: window.HasUnsent,
                        Closed: window.Closed,
                        Unread: window.Unread,
                        Chord: window.Chord,
                        // Closed windows get a target too. The shell answers a "go to" for a window no
                        // pane holds by saying it is not open any more, which is what the row's own
                        // "closed" label already tells you — and it is the same answer the ⌃P entry for
                        // that window gives, because it is literally the same id.
                        Target: CommandIds.Window(window.Id)));
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// Where clicking a world row goes. A world is not itself connectable, so "go to this world" has
    /// to mean one of its characters: the one already active (clicking the world you are in re-focuses
    /// it rather than dragging you elsewhere), else the first connected one (a world you click is a
    /// world you want to be looking at something live in), else simply the first. A world with no
    /// characters says so — see <see cref="NoCharactersTarget"/>.
    /// </summary>
    private static string WorldTarget(RailWorld world)
    {
        if (world.Characters.Count == 0)
        {
            return NoCharactersTarget(world.Name);
        }

        var pick = world.Characters.FirstOrDefault(c => c.Active)
            ?? world.Characters.FirstOrDefault(c => c.Connected)
            ?? world.Characters[0];
        return CommandIds.Character(pick.SessionKey);
    }
}
