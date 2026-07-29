using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Commands;

/// <summary>A character the command surface can switch to, with enough detail to label the entry.</summary>
public sealed record CharacterRef(string WorldName, string CharacterName, string SessionKey, bool Connected);

/// <summary>Live flags the catalog reads so stateful commands show their current value.</summary>
/// <param name="LoggingOn">Whether the focused character is logging.</param>
/// <param name="Zoomed">Whether a pane is zoomed.</param>
/// <param name="Frozen">Whether the focused pane's scrollback is frozen.</param>
/// <param name="TimestampsOn">Whether output lines carry timestamps.</param>
/// <param name="SecondInputOn">
/// Whether the active window is showing its second command line. Per window, not per app — the
/// surface acts on the window you are in, the same way <c>Clear window</c> does.
/// </param>
public sealed record CommandContext(
    bool LoggingOn = false,
    bool Zoomed = false,
    bool Frozen = false,
    bool TimestampsOn = false,
    bool SecondInputOn = false);

/// <summary>
/// A configuration screen the command surface can open: what it calls itself, the id the host
/// dispatches on, and the keyboard shortcut that opens it directly. Supplied by the host rather
/// than known here, because which screens exist — and which key each is bound to — is the UI's
/// business, and a catalog that guessed could advertise a key nothing is registered on.
/// </summary>
public sealed record SettingsEntry(string Title, string Id, string Shortcut);

/// <summary>
/// Generates the command-surface catalog from live state, per the design: every non-focused
/// character becomes a <c>Switch to…</c> entry, every non-active window a <c>Go to…</c> entry
/// subtitled with its owner and unread count, stateful commands read their current value
/// (<c>Pause logging</c> vs <c>Start logging</c>, <c>Unzoom pane</c>, <c>Resume scrollback</c>), and
/// every configuration screen the host offers becomes an <c>Open …</c> entry subtitled with its key.
/// Pure so the exact catalog is unit-testable; <see cref="CommandMatcher"/> ranks it against a query.
/// </summary>
public static class CommandCatalog
{
    public static IReadOnlyList<CommandItem> Build(
        Workspace workspace,
        IReadOnlyList<CharacterRef> characters,
        string? focusedSessionKey,
        CommandContext context,
        IReadOnlyList<SettingsEntry>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(context);

        var items = new List<CommandItem>();
        var activeWindow = workspace.Layout.FocusedPane.ActiveTab;

        // GO TO — switch character, then jump to windows.
        foreach (var character in characters)
        {
            if (character.SessionKey == focusedSessionKey)
            {
                continue;
            }

            var state = character.Connected ? "connected" : "offline";
            items.Add(new CommandItem(
                CommandGroup.GoTo,
                $"Switch to {character.CharacterName}",
                $"char:{character.SessionKey}",
                $"{character.WorldName} · {state}"));
        }

        foreach (var window in workspace.Windows)
        {
            if (window.Id == activeWindow)
            {
                continue;
            }

            var owner = window.SessionKey ?? "unowned";
            var unread = window.Unread > 0 ? $" · {window.Unread} unread" : string.Empty;
            items.Add(new CommandItem(
                CommandGroup.GoTo,
                $"Go to {window.Title}",
                $"win:{window.Id}",
                $"{owner}{unread}"));
        }

        // WORLD
        items.Add(new CommandItem(CommandGroup.World, "Reconnect", "world:reconnect"));
        items.Add(new CommandItem(CommandGroup.World, "Disconnect", "world:disconnect"));

        // TERMINAL — stateful labels.
        items.Add(context.LoggingOn
            ? new CommandItem(CommandGroup.Terminal, "Pause logging", "term:log-off")
            : new CommandItem(CommandGroup.Terminal, "Start logging", "term:log-on"));
        items.Add(context.Frozen
            ? new CommandItem(CommandGroup.Terminal, "Resume scrollback", "term:unfreeze")
            : new CommandItem(CommandGroup.Terminal, "Freeze pane", "term:freeze"));
        items.Add(new CommandItem(CommandGroup.Terminal, "Clear window", "term:clear"));

        // The client's own messages — the status-line notices that dismiss themselves — kept out of the
        // output window (and so out of the session log) and readable here instead.
        items.Add(new CommandItem(
            CommandGroup.Terminal, "Show client messages", "term:messages", "status-line notices"));

        // The command line's own history, browsable and searchable. Named here as well as bound to ⌃R
        // because a chord nothing mentions is a chord nobody finds — the same reason every settings screen
        // has a row even though each has an F-key.
        items.Add(new CommandItem(
            CommandGroup.Terminal, "Search command history", "term:history", "⌃R"));
        items.Add(context.TimestampsOn
            ? new CommandItem(CommandGroup.Terminal, "Hide timestamps", "term:timestamps-off")
            : new CommandItem(CommandGroup.Terminal, "Show timestamps", "term:timestamps-on"));
        items.Add(context.SecondInputOn
            ? new CommandItem(
                CommandGroup.Terminal, "Hide second input", "term:input2-off", "this window · ⌃B i")
            : new CommandItem(
                CommandGroup.Terminal, "Show second input", "term:input2-on", "this window · ⌃B i"));

        // LAYOUT
        items.Add(new CommandItem(CommandGroup.Layout, "Split right", "layout:split-right"));
        items.Add(new CommandItem(CommandGroup.Layout, "Split down", "layout:split-down"));
        items.Add(context.Zoomed
            ? new CommandItem(CommandGroup.Layout, "Unzoom pane", "layout:unzoom")
            : new CommandItem(CommandGroup.Layout, "Zoom pane", "layout:zoom"));
        items.Add(new CommandItem(CommandGroup.Layout, "Close pane", "layout:close"));

        // SETTINGS — one entry per configuration screen, in the order the host lists them (its F-key
        // order). Every screen or none: a surface offering one of them and hiding the rest would read
        // as "this is the only thing you can configure", which is the state the surface was in before.
        if (settings is not null)
        {
            foreach (var screen in settings)
            {
                items.Add(new CommandItem(CommandGroup.Settings, $"Open {screen.Title}", screen.Id, screen.Shortcut));
            }
        }

        return items;
    }
}
