using MuClient.Core.Commands;
using MuClient.Core.Automation;
using MuClient.Core.Configuration;
using MuClient.Core.Session;
using MuClient.Core.Text;
using MuClient.Core.Theming;
using MuClient.Core.Workspaces;
using MuClient.Graphics;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Events;
using SharpConsoleUI.Layout;
using SColor = SharpConsoleUI.Color;

namespace MuClient.Tui;

/// <summary>
/// The top-level MuGlyph application on SharpConsoleUI: a status line, a tabbed set of output
/// windows (main + trigger-routed spawn windows + the web view), and a command prompt. The tab set
/// is driven by the UI-agnostic <see cref="Workspace"/> model — a single pane holding many window
/// tabs — so spawn routing and unread badges reuse the tested Core logic. Splits (via SharpConsoleUI
/// splitters) layer on this later. Background session events are marshalled onto the UI thread.
/// </summary>
internal sealed class MuGlyphApp : IAsyncDisposable
{
    private const string MainWindowId = "main";
    private const string WebWindowId = "web";

    private readonly AppConfiguration _config;
    private readonly SessionManager _sessions = new();
    private readonly TerminalCapabilities _capabilities;
    private readonly Theme _theme;
    private readonly MarkupFormatter _formatter;
    private readonly Workspace _workspace = new(MainWindowId, "Main");
    private readonly Dictionary<string, MarkupControl> _panes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _connectedKeys = new(StringComparer.Ordinal);

    private readonly ConsoleWindowSystem _system;
    private readonly Window _window;
    private readonly MarkupControl _header;
    private readonly MarkupControl _statusBar;
    private readonly MarkupControl _rail;
    private readonly MarkupControl _inputGutter;
    private readonly Dictionary<string, TabControl> _paneTabs = new(StringComparer.Ordinal);
    private readonly PromptControl _input;
    private readonly GmcpStats _stats = new();
    private readonly MuClient.Web.WebPageFetcher _fetcher = new();

    private readonly CommandPalette _palette;
    private readonly WorldSettingsView _worldSettings;

    /// <summary>Per-world accents when a world hasn't set its own, keyed by position.</summary>
    private static readonly TerminalColor[] AccentPalette =
    {
        TerminalColor.FromRgb(0x00, 0xf5, 0xb7), // teal
        TerminalColor.FromRgb(0xff, 0x9f, 0x1c), // amber
        TerminalColor.FromRgb(0x9d, 0x7c, 0xff), // violet
        TerminalColor.FromRgb(0x5f, 0xaf, 0xff), // sky
    };

    private WorldSession? _active;
    private WorldDefinition? _pendingWorld;
    private string? _demoActiveKey;

    /// <summary>The rail + pane-area row currently in the window (index 1). Swapped on layout change.</summary>
    private IWindowControl _workspaceRow = null!;

    /// <summary>The window index the workspace row sits at (after the sticky-top header).</summary>
    private const int WorkspaceRowIndex = 1;

