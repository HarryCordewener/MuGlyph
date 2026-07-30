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
/// <param name="ScrolledBack">
/// Whether the focused pane is showing something other than its newest output, so
/// <c>Back to live output</c> has somewhere to go.
/// </param>
public sealed record CommandContext(
    bool LoggingOn = false,
    bool Zoomed = false,
    bool Frozen = false,
    bool TimestampsOn = false,
    bool SecondInputOn = false,
    bool ScrolledBack = false);

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
                CommandIds.Character(character.SessionKey),
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
                CommandIds.Window(window.Id),
                $"{owner}{unread}"));
        }

        // WORLD
        // Both carry their chord. The surface is where this client is discovered from, and a key nobody
        // can find is the same as no feature — ⌃L's newline sat unused until it was reported missing.
        items.Add(new CommandItem(CommandGroup.World, "Reconnect", "world:reconnect", "Alt+R"));
        items.Add(new CommandItem(CommandGroup.World, "Disconnect", "world:disconnect", "⌃D"));

        // TERMINAL — stateful labels.
        items.Add(context.LoggingOn
            ? new CommandItem(CommandGroup.Terminal, "Pause logging", "term:log-off")
            : new CommandItem(CommandGroup.Terminal, "Start logging", "term:log-on"));
        items.Add(context.Frozen
            ? new CommandItem(CommandGroup.Terminal, "Resume scrollback", "term:unfreeze")
            : new CommandItem(CommandGroup.Terminal, "Freeze pane", "term:freeze"));
        items.Add(new CommandItem(CommandGroup.Terminal, "Clear window", "term:clear"));

        // Scrollback position. Here as well as on the keyboard because this surface is where the client
        // is discovered from — every other thing you can do to an output window is listed here — and the
        // subtitles are how the keys get taught: a pane that scrolls and says so nowhere is a pane
        // nobody scrolls. The "back" entry appears only when there is somewhere to come back from.
        items.Add(new CommandItem(
            CommandGroup.Terminal, "Scroll to oldest", "term:scroll-oldest", "⌃Home · PgUp pages back"));
        if (context.ScrolledBack)
        {
            items.Add(new CommandItem(
                CommandGroup.Terminal, "Back to live output", "term:scroll-live", "⌃End"));
        }

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

        // The newline chord. Here for the same reason the scrollback keys are: a chord that works and is
        // named nowhere is a chord nobody finds, which is precisely how this one was reported as missing.
        // Alt+⏎ is the modifier+Enter this host delivers — a terminal reports Shift+⏎ and Ctrl+⏎ as a
        // bare ⏎, so naming those would be advertising a key that cannot fire.
        items.Add(new CommandItem(
            CommandGroup.Terminal, "Insert a newline in the command line", "term:newline", "Alt+⏎ · ⌃L"));

        // LAYOUT — every entry carries the chord that runs it, because this surface is where the client
        // is discovered from and the ⌃B keymap is otherwise visible only while the prefix is armed.
        items.Add(new CommandItem(CommandGroup.Layout, "Split right", "layout:split-right", "⌃B |"));
        items.Add(new CommandItem(CommandGroup.Layout, "Split down", "layout:split-down", "⌃B -"));
        items.Add(context.Zoomed
            ? new CommandItem(CommandGroup.Layout, "Unzoom pane", "layout:unzoom", "⌃B z")
            : new CommandItem(CommandGroup.Layout, "Zoom pane", "layout:zoom", "⌃B z"));
        items.Add(new CommandItem(CommandGroup.Layout, "Close pane", "layout:close", "⌃B x"));

        // Directional pane focus. Listed whether or not the workspace has a second pane, deliberately:
        // this is the surface that teaches the keyboard, and the entries are how a reader learns the
        // workspace splits at all. Each refuses out loud when there is nothing that way.
        items.Add(new CommandItem(CommandGroup.Layout, "Focus pane left", "layout:focus-left", "⌃←"));
        items.Add(new CommandItem(CommandGroup.Layout, "Focus pane right", "layout:focus-right", "⌃→"));
        items.Add(new CommandItem(CommandGroup.Layout, "Focus pane up", "layout:focus-up", "⌃↑"));
        items.Add(new CommandItem(CommandGroup.Layout, "Focus pane down", "layout:focus-down", "⌃↓"));
        items.Add(new CommandItem(CommandGroup.Layout, "Focus the next pane", "layout:cycle", "⌃O · ⌃B o"));

        // Pane size, in the plain words the request used ("increase/decrease a pane's horizontal or
        // vertical character size") rather than in the chord's terms. Listed for the same reason the
        // directional entries and the newline chord are: ⌥⇧+arrow is not a chord anybody guesses, and a
        // feature reachable only by a chord nobody knows is the state ⌃L's newline sat in until it was
        // reported as missing. Listed unconditionally too — each refuses out loud when the focused pane
        // has no border to move that way, which is how a reader learns the workspace resizes at all.
        // The arrow names what happens to *this* pane — ↑ taller, ↓ shorter — from either side of the
        // split; it named the direction the divider travelled until that was reported as inverted.
        items.Add(new CommandItem(CommandGroup.Layout, "Make this pane wider", "layout:wider", "⌥⇧→"));
        items.Add(new CommandItem(CommandGroup.Layout, "Make this pane narrower", "layout:narrower", "⌥⇧←"));
        items.Add(new CommandItem(CommandGroup.Layout, "Make this pane taller", "layout:taller", "⌥⇧↑"));
        items.Add(new CommandItem(CommandGroup.Layout, "Make this pane shorter", "layout:shorter", "⌥⇧↓"));

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
