using MuClient.Core.Commands;
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
    private readonly TabControl _tabs;
    private readonly PromptControl _input;
    private readonly GmcpStats _stats = new();
    private readonly MuClient.Web.WebPageFetcher _fetcher = new();

    private readonly CommandPalette _palette;

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

        _tabs = Controls.TabControl().AddTab("Main", main).Fill().Build();
        _tabs.TabPages[0].Tag = MainWindowId;
        _tabs.TabChanged += (_, e) => OnTabChanged(e.NewTab);

        // The connection rail (worlds → characters → windows) sits left of the pane area, joined by
        // a splitter. RailModel/RailRenderer keep the projection + markup tested; this just hosts it.
        _rail = new MarkupControl(new List<string>());
        var workspaceRow = Controls.HorizontalGrid()
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Column(c => c.Width(30).Add(_rail))
            .Column(c => c.Flex(1).Add(_tabs))
            .WithSplitterAfter(0)
            .Build();

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
            .AddControl(workspaceRow)
            .AddControl(_input)
            .AddControl(_statusBar)
            .Build();

        _palette = new CommandPalette(_system, BuildCatalog, () => _active?.SessionKey, DispatchCommand);

        _window.OnResize += (_, _) => ReportWindowSize();
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.Q, () => _system.RequestExit(0));
        // Next window (Ctrl+N, plus Ctrl+Tab where the terminal reports it) and close window (Ctrl+W).
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.N, NextWindow);
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.Tab, NextWindow);
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.W, CloseActiveWindow);
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.P, () => _palette.Toggle());
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
    public string RenderSnapshot()
    {
        LoadDemoScene();

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
        var chatPane = PaneFor(chat.Id, chat.Title);
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
        RefreshTabTitles();
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
            Accent = AccentPalette[0],
            Characters =
            {
                new CharacterDefinition { Name = "Corvid" },
                new CharacterDefinition { Name = "Rookery" },
            },
        });

        _config.Worlds.Add(new WorldDefinition
        {
            Name = "Grapevine",
            Host = "grapevine.haus",
            Port = 4000,
            Accent = AccentPalette[1],
            Characters = { new CharacterDefinition { Name = "Thistle" } },
        });

        _demoActiveKey = "Aetherfall.Corvid";
        _connectedKeys.Add("Aetherfall.Corvid");
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
        var window = _workspace.RouteSpawn(target, _active?.SessionKey);
        var pane = PaneFor(window.Id, window.Title);
        pane.AppendLine(_formatter.ToMarkup(line));
        RefreshTabTitles();
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
    }

    /// <summary>The window id of the visible tab (the input line belongs to it).</summary>
    private string ActiveWindowId() => _workspace.Layout.FocusedPane.ActiveTab ?? MainWindowId;

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
            RefreshTabTitles();
            return;
        }

        switch (id)
        {
            case "layout:zoom":
            case "layout:unzoom":
                _workspace.Layout.ToggleZoom();
                break;
            case "layout:close":
                CloseActiveWindow();
                break;
            case "layout:split-right":
                PaneCommands.Apply(_workspace.Layout, PaneCommand.SplitRight);
                break;
            case "layout:split-down":
                PaneCommands.Apply(_workspace.Layout, PaneCommand.SplitDown);
                break;
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

    private void OnTabChanged(TabPage? newTab)
    {
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
        if (_workspace.FindWindow(WebWindowId) is null)
        {
            _workspace.OpenWindow(WebWindowId, title, WindowKind.Auxiliary);
        }
        else
        {
            _workspace.FindWindow(WebWindowId)!.Title = title;
        }

        var pane = PaneFor(WebWindowId, title);
        pane.SetContent(page.Lines.Select(_formatter.ToMarkup).ToList());
        Activate(WebWindowId);
        RefreshTabTitles();
    }

    /// <summary>Returns the pane control for a window, creating its tab (Tag = id) on first use.</summary>
    private MarkupControl PaneFor(string id, string title)
    {
        if (_panes.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var control = new MarkupControl(new List<string>());
        control.LinkClicked += (_, e) => OnLinkClicked(e.Url);
        _panes[id] = control;
        _tabs.AddTab(title, control, false);
        _tabs.TabPages[_tabs.TabCount - 1].Tag = id;
        return control;
    }

    /// <summary>Cycles to the next window tab, wrapping (Ctrl+N / Ctrl+Tab).</summary>
    private void NextWindow()
    {
        if (_tabs.TabCount > 1)
        {
            _tabs.ActiveTabIndex = (_tabs.ActiveTabIndex + 1) % _tabs.TabCount;
        }
    }

    /// <summary>Closes the active window tab (Ctrl+W). The main window can't be closed.</summary>
    private void CloseActiveWindow()
    {
        var index = _tabs.ActiveTabIndex;
        if (index < 0 || index >= _tabs.TabCount)
        {
            return;
        }

        if (_tabs.TabPages[index].Tag is not string id || id == MainWindowId)
        {
            return;
        }

        _tabs.RemoveTab(index);
        _panes.Remove(id);
        _drafts.Remove(id);
        _workspace.CloseWindow(id);
        RefreshTabTitles();
    }

    /// <summary>Makes a window's tab the active one in the view (fires <see cref="OnTabChanged"/>).</summary>
    private void Activate(string id)
    {
        for (var i = 0; i < _tabs.TabCount; i++)
        {
            if (_tabs.TabPages[i].Tag as string == id)
            {
                _tabs.ActiveTabIndex = i;
                return;
            }
        }
    }

    /// <summary>Repaints every tab header from its window's title + unread/unsent badges.</summary>
    private void RefreshTabTitles()
    {
        foreach (var page in _tabs.TabPages)
        {
            if (page.Tag is string id && _workspace.FindWindow(id) is { } window)
            {
                page.Title = TabTitles.For(window);
            }
        }

        RefreshRail();
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