    public MuGlyphApp(AppConfiguration config, TerminalCapabilities capabilities, IConsoleDriver? driver = null)
    {
        _config = config;
        _capabilities = capabilities;
        _theme = ResolveTheme(config);
        _formatter = new MarkupFormatter(_theme);

        // A headless driver renders to a captured buffer (for snapshots/CI) instead of a real
        // terminal; hide the desktop panels so those frames are deterministic.
        var headless = driver is HeadlessConsoleDriver;
        var options = new ConsoleWindowSystemOptions(
            ShowTopPanel: !headless,
            ShowBottomPanel: !headless,
            EnableAnimations: !headless);
        _system = new ConsoleWindowSystem(driver ?? new NetConsoleDriver(RenderMode.Buffer), options);

        _header = Controls.Markup(HeaderMarkup()).StickyTop().Build();

        var main = new MarkupControl(new List<string>());
        main.LinkClicked += (_, e) => OnLinkClicked(e.Url);
        _panes[MainWindowId] = main;

        // The connection rail (worlds → characters → windows) sits left of the pane area, joined by
        // a splitter. RailModel/RailRenderer keep the projection + markup tested; this just hosts it.
        _rail = new MarkupControl(new List<string>());

        // The pane area renders the workspace's split tree (one TabControl per leaf pane). It's built
        // from the model and rebuilt whenever the layout changes; the initial row goes into the window.
        _workspaceRow = BuildWorkspaceRow();

        // A thin gutter above the input: which window the line goes to, other windows holding drafts,
        // and the character count — the design's input-region affordance. StatusFormatter builds it.
        _inputGutter = Controls.Markup("[dim]→ main  0[/]").StickyBottom().Build();

        _input = Controls.Prompt("›").WithHistory(true).StickyBottom().Build();
        _input.Entered += (_, text) => OnCommandEntered(text);
        _input.InputChanged += (_, text) => OnInputChanged(text);

        _statusBar = Controls.Markup("[dim]not connected[/]").StickyBottom().Build();

        var bg = ToColor(_theme.Resolve(TerminalColor.Default, isBackground: true));
        var fg = ToColor(_theme.Resolve(TerminalColor.Default, isBackground: false));

        _window = new WindowBuilder(_system)
            .WithTitle("MuGlyph — MU* client")
            .Maximized()
            .WithColors(fg, bg)
            .AddControl(_header)
            .AddControl(_workspaceRow)
            .AddControl(_inputGutter)
            .AddControl(_input)
            .AddControl(_statusBar)
            .Build();

        _palette = new CommandPalette(_system, BuildCatalog, () => _active?.SessionKey, DispatchCommand);
        _worldSettings = new WorldSettingsView(
            _system,
            () => ActiveWorld() is { } w ? (w.World, w.Accent) : ((WorldDefinition, TerminalColor)?)null);

        _window.OnResize += (_, _) => ReportWindowSize();
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.Q, () => _system.RequestExit(0));
        // Next window (Ctrl+N, plus Ctrl+Tab where the terminal reports it) and close window (Ctrl+W).
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.N, NextWindow);
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.Tab, NextWindow);
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.W, CloseActiveWindow);
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.O, CyclePane);
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.P, () => _palette.Toggle());
        _system.RegisterGlobalShortcut((ConsoleModifiers)0, ConsoleKey.F2, () => _worldSettings.Toggle());
        _system.AddWindow(_window);
    }

    /// <summary>Runs the UI loop, connecting <paramref name="world"/> once the window is shown.</summary>
    public int Run(WorldDefinition? world)
    {
        _pendingWorld = world;
        _window.OnShown += (_, _) => _ = StartAsync(_pendingWorld);
        return _system.Run();
    }

    /// <summary>
    /// Renders one demo frame to an ANSI string using a headless driver — no terminal or connection
    /// required. Used by the <c>--snapshot</c> mode to produce documentation images and CI golden
    /// snapshots. Requires the app to have been constructed with a <see cref="HeadlessConsoleDriver"/>.
    /// </summary>
    public string RenderSnapshot(string? view = null)
    {
        LoadDemoScene();

        // Optionally open a modal panel over the workspace so its frame can be captured too.
        if (string.Equals(view, "settings", StringComparison.OrdinalIgnoreCase))
        {
            _worldSettings.Open();
        }

        // Render exactly one frame, synchronously, inline on this thread. ForceRender() performs a
        // single render cycle (bypassing the frame-rate limiter) with no Run() loop, no driver
        // Initialize/Start, and no OnShown pass — a freshly-added window is dirty and paints on the
        // first call. The HeadlessConsoleDriver writes the composited frame straight to the console,
        // so we redirect Console.Out for the duration of that one call and keep what it wrote. (An
        // earlier Run()-on-a-worker-thread approach raced the input+render pump and hung/OOM'd.)
        var real = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            _system.ForceRender();
        }
        finally
        {
            Console.SetOut(real);
        }

        return writer.ToString();
    }

    /// <summary>Populates the windows with representative MU* content for snapshots/demos.</summary>
    private void LoadDemoScene()
    {
        SeedDemoWorlds();

        if (_workspace.FindWindow(MainWindowId) is { } mainWindow)
        {
            mainWindow.Title = "Aardwolf";
        }

        var parser = new AnsiParser();
        void Feed(MarkupControl pane, string ansiLine)
        {
            foreach (var line in parser.Feed(ansiLine + "\n"))
            {
                pane.AppendLine(_formatter.ToMarkup(line));
            }
        }

        var main = _panes[MainWindowId];
        Feed(main, "\x1b[1;36mThe Grand Plaza\x1b[0m");
        Feed(main, "\x1b[0;37mA marble fountain bubbles at the centre of a wide plaza. Merchants\x1b[0m");
        Feed(main, "\x1b[0;37mhawk their wares beneath striped awnings.\x1b[0m");
        Feed(main, "\x1b[0;32mA town guard\x1b[0m stands watch by the northern gate.");

        // A line with clickable MXP-style exits (rendered as [link=…] spans).
        var exits = new StyledLine(new[]
        {
            new StyledSpan("Exits: ", TextStyle.Default),
            Link("north"),
            new StyledSpan("  ", TextStyle.Default),
            Link("east"),
        });
        main.AppendLine(_formatter.ToMarkup(exits));

        // A spawn window fed by a "Chat" trigger target, left in the background with unread.
        var chat = _workspace.RouteSpawn("Chat");
        var chatPane = PaneContentFor(chat.Id, chat.Title);
        var chatParser = new AnsiParser();
        foreach (var line in chatParser.Feed("\x1b[1;35m[Chat]\x1b[0m Rivane: anyone up for the crypt run?\n"))
        {
            chatPane.AppendLine(_formatter.ToMarkup(line));
        }

        foreach (var line in chatParser.Feed("\x1b[1;35m[Chat]\x1b[0m Bob: aye, meet me at the gate\n"))
        {
            chatPane.AppendLine(_formatter.ToMarkup(line));
        }

        _workspace.NoteActivity(chat.Id); // second unread line

        // Sample vitals so the status-bar meters render in snapshots.
        _stats.Update("Char.Vitals", "{\"hp\":312,\"maxhp\":400,\"mp\":180,\"maxmp\":330}");
        _statusBar.SetContent(new List<string> { StatusBarMarkup("Corvid", "aetherfall.mux", 4201, "connected") });
        _header.SetContent(new List<string> { HeaderMarkup() });
        _input.Input = "say hello there";
        RebuildPaneArea(); // realise the Chat spawn tab, then refresh badges
    }

    /// <summary>
    /// Seeds a couple of demo worlds/characters (with accents) so the rail, command surface, and
    /// status bar have representative content in headless snapshots. Only runs when no worlds are
    /// configured, so it never masks a real config.
    /// </summary>
    private void SeedDemoWorlds()
    {
        if (_config.Worlds.Count > 0)
        {
            _demoActiveKey = $"{_config.Worlds[0].Name}.{_config.Worlds[0].Characters.FirstOrDefault()?.Name}";
            return;
        }

        _config.Worlds.Add(new WorldDefinition
        {
            Name = "Aetherfall",
            Host = "aetherfall.mux",
            Port = 4201,
            UseTls = true,
            Encoding = "UTF-8",
            KeepaliveSeconds = 30,
            Accent = AccentPalette[0],
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

        _config.Worlds.Add(new WorldDefinition
        {
            Name = "Grapevine",
            Host = "grapevine.haus",
            Port = 4000,
            Encoding = "ISO-8859-1",
            Accent = AccentPalette[1],
            Characters = { new CharacterDefinition { Name = "Thistle" } },
        });

        SeedDemoTriggerSets();

        _demoActiveKey = "Aetherfall.Corvid";
        _connectedKeys.Add("Aetherfall.Corvid");
    }

    /// <summary>Seeds representative trigger sets (triggers/aliases/macros/timers) for demo snapshots.</summary>
    private void SeedDemoTriggerSets()
    {
        var teal = TerminalColor.FromRgb(0x00, 0xf5, 0xb7);
        var pink = TerminalColor.FromRgb(0xe5, 0x8f, 0xb0);

        _config.TriggerSets.Add(new TriggerSet
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
                new Alias { Name = "wtf", Pattern = @"^wtf$", Substitution = "who\nfinger $1" },
            },
            Macros = { new Macro { Key = "Num5", Command = "look" }, new Macro { Key = "Ctrl+F1", Command = "score" } },
            Timers = { new TimerDefinition { Name = "keepalive", IntervalSeconds = 60, Command = "@@idle" } },
        });

        _config.TriggerSets.Add(new TriggerSet
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

    private static StyledSpan Link(string command) => new(
        command,
        new TextStyle(TerminalColor.FromIndex(11), TerminalColor.Default, TextAttributes.Underline),
        SpanInteraction.Command(command));

    private async Task StartAsync(WorldDefinition? world)
    {
        if (world is null)
        {
            SetStatus("No world configured. Pass a host/port on the command line.");
            return;
        }

        var session = _sessions.Open(world, _config.ScrollbackLines);
        BindSession(session);

        session.PrintSystem($"*** MuGlyph — theme '{_theme.Name}', graphics: {_capabilities.Protocol}.");

        try
        {
            await session.ConnectAsync().ConfigureAwait(false);
            ReportWindowSize();
        }
        catch
        {
            // WorldSession already surfaced the failure as a system line.
        }
    }

    private void BindSession(WorldSession session)
    {
        _active = session;
        if (_workspace.FindWindow(MainWindowId) is { } mainWindow)
        {
            mainWindow.Title = session.World.Name;
        }

        var main = _panes[MainWindowId];
        foreach (var line in session.Scrollback.Snapshot())
        {
            main.AppendLine(_formatter.ToMarkup(line));
        }

        session.LinePrinted += (_, line) => OnUi(() => OnLine(MainWindowId, line));
        session.PromptChanged += (_, _) => OnUi(UpdateStatus);
        session.StateChanged += (_, _) => OnUi(UpdateStatus);
        session.GmcpReceived += (_, e) => OnUi(() =>
        {
            if (_stats.Update(e.Package, e.Json))
            {
                UpdateStatus();
            }
        });
        session.SpawnLine += (_, e) => OnUi(() => OnSpawnLine(e.Target, e.Line));
        RefreshTabTitles();
        UpdateStatus();
    }

    /// <summary>Appends a line to a window's pane and badges it unread when it isn't the visible tab.</summary>
    private void OnLine(string windowId, StyledLine line)
    {
        if (_panes.TryGetValue(windowId, out var pane))
        {
            pane.AppendLine(_formatter.ToMarkup(line));
        }

        if (!_workspace.IsVisible(windowId))
        {
            _workspace.NoteActivity(windowId);
            RefreshTabTitles();
        }
    }

    /// <summary>Routes a trigger-spawned line to its spawn window (creating the tab on first use).</summary>
    private void OnSpawnLine(string target, StyledLine line)
    {
        var existed = _workspace.FindWindow(Workspace.SpawnWindowId(target)) is not null;
        var window = _workspace.RouteSpawn(target, _active?.SessionKey);
        PaneContentFor(window.Id, window.Title).AppendLine(_formatter.ToMarkup(line));

        // A first-seen spawn adds a tab to its pane, so rebuild; otherwise just refresh badges.
        if (existed)
        {
            RefreshTabTitles();
        }
        else
        {
            RebuildPaneArea();
        }
    }

    private void OnCommandEntered(string command)
    {
        // The entered command clears this window's draft and its unsent-input marker.
        var windowId = ActiveWindowId();
        _drafts.Remove(windowId);
        _workspace.SetUnsentInput(windowId, false);
        _input.Input = string.Empty;
        RefreshTabTitles();

        // `/web <url>` opens the in-TUI web view; everything else goes to the world.
        if (command.StartsWith("/web ", StringComparison.OrdinalIgnoreCase))
        {
            OpenWeb(command[5..].Trim());
            return;
        }

        _ = _active?.SendUserInputAsync(command);
    }

    /// <summary>Tracks the per-window input draft and the <c>✎</c> unsent-input marker as you type.</summary>
    private void OnInputChanged(string text)
    {
        var windowId = ActiveWindowId();
        if (string.IsNullOrEmpty(text))
        {
            _drafts.Remove(windowId);
        }
        else
        {
            _drafts[windowId] = text;
        }

        _workspace.SetUnsentInput(windowId, !string.IsNullOrEmpty(text));
        RefreshTabTitles();
        UpdateInputChrome();
    }

    /// <summary>The window id of the visible tab (the input line belongs to it).</summary>
    private string ActiveWindowId() => _workspace.Layout.FocusedPane.ActiveTab ?? MainWindowId;

    /// <summary>
    /// Refreshes the input region: the character-bound prompt (<c>Corvid@Aetherfall ›</c>) and the
    /// gutter (destination window, other windows holding drafts, character count). Both come from the
    /// tested <see cref="StatusFormatter"/>.
    /// </summary>
    private void UpdateInputChrome()
    {
        var session = _active;
        var character = session?.Character?.Name ?? (ActiveWorld() is { } aw ? aw.Character : null);
        var world = session?.World.Name ?? ActiveWorld()?.World.Name;
        _input.Prompt = StatusFormatter.CharacterPrompt(character, world);

        var activeId = ActiveWindowId();
        var destination = _workspace.FindWindow(activeId)?.Title ?? activeId;
        var drafts = _workspace.Windows
            .Where(w => w.HasUnsentInput && w.Id != activeId)
            .Select(w => w.Title)
            .ToList();
        _inputGutter.SetContent(new List<string> { $"[dim]{Escape(StatusFormatter.InputGutter(destination, drafts, _input.Input.Length))}[/]" });
    }

    /// <summary>The <c>world.character</c> key of the character whose windows the rail expands.</summary>
    private string? ActiveCharacterKey() => _active?.SessionKey ?? _demoActiveKey;

    /// <summary>The accent for a world at <paramref name="index"/>: its own, or the palette fallback.</summary>
    private static TerminalColor AccentFor(WorldDefinition world, int index) =>
        world.Accent.Kind == TerminalColorKind.Default ? AccentPalette[index % AccentPalette.Length] : world.Accent;

    /// <summary>The active world + resolved accent + focused character name, or null when disconnected.</summary>
    private (WorldDefinition World, TerminalColor Accent, string? Character)? ActiveWorld()
    {
        var key = ActiveCharacterKey();
        if (key is null)
        {
            return null;
        }

        var index = 0;
        foreach (var world in _config.Worlds)
        {
            var accent = AccentFor(world, index);
            if (world.Name == key)
            {
                return (world, accent, null);
            }

            foreach (var character in world.Characters)
            {
                if ($"{world.Name}.{character.Name}" == key)
                {
                    return (world, accent, character.Name);
                }
            }

            index++;
        }

        return null;
    }

    /// <summary>Renders a <see cref="TerminalColor"/> as a <c>#rrggbb</c> markup colour.</summary>
    private static string AccentHex(TerminalColor accent) =>
        accent.Kind == TerminalColorKind.Rgb
            ? $"#{accent.R:x2}{accent.G:x2}{accent.B:x2}"
            : "#00f5b7";

    /// <summary>
    /// Projects live config + workspace state into rail rows: each world (with an accent), its
    /// characters (connected dot, active marker), and — under the active character — the workspace's
    /// windows with their unread/unsent/pane detail. Ranking/markup stays in the tested Core/renderer.
    /// </summary>
    private IReadOnlyList<RailRow> BuildRail()
    {
        var activeKey = ActiveCharacterKey();

        // Friendly pane labels for the window rows: the first pane is "main", later panes number up.
        var panes = _workspace.Layout.Panes;
        var paneLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < panes.Count; i++)
        {
            paneLabels[panes[i].Id] = i == 0 ? "main" : $"pane {i + 1}";
        }

        var worlds = new List<RailWorld>();
        var index = 0;
        foreach (var world in _config.Worlds)
        {
            var accent = world.Accent.Kind == TerminalColorKind.Default
                ? AccentPalette[index % AccentPalette.Length]
                : world.Accent;

            var characters = new List<RailCharacter>();
            foreach (var character in world.Characters)
            {
                var key = $"{world.Name}.{character.Name}";
                var active = key == activeKey;
                var windows = active ? BuildRailWindows(paneLabels) : Array.Empty<RailWindow>();
                characters.Add(new RailCharacter(
                    character.Name,
                    key,
                    Connected: _connectedKeys.Contains(key),
                    Active: active,
                    Unread: windows.Sum(w => w.Unread),
                    windows));
            }

            worlds.Add(new RailWorld(world.Name, world.Host, world.Port, accent, characters));
            index++;
        }

        return RailModel.Build(worlds);
    }

    /// <summary>The active character's windows, in registration order, as rail window rows.</summary>
    private IReadOnlyList<RailWindow> BuildRailWindows(IReadOnlyDictionary<string, string> paneLabels)
    {
        var windows = new List<RailWindow>();
        foreach (var window in _workspace.Windows)
        {
            var pane = _workspace.Layout.FindWindow(window.Id);
            var label = pane is not null ? paneLabels.GetValueOrDefault(pane.Id) : null;
            windows.Add(new RailWindow(window.Title, label, window.Unread, window.HasUnsentInput, Closed: pane is null));
        }

        return windows;
    }

    /// <summary>Repaints the rail from current state.</summary>
    private void RefreshRail() => _rail.SetContent(RailRenderer.Render(BuildRail()));

    /// <summary>Builds the ⌃P command catalog from live config + workspace state.</summary>
    private IReadOnlyList<CommandItem> BuildCatalog()
    {
        var context = new CommandContext(
            LoggingOn: false,
            Zoomed: _workspace.Layout.ZoomedPaneId is not null,
            Frozen: _workspace.Layout.FocusedPane.Frozen);
        return CommandCatalog.Build(_workspace, BuildCharacterRefs(), _active?.SessionKey, context);
    }

    private IReadOnlyList<CharacterRef> BuildCharacterRefs()
    {
        var refs = new List<CharacterRef>();
        foreach (var world in _config.Worlds)
        {
            foreach (var character in world.Characters)
            {
                var key = $"{world.Name}.{character.Name}";
                refs.Add(new CharacterRef(world.Name, character.Name, key, _active?.SessionKey == key));
            }
        }

        return refs;
    }

    /// <summary>Runs a command-surface entry by its id, doing what the current shell supports.</summary>
    private void DispatchCommand(string id)
    {
        if (id.StartsWith("win:", StringComparison.Ordinal))
        {
            Activate(id["win:".Length..]);
            return;
        }

        switch (id)
        {
            case "layout:zoom":
            case "layout:unzoom":
                _workspace.Layout.ToggleZoom();
                RebuildPaneArea(); // zoom collapses the tree to one pane (or restores it)
                return;
            case "layout:close":
                CloseActiveWindow(); // rebuilds the pane area itself
                return;
            case "layout:split-right":
                PaneCommands.Apply(_workspace.Layout, PaneCommand.SplitRight);
                RebuildPaneArea();
                return;
            case "layout:split-down":
                PaneCommands.Apply(_workspace.Layout, PaneCommand.SplitDown);
                RebuildPaneArea();
                return;
            case "term:freeze":
            case "term:unfreeze":
                _workspace.Layout.ToggleFreezeFocused();
                break;
            case "term:clear":
                if (_panes.TryGetValue(ActiveWindowId(), out var pane))
                {
                    pane.SetContent(new List<string>());
                }

                break;
            case "world:reconnect":
                _ = _active?.ConnectAsync();
                break;
            case "world:disconnect":
                _ = _active?.DisconnectAsync();
                break;
            default:
                _active?.PrintSystem($"*** '{id}' isn't wired in this build yet.");
                break;
        }

        RefreshTabTitles();
    }

    private void OnLinkClicked(string url)
    {
        if (url.StartsWith(MarkupFormatter.SendScheme, StringComparison.Ordinal))
        {
            _ = _active?.SendRawAsync(Uri.UnescapeDataString(url[MarkupFormatter.SendScheme.Length..]));
        }
        else if (url.StartsWith(MarkupFormatter.PromptScheme, StringComparison.Ordinal))
        {
            _input.Input = Uri.UnescapeDataString(url[MarkupFormatter.PromptScheme.Length..]);
        }
        else
        {
            OpenWeb(url);
        }
    }

    private void OnTabChanged(string paneId, TabPage? newTab)
    {
        _workspace.Layout.Focus(paneId);
        if (newTab?.Tag is string id)
        {
            _workspace.ActivateWindow(id);
            // Restore this window's saved input draft into the shared prompt.
            _input.Input = _drafts.GetValueOrDefault(id, string.Empty);
            RefreshTabTitles();
        }
    }

    private void OpenWeb(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        _active?.PrintSystem($"*** Opening {url} in the web view...");
        _ = LoadWebAsync(url);
    }

    private async Task LoadWebAsync(string url)
    {
        var width = Math.Max(20, _window.Width - 4);
        try
        {
            var page = await _fetcher.FetchAsync(url, width).ConfigureAwait(false);
            OnUi(() => ShowWeb(page));
        }
        catch (Exception ex)
        {
            OnUi(() => _active?.PrintSystem($"*** Failed to load {url}: {ex.Message}"));
        }
    }

    private void ShowWeb(MuClient.Web.WebPage page)
    {
        var title = page.Title ?? page.Url;
        var isNew = _workspace.FindWindow(WebWindowId) is null;
        if (isNew)
        {
            _workspace.OpenWindow(WebWindowId, title, WindowKind.Auxiliary);
        }
        else
        {
            _workspace.FindWindow(WebWindowId)!.Title = title;
        }

        PaneContentFor(WebWindowId, title).SetContent(page.Lines.Select(_formatter.ToMarkup).ToList());
        if (isNew)
        {
            RebuildPaneArea(); // realise the new tab before activating it
        }

        Activate(WebWindowId);
    }

    /// <summary>The content control for a window, created (with link routing) on first use.</summary>
    private MarkupControl PaneContentFor(string id, string title)
    {
        if (_panes.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var control = new MarkupControl(new List<string>());
        control.LinkClicked += (_, e) => OnLinkClicked(e.Url);
        _panes[id] = control;
        return control;
    }

    /// <summary>
    /// Builds the rail + pane-area row: a fixed-width rail column, a splitter, and the pane area
    /// projected from the workspace's split tree. Called at construction and by
    /// <see cref="RebuildPaneArea"/> on every layout change.
    /// </summary>
    private IWindowControl BuildWorkspaceRow()
    {
        _paneTabs.Clear();

        // When a pane is zoomed, render just that pane full-area; otherwise render the whole tree.
        var zoomed = _workspace.Layout.ZoomedPaneId is { } zid ? _workspace.Layout.FindPane(zid) : null;
        var paneArea = zoomed is not null
            ? BuildPaneTabs(zoomed)
            : BuildLayoutNode(_workspace.Layout.Root);

        return Controls.HorizontalGrid()
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Column(c => c.Width(30).Add(_rail))
            .Column(c => c.Flex(1).Add(paneArea))
            .WithSplitterAfter(0)
            .Build();
    }

    /// <summary>
    /// Recursively realises a layout node: a leaf <see cref="PaneNode"/> becomes a tab strip, a
    /// <see cref="SplitNode"/> becomes a proportional grid (columns for a row split, rows for a
    /// column split) with a draggable splitter between children.
    /// </summary>
    private IWindowControl BuildLayoutNode(MuClient.Core.Workspaces.LayoutNode node)
    {
        if (node is PaneNode pane)
        {
            return BuildPaneTabs(pane);
        }

        var split = (SplitNode)node;
        var children = split.Children.Select(BuildLayoutNode).ToList();
        var lengths = split.Sizes.Select(s => GridLength.Star(Math.Max(0.01, s))).ToArray();

        var grid = Controls.Grid().WithVerticalAlignment(VerticalAlignment.Fill);
        if (split.Direction == SplitDirection.Row)
        {
            // Columns divide the width; a single full-height row hosts them. Place(control, row, col…).
            grid.Columns(lengths).Rows(GridLength.Star(1));
            for (var i = 0; i < children.Count; i++)
            {
                grid.Place(children[i], 0, i, 1, 1);
            }

            for (var i = 0; i < children.Count - 1; i++)
            {
                grid.ColumnSplitterAfter(i);
            }
        }
        else
        {
            // Rows divide the height; a single full-width column hosts them. Place(control, row, col…).
            grid.Rows(lengths).Columns(GridLength.Star(1));
            for (var i = 0; i < children.Count; i++)
            {
                grid.Place(children[i], i, 0, 1, 1);
            }

            for (var i = 0; i < children.Count - 1; i++)
            {
                grid.RowSplitterAfter(i);
            }
        }

        return grid.Build();
    }

    /// <summary>Builds a leaf pane's tab strip from its window ids, tracking it under its pane id.</summary>
    private IWindowControl BuildPaneTabs(PaneNode pane)
    {
        var builder = Controls.TabControl();
        var ids = new List<string>();
        foreach (var windowId in pane.Tabs)
        {
            if (_workspace.FindWindow(windowId) is not { } window)
            {
                continue;
            }

            builder.AddTab(TabTitles.For(window), PaneContentFor(windowId, window.Title));
            ids.Add(windowId);
        }

        var tabs = builder.Fill().Build();
        for (var i = 0; i < ids.Count; i++)
        {
            tabs.TabPages[i].Tag = ids[i];
        }

        if (pane.ActiveIndex >= 0 && pane.ActiveIndex < tabs.TabCount)
        {
            tabs.ActiveTabIndex = pane.ActiveIndex;
        }

        var paneId = pane.Id;
        tabs.TabChanged += (_, e) => OnTabChanged(paneId, e.NewTab);
        _paneTabs[paneId] = tabs;
        return tabs;
    }

    /// <summary>Rebuilds the pane area from the model and swaps it into the live window.</summary>
    private void RebuildPaneArea()
    {
        var row = BuildWorkspaceRow();
        _window.RemoveContent(_workspaceRow);
        _window.InsertControl(WorkspaceRowIndex, row);
        _workspaceRow = row;
        RefreshTabTitles();
    }

    /// <summary>The TabControl of the focused pane, or null if none is realised.</summary>
    private TabControl? FocusedTabs() => _paneTabs.GetValueOrDefault(_workspace.Layout.FocusedPaneId);

    /// <summary>Cycles to the next window tab in the focused pane, wrapping (Ctrl+N / Ctrl+Tab).</summary>
    private void NextWindow()
    {
        if (FocusedTabs() is { TabCount: > 1 } tabs)
        {
            tabs.ActiveTabIndex = (tabs.ActiveTabIndex + 1) % tabs.TabCount;
        }
    }

    /// <summary>Moves focus to the next pane in the split (Ctrl+O), routing input to its active tab.</summary>
    private void CyclePane()
    {
        if (_workspace.Layout.Panes.Count <= 1)
        {
            return;
        }

        _workspace.Layout.CycleFocus();
        if (FocusedTabs() is { } tabs)
        {
            _window.FocusControl(tabs);
        }

        // The input line follows focus: restore the newly focused window's draft.
        _input.Input = _drafts.GetValueOrDefault(ActiveWindowId(), string.Empty);
        RefreshTabTitles();
    }

    /// <summary>Closes the focused pane's active window (Ctrl+W). The main window can't be closed.</summary>
    private void CloseActiveWindow()
    {
        var id = ActiveWindowId();
        if (id == MainWindowId)
        {
            return;
        }

        _panes.Remove(id);
        _drafts.Remove(id);
        _workspace.CloseWindow(id);
        RebuildPaneArea();
    }

    /// <summary>Makes a window active in its hosting pane (model + view) and focuses that pane.</summary>
    private void Activate(string id)
    {
        if (!_workspace.ActivateWindow(id))
        {
            return;
        }

        if (_workspace.Layout.FindWindow(id) is { } pane &&
            _paneTabs.GetValueOrDefault(pane.Id) is { } tabs)
        {
            for (var i = 0; i < tabs.TabCount; i++)
            {
                if (tabs.TabPages[i].Tag as string == id)
                {
                    tabs.ActiveTabIndex = i;
                    break;
                }
            }
        }

        RefreshTabTitles();
    }

    /// <summary>Repaints every pane's tab headers from window titles + unread/unsent badges.</summary>
    private void RefreshTabTitles()
    {
        foreach (var tabs in _paneTabs.Values)
        {
            foreach (var page in tabs.TabPages)
            {
                if (page.Tag is string id && _workspace.FindWindow(id) is { } window)
                {
                    page.Title = TabTitles.For(window);
                }
            }
        }

        RefreshRail();
        UpdateInputChrome();
    }

    private void ReportWindowSize()
    {
        var session = _active;
        if (session is null || !session.IsConnected)
        {
            return;
        }

        // Advertise the terminal size to the server over NAWS whenever the window resizes so wrapping
        // stays correct. The compositor already redraws the UI itself on a size shift.
        _ = session.SetWindowSizeAsync(Math.Max(1, _window.Width), Math.Max(1, _window.Height));
    }

    private void SetStatus(string markup) => _statusBar.SetContent(new List<string> { markup });

    private void UpdateStatus()
    {
        var session = _active;
        if (session is null)
        {
            SetStatus($"[dim]not connected · Graphics {Escape(_capabilities.Protocol.ToString())} · ⌃P palette · ⌃Q quit[/]");
            return;
        }

        // Keep the rail's connected dot in sync with the live session state.
        if (session.SessionKey is { } key)
        {
            if (session.IsConnected)
            {
                _connectedKeys.Add(key);
            }
            else
            {
                _connectedKeys.Remove(key);
            }
        }

        var character = session.Character?.Name ?? session.World.Name;
        SetStatus(StatusBarMarkup(character, session.World.Host, session.World.Port, session.State.ToString().ToLowerInvariant()));
        _header.SetContent(new List<string> { HeaderMarkup() });
        RefreshRail();
    }

    /// <summary>
    /// The design header row: the brand affordance on the left, the active world (with its accent)
    /// in the middle, and connection/graphics/palette hints on the right.
    /// </summary>
    private string HeaderMarkup()
    {
        var brand = "[bold #00f5b7]☰ glyph·tui[/]";

        string middle;
        if (ActiveWorld() is { } active)
        {
            var hex = AccentHex(active.Accent);
            var who = active.Character is { } name ? $" [dim]· {Escape(name)}[/]" : string.Empty;
            middle = $"[{hex}]▚[/] [bold]{Escape(active.World.Name)}[/]{who}";
        }
        else
        {
            middle = "[dim]multi-world MU* workspace[/]";
        }

        var worldCount = _config.Worlds.Count;
        var connected = _connectedKeys.Count;
        var conn = worldCount > 0 ? $"{connected}/{worldCount} connected   " : string.Empty;
        var right = $"[dim]{conn}◉ LOG off   Graphics {Escape(_capabilities.Protocol.ToString())}   ⌃P palette[/]";

        return $"{brand}   {middle}          {right}";
    }

    /// <summary>
    /// The design status bar: connection state, HP/EN meters (from GMCP vitals when present),
    /// host, and the palette hint. Meters render via <see cref="Meters"/>.
    /// </summary>
    private string StatusBarMarkup(string character, string host, int port, string state)
    {
        var accent = ActiveWorld() is { } world ? AccentHex(world.Accent) : "#00f5b7";
        var parts = new List<string> { $"[{accent}]●[/] [bold]{Escape(character)}[/] [dim]{Escape(state)}[/]" };

        var hp = _stats.GetInt("hp");
        var maxhp = _stats.GetInt("maxhp");
        if (hp is not null && maxhp is > 0)
        {
            parts.Add($"[#ff5f5f]HP[/] [#ff5f5f]{Meters.Bar(hp.Value, maxhp.Value, 8)}[/] {hp}");
        }

        var mp = _stats.GetInt("mp");
        var maxmp = _stats.GetInt("maxmp");
        if (mp is not null && maxmp is > 0)
        {
            parts.Add($"[#5fafff]EN[/] [#5fafff]{Meters.Bar(mp.Value, maxmp.Value, 8)}[/] {mp}");
        }

        parts.Add($"[dim]{Escape($"{host}:{port}")}[/]");
        parts.Add("[dim]⌃P palette[/]");
        return string.Join("   ", parts);
    }

    /// <summary>Marshals an action onto the UI thread (session events fire on background threads).</summary>
    private void OnUi(Action action) => _system.EnqueueOnUIThread(action);

    private static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");

    private static SColor ToColor(Rgb rgb) => new(rgb.R, rgb.G, rgb.B, 255);

    /// <summary>
    /// Resolves the active theme: a built-in by <see cref="AppConfiguration.ThemeName"/> unless
    /// the config carries a customised inline <see cref="AppConfiguration.Theme"/>.
    /// </summary>
    private static Theme ResolveTheme(AppConfiguration config)
    {
        if (config.Theme is { } inline && !string.Equals(inline.Name, config.ThemeName, StringComparison.OrdinalIgnoreCase))
        {
            return inline;
        }

        return ThemeLibrary.Get(config.ThemeName);
    }

    public async ValueTask DisposeAsync()
    {
        _fetcher.Dispose();
        await _sessions.DisposeAsync().ConfigureAwait(false);
    }
}
