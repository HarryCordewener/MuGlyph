using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Input;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Theming;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Events;
using SharpConsoleUI.Layout;
using SColor = SharpConsoleUI.Color;
// Aliased rather than a plain using: SharpConsoleUI.Imaging also has a HalfBlockRenderer, which
// would collide with SharpMUTerm.Graphics'.
using PixelBuffer = SharpConsoleUI.Imaging.PixelBuffer;
using ImageScaleMode = SharpConsoleUI.Imaging.ImageScaleMode;
using static SharpMUTerm.Tui.MarkupText;

namespace SharpMUTerm.Tui;

/// <summary>
/// The top-level SharpMUTerm application on SharpConsoleUI: a status line, a tabbed set of output
/// windows (main + trigger-routed spawn windows + the web view), and a command prompt. The tab set
/// is driven by the UI-agnostic <see cref="Workspace"/> model — a single pane holding many window
/// tabs — so spawn routing and unread badges reuse the tested Core logic. Splits (via SharpConsoleUI
/// splitters) layer on this later. Background session events are marshalled onto the UI thread.
/// </summary>
internal sealed class SharpMUTermApp : IAsyncDisposable
{
    private const string MainWindowId = "main";
    private const string WebWindowId = "web";

    private readonly AppConfiguration _config;
    private readonly SessionManager _sessions = new();
    private readonly TerminalCapabilities _capabilities;
    private readonly Theme _theme;
    private readonly MarkupFormatter _formatter;
    private readonly Workspace _workspace;
    private readonly Dictionary<string, MarkupControl> _panes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);
    private readonly InputHistory _history = new();
    private bool _suppressInputChanged;

    // Per-window markup line buffer (the scrollback source of truth) and, per frozen pane, the buffer
    // length of its active window at the moment it froze — the split point between pinned scrollback and
    // the live tail. Kept here (not read back from the controls) so freeze can rebuild both regions.
    private readonly Dictionary<string, List<string>> _lines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _freezePoints = new(StringComparer.Ordinal);
    private readonly HashSet<string> _connectedKeys = new(StringComparer.Ordinal);

    private readonly ConsoleWindowSystem _system;
    private readonly Window _window;
    private readonly MarkupControl _header;
    private readonly MarkupControl _statusBar;
    private readonly MarkupControl _rail;
    private readonly MarkupControl _railSpacer = new(new List<string>());
    private readonly Dictionary<string, TabControl> _paneTabs = new(StringComparer.Ordinal);

    /// <summary>
    /// Guards <see cref="_paneTabs"/>. Everything else touches it on the UI thread, but a mouse frame
    /// arrives on the driver's input thread and has to read it to locate the panes — enumerating it
    /// while a rebuild clears and refills it would throw.
    /// </summary>
    private readonly object _paneTabsLock = new();
    private readonly PromptControl _input;
    private readonly GmcpStats _stats = new();
    private readonly SharpMUTerm.Web.WebPageFetcher _fetcher = new();
    private readonly WebImageLoader _imageLoader = new();

    /// <summary>
    /// The web page currently in the web tab, its markup lines, and the images that decoded — keyed
    /// by index into <see cref="SharpMUTerm.Web.WebPage.Images"/>. Together these are everything
    /// <see cref="BuildWebContent"/> needs; an empty image map means the tab is the plain text-mode
    /// page it has always been.
    /// </summary>
    private SharpMUTerm.Web.WebPage? _webPage;
    private IReadOnlyList<string> _webMarkup = Array.Empty<string>();
    private readonly Dictionary<int, PixelBuffer> _webImages = new();

    /// <summary>
    /// Cancels the in-flight image fetches of a superseded page. Loading is per-page and a new
    /// navigation invalidates the old one's images outright.
    /// </summary>
    private CancellationTokenSource? _webImageCts;

    private readonly CommandPalette _palette;
    private readonly SettingsOverlay _settings;

    /// <summary>Per-world accents when a world hasn't set its own, keyed by position.</summary>
    internal static readonly TerminalColor[] AccentPalette =
    {
        TerminalColor.FromRgb(0x00, 0xf5, 0xb7), // teal
        TerminalColor.FromRgb(0xff, 0x9f, 0x1c), // amber
        TerminalColor.FromRgb(0x9d, 0x7c, 0xff), // violet
        TerminalColor.FromRgb(0x5f, 0xaf, 0xff), // sky
    };

    private WorldSession? _active;
    private WorldDefinition? _pendingWorld;
    private string? _demoActiveKey;
    private readonly bool _headless;
    private bool _railCollapsed;
    private bool _showTimestamps;
    private bool _prefixArmed;
    private bool _moveMode;
    private string? _moveWindowId;
    private string? _moveTargetPaneId;
    private Edge? _moveEdge;
    private readonly Dictionary<string, char> _moveLetters = new(StringComparer.Ordinal);

    /// <summary>Assembles pane drag-and-drop out of the driver's raw mouse frames (see PaneDragTracker).</summary>
    private readonly PaneDragTracker _paneDrag = new();

    /// <summary>The pane the live mouse drag is hovering, and the edge it would split — null when idle.</summary>
    private string? _dragTargetPaneId;
    private Edge? _dragEdge;
    private bool _dragActive;

    /// <summary>The rail + pane-area row currently in the window (index 1). Swapped on layout change.</summary>
    private IWindowControl _workspaceRow = null!;

    /// <summary>The window index the workspace row sits at (after the sticky-top header).</summary>
    private const int WorkspaceRowIndex = 1;

    public SharpMUTermApp(AppConfiguration config, TerminalCapabilities capabilities, IConsoleDriver? driver = null)
    {
        _config = config;
        _capabilities = capabilities;
        _theme = ResolveTheme(config);
        _formatter = new MarkupFormatter(_theme);

        // Resume the last session's workspace (panes/windows/focus) when the config carries one;
        // otherwise start with a single main window. Real startup and the demo share this path.
        _workspace = ResumeOrNew(config);

        // A headless driver renders to a captured buffer (for snapshots/CI) instead of a real
        // terminal; hide the desktop panels so those frames are deterministic.
        var headless = driver is HeadlessConsoleDriver;
        _headless = headless;
        var options = new ConsoleWindowSystemOptions(
            ShowTopPanel: !headless,
            ShowBottomPanel: !headless,
            EnableAnimations: !headless);
        _system = new ConsoleWindowSystem(driver ?? new NetConsoleDriver(RenderMode.Buffer), options);

        _header = Controls.Markup(HeaderMarkup()).StickyTop().Build();
        _header.LinkClicked += (_, e) => OnLinkClicked(e.Url);
        _header.BackgroundColor = ToColor(_theme.StatusBackground); // the menu bar is a distinct chrome band
        // Keep the clickable brand button on-brand (violet) instead of the driver's default link highlight.
        var brand = AccentPalette[2];
        _header.FocusedLinkBackgroundColor = ToColor(new Rgb(brand.R, brand.G, brand.B));
        _header.FocusedLinkForegroundColor = ToColor(_theme.Resolve(TerminalColor.Default, isBackground: true));

        var main = new MarkupControl(new List<string>());
        main.LinkClicked += (_, e) => OnLinkClicked(e.Url);
        _panes[MainWindowId] = main;

        // The connection rail (worlds → characters → windows) sits left of the pane area, joined by
        // a splitter. RailModel/RailRenderer keep the projection + markup tested; this just hosts it.
        _rail = new MarkupControl(new List<string>());

        // The pane area renders the workspace's split tree (one TabControl per leaf pane). It's built
        // from the model and rebuilt whenever the layout changes; the initial row goes into the window.
        _workspaceRow = BuildWorkspaceRow();

        // The input row reads as one solid full-width band: the PromptControl fills the field area to
        // the right edge with InputBackgroundColor on its own, and we paint the prompt cells with the
        // same colour (via PromptMarkup) so the label at the left carries the band too — no gap.
        var inputBg = ToColor(new Rgb(0x33, 0x39, 0x4c));

        // Draft-safe history is ours (InputHistory), not the framework's: ↑ stashes the live draft,
        // ↓ past the newest entry restores it. So the built-in recall is off.
        _input = Controls.Prompt(PromptMarkup("›"))
            .WithHistory(false)
            .WithInputBackgroundColor(inputBg)
            .WithInputFocusedBackgroundColor(inputBg)
            .StickyBottom()
            .Build();
        _input.Entered += (_, text) => OnCommandEntered(text);
        _input.InputChanged += (_, text) => OnInputChanged(text);

        _statusBar = Controls.Markup("[dim]not connected[/]").StickyBottom().Build();

        var bg = ToColor(_theme.Resolve(TerminalColor.Default, isBackground: true));
        var fg = ToColor(_theme.Resolve(TerminalColor.Default, isBackground: false));

        _window = new WindowBuilder(_system)
            .WithTitle("SharpMUTerm — MU* client")
            .Maximized()
            .Frameless() // no outer chrome — the workspace fills the whole screen for maximum room
            .WithColors(fg, bg)
            .AddControl(_header)
            .AddControl(_workspaceRow)
            .AddControl(_input)
            .AddControl(_statusBar)
            .Build();

        _palette = new CommandPalette(_system, BuildCatalog, () => _active?.SessionKey, DispatchCommand);
        _settings = new SettingsOverlay(_system, SaveConfiguration);

        _window.OnResize += (_, _) =>
        {
            ReportWindowSize();
            _header.SetContent(new List<string> { HeaderMarkup() }); // re-align the status cluster to the new width
            SyncInputWidth(); // keep the input band spanning the full row after a resize
        };

        // The PromptControl otherwise measures to its content width, leaving the band short of the right
        // edge; pinning Width to the window makes the field fill (and its background paint) run edge-to-edge.
        SyncInputWidth();
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.Q, () => _system.RequestExit(0));
        // Next window (Ctrl+N, plus Ctrl+Tab where the terminal reports it) and close window (Ctrl+W).
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.N, NextWindow);
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.Tab, NextWindow);
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.W, CloseActiveWindow);
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.O, CyclePane);
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.P, ToggleMenu);
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.B, ArmPrefix);
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.F, ToggleFreeze);
        _window.PreviewKeyPressed += OnWindowKey;
        // Pane drag-and-drop listens at the driver, not at a control: SharpConsoleUI delivers mouse
        // frames to the control that was pressed (it captures on Button1Pressed), so a control-level
        // handler would only ever see the *source* pane. The driver stream carries every frame in
        // desktop cells, which is exactly what a drag between panes needs.
        _system.ConsoleDriver.MouseEvent += OnDriverMouseEvent;
        RegisterSettingsShortcuts();
        _system.AddWindow(_window);
    }

    /// <summary>Captures the current workspace (panes/windows/focus) so it can be persisted and resumed.</summary>
    public WorkspaceState CaptureSession() => WorkspaceState.Capture(_workspace);

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
        // Workspace-state variants (rail collapsed / ⌃B armed) apply before the demo scene renders.
        if (string.Equals(view, "collapsed", StringComparison.OrdinalIgnoreCase))
        {
            _railCollapsed = true;
        }
        else if (string.Equals(view, "prefix", StringComparison.OrdinalIgnoreCase))
        {
            _prefixArmed = true;
        }
        else if (string.Equals(view, "timestamps", StringComparison.OrdinalIgnoreCase))
        {
            _showTimestamps = true;
        }

        LoadDemoScene();

        // Activate the Chat spawn window so its dim "⇱ capture …" header renders under the tab strip.
        if (string.Equals(view, "spawn", StringComparison.OrdinalIgnoreCase))
        {
            _workspace.ActivateWindow(Workspace.SpawnWindowId("Chat"));
            RebuildPaneArea();
        }

        // Split the workspace: Aardwolf (main) stays in the left pane, the Chat window moves to the
        // new right pane (split moves the focused pane's non-active tabs across, per the design).
        if (string.Equals(view, "split", StringComparison.OrdinalIgnoreCase))
        {
            PaneCommands.Apply(_workspace.Layout, PaneCommand.SplitRight);
            RebuildPaneArea();
        }

        // Freeze the focused pane so the pinned-scrollback / live-tail split + FROZEN bar render, then
        // feed a couple of lines that land in the live tail below the bar.
        if (string.Equals(view, "freeze", StringComparison.OrdinalIgnoreCase))
        {
            ToggleFreeze();
            var parser = new AnsiParser();
            foreach (var text in new[]
            {
                "\x1b[0;32mA courier\x1b[0m jogs in from the east, breathless.",
                "\x1b[0;37mThe courier says, 'Word from the northern watch!'\x1b[0m",
            })
            {
                foreach (var line in parser.Feed(text + "\n"))
                {
                    AppendWindowLine(MainWindowId, _formatter.ToMarkup(line, Stamp()));
                }
            }
        }

        // Move mode needs a split to have multiple target panes; set it up then arm move mode.
        if (string.Equals(view, "move", StringComparison.OrdinalIgnoreCase))
        {
            PaneCommands.Apply(_workspace.Layout, PaneCommand.SplitRight);
            RebuildPaneArea();
            EnterMoveMode();

            // Drive the real key handler so the frame shows move mode as a user would leave it after
            // picking a target pane and an edge: "b", then ←.
            HandleMoveKey(new KeyPressedEventArgs(new ConsoleKeyInfo('b', ConsoleKey.B, false, false, false), false));
            HandleMoveKey(new KeyPressedEventArgs(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false), false));
        }

        // Mouse drag: split, lay the frame out so the panes have real bounds, then drive an actual
        // press + drag through the headless driver's mouse event. Nothing here fakes the preview —
        // it is whatever the pointer path produces, which is what makes this frame worth looking at.
        if (string.Equals(view, "drag", StringComparison.OrdinalIgnoreCase))
        {
            PaneCommands.Apply(_workspace.Layout, PaneCommand.SplitRight);
            RebuildPaneArea();
            RenderFrame();
            SimulateSnapshotDrag();

            // The frame above left the driver's front buffer populated, so the closing render would
            // emit only the cells that changed. The headless driver ignores InvalidateFrontBuffer
            // (the interface default is an empty body), but re-initialising it builds a fresh buffer,
            // which together with a full repaint makes the closing render a whole frame again.
            if (_system.ConsoleDriver is HeadlessConsoleDriver headlessDriver)
            {
                headlessDriver.Initialize(_system);
            }

            _system.ForceFullRepaint();
        }

        // History-recall state: seed a couple of sent commands, then recall the newest so the input
        // shows a recalled line and the gutter shows the "history · ↓ back to draft" affordance.
        if (string.Equals(view, "history", StringComparison.OrdinalIgnoreCase))
        {
            _history.Add("look");
            _history.Add("say Well met, traveller.");
            if (_history.Recall("wh") is { } recalled)
            {
                ApplyRecalledText(recalled);
            }
        }

        // The ☰ menu (command surface): optionally split first so the menu is shown over a two-pane
        // workspace, then open the palette so its modal paints over the demo scene.
        if (string.Equals(view, "menu", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(view, "menu-split", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(view, "menu-split", StringComparison.OrdinalIgnoreCase))
            {
                PaneCommands.Apply(_workspace.Layout, PaneCommand.SplitRight);
                RebuildPaneArea();
            }

            _palette.Toggle();
        }

        // Settings screens (composed-control or markup — SettingsView hands back a control factory
        // either way) open over the workspace for their --view name. A "<name>-edit" view opens the
        // same screen and then drives real keys into it, so the frame shows a field genuinely mid-edit
        // rather than a hand-drawn impression of one.
        var editing = view is not null && view.EndsWith(EditViewSuffix, StringComparison.OrdinalIgnoreCase);
        var screenView = editing ? view![..^EditViewSuffix.Length] : view;
        if (screenView is not null && SettingsView(screenView) is { } screen)
        {
            _settings.OpenForSnapshot(screen.Key, screen.Open());
            if (editing)
            {
                foreach (var key in EditSnapshotKeys(screenView))
                {
                    _settings.SimulateKey(key);
                }
            }
        }

        SyncInputWidth(); // the window now carries the snapshot size, so the band fills its full width
        return RenderFrame();
    }

    /// <summary>
    /// Renders exactly one frame, synchronously, inline on this thread, and returns it as ANSI.
    /// ForceRender() performs a single render cycle (bypassing the frame-rate limiter) with no Run()
    /// loop, no driver Initialize/Start, and no OnShown pass — a freshly-added window is dirty and
    /// paints on the first call. The HeadlessConsoleDriver writes the composited frame straight to the
    /// console, so Console.Out is redirected for the duration of that one call and what it wrote is
    /// kept. (An earlier Run()-on-a-worker-thread approach raced the input+render pump and hung/OOM'd.)
    /// A frame also arranges the layout, so control bounds are only real after one has been rendered.
    /// </summary>
    private string RenderFrame()
    {
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

    /// <summary>
    /// Drives a genuine pane drag through the headless driver for the <c>drag</c> snapshot: primary
    /// button down on the first pane's tab strip, then a drag frame over the second pane's left edge.
    /// The button is deliberately left down so the frame captures the live drop preview. Requires a
    /// frame to have been rendered already, so the panes have real bounds to hit.
    /// </summary>
    private void SimulateSnapshotDrag()
    {
        if (_system.ConsoleDriver is not HeadlessConsoleDriver driver)
        {
            return;
        }

        var panes = _workspace.Layout.Panes;
        var surface = PaneSnapshot();
        if (panes.Count < 2 ||
            surface.RectOf(panes[0].Id) is not { } source ||
            surface.RectOf(panes[1].Id) is not { } target)
        {
            return;
        }

        driver.SimulateMouseEvent(
            new List<MouseFlags> { MouseFlags.Button1Pressed },
            new System.Drawing.Point(source.X + 2, source.Y));

        driver.SimulateMouseEvent(
            new List<MouseFlags> { MouseFlags.Button1Pressed, MouseFlags.Button1Dragged, MouseFlags.ReportMousePosition },
            new System.Drawing.Point(target.X + 1, target.Y + (target.Height / 2)));
    }

    /// <summary>
    /// Feeds representative MU* output into the windows the resumed session already opened, for
    /// snapshots/demos. The workspace structure (main + Chat, panes, focus) comes from the config's
    /// resumed <c>LastSession</c> — this only supplies scrollback, which is never persisted.
    /// </summary>
    private void LoadDemoScene()
    {
        InitDemoRuntimeState();

        var parser = new AnsiParser();
        void Feed(string windowId, string ansiLine)
        {
            foreach (var line in parser.Feed(ansiLine + "\n"))
            {
                AppendWindowLine(windowId, _formatter.ToMarkup(line, Stamp()));
            }
        }

        Feed(MainWindowId, "\x1b[1;36mThe Grand Plaza\x1b[0m");
        Feed(MainWindowId, "\x1b[0;37mA marble fountain bubbles at the centre of a wide plaza. Merchants\x1b[0m");
        Feed(MainWindowId, "\x1b[0;37mhawk their wares beneath striped awnings.\x1b[0m");
        Feed(MainWindowId, "\x1b[0;32mA town guard\x1b[0m stands watch by the northern gate.");

        // A line with clickable MXP-style exits (rendered as [link=…] spans).
        var exits = new StyledLine(new[]
        {
            new StyledSpan("Exits: ", TextStyle.Default),
            Link("north"),
            new StyledSpan("  ", TextStyle.Default),
            Link("east"),
        });
        AppendWindowLine(MainWindowId, _formatter.ToMarkup(exits, Stamp()));

        // A trigger-highlighted line: carries a left-rule colour, so it gets the 2-col rule treatment.
        var highlighted = new StyledLine(
            new[] { new StyledSpan("[public] Rivane: to the crypt, then!", TextStyle.Default) },
            TerminalColor.FromRgb(0x00, 0xf5, 0xb7));
        AppendWindowLine(MainWindowId, _formatter.ToMarkup(highlighted, Stamp()));

        // The Chat spawn window already exists (opened by the resumed session); feed its backlog and
        // leave it in the background with unread, as if lines arrived while another tab was focused.
        var chatId = Workspace.SpawnWindowId("Chat");
        PaneContentFor(chatId, "Chat");
        var chatParser = new AnsiParser();
        foreach (var text in new[]
        {
            "\x1b[1;35m[Chat]\x1b[0m Rivane: anyone up for the crypt run?",
            "\x1b[1;35m[Chat]\x1b[0m Bob: aye, meet me at the gate",
        })
        {
            foreach (var line in chatParser.Feed(text + "\n"))
            {
                AppendWindowLine(chatId, _formatter.ToMarkup(line, Stamp()));
            }

            _workspace.NoteActivity(chatId); // each line accrues unread while Chat is in the background
        }

        _statusIdentity = ("Corvid", "aetherfall.mux", 4201, "connected");
        _statusBar.SetContent(new List<string> { StatusBarMarkup("Corvid", "aetherfall.mux", 4201, "connected") });
        _header.SetContent(new List<string> { HeaderMarkup() });
        _input.Input = "say hello there";
        RebuildPaneArea(); // realise the Chat spawn tab, then refresh badges
    }

    /// <summary>
    /// Rebuilds the workspace from a saved session, or a single main window when there's none. Corrupt
    /// state falls back to a fresh workspace rather than failing to start.
    /// </summary>
    private static Workspace ResumeOrNew(AppConfiguration config)
    {
        if (config.LastSession is { Windows.Count: > 0 } state)
        {
            try
            {
                return state.Restore();
            }
            catch
            {
                // A saved session that no longer deserialises shouldn't block startup — start fresh.
            }
        }

        return new Workspace(MainWindowId, "Main");
    }

    /// <summary>Sets the demo's focused/connected character from the resumed config (snapshot chrome).</summary>
    private void InitDemoRuntimeState()
    {
        if (_config.Worlds.FirstOrDefault() is { } world &&
            world.Characters.FirstOrDefault() is { } character)
        {
            _demoActiveKey = $"{world.Name}.{character.Name}";
            _connectedKeys.Add(_demoActiveKey);
        }
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

        session.PrintSystem($"*** SharpMUTerm — theme '{_theme.Name}', graphics: {_capabilities.Protocol}.");

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

    /// <summary>The optional output-view timestamp gutter, or null when the column is off. Headless
    /// snapshots use a fixed clock so golden images stay stable.</summary>
    private string? Stamp() => _showTimestamps ? (_headless ? "09:24" : DateTime.Now.ToString("HH:mm")) : null;

    /// <summary>
    /// Appends one already-formatted markup line to a window: records it in the scrollback buffer and,
    /// if the window has a live control, paints it. A frozen pane's live control is its tail region, so
    /// new lines land below the <c>▲ FROZEN ⌃F</c> bar while the pinned scrollback stays put.
    /// </summary>
    private void AppendWindowLine(string windowId, string markup)
    {
        if (!_lines.TryGetValue(windowId, out var buffer))
        {
            _lines[windowId] = buffer = new List<string>();
        }

        buffer.Add(markup);

        // Cap the UI-side buffer at the configured scrollback so a long session doesn't grow without
        // bound (and freeze rebuilds stay cheap); shift the freeze point down by whatever we trimmed.
        var cap = Math.Max(1, _config.ScrollbackLines);
        if (buffer.Count > cap)
        {
            var excess = buffer.Count - cap;
            buffer.RemoveRange(0, excess);
            if (_freezePoints.TryGetValue(windowId, out var point))
            {
                _freezePoints[windowId] = Math.Max(0, point - excess);
            }
        }

        if (_panes.TryGetValue(windowId, out var control))
        {
            control.AppendLine(markup);
        }
    }

    /// <summary>The trigger pattern that routes to a spawn <paramref name="target"/>, for its capture line.</summary>
    private string? CaptureFor(string target) =>
        _config.TriggerSets.SelectMany(s => s.Triggers)
            .FirstOrDefault(t => string.Equals(t.Actions.SpawnTarget, target, StringComparison.Ordinal))
            ?.Pattern;

    /// <summary>Appends a line to a window's pane and badges it unread when it isn't the visible tab.</summary>
    private void OnLine(string windowId, StyledLine line)
    {
        AppendWindowLine(windowId, _formatter.ToMarkup(line, Stamp()));

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
        window.CapturePattern ??= CaptureFor(target); // label the pane with the trigger that feeds it
        window.OwnerLabel ??= _active?.Character?.Name ?? _workspace.FindWindow(MainWindowId)?.Title;
        PaneContentFor(window.Id, window.Title); // ensure the live control exists before buffering
        AppendWindowLine(window.Id, _formatter.ToMarkup(line, Stamp()));

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
        // The entered command clears this window's draft and its unsent-input marker, and joins
        // the draft-safe history so ↑/↓ can recall it without clobbering a future draft.
        _history.Add(command);
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

        // `/graphics` reports where the degradation chain settled and, when it degraded, why — so a
        // missing picture is an explanation rather than a mystery.
        if (command.Trim().Equals("/graphics", StringComparison.OrdinalIgnoreCase))
        {
            // Appended to the window rather than routed through the session, so it still answers
            // when nothing is connected — which is exactly when someone is checking their terminal.
            var report = InlineImagePolicy.Describe(_capabilities, WebGraphicsSurface());
            AppendWindowLine(windowId, $"[dim]*** Graphics: {Escape(report)}.[/]");
            return;
        }

        _ = _active?.SendUserInputAsync(command);
    }

    /// <summary>Tracks the per-window input draft and the <c>✎</c> unsent-input marker as you type.</summary>
    private void OnInputChanged(string text)
    {
        // Programmatic recall (setting _input.Input) also raises InputChanged; skip our draft/history
        // bookkeeping for it. A genuine keystroke while recalling re-bases the recalled line as the draft.
        if (_suppressInputChanged)
        {
            return;
        }

        if (_history.IsRecalling)
        {
            _history.Rebase();
            UpdateInputChrome();
        }

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
    /// Handles ↑/↓ as draft-safe history recall on a single-line input. Multi-line drafts keep the
    /// arrows for cursor movement, and ↓ at the live draft is left to the control. Returns whether the
    /// key was consumed.
    /// </summary>
    private bool TryRecallKey(KeyPressedEventArgs e)
    {
        if (_input.Input.Contains('\n'))
        {
            return false;
        }

        string? text;
        switch (e.KeyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                text = _history.Recall(_input.Input);
                break;
            case ConsoleKey.DownArrow:
                if (!_history.IsRecalling)
                {
                    return false;
                }

                text = _history.Forward();
                break;
            default:
                return false;
        }

        e.Handled = true;
        if (text is not null)
        {
            ApplyRecalledText(text);
        }

        return true;
    }

    /// <summary>Puts a recalled entry into the input without tripping draft/history bookkeeping.</summary>
    private void ApplyRecalledText(string text)
    {
        _suppressInputChanged = true;
        try
        {
            _input.Input = text;
        }
        finally
        {
            _suppressInputChanged = false;
        }

        UpdateInputChrome();
    }

    /// <summary>
    /// Refreshes the input region: the character-bound prompt (<c>Corvid@Aetherfall ›</c>) and the
    /// status bar (which carries the live character count now that the gutter is gone).
    /// </summary>
    private void UpdateInputChrome()
    {
        var session = _active;
        var character = session?.Character?.Name ?? (ActiveWorld() is { } aw ? aw.Character : null);
        var world = session?.World.Name ?? ActiveWorld()?.World.Name;
        _input.Prompt = PromptMarkup(StatusFormatter.CharacterPrompt(character, world));
        RefreshStatusBar();
    }

    /// <summary>
    /// The input band's background hex — shared by the field fill (<c>WithInputBackgroundColor</c>) and
    /// the prompt cells (<see cref="PromptMarkup"/>) so the input row reads as one solid full-width band.
    /// Keep in sync with the <c>inputBg</c> RGB in the constructor.
    /// </summary>
    private const string InputBandHex = "#33394c";

    /// <summary>
    /// Wraps the prompt label so its cells carry the band background. The PromptControl already fills
    /// the field to the right edge with the same colour, so painting the label to match makes the whole
    /// row a continuous band with no gap at the prompt. Brackets in names are escaped to block injection.
    /// </summary>
    private static string PromptMarkup(string prompt) =>
        $"[on {InputBandHex}]{prompt.Replace("[", "[[").Replace("]", "]]")}[/]";

    /// <summary>
    /// Pins the input control's width to the window so the field (and its background fill) runs to the
    /// right edge — otherwise the PromptControl measures to content width and the band stops mid-row.
    /// </summary>
    private void SyncInputWidth() => _input.Width = HeaderWidth();

    /// <summary>The active connection's status-bar identity (character/host/port/state), or null.</summary>
    private (string Character, string Host, int Port, string State)? _statusIdentity;

    /// <summary>Repaints the connection status bar from the stored identity, folding in the live char
    /// count. A no-op while a transient status (move-mode prompt) owns the bar or nothing's connected.</summary>
    private void RefreshStatusBar()
    {
        if (_moveMode || _statusIdentity is not { } id)
        {
            return;
        }

        SetStatus(StatusBarMarkup(id.Character, id.Host, id.Port, id.State));
    }

    /// <summary>The <c>world.character</c> key of the character whose windows the rail expands.</summary>
    private string? ActiveCharacterKey() => _active?.SessionKey ?? _demoActiveKey;

    /// <summary>
    /// One settings screen: the F-key that toggles it, the <c>--view</c> names that select it for a
    /// snapshot, and the factory that opens it — a fresh <see cref="SettingsSession"/> (its own cursor
    /// and undo log) plus the control factory that renders that session.
    /// </summary>
    private readonly record struct SettingsScreen(ConsoleKey Key, string[] Views, Func<ScreenBinding> Open);

    /// <summary>
    /// The F2–F9 settings screens, in F-key order. Both the global shortcuts and the <c>--view</c>
    /// snapshot lookup read this one table, so a screen can't be bound to a key without also being
    /// reachable by name. Each control is built on demand from live config by its pure renderer, so
    /// re-opening always reflects current state, and every screen hands back a composed tree of real
    /// panels.
    /// </summary>
    private IReadOnlyList<SettingsScreen> SettingsScreens() => new SettingsScreen[]
    {
        new(ConsoleKey.F2, new[] { "triggers" }, TriggersScreen),
        new(ConsoleKey.F3, new[] { "aliases" }, AliasesScreen),
        new(ConsoleKey.F4, new[] { "keypad" }, KeypadScreen),
        new(ConsoleKey.F5, new[] { "worlds", "settings" }, WorldsScreen),
        new(ConsoleKey.F6, new[] { "timers" }, TimersScreen),
        new(ConsoleKey.F7, new[] { "textansi" }, TextAnsiScreen),
        new(ConsoleKey.F8, new[] { "input" }, InputSpellcheckScreen),
        new(ConsoleKey.F9, new[] { "logging" }, CharacterLoggingScreen),
    };

    /// <summary>
    /// Binds each screen's F-key to the full-screen settings overlay. Esc / the same F-key closes.
    /// </summary>
    private void RegisterSettingsShortcuts()
    {
        foreach (var screen in SettingsScreens())
        {
            var (key, open) = (screen.Key, screen.Open);
            _system.RegisterGlobalShortcut((ConsoleModifiers)0, key, () => _settings.Toggle(key, open));
        }
    }

    /// <summary>
    /// Persists the configuration the settings screens edit — the ⏎ Save action. The workspace layout
    /// is captured alongside it so a save never rolls back the resumed session; a failed write is
    /// swallowed for the same reason startup's is (the config is a convenience, not the session).
    /// </summary>
    private void SaveConfiguration()
    {
        try
        {
            _config.LastSession = CaptureSession();
            ConfigurationStore.Save(ConfigurationStore.DefaultPath, _config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            SetStatus($"[red]could not save settings:[/] {Escape(ex.Message)}");
        }
    }

    /// <summary>Distinct spawn-window targets referenced by any trigger (for the F2 route-to list).</summary>
    private IReadOnlyList<string> SpawnTargets() =>
        _config.TriggerSets.SelectMany(s => s.Triggers)
            .Select(t => t.Actions.SpawnTarget)
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Every configured macro across all trigger sets (for the F4 keypad/hotkey list).</summary>
    private IReadOnlyList<Macro> Macros() => _config.TriggerSets.SelectMany(s => s.Macros).ToList();

    /// <summary>Index of the world hosting the active character (0 when none).</summary>
    private int ActiveWorldIndex()
    {
        var key = ActiveCharacterKey();
        for (var i = 0; i < _config.Worlds.Count; i++)
        {
            var world = _config.Worlds[i];
            if (world.Name == key || world.Characters.Any(c => $"{world.Name}.{c.Name}" == key))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Index of the active character within its world (0 when none).</summary>
    private int ActiveCharacterIndex()
    {
        var key = ActiveCharacterKey();
        var world = _config.Worlds.ElementAtOrDefault(ActiveWorldIndex());
        if (world is not null)
        {
            for (var i = 0; i < world.Characters.Count; i++)
            {
                if ($"{world.Name}.{world.Characters[i].Name}" == key)
                {
                    return i;
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// The logging settings the status bar reports on: the active character's. With nothing connected
    /// there is no session being logged, so the bar reads the defaults (<c>LOG off</c>) rather than some
    /// other character's format — the settings themselves are edited on F5, per character, where the
    /// character they belong to is on screen.
    /// </summary>
    private LoggingSettings ActiveLogging()
    {
        if (ActiveCharacterKey() is null)
        {
            return new LoggingSettings();
        }

        var world = _config.Worlds.ElementAtOrDefault(ActiveWorldIndex());
        return world?.Characters.ElementAtOrDefault(ActiveCharacterIndex())?.Logging ?? new LoggingSettings();
    }

    /// <summary>
    /// Opens the F5 Worlds &amp; Characters screen: four panes (worlds → characters → the selected
    /// character's trigger sets → the selected world's security checkboxes), seeded on whatever is
    /// connected so the screen opens where the user already is.
    /// </summary>
    private ScreenBinding WorldsScreen() => WorldsScreen(WorldsScreenRenderer.FKey, onCharacters: false);

    /// <summary>
    /// F9 opens the same screen, focused on the character pane. Logging is per character and now lives
    /// in that character's form, so the key that used to open a Logging screen of its own is kept as a
    /// second door into where the setting moved rather than retired: an F-key is muscle memory, and one
    /// that had quietly stopped doing anything would be worse than the screen it replaced.
    /// <para>
    /// It is a seeding difference and nothing more — the same renderer, the same session shape, the same
    /// undo log — so there is no second surface to keep in step. The header is told which key opened it,
    /// so the screen offers F9 to close what F9 opened.
    /// </para>
    /// </summary>
    private ScreenBinding CharacterLoggingScreen() =>
        WorldsScreen(WorldsScreenRenderer.LogFKey, onCharacters: true);

    private ScreenBinding WorldsScreen(string fkey, bool onCharacters)
    {
        // SelectionIn, not CursorIn: both list panes end in their own buttons, and the cursor has to
        // leave the list to press one. The *selection* is what the detail column and the delete buttons
        // are about, and it stays on the row the user was looking at.
        var session = new SettingsSession(selection => WorldsScreenRenderer.Model(
            _config.Worlds,
            _config.TriggerSets,
            selection.SelectionIn(WorldsScreenRenderer.WorldsPane),
            selection.SelectionIn(WorldsScreenRenderer.CharactersPane)));
        session.Selection.Seed(WorldsScreenRenderer.WorldsPane, ActiveWorldIndex());
        session.Selection.Seed(WorldsScreenRenderer.CharactersPane, ActiveCharacterIndex());
        if (onCharacters)
        {
            session.Selection.FocusPane(WorldsScreenRenderer.CharactersPane);
        }

        return new ScreenBinding(session, () => WorldsScreenView.Build(
            _config.Worlds,
            _config.TriggerSets,
            session.Selection.SelectionIn(WorldsScreenRenderer.WorldsPane),
            session.Selection.SelectionIn(WorldsScreenRenderer.CharactersPane),
            _system.DesktopDimensions.Width,
            session.Focus(),
            fkey));
    }

    /// <summary>
    /// Opens the F2 Triggers &amp; spawn routing screen: the rule list, then the rule's toggles.
    /// <see cref="ScreenSelection.SelectionIn"/>, not <c>CursorIn</c> — the list pane ends in its own
    /// buttons, and the cursor has to leave the list to press one. The selection is what the editor
    /// pane and the <c>[[- del]]</c> row are about, and it stays on the rule the user was looking at.
    /// </summary>
    private ScreenBinding TriggersScreen()
    {
        var session = new SettingsSession(selection =>
            TriggersScreenRenderer.Model(_config.TriggerSets, selection.SelectionIn(0), SpawnTargets()));

        return new ScreenBinding(session, () => TriggersScreenView.Build(
            _config.TriggerSets,
            session.Selection.SelectionIn(0),
            SpawnTargets(),
            _system.DesktopDimensions.Width,
            session.Focus()));
    }

    /// <summary>Opens the F3 Aliases screen: the alias list, then the alias's toggles.</summary>
    private ScreenBinding AliasesScreen()
    {
        var session = new SettingsSession(selection =>
            AliasesScreenRenderer.Model(_config.TriggerSets, selection.SelectionIn(0)));

        return new ScreenBinding(session, () => AliasesScreenView.Build(
            _config.TriggerSets, session.Selection.SelectionIn(0), _system.DesktopDimensions.Width, session.Focus()));
    }

    /// <summary>
    /// Opens the F4 Keypad &amp; hotkeys screen: one pane, the binding list. The trigger sets go in
    /// alongside the flattened macro list because a binding's home is a set — the flattened list alone
    /// cannot say which one <c>[[+ binding]]</c> should add to.
    /// </summary>
    private ScreenBinding KeypadScreen()
    {
        var session = new SettingsSession(selection =>
            KeypadScreenRenderer.Model(Macros(), _config.TriggerSets, selection.SelectionIn(0)));

        return new ScreenBinding(session, () => KeypadScreenView.Build(
            Macros(),
            _config.TriggerSets,
            session.Selection.SelectionIn(0),
            _system.DesktopDimensions.Width,
            session.Focus()));
    }

    /// <summary>Opens the F6 Timers screen: the timer list, then the timer's toggles.</summary>
    private ScreenBinding TimersScreen()
    {
        var session = new SettingsSession(selection =>
            TimersScreenRenderer.Model(_config.TriggerSets, selection.SelectionIn(0)));

        return new ScreenBinding(session, () => TimersScreenView.Build(
            _config.TriggerSets, session.Selection.SelectionIn(0), _system.DesktopDimensions.Width, session.Focus()));
    }

    /// <summary>Opens the F7 Text &amp; ANSI screen, bound to the app's text preferences.</summary>
    private ScreenBinding TextAnsiScreen() =>
        OptionsScreen(() => OptionsScreenRenderer.TextAnsiScreen(_config.Text));

    /// <summary>Opens the F8 Input &amp; spellcheck screen, bound to the app's input preferences.</summary>
    private ScreenBinding InputSpellcheckScreen() =>
        OptionsScreen(() => OptionsScreenRenderer.InputSpellcheckScreen(_config.Input));

    /// <summary>
    /// The shared open path for the single-list option screens (F7/F8). <paramref name="screen"/> is
    /// re-projected from config on every key, so a flipped checkbox shows up in both the row it lives
    /// on and the model the next keystroke navigates.
    /// </summary>
    private ScreenBinding OptionsScreen(Func<OptionsScreenRenderer.OptionsScreen> screen)
    {
        var session = new SettingsSession(_ => OptionsScreenRenderer.Model(screen()));
        return new ScreenBinding(session, () => OptionsScreenView.Build(
            screen(), _system.DesktopDimensions.Width, session.Focus()));
    }

    /// <summary>The <c>--view</c> suffix that opens a settings screen with a field being typed into.</summary>
    private const string EditViewSuffix = "-edit";

    /// <summary>
    /// The keys a <c>&lt;name&gt;-edit</c> snapshot drives into a freshly opened screen. ⏎ opens the
    /// focused row's first field — which on every list screen is now its <em>name</em> — ⇥ commits it
    /// and steps to the next, and the rest is typing. Two screens walk further than the first field,
    /// because a still frame should land on the thing that screen's editing actually added: F5 rewrites
    /// a host's suffix ("no way to change a host" is the gap the whole mode closes), and F2 steps on to
    /// its route group and moves the dot, which is the only way to see that a radio list is live rather
    /// than a report. The <c>logging</c> view opens F5 on the character pane, so it steps twice more to
    /// reach the log format — past the name and the on-connect line — because the character's log is
    /// the whole reason that view exists.
    /// </summary>
    private static IEnumerable<ConsoleKeyInfo> EditSnapshotKeys(string view)
    {
        yield return Stroke('\r', ConsoleKey.Enter);

        if (string.Equals(view, "logging", StringComparison.OrdinalIgnoreCase))
        {
            // name → on connect → log: the character row's fields, in order.
            yield return Stroke('\t', ConsoleKey.Tab);
            yield return Stroke('\t', ConsoleKey.Tab);
            yield break;
        }

        if (string.Equals(view, "triggers", StringComparison.OrdinalIgnoreCase))
        {
            // name → pattern → route: two steps, because the name now leads the row's fields.
            yield return Stroke('\t', ConsoleKey.Tab);
            yield return Stroke('\t', ConsoleKey.Tab);
            yield return Stroke('\0', ConsoleKey.DownArrow);
            yield break;
        }

        if (!string.Equals(view, "worlds", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(view, "settings", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        yield return Stroke('\t', ConsoleKey.Tab);
        for (var i = 0; i < 3; i++)
        {
            yield return Stroke('\b', ConsoleKey.Backspace);
        }

        foreach (var c in "net")
        {
            yield return Stroke(c, ConsoleKey.NoName);
        }
    }

    private static ConsoleKeyInfo Stroke(char c, ConsoleKey key) => new(c, key, false, false, false);

    /// <summary>Maps a <c>--view</c> name to a settings screen (F-key + open factory) for snapshots.</summary>
    private (ConsoleKey Key, Func<ScreenBinding> Open)? SettingsView(string view)
    {
        foreach (var screen in SettingsScreens())
        {
            if (screen.Views.Contains(view, StringComparer.OrdinalIgnoreCase))
            {
                return (screen.Key, screen.Open);
            }
        }

        return null;
    }

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
    private void RefreshRail() => _rail.SetContent(RenderRailLines());

    /// <summary>Builds the ⌃P command catalog from live config + workspace state.</summary>
    private IReadOnlyList<CommandItem> BuildCatalog()
    {
        var context = new CommandContext(
            LoggingOn: false,
            Zoomed: _workspace.Layout.ZoomedPaneId is not null,
            Frozen: _workspace.Layout.FocusedPane.Frozen,
            TimestampsOn: _showTimestamps);
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
                ToggleFreeze();
                return;
            case "term:clear":
                if (_panes.TryGetValue(ActiveWindowId(), out var pane))
                {
                    pane.SetContent(new List<string>());
                }

                break;
            case "term:timestamps-on":
            case "term:timestamps-off":
                _showTimestamps = !_showTimestamps;
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

    /// <summary>
    /// Freezes or resumes the focused pane (⌃F). Freezing records the active window's current scrollback
    /// length as the split point (pinned scrollback above, live tail below); resuming clears it and
    /// re-flows the whole buffer back into the single output control.
    /// </summary>
    private void ToggleFreeze()
    {
        var pane = _workspace.Layout.FocusedPane;
        var windowId = pane.ActiveTab ?? MainWindowId;
        _workspace.Layout.ToggleFreezeFocused();

        if (pane.Frozen)
        {
            _freezePoints[windowId] = _lines.TryGetValue(windowId, out var buf) ? buf.Count : 0;
        }
        else
        {
            _freezePoints.Remove(windowId);
            if (_lines.TryGetValue(windowId, out var buf) && _panes.TryGetValue(windowId, out var control))
            {
                control.SetContent(new List<string>(buf));
            }
        }

        RebuildPaneArea();
    }

    /// <summary>The custom link scheme the header's <c>☰</c> affordance uses to open the menu.</summary>
    private const string MenuScheme = "sharpmuterm-menu:";

    /// <summary>Opens/closes the command surface (⌃P or the header ☰ menu) and flips the header caret.</summary>
    private void ToggleMenu()
    {
        _palette.Toggle();
        _header.SetContent(new List<string> { HeaderMarkup() });
    }

    private void OnLinkClicked(string url)
    {
        if (url.StartsWith(MenuScheme, StringComparison.Ordinal))
        {
            ToggleMenu();
        }
        else if (url.StartsWith(MarkupFormatter.SendScheme, StringComparison.Ordinal))
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
        // A tab switch resets the history cursor (hIdx → -1) so recall restarts against this tab's draft.
        _history.ResetCursor();
        if (newTab?.Tag is string id)
        {
            _workspace.ActivateWindow(id);
            // Restore this window's saved input draft into the shared prompt (not a keystroke: no rebase).
            _suppressInputChanged = true;
            try
            {
                _input.Input = _drafts.GetValueOrDefault(id, string.Empty);
            }
            finally
            {
                _suppressInputChanged = false;
            }

            RefreshTabTitles();
            UpdateInputChrome();
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

    private void ShowWeb(SharpMUTerm.Web.WebPage page)
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

        // A new page invalidates the previous one's images, in flight or already decoded.
        _webImageCts?.Cancel();
        _webImageCts?.Dispose();
        _webImageCts = null;
        _webImages.Clear();

        _webPage = page;
        _webMarkup = page.Lines.Select(_formatter.ToMarkup).ToList();
        PaneContentFor(WebWindowId, title).SetContent(_webMarkup.ToList());
        if (isNew)
        {
            RebuildPaneArea(); // realise the new tab before activating it
        }

        Activate(WebWindowId);
        StartWebImageLoad(page);
    }

    /// <summary>
    /// Kicks off the background fetch/decode of a page's inline images, but only when this view can
    /// actually draw one. With no graphics the placeholders the HTML renderer already emitted are the
    /// finished product, so nothing is fetched at all — a terminal without graphics does not pay for
    /// images it cannot show.
    /// </summary>
    private void StartWebImageLoad(SharpMUTerm.Web.WebPage page)
    {
        if (page.Images.Count == 0 ||
            ResolveInlineImagePresentation() == InlineImagePresentation.TextPlaceholder)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _webImageCts = cts;
        _ = LoadWebImagesAsync(page, WebImageColumns(), cts.Token);
    }

    /// <summary>
    /// Fetches each of a page's images in turn and folds the ones that decode back into the view.
    /// Each arrival repaints on its own rather than the page waiting on the whole set, so pictures
    /// fill in progressively where their placeholders were. Sequential on purpose: a MU* client has
    /// no business opening a dozen simultaneous connections to whatever host a page names.
    /// </summary>
    private async Task LoadWebImagesAsync(
        SharpMUTerm.Web.WebPage page, int columns, CancellationToken cancellationToken)
    {
        for (var i = 0; i < page.Images.Count && i < MaxInlineWebImages; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            PixelBuffer? buffer;
            try
            {
                buffer = await _imageLoader
                    .LoadAsync(page.Images[i].Source, columns, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (buffer is null || cancellationToken.IsCancellationRequested)
            {
                continue; // the placeholder line stays put — a perfectly good outcome
            }

            var index = i;
            var decoded = buffer;
            OnUi(() =>
            {
                // The page may have been replaced while this image was in flight.
                if (cancellationToken.IsCancellationRequested || !ReferenceEquals(_webPage, page))
                {
                    return;
                }

                _webImages[index] = decoded;
                RebuildPaneArea();
            });
        }
    }

    /// <summary>How many images one page may draw, so an image-heavy page cannot stall the client.</summary>
    private const int MaxInlineWebImages = 12;

    /// <summary>Columns an inline web image may span, leaving room for the rail and pane chrome.</summary>
    private int WebImageColumns() => Math.Clamp(_window.Width - 8, 8, 200);

    /// <summary>
    /// What this view can actually put on screen. Asked fresh rather than cached in the constructor:
    /// the console driver only knows whether the terminal speaks Kitty graphics <em>after</em> it has
    /// initialised and run its capability probe.
    /// </summary>
    private GraphicsSurface WebGraphicsSurface() =>
        GraphicsSurface.Compositor(_system.ConsoleDriver is IGraphicsProtocol { SupportsKittyGraphics: true });

    /// <summary>The presentation the degradation chain settles on for this terminal and this view.</summary>
    private InlineImagePresentation ResolveInlineImagePresentation() =>
        InlineImagePolicy.Select(_capabilities, WebGraphicsSurface());

    /// <summary>
    /// Builds the web tab: the page's markup split around whichever images decoded, stacked in a
    /// scrollable panel. With no decoded images this is a single markup control holding every line —
    /// exactly the control the web view used before images existed.
    /// </summary>
    private IWindowControl BuildWebContent(string title)
    {
        var live = PaneContentFor(WebWindowId, title);
        if (_webPage is null || _webImages.Count == 0)
        {
            return live;
        }

        var boxes = new Dictionary<int, WebImageLayout.CellBox>();
        foreach (var (index, buffer) in _webImages)
        {
            boxes[index] = new WebImageLayout.CellBox(buffer.Width, Math.Max(1, buffer.Height / WebImageLayout.PixelsPerCell));
        }

        var blocks = WebViewComposer.Compose(_webMarkup, _webPage.Images, boxes);
        var panel = Controls.ScrollablePanel()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill);

        var usedLiveControl = false;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case WebTextBlock text:
                    // Reuse the window's own control for the first run so link routing and the
                    // pane's identity survive; later runs get plain markup controls with the same
                    // link handler.
                    if (!usedLiveControl)
                    {
                        usedLiveControl = true;
                        live.SetContent(text.Lines.ToList());
                        panel.AddControl(live);
                    }
                    else
                    {
                        // Later runs mirror PaneContentFor's plain control, link routing included,
                        // so a link reads the same wherever on the page it sits.
                        var markup = new MarkupControl(text.Lines.ToList());
                        markup.LinkClicked += (_, e) => OnLinkClicked(e.Url);
                        panel.AddControl(markup);
                    }

                    break;

                case WebImageBlock image:
                    panel.AddControl(new ImageControl
                    {
                        Source = _webImages[image.Index],
                        ScaleMode = ImageScaleMode.Fit,
                        MinimumHeight = image.Box.Rows,
                    });
                    break;
            }
        }

        if (!usedLiveControl)
        {
            // An all-image page: the window still needs its own control in the tree.
            live.SetContent(new List<string>());
            panel.AddControl(live);
        }

        return panel.Build();
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
        lock (_paneTabsLock)
        {
            _paneTabs.Clear();
        }


        // When a pane is zoomed, render just that pane full-area; otherwise render the whole tree.
        var zoomed = _workspace.Layout.ZoomedPaneId is { } zid ? _workspace.Layout.FindPane(zid) : null;
        var paneArea = zoomed is not null
            ? BuildPaneTabs(zoomed)
            : BuildLayoutNode(_workspace.Layout.Root);

        // Size the rail to what its rows actually need (clamped), so it never hogs width nor clips.
        var railLines = RenderRailLines();
        _rail.SetContent(railLines);
        var railWidth = RailWidth(railLines);

        // rail │ thin divider │ 1-col spacer (breathing room) │ output — a solid 1-col bar in the
        // border colour instead of the framework's double-line splitter, for a calmer single-line look.
        // Stretch (control default is Left) so the window arranges the whole row at the full console
        // width; without it the row floats at content width and the Flex pane column can't claim the
        // space to the right edge. (SharpConsoleUI docs/patterns.md — sidebar+content layouts.)
        var row = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Column(c => c.Width(railWidth).Add(_rail))
            .Column(c => c.Width(1).Add(Divider()))
            .Column(c => c.Width(1).Add(_railSpacer))
            .Column(c => c.Flex(1).Add(paneArea))
            .Build();
        return row;
    }

    /// <summary>A thin solid divider control (one cell of the border colour), filling its cell.</summary>
    private MarkupControl Divider()
    {
        var control = new MarkupControl(new List<string>()) { BackgroundColor = ToColor(_theme.Border) };
        return control;
    }

    /// <summary>Renders the current rail rows to markup (collapsed or expanded).</summary>
    private List<string> RenderRailLines()
    {
        var rows = BuildRail();
        return _railCollapsed ? RailRenderer.RenderCollapsed(rows) : RailRenderer.Render(rows);
    }

    /// <summary>
    /// The rail column width: the widest row's visible width plus a small margin, clamped so a long
    /// world or window name can't run away with the layout and a sparse rail still reads. Collapsed,
    /// it hugs its short status strip.
    /// </summary>
    private int RailWidth(IReadOnlyList<string> lines)
    {
        var widest = lines.Count == 0 ? 0 : lines.Max(MarkupWidth);
        return _railCollapsed
            ? Math.Clamp(widest + 1, 4, 10)
            : Math.Clamp(widest + 2, 16, 44);
    }

    /// <summary>Visible width of a markup string: strips <c>[…]</c> tags, unescapes <c>[[</c>/<c>]]</c>,
    /// and counts text elements (so combining/wide runes count once).</summary>
    private static int MarkupWidth(string markup)
    {
        var sb = new System.Text.StringBuilder(markup.Length);
        var i = 0;
        while (i < markup.Length)
        {
            var ch = markup[i];
            if (ch == '[')
            {
                if (i + 1 < markup.Length && markup[i + 1] == '[')
                {
                    sb.Append('[');
                    i += 2;
                    continue;
                }

                var close = markup.IndexOf(']', i + 1);
                i = close < 0 ? markup.Length : close + 1; // skip the whole tag
                continue;
            }

            if (ch == ']' && i + 1 < markup.Length && markup[i + 1] == ']')
            {
                sb.Append(']');
                i += 2;
                continue;
            }

            sb.Append(ch);
            i++;
        }

        return new System.Globalization.StringInfo(sb.ToString()).LengthInTextElements;
    }

    /// <summary>
    /// Recursively realises a layout node: a leaf <see cref="PaneNode"/> becomes a tab strip, a
    /// <see cref="SplitNode"/> becomes a proportional grid (columns for a row split, rows for a
    /// column split) with a draggable splitter between children.
    /// </summary>
    private IWindowControl BuildLayoutNode(SharpMUTerm.Core.Workspaces.LayoutNode node)
    {
        if (node is PaneNode pane)
        {
            if (_dragActive)
            {
                return BuildDragPane(pane);
            }

            return _moveMode && _moveLetters.TryGetValue(pane.Id, out var letter)
                ? BuildMovePane(pane, letter)
                : BuildPaneTabs(pane);
        }

        var split = (SplitNode)node;
        var children = split.Children.Select(BuildLayoutNode).ToList();

        // Interleave a thin 1-cell divider between children (a solid border-colour bar) instead of the
        // framework's double-line splitter, so splits read as a single calm line.
        var tracks = new List<GridLength>();
        for (var i = 0; i < children.Count; i++)
        {
            tracks.Add(GridLength.Star(Math.Max(0.01, split.Sizes[i])));
            if (i < children.Count - 1)
            {
                tracks.Add(GridLength.Cells(1));
            }
        }

        var grid = Controls.Grid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill);
        if (split.Direction == SplitDirection.Row)
        {
            grid.Columns(tracks.ToArray()).Rows(GridLength.Star(1));
            for (var i = 0; i < children.Count; i++)
            {
                grid.Place(children[i], 0, i * 2, 1, 1);
                if (i < children.Count - 1)
                {
                    grid.Place(Divider(), 0, i * 2 + 1, 1, 1);
                }
            }
        }
        else
        {
            grid.Rows(tracks.ToArray()).Columns(GridLength.Star(1));
            for (var i = 0; i < children.Count; i++)
            {
                grid.Place(children[i], i * 2, 0, 1, 1);
                if (i < children.Count - 1)
                {
                    grid.Place(Divider(), i * 2 + 1, 0, 1, 1);
                }
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

            builder.AddTab(
                TabTitles.For(window, ActiveCharacterKey(), isActive: pane.ActiveTab == windowId),
                BuildTabContent(pane, windowId, window));
            ids.Add(windowId);
        }

        // Stretch so the tab strip + content fill their pane column to the right edge; the control
        // default is Left, which self-sizes to content and leaves the pane short (docs/patterns.md §12).
        var tabs = builder.Fill().WithAlignment(HorizontalAlignment.Stretch).Build();
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
        lock (_paneTabsLock)
        {
            _paneTabs[paneId] = tabs;
        }

        return tabs;
    }

    /// <summary>
    /// Chooses a tab's content: a frozen <em>active</em> window gets the pinned/live split; a spawn
    /// window with a capture pattern gets a dim <c>⇱ capture …</c> header over its output; everything
    /// else shows the plain live control.
    /// </summary>
    private IWindowControl BuildTabContent(PaneNode pane, string windowId, WorkspaceWindow window)
    {
        if (pane.Frozen && pane.ActiveTab == windowId)
        {
            return BuildFrozenContent(windowId, window.Title);
        }

        if (window.Kind == WindowKind.Spawn && !string.IsNullOrEmpty(window.CapturePattern))
        {
            return BuildSpawnContent(windowId, window);
        }

        if (windowId == WebWindowId)
        {
            return BuildWebContent(window.Title);
        }

        return PaneContentFor(windowId, window.Title);
    }

    /// <summary>Wraps a spawn window's output under a dim capture line naming its trigger pattern.</summary>
    private IWindowControl BuildSpawnContent(string windowId, WorkspaceWindow window)
    {
        var header = new MarkupControl(new List<string> { CaptureLineRenderer.Line(window.CapturePattern!) });
        var output = PaneContentFor(windowId, window.Title);

        var grid = Controls.Grid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill);
        grid.Rows(GridLength.Cells(1), GridLength.Star(1)).Columns(GridLength.Star(1));
        grid.Place(header, 0, 0, 1, 1);
        grid.Place(output, 1, 0, 1, 1);
        return grid.Build();
    }

    /// <summary>
    /// Builds a frozen window's content: a vertical split of pinned scrollback (buffer up to the freeze
    /// point), the <c>▲ FROZEN ⌃F</c> bar, and the live tail (buffer since the freeze). The tail is the
    /// window's real control, so incoming lines keep landing below the bar while the top stays pinned.
    /// </summary>
    private IWindowControl BuildFrozenContent(string windowId, string title)
    {
        var buffer = _lines.TryGetValue(windowId, out var b) ? b : new List<string>();
        var split = _freezePoints.TryGetValue(windowId, out var p) ? Math.Clamp(p, 0, buffer.Count) : buffer.Count;

        var frozen = new MarkupControl(buffer.Take(split).ToList());
        frozen.LinkClicked += (_, e) => OnLinkClicked(e.Url);

        var bar = new MarkupControl(new List<string> { FreezeBarRenderer.Bar(FrozenAccentHex()) });

        var live = PaneContentFor(windowId, title);
        live.SetContent(buffer.Skip(split).ToList());

        // Pinned scrollback gets the lion's share; a single "❄ FROZEN ⌃F ───" line is both label and
        // border, with the live tail a few rows below it.
        var grid = Controls.Grid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill);
        grid.Rows(GridLength.Star(3), GridLength.Cells(1), GridLength.Star(1)).Columns(GridLength.Star(1));
        grid.Place(frozen, 0, 0, 1, 1);
        grid.Place(bar, 1, 0, 1, 1);
        grid.Place(live, 2, 0, 1, 1);
        return grid.Build();
    }

    /// <summary>The frozen-split chrome colour (design token #c678dd / ANSI 5), resolved through the theme.</summary>
    private string FrozenAccentHex()
    {
        var rgb = _theme.ResolveIndex(5);
        return $"#{rgb.R:x2}{rgb.G:x2}{rgb.B:x2}";
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

    /// <summary>
    /// Arms the tmux-style ⌃B prefix: the header shows <c>⌃B — awaiting …</c> and the next key is
    /// consumed by <see cref="OnWindowKey"/> as a pane command. Ignored while an overlay is open.
    /// </summary>
    private void ArmPrefix()
    {
        if (_palette.IsOpen || _settings.IsOpen)
        {
            return;
        }

        _prefixArmed = true;
        _header.SetContent(new List<string> { HeaderMarkup() });
    }

    /// <summary>Consumes the key after ⌃B and runs the matching pane command (tmux-style).</summary>
    private void OnWindowKey(object? sender, KeyPressedEventArgs e)
    {
        if (_moveMode)
        {
            HandleMoveKey(e);
            return;
        }

        // Escape abandons a mouse drag. A terminal that loses the button-up (the pointer left the
        // window, the terminal dropped a frame) would otherwise strand the preview over the panes.
        if (_dragActive && e.KeyInfo.Key == ConsoleKey.Escape)
        {
            e.Handled = true;
            _paneDrag.Reset(); // no mouse frame ends this one, so the gesture has to be dropped here
            EndDrag();
            return;
        }

        if (!_prefixArmed)
        {
            // Draft-safe history recall on ↑/↓ — our own, so a half-typed draft survives (see InputHistory).
            if (!_palette.IsOpen && !_settings.IsOpen)
            {
                TryRecallKey(e);
            }

            return;
        }

        _prefixArmed = false;
        e.Handled = true;
        switch (char.ToLowerInvariant(e.KeyInfo.KeyChar))
        {
            case '|': PaneCommands.Apply(_workspace.Layout, PaneCommand.SplitRight); RebuildPaneArea(); break;
            case '-': PaneCommands.Apply(_workspace.Layout, PaneCommand.SplitDown); RebuildPaneArea(); break;
            case 'z': _workspace.Layout.ToggleZoom(); RebuildPaneArea(); break;
            case 'o': CyclePane(); break;
            case 'x': CloseActiveWindow(); break;
            case 'b': _railCollapsed = !_railCollapsed; RebuildPaneArea(); break;
            case '<': if (_workspace.Layout.ReorderActiveTab(-1)) RefreshTabTitles(); break;
            case '>': if (_workspace.Layout.ReorderActiveTab(1)) RefreshTabTitles(); break;
            case 'm': EnterMoveMode(); break;
            default: break; // any other key just disarms
        }

        _header.SetContent(new List<string> { HeaderMarkup() });
    }

    /// <summary>
    /// Enters move mode (⌃B m): the active window lifts, every pane dims and shows a target letter
    /// (a–j), and the status bar becomes the move prompt. a–j pick the destination, arrows toggle an
    /// edge (split there), ⏎ commits, Esc cancels.
    /// </summary>
    private void EnterMoveMode()
    {
        _moveWindowId = ActiveWindowId();
        _moveMode = true;
        _moveTargetPaneId = null;
        _moveLetters.Clear();
        var letter = 'a';
        foreach (var pane in _workspace.Layout.Panes)
        {
            if (letter > 'j')
            {
                break;
            }

            _moveLetters[pane.Id] = letter++;
        }

        RebuildPaneArea();
        SetStatus(MovePromptMarkup());
    }

    /// <summary>Handles a key while in move mode: pick pane (a–j), edge (arrows), commit (⏎), cancel (Esc).</summary>
    private void HandleMoveKey(KeyPressedEventArgs e)
    {
        e.Handled = true;
        var key = e.KeyInfo.Key;
        var ch = char.ToLowerInvariant(e.KeyInfo.KeyChar);

        if (key == ConsoleKey.Escape)
        {
            ExitMoveMode(commit: false);
            return;
        }

        if (key == ConsoleKey.Enter)
        {
            ExitMoveMode(commit: true);
            return;
        }

        // Arrows pick the edge to split the target toward — the keyboard stand-in for dropping on a
        // pane's edge rather than its middle. Pressing the same arrow again returns to a tab drop.
        if (MoveEdgeFor(key) is { } edge)
        {
            _moveEdge = _moveEdge == edge ? null : edge;
            RebuildPaneArea();
            SetStatus(MovePromptMarkup());
            return;
        }

        if (ch is >= 'a' and <= 'j')
        {
            // Only retarget on a real match — an unmapped letter must not clear the current target.
            var match = _moveLetters.FirstOrDefault(kv => kv.Value == ch);
            if (match.Key is not null)
            {
                _moveTargetPaneId = match.Key;
                RebuildPaneArea();
                SetStatus(MovePromptMarkup());
            }
        }
    }

    /// <summary>The split edge an arrow key selects in move mode, or null for any other key.</summary>
    private static Edge? MoveEdgeFor(ConsoleKey key) => key switch
    {
        ConsoleKey.LeftArrow => Edge.Left,
        ConsoleKey.RightArrow => Edge.Right,
        ConsoleKey.UpArrow => Edge.Top,
        ConsoleKey.DownArrow => Edge.Bottom,
        _ => null,
    };

    /// <summary>Applies (or cancels) the move and leaves move mode.</summary>
    private void ExitMoveMode(bool commit)
    {
        if (commit && _moveWindowId is { } win && _moveTargetPaneId is { } pane)
        {
            // The same commit the mouse drop uses, so both routes land identically.
            PaneDrop.Apply(_workspace.Layout, win, pane, _moveEdge);
        }

        _moveMode = false;
        _moveWindowId = null;
        _moveTargetPaneId = null;
        _moveEdge = null;
        _moveLetters.Clear();
        RebuildPaneArea();
        UpdateStatus();
    }

    /// <summary>The move-mode status prompt.</summary>
    private string MovePromptMarkup()
    {
        var name = _moveWindowId is { } id && _workspace.FindWindow(id) is { } w ? Escape(w.Title) : "window";
        return $"[#e5c07b]MOVE[/] [bold]{name}[/] [dim]→[/] [#00f5b7]{DropLabel(_moveTargetPaneId, _moveEdge)}[/]"
            + "   [dim]a–j pane · ←↑↓→ edge · ⏎ commit · Esc cancel[/]";
    }

    /// <summary>Human-readable description of a pending drop, for the move prompt and drag preview.</summary>
    private string DropLabel(string? paneId, Edge? edge)
    {
        if (paneId is null)
        {
            return "no target";
        }

        var name = PaneLabel(paneId);
        return edge switch
        {
            Edge.Left => $"split {name} left",
            Edge.Right => $"split {name} right",
            Edge.Top => $"split {name} top",
            Edge.Bottom => $"split {name} bottom",
            _ => $"tab in {name}",
        };
    }

    /// <summary>The rail's friendly name for a pane ("main" for the first, "pane N" after it).</summary>
    private string PaneLabel(string paneId)
    {
        var index = 0;
        foreach (var pane in _workspace.Layout.Panes)
        {
            if (pane.Id == paneId)
            {
                return index == 0 ? "main" : $"pane {index + 1}";
            }

            index++;
        }

        return paneId;
    }

    /// <summary>
    /// The adapter between the console driver's raw mouse frames and the tested
    /// <see cref="PaneDragTracker"/>. Deliberately thin: it decides nothing, it only hands the frame
    /// over (with a geometry snapshot the tracker asks for at most once per gesture) and marshals the
    /// tracker's verdict onto the UI thread. Driver events arrive on the input thread.
    /// </summary>
    private void OnDriverMouseEvent(object sender, List<MouseFlags> flags, System.Drawing.Point point)
    {
        // Overlays own the whole screen while they're up; a drag underneath them would target panes
        // the user can't even see.
        if (_palette.IsOpen || _settings.IsOpen || _moveMode)
        {
            return;
        }

        var result = _paneDrag.Handle(flags, point.X, point.Y, PaneSnapshot);
        if (result.Action == PaneDragAction.None)
        {
            return;
        }

        OnUiThread(() => ApplyDragResult(result));
    }

    /// <summary>
    /// Reads the pane area's live geometry back out of the framework's arranged layout, in desktop
    /// cells. A control's <see cref="SharpConsoleUI.Layout.LayoutNode.AbsoluteBounds"/> is in
    /// window-content space, so the window's own origin and inset are added back on.
    /// Internal so a headless test can check the mapping against the framework's own hit testing —
    /// it is the one part of the drag that no pure unit test can pin down.
    /// </summary>
    internal PaneDragSurface PaneSnapshot()
    {
        var origin = ContentOrigin();
        var rects = new Dictionary<string, PaneRect>(StringComparer.Ordinal);
        var windows = new Dictionary<string, string>(StringComparer.Ordinal);

        KeyValuePair<string, TabControl>[] realised;
        lock (_paneTabsLock)
        {
            realised = _paneTabs.ToArray();
        }

        foreach (var (paneId, tabs) in realised)
        {
            if (_window.GetLayoutNode(tabs) is not { } node)
            {
                continue;
            }

            var bounds = node.AbsoluteBounds;
            rects[paneId] = new PaneRect(origin.X + bounds.X, origin.Y + bounds.Y, bounds.Width, bounds.Height);

            if (_workspace.Layout.FindPane(paneId)?.ActiveTab is { } windowId)
            {
                windows[paneId] = windowId;
            }
        }

        return new PaneDragSurface(rects, windows);
    }

    /// <summary>
    /// The desktop cell that window-content coordinate (0,0) paints at: the window's position, offset
    /// past any top desktop panel and the window's own frame + padding. Mirrors the framework's own
    /// <c>InsetLeft</c>/<c>InsetTop</c> (frame thickness plus padding), which are internal to it.
    /// </summary>
    private System.Drawing.Point ContentOrigin()
    {
        var frame = _window.BorderStyle == BorderStyle.Frameless ? 0 : 1;
        return new System.Drawing.Point(
            _window.Left + frame + _window.Padding.Left,
            _window.Top + _system.DesktopUpperLeft.Y + frame + _window.Padding.Top);
    }

    /// <summary>Applies a tracker verdict: paint, tear down, or commit the drop and rebuild.</summary>
    private void ApplyDragResult(PaneDragResult result)
    {
        switch (result.Action)
        {
            case PaneDragAction.Begin:
            case PaneDragAction.Update:
                _dragActive = true;
                _dragTargetPaneId = result.TargetPaneId;
                _dragEdge = result.Edge;
                RebuildPaneArea();
                SetStatus(DragPromptMarkup(result.WindowId, result.TargetPaneId, result.Edge));
                break;

            case PaneDragAction.Commit:
                if (result.WindowId is { } windowId && result.TargetPaneId is { } paneId)
                {
                    if (PaneDrop.Apply(_workspace.Layout, windowId, paneId, result.Edge))
                    {
                        _workspace.ActivateWindow(windowId);
                    }
                }

                EndDrag();
                break;

            default:
                EndDrag();
                break;
        }
    }

    /// <summary>
    /// Leaves the drag preview and restores the real pane area and status line. It deliberately does
    /// not reset the tracker: the tracker ends its own gesture, and a press that lands mid-preview
    /// both cancels the stale drag and arms the next one in the same frame.
    /// </summary>
    private void EndDrag()
    {
        _dragActive = false;
        _dragTargetPaneId = null;
        _dragEdge = null;
        RebuildPaneArea();
        UpdateStatus();
    }

    /// <summary>The status line shown while a pane drag is in flight.</summary>
    private string DragPromptMarkup(string? windowId, string? targetPaneId, Edge? edge)
    {
        var name = windowId is { } id && _workspace.FindWindow(id) is { } window ? Escape(window.Title) : "window";
        return $"[#e5c07b]DRAG[/] [bold]{name}[/] [dim]→[/] [{PaneDropRenderer.ZoneColor}]{DropLabel(targetPaneId, edge)}[/]"
            + "   [dim]release to drop · Esc cancel[/]";
    }

    /// <summary>A pane rendered as a live drop target, sized from the drag's frozen geometry.</summary>
    private IWindowControl BuildDragPane(PaneNode pane)
    {
        var rect = _paneDrag.Surface?.RectOf(pane.Id) ?? default;
        var hovered = pane.Id == _dragTargetPaneId;
        var lines = PaneDropRenderer.Render(
            PaneLabel(pane.Id),
            DropLabel(pane.Id, _dragEdge),
            rect.Width,
            rect.Height,
            hovered,
            _dragEdge);

        return new MarkupControl(lines) { HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    /// <summary>
    /// Runs UI work on the UI thread. Headless (snapshot and test) runs have no main loop to drain the
    /// queue, and are single-threaded anyway, so they run it inline.
    /// </summary>
    private void OnUiThread(Action action)
    {
        if (_headless || _system.IsOnUIThread)
        {
            action();
            return;
        }

        _system.EnqueueOnUIThread(action);
    }

    /// <summary>A pane rendered as a move-mode target: a big letter over the dimmed window list.</summary>
    private IWindowControl BuildMovePane(PaneNode pane, char letter)
    {
        var selected = pane.Id == _moveTargetPaneId;
        var color = selected ? "#00f5b7" : "#e5c07b";
        var lines = new List<string> { string.Empty, string.Empty };
        lines.Add($"     [bold {color}]▛▀▀▜[/]");
        lines.Add($"     [bold {color}]▌ {char.ToUpperInvariant(letter)} ▐[/]");
        lines.Add($"     [bold {color}]▙▄▄▟[/]");
        lines.Add(string.Empty);
        if (selected)
        {
            lines.Add($"     [{PaneDropRenderer.ZoneColor}]{DropLabel(pane.Id, _moveEdge)}[/]");
            lines.Add(string.Empty);
        }

        foreach (var windowId in pane.Tabs)
        {
            if (_workspace.FindWindow(windowId) is { } window)
            {
                lines.Add($"     [dim]▪ {Escape(window.Title)}[/]");
            }
        }

        return new MarkupControl(lines);
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

        // The input line follows focus: reset the history cursor and restore the window's draft.
        _history.ResetCursor();
        _suppressInputChanged = true;
        try
        {
            _input.Input = _drafts.GetValueOrDefault(ActiveWindowId(), string.Empty);
        }
        finally
        {
            _suppressInputChanged = false;
        }

        RefreshTabTitles();
        UpdateInputChrome();
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
        _lines.Remove(id);         // don't resurrect old scrollback if a same-id spawn reopens
        _freezePoints.Remove(id);
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
        var focusedCharacter = ActiveCharacterKey();
        foreach (var (paneId, tabs) in _paneTabs)
        {
            var activeTab = _workspace.Layout.FindPane(paneId)?.ActiveTab;
            foreach (var page in tabs.TabPages)
            {
                if (page.Tag is string id && _workspace.FindWindow(id) is { } window)
                {
                    page.Title = TabTitles.For(window, focusedCharacter, isActive: id == activeTab);
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
        _statusIdentity = (character, session.World.Host, session.World.Port, session.State.ToString().ToLowerInvariant());
        RefreshStatusBar();
        _header.SetContent(new List<string> { HeaderMarkup() });
        RefreshRail();
    }

    /// <summary>
    /// The design header row: the brand affordance on the left, the active world (with its accent)
    /// in the middle, and connection/graphics/palette hints on the right.
    /// </summary>
    private string HeaderMarkup()
    {
        // The menu affordance opens the command surface (caret flips to ▾ while it's open). The whole
        // identity cluster is a powerline ribbon — menu ▸ world ▸ character — flowing accent colours.
        var caret = _palette is { IsOpen: true } ? "▾" : Glyphs.Menu;
        var dark = Hex(_theme.Resolve(TerminalColor.Default, isBackground: true));
        var headerBg = Hex(_theme.StatusBackground);
        var chip = "#3f4859"; // dim chrome the character segment sits on

        // Build the ribbon by hand so only the brand "button" is a link (wrapping the whole bar makes
        // the driver's link highlight repaint every segment and flatten the flowing colours).
        var brandBg = AccentHex(AccentPalette[2]); // violet
        var sb = new System.Text.StringBuilder();
        sb.Append($"[link={MenuScheme}toggle][bold {dark} on {brandBg}] {caret} muterm [/][/]");

        var tail = brandBg;
        if (ActiveWorld() is { } active)
        {
            var worldAccent = AccentHex(active.Accent);
            sb.Append($"[{tail} on {worldAccent}]{Glyphs.PowerRight}[/]");
            sb.Append($"[bold {dark} on {worldAccent}] {Escape(active.World.Name)} [/]");
            tail = worldAccent;
            if (active.Character is { } name)
            {
                sb.Append($"[{tail} on {chip}]{Glyphs.PowerRight}[/]");
                sb.Append($"[{worldAccent} on {chip}] ● {Escape(name)} [/]");
                tail = chip;
            }
        }

        sb.Append($"[{tail} on {headerBg}]{Glyphs.PowerRight}[/]");
        var leftBar = sb.ToString();

        // The ⌃B prefix indicator shows only while armed (design: "⌃B — awaiting | - z o x b m < >").
        if (_prefixArmed)
        {
            return $"{leftBar}  [#e5c07b]⌃B — awaiting[/]  [dim]| - z o x b m < >[/]";
        }

        var connected = _connectedKeys.Count;
        var conn = _config.Worlds.Count > 0 ? $"{connected}/{_config.Worlds.Count} connected   " : string.Empty;
        var logFormat = ActiveLogging().Format;
        var log = logFormat == LogFormat.None
            ? $"[dim]{Glyphs.Log} LOG off[/]"
            : $"[#00f5b7]{Glyphs.Log}[/] [dim]LOG {logFormat.ToString().ToLowerInvariant()}[/]";
        var right = $"[dim]{conn}[/]{log}   [dim]Graphics {Escape(_capabilities.Protocol.ToString())} [/]";

        // Right-align the status cluster to the far edge so the menu bar spans the whole console.
        var gap = Math.Max(3, HeaderWidth() - MarkupWidth(leftBar) - MarkupWidth(right));
        return $"{leftBar}{new string(' ', gap)}{right}";
    }

    /// <summary>The header width to lay out against — the live window width, or a sane default early on.</summary>
    private int HeaderWidth() => _window is { Width: > 0 } ? _window.Width : 160;

    /// <summary>Formats an <see cref="Rgb"/> as <c>#rrggbb</c> markup.</summary>
    private static string Hex(Rgb rgb) => $"#{rgb.R:x2}{rgb.G:x2}{rgb.B:x2}";

    /// <summary>
    /// The design status bar. The connection identity (● name · state) anchors the left edge; the
    /// keepalive sparkline, host/encoding, char-count (or history hint), and palette hint form a
    /// cluster right-aligned to the far edge — mirroring the header's left/right split.
    /// </summary>
    private string StatusBarMarkup(string character, string host, int port, string state)
    {
        var accent = ActiveWorld() is { } world ? AccentHex(world.Accent) : "#00f5b7";
        var left = $"[{accent}]●[/] [bold]{Escape(character)}[/] [dim]{Escape(state)}[/]";

        var right = new List<string>();

        // Keepalive latency sparkline + last ack (compact), per the design status bar.
        var spark = Meters.Sparkline(new[] { 38, 44, 41, 47, 40, 43 });
        var ackMs = ActiveWorld() is { } w && w.World.KeepaliveSeconds > 0 ? 41 : 0;
        right.Add($"[dim]{Glyphs.Heartbeat}[/] [#98c379]{spark}[/] [dim]{ackMs}ms[/]");

        var encoding = ActiveWorld() is { } enc ? enc.World.Encoding : "UTF-8";
        right.Add($"[dim]{Escape($"{host}:{port}")}  {Escape(encoding)}[/]");

        // The character count lives at the bottom now (the input gutter is gone); while recalling
        // history it becomes the "back to draft" hint instead.
        right.Add(_history.IsRecalling
            ? $"[{AccentHex(AccentPalette[0])}]history[/] [dim]· ↓ back to draft[/]"
            : $"[dim]{_input.Input.Length} chars[/]");

        right.Add("[dim]⌃P palette[/]");
        var rightBar = string.Join("   ", right);

        // Right-align the cluster to the far edge; identity stays pinned left.
        var gap = Math.Max(3, HeaderWidth() - MarkupWidth(left) - MarkupWidth(rightBar));
        return $"{left}{new string(' ', gap)}{rightBar}";
    }

    /// <summary>Marshals an action onto the UI thread (session events fire on background threads).</summary>
    private void OnUi(Action action) => _system.EnqueueOnUIThread(action);

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
        _system.ConsoleDriver.MouseEvent -= OnDriverMouseEvent;
        _webImageCts?.Cancel();
        _webImageCts?.Dispose();
        _imageLoader.Dispose();
        _fetcher.Dispose();
        await _sessions.DisposeAsync().ConfigureAwait(false);
    }
}
