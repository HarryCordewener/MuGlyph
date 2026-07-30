using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Telnet;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Tui;

/// <summary>
/// Builds the configuration behind the headless demo/snapshots. Rather than assembling windows and
/// panes imperatively, it produces a full <see cref="AppConfiguration"/> — worlds, characters, shared
/// trigger sets, and a saved <see cref="WorkspaceState"/> — that the app resumes exactly as it would a
/// real user's last session. So the demo exercises the genuine "load config → resume last session"
/// startup path, and its screenshots reflect real-world behaviour.
/// </summary>
internal static class DemoScene
{
    /// <summary>The world.character the demo resumes as focused/connected.</summary>
    public const string ActiveSessionKey = "Aetherfall.Corvid";

    public static AppConfiguration Build()
    {
        var config = new AppConfiguration();
        AddWorlds(config);
        AddTriggerSets(config);
        config.LastSession = BuildLastSession();
        return config;
    }

    private static void AddWorlds(AppConfiguration config)
    {
        config.Worlds.Add(new WorldDefinition
        {
            Name = "Aetherfall",
            Host = "aetherfall.mux",
            Port = 4201,
            UseTls = true,

            // Left on `auto` — the default, and the case the F5 screen and the status row both exist to
            // show. Grapevine below pins one, so a single frame shows both a follower and an override.
            Encoding = TelnetSessionOptions.AutoEncodingName,
            KeepaliveSeconds = 30,
            Accent = SharpMUTermApp.AccentPalette[0],
            Characters =
            {
                new CharacterDefinition
                {
                    Name = "Corvid",
                    AutoLogin = true,
                    OnConnect = "@@ +who; look",
                    TriggerSets = { "Comms", "Trade" },
                    Logging = new LoggingSettings { Format = LogFormat.Html },
                },
                new CharacterDefinition { Name = "Rookery", TriggerSets = { "Comms" } },
            },
        });

        config.Worlds.Add(new WorldDefinition
        {
            Name = "Grapevine",
            Host = "grapevine.haus",
            Port = 4000,
            Encoding = "ISO-8859-1",
            Accent = SharpMUTermApp.AccentPalette[1],
            Characters = { new CharacterDefinition { Name = "Thistle" } },
        });
    }

