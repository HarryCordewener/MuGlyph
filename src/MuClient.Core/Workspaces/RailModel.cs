using MuClient.Core.Text;

namespace MuClient.Core.Workspaces;

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

/// <summary>One rendered row of the connection rail. The view maps these to markup/glyphs.</summary>
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
    string? Pane = null);

/// <summary>A world as projected into the rail: its identity, accent, and characters.</summary>
public sealed record RailWorld(string Name, string Host, int Port, TerminalColor Accent, IReadOnlyList<RailCharacter> Characters);

/// <summary>A character in the rail: connection/active state, unread total, and its windows.</summary>
public sealed record RailCharacter(string Name, string SessionKey, bool Connected, bool Active, int Unread, IReadOnlyList<RailWindow> Windows);

/// <summary>A window in the rail: its title, hosting pane (or closed), unread, and unsent marker.</summary>
public sealed record RailWindow(string Title, string? Pane, int Unread, bool HasUnsent, bool Closed);

/// <summary>
/// Projects the worlds/characters/windows tree into a flat list of <see cref="RailRow"/>s for the
/// connection rail, matching the design: a CONNECTIONS header, each world with its host line and an
/// accent, characters indented with a connected dot and active marker, and — under the <b>active</b>
/// character only — its windows with unread/unsent/pane detail. A world with no characters prints
/// "no characters". Pure and unit-testable.
/// </summary>
public static class RailModel
{
    public static IReadOnlyList<RailRow> Build(IReadOnlyList<RailWorld> worlds)
    {
        ArgumentNullException.ThrowIfNull(worlds);

        var rows = new List<RailRow> { new(RailRowKind.Header, 0, "CONNECTIONS") };

        foreach (var world in worlds)
        {
            rows.Add(new RailRow(RailRowKind.World, 0, world.Name, Accent: world.Accent));

            if (world.Characters.Count == 0)
            {
                rows.Add(new RailRow(RailRowKind.Empty, 2, "no characters"));
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
                    Unread: character.Unread));

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
                        Pane: window.Pane));
                }
            }
        }

        return rows;
    }
}
