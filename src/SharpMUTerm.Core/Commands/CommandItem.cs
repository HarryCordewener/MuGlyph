namespace SharpMUTerm.Core.Commands;

/// <summary>The section a <see cref="CommandItem"/> is grouped under in the command surface (⌃P).</summary>
public enum CommandGroup
{
    /// <summary>Navigation: switch character, jump to a window.</summary>
    GoTo,

    /// <summary>World/connection actions: connect, disconnect, reconnect.</summary>
    World,

    /// <summary>Terminal/session actions: logging, freeze/scrollback, clear.</summary>
    Terminal,

    /// <summary>Layout actions: split, zoom, close pane, move.</summary>
    Layout,

    /// <summary>The configuration screens — one entry per screen the host offers, F-key and all.</summary>
    Settings,
}

/// <summary>
/// One entry in the command surface — a title, an optional subtitle (its owner / unread / current
/// value), the group it renders under, and a stable <see cref="Id"/> the UI dispatches on. The
/// catalog is generated from live state, so <see cref="Subtitle"/> often carries dynamic detail.
/// </summary>
public sealed record CommandItem(CommandGroup Group, string Title, string Id, string? Subtitle = null);