    private static void AddTriggerSets(AppConfiguration config)
    {
        var teal = TerminalColor.FromRgb(0x00, 0xf5, 0xb7);
        var pink = TerminalColor.FromRgb(0xe5, 0x8f, 0xb0);

        config.TriggerSets.Add(new TriggerSet
        {
            Name = "Comms",
            Description = "channel + page routing",
            Triggers =
            {
                // The F2 screen opens on this rule, so it is the one that has to exercise the editor
                // pane: a capture group to reference, a rewrite that uses it, an attribute, a script
                // callback, and case-sensitive matching. Its `respond` is deliberately left off — the
                // frame should show both states of an action, not four filled wells.
                new Trigger
                {
                    Name = "public",
                    Pattern = @"^\[public\] (.+)$",
                    CaseSensitive = true,
                    Actions = new TriggerActions
                    {
                        SpawnTarget = "Chat",
                        HighlightForeground = teal,
                        AddAttributes = TextAttributes.Bold,
                        Rewrite = "» $1",
                        ScriptCallback = "onChannel",
                    },
                },
                new Trigger
                {
                    Name = "page",
                    Pattern = @"^(\w+) pages:",
                    Actions = new TriggerActions
                    {
                        SpawnTarget = "pages",
                        HighlightForeground = pink,
                        SendResponse = "page $1=afk, back shortly",
                    },
                },
                new Trigger
                {
                    Name = "mute spam",
                    Pattern = @"has connected\.$",
                    Enabled = false,
                    Actions = new TriggerActions { Gag = true },
                },
            },
            Aliases =
            {
                new Alias { Name = "say", Pattern = @"^'(.*)", Substitution = "say $1" },
                new Alias { Name = "wtf", Pattern = @"^wtf\s+(.+)$", Substitution = "who\nfinger $1" },
            },
            // A movement keypad: enough bound keys, of differing command lengths, to actually show
            // the 3x3 grid doing its job (and one command long enough to be ellipsised).
            Macros =
            {
                new Macro { Name = "walk NW", Key = "Num7", Command = "northwest" },
                new Macro { Name = "walk N", Key = "Num8", Command = "north" },
                new Macro { Name = "walk NE", Key = "Num9", Command = "northeast" },
                new Macro { Name = "walk W", Key = "Num4", Command = "west" },
                new Macro { Name = "look", Key = "Num5", Command = "look" },
                new Macro { Name = "walk E", Key = "Num6", Command = "east" },
                new Macro { Name = "altar", Key = "Num1", Command = "look at altar" },
                new Macro { Name = "walk S", Key = "Num2", Command = "south" },
                new Macro { Name = "score", Key = "Ctrl+F1", Command = "score" },
            },
            Timers = { new TimerDefinition { Name = "keepalive", IntervalSeconds = 60, Command = "@@idle" } },
        });

        config.TriggerSets.Add(new TriggerSet
        {
            Name = "Trade",
            Description = "auction + market watch",
            Triggers =
            {
                new Trigger
                {
                    Name = "auction",
                    Pattern = @"^\[trade\]",
                    Actions = new TriggerActions { SpawnTarget = "trade", HighlightForeground = TerminalColor.FromRgb(0xe5, 0xc0, 0x7b) },
                },
            },
            Timers = { new TimerDefinition { Name = "market", IntervalSeconds = 300, Command = "prices", OneShot = false } },
        });

        // A third set, and deliberately a lopsided one. Two sets is enough to show that rules belong to
        // a set; it is not enough to show what the set screens have to cope with, which is that a set
        // holds four *kinds* of thing and rarely holds all four. Combat has no triggers and no timers, so
        // F2 and F6 draw it as a set with nothing of theirs in it — the one state a flattened pane could
        // not show at all before, and the state a set is in the moment it is created. It is also left
        // unassigned, so the F5 checklist shows both states of the checkbox on one character.
        config.TriggerSets.Add(new TriggerSet
        {
            Name = "Combat",
            Description = "hp + damage tracking",
            Aliases = { new Alias { Name = "hp", Pattern = @"^hp$", Substitution = "score\nconsider" } },
            Macros = { new Macro { Name = "flee", Key = "Ctrl+F2", Command = "flee" } },
        });
    }

    /// <summary>
    /// The saved session the demo resumes: Corvid's main window and a Chat spawn (routed by the
    /// <c>Comms</c> set's <c>public</c> trigger), both hosted as tabs in a single pane.
    /// </summary>
    /// <remarks>
    /// <b>The main window's title is its character's name because that is what a live session writes.</b>
    /// <c>SharpMUTermApp.BindSession</c> titles a session's own window <c>SessionTitle(session)</c>; the demo
    /// has no session, so whatever is written here is what every snapshot shows. It said <c>main</c>, and that
    /// one word of divergence hid the rail repeating the world's name under the character — the frames were
    /// checking a shape the running client never has. Keep it equal to
    /// <see cref="ActiveSessionKey"/>'s character; <c>RailWindowRowTests</c> holds the two together.
    /// </remarks>
    private static WorkspaceState BuildLastSession()
    {
        var chatId = Workspace.SpawnWindowId("Chat");
        return new WorkspaceState
        {
            Windows =
            {
                new WorkspaceWindowState
                {
                    Id = "main",
                    Title = "Corvid",
                    Kind = WindowKind.Main,
                    SessionKey = ActiveSessionKey,
                },
                new WorkspaceWindowState
                {
                    Id = chatId,
                    Title = "Chat",
                    Kind = WindowKind.Spawn,
                    SessionKey = ActiveSessionKey,
                    OwnerLabel = "Corvid",
                    CapturePattern = @"^\[Chat\]",
                },
            },
            Root = new LayoutNodeState
            {
                Type = "pane",
                Id = "p1",
                Tabs = { "main", chatId },
                ActiveIndex = 0,
            },
            FocusedPaneId = "p1",
        };
    }
}
