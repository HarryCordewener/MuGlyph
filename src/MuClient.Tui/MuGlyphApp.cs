using MuClient.Core.Commands;
using MuClient.Core.Automation;
using MuClient.Core.Configuration;
using MuClient.Core.Input;
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
    private readonly PromptControl _input;
    private readonly GmcpStats _stats = new();
    private readonly MuClient.Web.WebPageFetcher _fetcher = new();

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
    private readonly Dictionary<string, char> _moveLetters = new(StringComparer.Ordinal);

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
        // Keep the clickable "glyph" button on-brand (violet) instead of the driver's default link highlight.
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
            .WithTitle("MuGlyph — MU* client")
            .Maximized()
            .Frameless() // no outer chrome — the workspace fills the whole screen for maximum room
            .WithColors(fg, bg)
            .AddControl(_header)
            .AddControl(_workspaceRow)
            .AddControl(_input)
            .AddControl(_statusBar)
            .Build();

        _palette = new CommandPalette(_system, BuildCatalog, () => _active?.SessionKey, DispatchCommand);
        _settings = new SettingsOverlay(_system);

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

        // Optionally open a settings screen over the workspace so its frame can be captured too.
        if (view is not null && SettingsView(view) is { } screen)
        {
            _settings.OpenForSnapshot(screen.Key, screen.Content);
        }

        // Render exactly one frame, synchronously, inline on this thread. ForceRender() performs a
        // single render cycle (bypassing the frame-rate limiter) with no Run() loop, no driver
        // Initialize/Start, and no OnShown pass — a freshly-added window is dirty and paints on the
        // first call. The HeadlessConsoleDriver writes the composited frame straight to the console,
        // so we redirect Console.Out for the duration of that one call and keep what it wrote. (An
        // earlier Run()-on-a-worker-thread approach raced the input+render pump and hung/OOM'd.)
        SyncInputWidth(); // the window now carries the snapshot size, so the band fills its full width
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
    /// Binds F2–F9 to the full-screen settings overlay. Each screen is rendered on demand from live
    /// config by its pure renderer, so re-opening always reflects current state. Esc / same F-key closes.
    /// </summary>
    private void RegisterSettingsShortcuts()
    {
        void Bind(ConsoleKey key, Func<IReadOnlyList<string>> content) =>
            _system.RegisterGlobalShortcut((ConsoleModifiers)0, key, () => _settings.Toggle(key, content));

        Bind(ConsoleKey.F2, () => TriggersScreenRenderer.Render(_config.TriggerSets, 0, SpawnTargets()));
        Bind(ConsoleKey.F3, () => AliasesScreenRenderer.Render(_config.TriggerSets, 0));
        Bind(ConsoleKey.F4, () => KeypadScreenRenderer.Render(Macros()));
        Bind(ConsoleKey.F5, () => WorldsScreenRenderer.Render(_config.Worlds, _config.TriggerSets, ActiveWorldIndex(), ActiveCharacterIndex(), _system.DesktopDimensions.Width, _system.DesktopDimensions.Height));
        Bind(ConsoleKey.F6, () => TimersScreenRenderer.Render(_config.TriggerSets, 0));
        Bind(ConsoleKey.F7, OptionsScreenRenderer.TextAnsi);
        Bind(ConsoleKey.F8, OptionsScreenRenderer.InputSpellcheck);
        Bind(ConsoleKey.F9, () => OptionsScreenRenderer.Logging(ActiveLogging()));
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

    /// <summary>The active character's logging settings, for the F9 logging screen.</summary>
    private LoggingSettings ActiveLogging()
    {
        var world = _config.Worlds.ElementAtOrDefault(ActiveWorldIndex());
        return world?.Characters.ElementAtOrDefault(ActiveCharacterIndex())?.Logging ?? new LoggingSettings();
    }

    /// <summary>Maps a <c>--view</c> name to a settings screen (F-key + content) for snapshots.</summary>
    private (ConsoleKey Key, Func<IReadOnlyList<string>> Content)? SettingsView(string view) => view.ToLowerInvariant() switch
    {
        "triggers" => (ConsoleKey.F2, () => TriggersScreenRenderer.Render(_config.TriggerSets, 0, SpawnTargets())),
        "aliases" => (ConsoleKey.F3, () => AliasesScreenRenderer.Render(_config.TriggerSets, 0)),
        "keypad" => (ConsoleKey.F4, () => KeypadScreenRenderer.Render(Macros())),
        "worlds" or "settings" => (ConsoleKey.F5, () => WorldsScreenRenderer.Render(_config.Worlds, _config.TriggerSets, ActiveWorldIndex(), ActiveCharacterIndex(), _system.DesktopDimensions.Width, _system.DesktopDimensions.Height)),
        "timers" => (ConsoleKey.F6, () => TimersScreenRenderer.Render(_config.TriggerSets, 0)),
        "textansi" => (ConsoleKey.F7, OptionsScreenRenderer.TextAnsi),
        "input" => (ConsoleKey.F8, OptionsScreenRenderer.InputSpellcheck),
        "logging" => (ConsoleKey.F9, () => OptionsScreenRenderer.Logging(ActiveLogging())),
        _ => null,
    };

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
    private const string MenuScheme = "muglyph-menu:";

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
    private IWindowControl BuildLayoutNode(MuClient.Core.Workspaces.LayoutNode node)
    {
        if (node is PaneNode pane)
        {
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
        _paneTabs[paneId] = tabs;
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

    /// <summary>Applies (or cancels) the move and leaves move mode.</summary>
    private void ExitMoveMode(bool commit)
    {
        if (commit && _moveWindowId is { } win && _moveTargetPaneId is { } pane && pane != _workspace.Layout.FindWindow(win)?.Id)
        {
            _workspace.Layout.MoveWindowToPane(win, pane);
        }

        _moveMode = false;
        _moveWindowId = null;
        _moveTargetPaneId = null;
        _moveLetters.Clear();
        RebuildPaneArea();
        UpdateStatus();
    }

    /// <summary>The move-mode status prompt.</summary>
    private string MovePromptMarkup()
    {
        var name = _moveWindowId is { } id && _workspace.FindWindow(id) is { } w ? Escape(w.Title) : "window";
        return $"[#e5c07b]MOVE[/] [bold]{name}[/]   [dim]a–j pane · ←↑↓→ edge · ⏎ commit · Esc cancel[/]";
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

        // Build the ribbon by hand so only the glyph "button" is a link (wrapping the whole bar makes
        // the driver's link highlight repaint every segment and flatten the flowing colours).
        var brandBg = AccentHex(AccentPalette[2]); // violet
        var sb = new System.Text.StringBuilder();
        sb.Append($"[link={MenuScheme}toggle][bold {dark} on {brandBg}] {caret} glyph [/][/]");

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
