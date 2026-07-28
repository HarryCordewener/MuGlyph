using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
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
            Encoding = "UTF-8",
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
                new Trigger
                {
                    Name = "public",
                    Pattern = @"^\[public\]",
                    Actions = new TriggerActions { SpawnTarget = "Chat", HighlightForeground = teal },
                },
                new Trigger
                {
                    Name = "page",
                    Pattern = @"^\w+ pages:",
                    Actions = new TriggerActions { SpawnTarget = "pages", HighlightForeground = pink },
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
                new Macro { Key = "Num7", Command = "northwest" },
                new Macro { Key = "Num8", Command = "north" },
                new Macro { Key = "Num9", Command = "northeast" },
                new Macro { Key = "Num4", Command = "west" },
                new Macro { Key = "Num5", Command = "look" },
                new Macro { Key = "Num6", Command = "east" },
                new Macro { Key = "Num1", Command = "look at altar" },
                new Macro { Key = "Num2", Command = "south" },
                new Macro { Key = "Ctrl+F1", Command = "score" },
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
    }

    /// <summary>
    /// The saved session the demo resumes: Corvid's main window and a Chat spawn (routed by the
    /// <c>Comms</c> set's <c>public</c> trigger), both hosted as tabs in a single pane.
    /// </summary>
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
                    Title = "main",
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
