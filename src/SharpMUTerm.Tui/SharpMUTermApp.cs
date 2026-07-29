using Microsoft.Extensions.Logging;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Diagnostics;
using SharpMUTerm.Core.Input;
using SharpMUTerm.Core.Logging;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Telnet;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Transport;
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

    /// <summary>
    /// The scroll viewport each output region is shown through, keyed by region — a window id for its
    /// live output, <see cref="FrozenRegionKey"/> for a frozen pane's pinned half, <see cref="WebWindowId"/>
    /// for the web document. Held here rather than rebuilt with the pane area so a reader's scroll
    /// position survives a split, a tab change or a freeze; see <see cref="ScrollViewFor"/>.
    /// </summary>
    private readonly Dictionary<string, ScrollablePanelControl> _paneScrolls = new(StringComparer.Ordinal);

    /// <summary>
    /// The markup control holding a frozen pane's pinned scrollback, per window. It is a second control
    /// for the same buffer (the window's own control shows the live tail below the bar), and it is kept
    /// rather than rebuilt so its scroll viewport keeps a stable child and its parse cache survives —
    /// the pinned half is precisely the half a reader scrolls through.
    /// </summary>
    private readonly Dictionary<string, MarkupControl> _frozenPanes = new(StringComparer.Ordinal);
    private readonly DraftStore _drafts;
    private readonly InputBarVisibility _secondBars;
    private readonly InputHistory _history;

    /// <summary>
    /// The second bar's own recall list. The bars exist to keep two lines apart (an IC one and an OOC
    /// one), and a shared history would put the other bar's sends under ↑ on both — which is the same
    /// mixing the second bar was added to stop.
    /// </summary>
    private readonly InputHistory _secondHistory;

    // Per-window markup line buffer (the scrollback source of truth) and, per frozen pane, the buffer
    // length of its active window at the moment it froze — the split point between pinned scrollback and
    // the live tail. Kept here (not read back from the controls) so freeze can rebuild both regions.
    private readonly Dictionary<string, List<string>> _lines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _freezePoints = new(StringComparer.Ordinal);
    private readonly HashSet<string> _connectedKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// The output window each open session prints into. This is the session ↔ pane link NAWS resolves
    /// through: a session's pane is whichever pane hosts this window, so a split changes what the
    /// session's server should be told without the terminal changing size at all. Written by
    /// <see cref="AttachSession"/> from <see cref="BindSession"/> — the one place that decides where a
    /// session's lines land — so the size we report and the text we print can never disagree about
    /// which window a session owns.
    /// </summary>
    private readonly Dictionary<WorldSession, string> _sessionWindows = new();

    /// <summary>
    /// How often one session may be told a new size over NAWS. A report is a nine-byte
    /// subnegotiation and the server does nothing urgent with it — it is the width future lines will
    /// be wrapped at, not anything on screen now — so the only thing that has to be prompt is the
    /// size a resize <em>settles</em> on. What must not happen is the other end: dragging a terminal
    /// edge produces a size per frame, and the report rides the frame, so an unlimited path writes to
    /// every connected world sixty times a second for as long as the drag lasts.
    /// <para>
    /// 250 ms caps that at four writes per second per world while staying well inside the ~300 ms a
    /// person reads as "instant", and the leading edge is not delayed at all
    /// (<see cref="OfferWindowSize"/>), so a split or a one-shot resize is as immediate as it ever
    /// was. Deliberately a constant and not an F8 option: it is protocol hygiene rather than a
    /// preference, there is no answer a user is in a position to prefer, and a row honest enough to
    /// say what it does ("how often we tell the server the window size") would be a knob whose only
    /// wrong settings are the ones a user might pick.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan WindowSizeReportInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>What one session has been told over NAWS, and what it is waiting to be told.</summary>
    private sealed class SizeReport
    {
        /// <summary>The size actually sent, or null when the session has been told nothing yet.</summary>
        public (int Width, int Height)? Sent;

        /// <summary>When <see cref="Sent"/> went out; meaningless while it is null.</summary>
        public DateTimeOffset SentAt;

        /// <summary>The newest size the interval is holding back — replaced, never queued.</summary>
        public (int Width, int Height)? Pending;
    }

    /// <summary>Per-session NAWS bookkeeping. UI thread only.</summary>
    private readonly Dictionary<WorldSession, SizeReport> _sizeReports = new();

    /// <summary>The clock and timer source behind the rate limit; a fake one makes the tests exact.</summary>
    private readonly TimeProvider _time;

    /// <summary>The one-shot trailing flush, and the moment it is currently armed for.</summary>
    private ITimer? _sizeFlushTimer;
    private DateTimeOffset? _sizeFlushDueAt;

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

    /// <summary>
    /// The two command lines. <see cref="_input"/> is the one every window has; <see cref="_second"/>
    /// is shown per window and sends to the same place — the point is two persistent drafts, not two
    /// destinations. <see cref="_armed"/> is the one ⏎ sends from, and is what the caret sits on.
    /// </summary>
    private readonly InputBarControl _input = new();
    private readonly InputBarControl _second = new();
    private InputBarControl _armed;
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
    /// The <see cref="ImageControl"/> drawing each decoded image, kept across pane rebuilds. A page's
    /// images arrive one at a time and every arrival rebuilds the pane area, so a control built fresh
    /// each time would be a new control per image per rebuild — and under Kitty each control owns a
    /// transmitted image the framework only deletes when the control it belongs to is re-parented or
    /// disposed. Reusing the control keeps that bookkeeping with the framework, where it belongs.
    /// </summary>
    private readonly Dictionary<int, ImageControl> _webImageControls = new();

    /// <summary>
    /// Cancels the in-flight image fetches of a superseded page. Loading is per-page and a new
    /// navigation invalidates the old one's images outright.
    /// </summary>
    private CancellationTokenSource? _webImageCts;

    /// <summary>
    /// The in-flight image load started by <see cref="StartWebImageLoad"/>. Kept so a headless caller
    /// (the <c>web</c> snapshot) can wait for the pictures before rendering its one frame; the live
    /// app never waits on it.
    /// </summary>
    private Task _webImageLoad = Task.CompletedTask;

    private readonly CommandPalette _palette;
    private readonly SettingsOverlay _settings;

    /// <summary>The ⌃P ▸ <c>Show client messages</c> viewer over the diagnostics log.</summary>
    private readonly MessageLogOverlay _messageLog;

    /// <summary>The ⌃Q confirmation. Nothing ends the loop except a yes it collected.</summary>
    private readonly QuitOverlay _quit;

    /// <summary>
    /// The ⌃R history surface: the armed command line's own history, newest first, filtered by typing.
    /// ⏎ there inserts an entry; it never sends one.
    /// </summary>
    private readonly HistorySurface _historySearch;

    /// <summary>Whether a confirmed quit has asked the loop to end — the headless view of the exit.</summary>
    private bool _exiting;

    /// <summary>Per-world accents when a world hasn't set its own, keyed by position.</summary>
    internal static readonly TerminalColor[] AccentPalette =
    {
        TerminalColor.FromRgb(0x00, 0xf5, 0xb7), // teal
        TerminalColor.FromRgb(0xff, 0x9f, 0x1c), // amber
        TerminalColor.FromRgb(0x9d, 0x7c, 0xff), // violet
        TerminalColor.FromRgb(0x5f, 0xaf, 0xff), // sky
    };

    /// <summary>The client diagnostics pipeline: the message log, the rolling file, the level switch.</summary>
    private readonly ClientDiagnostics _diagnostics;

    /// <summary>Whether this app built its own pipeline (and so must dispose it) or was handed one.</summary>
    private readonly bool _ownsDiagnostics;

    /// <summary>The capped in-memory history behind ⌃P ▸ <c>Show client messages</c>.</summary>
    private readonly ClientMessageLog _messages;

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

    /// <summary>
    /// The chords the app claims globally, by the action each runs. It is the same delegate
    /// <c>RegisterGlobalShortcut</c> was handed, kept so <see cref="SimulateKey"/> can run the shortcuts
    /// in the order the framework does — a headless test never enters <c>Run()</c>, where that ordering
    /// otherwise lives.
    /// </summary>
    private readonly Dictionary<(ConsoleModifiers Modifiers, ConsoleKey Key), Func<bool>> _shortcuts = new();

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

    /// <param name="time">
    /// The clock and timer source the NAWS rate limit runs on. Defaults to the real one; a test
    /// passes a manual provider so "the trailing update lands once the frames stop" is an assertion
    /// rather than a sleep.
    /// </param>
    /// <param name="diagnostics">
    /// The client diagnostics pipeline (see <see cref="ClientDiagnostics"/>): where transient status
    /// notices are recorded and where the telnet stack's own logging arrives. Defaults to a
    /// memory-only one, so constructing an app in a test or a snapshot leaves no file behind; the real
    /// entry point passes one with a rolling file.
    /// </param>
    public SharpMUTermApp(
        AppConfiguration config,
        TerminalCapabilities capabilities,
        IConsoleDriver? driver = null,
        TimeProvider? time = null,
        ClientDiagnostics? diagnostics = null)
    {
        _config = config;
        _capabilities = capabilities;
        _time = time ?? TimeProvider.System;
        _diagnostics = diagnostics ?? ClientDiagnostics.InMemory();
        _ownsDiagnostics = diagnostics is null;
        _messages = _diagnostics.Messages;
        _sessions.Logger = _diagnostics.For("SharpMUTerm.Session");
        _theme = ResolveTheme(config);
        _formatter = new MarkupFormatter(_theme, config.Text);
        _drafts = new DraftStore(() => config.Input.KeepDrafts);
        _secondBars = new InputBarVisibility(() => config.Input.SecondBar);

        // Both recall lists are built with the credential rule wired in, rather than filtering at the one
        // call site that adds to them: the rule is an invariant of what history may contain, and a store
        // that trusted its callers would be one bad call away from holding a password. The setting is read
        // per line (see InputHistory's ctor), so unticking it takes effect on the next command.
        _history = new InputHistory(ignore: IgnoreForHistory);
        _secondHistory = new InputHistory(ignore: IgnoreForHistory);
        _armed = _input;

        // Resume the last session's workspace (panes/windows/focus) when the config carries one;
        // otherwise start with a single main window. Real startup and the demo share this path.
        _workspace = ResumeOrNew(config);

        var headless = driver is HeadlessConsoleDriver;
        _headless = headless;

        // No desktop panels, in any driver. The framework's defaults are a top bar carrying the
        // assembly name and a clock, and a bottom bar whose TaskBarElement lists every window's title
        // ellipsised to fifteen cells — which on a single maximised frameless client is one row of
        // "SharpMU...lient" and nothing else. Both restate what the app's own header band already
        // says, both cost a row of the workspace, and neither was ever visible in a snapshot (they
        // were off in headless only), so the frames we verify against now match a real terminal.
        //
        // ExitKey off. The framework carries a quit-from-anywhere key of its own, defaulting to the very
        // chord we register (ConsoleWindowSystemOptions.ExitKey, InputCoordinator.cs:144), and it calls
        // RequestExit with nothing in between. Ours wins today only because an application global
        // shortcut is tried first and ours returns true — a second door standing open behind the
        // confirmation, which is one refactor away from being the door that gets used. There is exactly
        // one way out of this client now, and it goes through QuitOverlay.
        var options = new ConsoleWindowSystemOptions(
            ShowTopPanel: false,
            ShowBottomPanel: false,
            EnableAnimations: !headless,
            ExitKey: null);
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

        // The input area is one or two bars pinned above the status line. Each paints its own full-width
        // band (see InputBarControl), so the row reads as solid from the prompt to the right edge with
        // no gap where a label ends. Draft-safe history is ours (InputHistory) and per bar.
        SetUpBar(_input, InputBar.Primary);
        SetUpBar(_second, InputBar.Secondary);
        _second.Visible = false;

        _statusBar = Controls.Markup("[dim]not connected[/]").StickyBottom().Build();

        // The window paints the backdrop, not the text background: everything that is not a pane — the
        // connection rail, the status line, the gaps a split leaves — sits on it, so the panes read as
        // raised surfaces and an empty one is still a visible rectangle. See WorkspacePalette.
        var bg = ToColor(WorkspacePalette.Backdrop(_theme));
        var fg = ToColor(_theme.Resolve(TerminalColor.Default, isBackground: false));

        // The title is never drawn — the window is frameless and there is no task bar left to list it
        // in — so it is the app's name for diagnostics (the framework logs windows by title) and not a
        // caption. Hence the bare name rather than the old tagline, which only ever appeared as the
        // truncated "SharpMU...lient" the task bar made of it.
        _window = new WindowBuilder(_system)
            .WithTitle("SharpMUTerm")
            .Maximized()
            .Frameless() // no outer chrome — the workspace fills the whole screen for maximum room
            .WithColors(fg, bg)
            .AddControl(_header)
            .AddControl(_workspaceRow)
            .AddControl(_input)
            .AddControl(_second)
            .AddControl(_statusBar)
            .Build();

        _palette = new CommandPalette(_system, BuildCatalog, () => _active?.SessionKey, id => DispatchCommand(id));
        _messageLog = new MessageLogOverlay(_system, _diagnostics);
        _settings = new SettingsOverlay(_system, SaveConfiguration);
        _quit = new QuitOverlay(_system, QuitFactsNow, Quit);

        // Everything the history surface needs is read at the moment it opens, so it is always the armed
        // command line's own list — history is per bar, and the surface must not outlive that fact.
        _historySearch = new HistorySurface(
            _system,
            () => HistoryFor(BarKind(ActiveBar())).Entries,
            HistoryBarLabel,
            InsertHistoryEntry);

        _window.OnResize += (_, _) =>
        {
            // NAWS is deliberately not reported from here. At this moment the panes still carry the
            // *old* window's arranged rectangles — the new ones don't exist until the next frame is
            // laid out — so a report made here would announce a size that was already wrong. The
            // repaint this resize forces reports them once they are real; see ReportPaneSizes.
            _header.SetContent(new List<string> { HeaderMarkup() }); // re-align the status cluster to the new width
            SyncInputWidth(); // keep the input band spanning the full row after a resize
            SyncInputBars();  // and re-derive how tall the bars may grow in the new window
        };

        // Pinning each bar's Width to the window makes its band paint edge to edge; without it a bar
        // measures to its content and the row stops mid-screen.
        SyncInputWidth();
        SyncInputBars();

        // The command line starts with the keyboard. It is the whole reason the per-window drafts read
        // as broken: SharpConsoleUI focuses nothing on its own, the app never asked, and so every plain
        // keystroke went to a control that had no use for it — no typing reached the prompt, no draft
        // was ever recorded, and every tab switch recalled the empty string it had stored.
        _window.FocusControl(_input);

        // …and keeps it. SharpConsoleUI hands a key — and a paste — to whichever control holds focus,
        // and anything the mouse lands on takes it: a click in the output pane focuses that pane's
        // MarkupControl, a click on a tab strip focuses the TabControl, and ⇥ with a single command line
        // up walks focus off the input area altogether. Typing survived all of that because
        // HandleWindowKey routes it to the armed bar explicitly; paste does not go through that handler
        // at all, so a paste after a click was delivered to a control that is not an IPasteTarget and
        // dropped without a trace. The caret went with it — nothing else in this window reports a
        // logical cursor, so the terminal's cursor simply vanished.
        //
        // So focus is not something this window is allowed to drift. It belongs to the armed command
        // line, and this puts it back the instant anything takes it, which is what makes "which bar ⏎
        // sends from", "which control the framework will paste into" and "where the caret is drawn" one
        // fact rather than three that agree until they don't.
        _window.FocusManager.FocusChanged += (_, _) => PinFocusToArmedBar();
        _window.PreviewKeyPressed += OnWindowKey;

        // NAWS rides the frame. Pane rectangles exist only while an arranged layout does, and every
        // layout change (a resize, a split, a closed tab, a zoom, a window moved between panes) tears
        // the pane area down and rebuilds it — so inside RebuildPaneArea, where the change is made,
        // there is nothing to measure yet. PostBufferPaint is raised after the arrange pass, which
        // makes it the first moment the new layout can be read, and every one of those changes
        // repaints. One hook therefore covers the lot, and none of them can be forgotten later.
        // (The event's adder is a silent no-op while a window has no renderer; this one has had one
        // since its constructor ran, and NawsPaneReportTests fails loudly if that ever stops holding.)
        _window.PostBufferPaint += (_, _, _) => ReportPaneSizes();
        // Pane drag-and-drop listens at the driver, not at a control: SharpConsoleUI delivers mouse
        // frames to the control that was pressed (it captures on Button1Pressed), so a control-level
        // handler would only ever see the *source* pane. The driver stream carries every frame in
        // desktop cells, which is exactly what a drag between panes needs.
        _system.ConsoleDriver.MouseEvent += OnDriverMouseEvent;
        RegisterGlobalShortcuts();
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
            ReArmWholeFrame();
        }

        // More output than a pane can hold — the state in which the client showed its oldest screenful
        // for ever and every new line landed off-screen. `scrollback` is the settled live tail;
        // `scrollback-up` is the same buffer after real PgUp keystrokes, so the frame carries an earlier
        // region *and* the status row's scrollback segment. Every other view fits in a pane, which is
        // precisely why no snapshot ever caught this.
        if (string.Equals(view, "scrollback", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(view, "scrollback-up", StringComparison.OrdinalIgnoreCase))
        {
            LoadLongScene(MainWindowId, ScrollbackSceneLines);
            SettleScroll();

            if (string.Equals(view, "scrollback-up", StringComparison.OrdinalIgnoreCase))
            {
                SimulateKey(new ConsoleKeyInfo('\0', ConsoleKey.PageUp, false, false, false));
                SimulateKey(new ConsoleKeyInfo('\0', ConsoleKey.PageUp, false, false, false));
                ReArmWholeFrame();
            }
        }

        // A frozen pane whose pinned half holds far more than its three-quarters of the pane, scrolled
        // up inside it — the two features composing, which is the frame that says whether they do.
        if (string.Equals(view, "freeze-scrollback", StringComparison.OrdinalIgnoreCase))
        {
            LoadLongScene(MainWindowId, ScrollbackSceneLines);
            ToggleFreeze();
            LoadLongScene(MainWindowId, 6, first: ScrollbackSceneLines + 1); // a few lines into the live tail
            SettleScroll();
            SimulateKey(new ConsoleKeyInfo('\0', ConsoleKey.PageUp, false, false, false));
            ReArmWholeFrame();
        }

        // The web view with an inline picture: a small page whose <img> is a data: URI, driven through
        // the same render → fetch → decode → compose path a /web command takes, so the frame shows a
        // genuinely decoded image rather than an impression of one.
        if (string.Equals(view, "web", StringComparison.OrdinalIgnoreCase))
        {
            ShowDemoWebPage();
        }

        // History-recall state: seed a couple of sent commands, then recall the newest so the input
        // shows a recalled line and the gutter shows the "history · ↓ back to draft" affordance.
        if (string.Equals(view, "history", StringComparison.OrdinalIgnoreCase))
        {
            _history.Add("look");
            _history.Add("say Well met, traveller.");
            if (_history.Recall("wh") is { } recalled)
            {
                _input.Text = recalled;
                UpdateInputChrome();
            }
        }

        // The ⌃R history surface, over a command line that has a history to show. `history-search` is the
        // state it opens in — the plain chronological list, newest first — and `history-search-filter`
        // types a real query in through the surface's own key handler, so the frame shows the filter, the
        // marked matches and the narrowed count rather than an impression of them. Deliberately not called
        // `history`: that view is ↑/↓ recall in the command line, and this is the surface over it.
        if (view is not null && view.StartsWith("history-search", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var command in new[]
            {
                "look",
                "say Well met, traveller.",
                "page Rookery = the courier came through the north gate",
                "+who",
                "pose leans on the fountain's rim, watching the plaza fill.",
                "say the northern watch sent word",
                "score",
            })
            {
                _history.Add(command);
            }

            // A line nobody should find in the surface, entered exactly as a user would enter it. It is
            // seeded here on purpose: the frame is where "the password is not in the list" is visible.
            _history.Add("connect Corvid hunter2");

            _historySearch.OpenForSnapshot();
            if (view.EndsWith("-filter", StringComparison.OrdinalIgnoreCase))
            {
                _historySearch.SimulateTyping("north");
            }
        }

        // The command line carrying a real draft: one long enough to wrap, so the frame shows the bar
        // grown past its floor instead of a single row scrolled sideways. `draft2` additionally raises
        // this window's second bar and puts an OOC line in it, with ⏎ armed on the second — the pair of
        // states no amount of staring at the default frame would show.
        if (string.Equals(view, "draft", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(view, "draft2", StringComparison.OrdinalIgnoreCase))
        {
            _input.SetAndNotify(
                "pose walks slowly across the plaza, pausing at the fountain to trail a hand through the "
                + "cold water, then turns north toward the gate where the courier is catching breath.");

            if (string.Equals(view, "draft2", StringComparison.OrdinalIgnoreCase))
            {
                ToggleSecondBar();
                _second.SetAndNotify("ooc back in five — kettle");
                ArmBar(_second);
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

        // The client message viewer, over a demo scene that has said a few things. It is the only way to
        // look at the surface without a terminal, and the messages are seeded here rather than faked in
        // the view: what the snapshot shows is what Notice actually records.
        if (string.Equals(view, "messages", StringComparison.OrdinalIgnoreCase))
        {
            RefusePrefix("nothing to split — a split moves this pane's other tabs across, and it has none");
            RefuseCommand("nothing to reconnect — pick a character first (⌃P ▸ Switch to …)");
            Notice("switched to Corvid · offline — ⌃P ▸ Reconnect connects it", MessageSeverity.Info, "⌃P");
            Notice("could not connect to aetherfall.mux:4201 — no route to host", MessageSeverity.Error, "⌃P");
            _messageLog.Toggle();
        }

        // The ⌃Q confirmation, over a workspace that has something to lose: a second world marked
        // connected (the demo scene's own way of saying so, and what the header's "n/m connected" reads),
        // a line typed into the command line through the bar's real change notification, and then the
        // registered shortcut itself — so the frame shows what pressing ⌃Q does, not an impression of it.
        if (string.Equals(view, "quit", StringComparison.OrdinalIgnoreCase))
        {
            if (_config.Worlds.ElementAtOrDefault(1) is { Characters.Count: > 0 } second)
            {
                _connectedKeys.Add($"{second.Name}.{second.Characters[0].Name}");
                _header.SetContent(new List<string> { HeaderMarkup() }); // the band counts them: 2/2
            }

            _input.SetAndNotify("say back in a moment — kettle's on");
            _shortcuts[(ConsoleModifiers.Control, ConsoleKey.Q)]();
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
    /// Renders one more frame, the way a test drives the client on past a change it has just made —
    /// a split, a resize, a session that has only now connected. It marks the window dirty first
    /// because <c>RenderCoordinator.RenderWindows</c> skips any window with no pending work, so a
    /// second <see cref="RenderFrame"/> on an untouched window would arrange nothing and paint
    /// nothing, and the layout a test is waiting to read would never be built.
    /// <para>
    /// It exists for <see cref="ReportPaneSizes"/>, which rides the paint: NAWS is announced from the
    /// frame that realises a layout, so proving it is announced means driving a real frame.
    /// </para>
    /// </summary>
    internal void RenderNextFrame()
    {
        _system.ForceFullRepaint();
        RenderFrame();
    }

    /// <summary>
    /// Renders one more frame and hands back the <em>whole</em> frame, not the cells that changed. A
    /// second render over a populated front buffer emits a delta, which is right for a terminal and
    /// useless to a reader — so the headless driver is re-initialised first (a fresh buffer) and a full
    /// repaint forced, the same recipe the <c>drag</c> snapshot uses for the same reason.
    /// </summary>
    internal string RenderWholeFrame()
    {
        ReArmWholeFrame();
        return RenderFrame();
    }

    /// <summary>
    /// Renders a frame and leaves the driver ready to emit the next one in full. Scrolled panes need it:
    /// <c>ScrollablePanelControl</c>'s auto-scroll moves the offset <em>during</em> paint (its children
    /// were already arranged at the old one) and then asks for a relayout, so the frame that discovers a
    /// pane has overflowed is still showing the top of it and only the next frame shows the tail. A
    /// terminal spends 16 ms getting there and nobody sees it; a one-frame snapshot would publish the
    /// stale frame as the answer.
    /// </summary>
    private void SettleScroll()
    {
        RenderFrame();
        ReArmWholeFrame();
    }

    /// <summary>Gives the headless driver a fresh buffer and marks everything dirty, so the next render is whole.</summary>
    private void ReArmWholeFrame()
    {
        if (_system.ConsoleDriver is HeadlessConsoleDriver headless)
        {
            headless.Initialize(_system);
        }

        _system.ForceFullRepaint();
    }

    /// <summary>
    /// Drives a genuine pane drag through the headless driver for the <c>drag</c> snapshot: primary
    /// button down on the first pane's tab strip, then a drag frame over the second pane's left edge.
    /// The button is deliberately left down so the frame captures the live drop preview. Requires a
    /// frame to have been rendered already, so the panes have real bounds to hit.
    /// <para>
    /// The auto-repeat frame between them is the host's, not a terminal's: SharpConsoleUI's Unix reader
    /// re-raises a bare <c>Button1Pressed</c> at the pointer's current cell every 100 ms while the
    /// button is held. It is here because the frame this snapshot documents is the one a real mouse
    /// produces, and a real mouse never gets to the drop without passing through several of these — and
    /// while they were read as fresh presses, this preview was what flashed up and vanished.
    /// </para>
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

        var origin = new System.Drawing.Point(source.X + 2, source.Y);
        var drop = new System.Drawing.Point(target.X + 1, target.Y + (target.Height / 2));

        driver.SimulateMouseEvent(new List<MouseFlags> { MouseFlags.Button1Pressed }, origin);
        driver.SimulateMouseEvent(new List<MouseFlags> { MouseFlags.Button1Pressed }, origin); // auto-repeat

        driver.SimulateMouseEvent(
            new List<MouseFlags> { MouseFlags.Button1Pressed, MouseFlags.Button1Dragged, MouseFlags.ReportMousePosition },
            drop);

        driver.SimulateMouseEvent(new List<MouseFlags> { MouseFlags.Button1Pressed }, drop); // and another
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
        _input.SetAndNotify("say hello there");
        RebuildPaneArea(); // realise the Chat spawn tab, then refresh badges
    }

    /// <summary>
    /// Lines the <c>scrollback</c> snapshots and the scrollback tests feed: comfortably more than any
    /// pane at any terminal size the snapshot pipeline uses, so "the tail is visible" is a claim about
    /// scrolling rather than about a buffer that happened to fit.
    /// </summary>
    internal const int ScrollbackSceneLines = 240;

    /// <summary>
    /// Feeds <paramref name="count"/> numbered lines into a window, through the same
    /// <see cref="AppendWindowLine"/> path a session's output takes. Each line names its own number, so a
    /// rendered frame says <em>which</em> region of the buffer is on screen instead of only that
    /// something is — the difference between a test that would have caught the scrolling defect and one
    /// that would have passed while the client showed line 1 for ever.
    /// </summary>
    internal void LoadLongScene(string windowId, int count, int first = 1)
    {
        for (var i = 0; i < count; i++)
        {
            AppendWindowLine(windowId, ScrollbackSceneLine(first + i));
        }
    }

    /// <summary>The markup of one numbered scene line. Shared so a test can look for the exact text.</summary>
    internal static string ScrollbackSceneLine(int number) =>
        $"[dim]line[/] [bold]{number:0000}[/] [dim]· the courier's road runs on past the northern watch[/]";

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
            Notice("No world configured. Pass a host/port on the command line, or add one on F5.");
            return;
        }

        var session = OpenSession(world);
        BindSession(session);

        session.PrintSystem($"*** SharpMUTerm — theme '{_theme.Name}', graphics: {_capabilities.Protocol}.");

        try
        {
            await session.ConnectAsync().ConfigureAwait(false);

            // A freshly connected session has never been told anything, and there is no guarantee of
            // another frame soon enough to matter — so announce now, on the UI thread, where the pane
            // geometry and the report bookkeeping live.
            OnUiThread(ReportPaneSizes);
        }
        catch
        {
            // WorldSession already surfaced the failure as a system line.
        }
    }

    /// <summary>
    /// Builds the session for a world as a given <paramref name="character"/> — or, with none named, as
    /// its <em>first configured character</em> — so the character's trigger sets, auto-login, on-connect
    /// lines and log actually reach the runtime. A world with no characters still connects, anonymously,
    /// which is what a host typed on the command line is.
    /// <para>
    /// This is the seam the F2/F3/F5/F6 screens all hang off: the session holds the <em>same</em>
    /// <see cref="Trigger"/>/<see cref="Alias"/>/<see cref="TimerDefinition"/> objects the screens
    /// edit, so editing one is seen by the next line without a reload. Adding or removing a rule is
    /// not — the engines were handed the list at construction — so that still needs a reconnect.
    /// </para>
    /// <para>
    /// Picking a <em>different</em> character is <see cref="SwitchToCharacter"/>: it opens a second
    /// session through here rather than re-pointing this one, because a session's automation, log and
    /// scrollback all belong to the character it was opened as.
    /// </para>
    /// </summary>
    private WorldSession OpenSession(WorldDefinition world, CharacterDefinition? character = null)
    {
        character ??= world.Characters.FirstOrDefault();
        return character is null
            ? _sessions.Open(
                world,
                _config.ScrollbackLines,
                _config.Text,
                _config.Input,
                TelnetFactory,
                _config.ScrollbackSpill)
            : _sessions.Open(
                world,
                character,
                _config.ResolveTriggerSets(character),
                _config.ScrollbackLines,
                OpenLog(world, character),
                _config.Text,
                _config.Input,
                TelnetFactory,
                _config.ScrollbackSpill);
    }

    /// <summary>
    /// The transport every session this app opens connects through. Null (the default) is the real
    /// telnet stack. Internal because a headless test dispatching <c>Reconnect</c> has to be able to
    /// watch a connect happen without one reaching the network.
    /// </summary>
    internal Func<ConnectionOptions, ITelnetSession>? TelnetFactory { get; set; }

    /// <summary>
    /// Opens the character's log sink for this session, per its <see cref="LoggingSettings"/> — the
    /// two fields F5 draws on the character's own row. <see cref="LogFormat.None"/> (the default)
    /// opens nothing, and a folder that can't be written is reported as a system line rather than
    /// taken as a reason not to connect.
    /// <para>
    /// Resolved once, at connect: a log file is a handle, and re-pointing one mid-session would mean
    /// closing a file the user is still tailing. The F5 fields therefore apply on the next connect,
    /// which is what the screen says.
    /// </para>
    /// </summary>
    /// <summary>
    /// Where a character's session log is written: its own configured folder, or the app's default
    /// <c>logs</c> directory. The same folder the client's diagnostics file lives in, and deliberately
    /// not the same file — one is a transcript someone keeps, the other is client chrome.
    /// </summary>
    private static string LogFolder(CharacterDefinition? character) =>
        string.IsNullOrWhiteSpace(character?.Logging.Directory)
            ? Path.Combine(Path.GetDirectoryName(ConfigurationStore.DefaultPath)!, "logs")
            : character!.Logging.Directory!;

    private ILogSink? OpenLog(WorldDefinition world, CharacterDefinition? character, LogFormat? forceFormat = null)
    {
        var format = forceFormat ?? character?.Logging.Format ?? LogFormat.None;
        if (format == LogFormat.None)
        {
            return null;
        }

        var folder = LogFolder(character);
        var stem = $"{world.Name}.{character?.Name ?? "anonymous"}-{DateTime.Now:yyyyMMdd-HHmmss}"
            .Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_');

        try
        {
            var sinks = new List<ILogSink>(2);
            if (format is LogFormat.Plain or LogFormat.Both)
            {
                sinks.Add(PlainTextLogSink.CreateFile(Path.Combine(folder, stem + ".log")));
            }

            if (format is LogFormat.Html or LogFormat.Both)
            {
                sinks.Add(HtmlLogSink.CreateFile(Path.Combine(folder, stem + ".html"), stem));
            }

            return sinks.Count == 1 ? sinks[0] : new CompositeLogSink(sinks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Notice($"could not open the log: {ex.Message}", MessageSeverity.Error);
            return null;
        }
    }

    /// <summary>
    /// Makes a session the active one and wires its output into <paramref name="windowId"/> — the main
    /// window for the session the shell starts with, and a tab of its own for every character switched
    /// to afterwards. The window id is a parameter rather than the constant it used to be because the
    /// routing and the NAWS registry have to name the same window: report a pane the session doesn't
    /// print into and the server is told the size of something else.
    /// </summary>
    private void BindSession(WorldSession session, string? windowId = null)
    {
        windowId ??= MainWindowId;

        _active = session;
        AttachSession(session, windowId);
        if (_workspace.FindWindow(windowId) is { } window)
        {
            // The main window is titled for the world (it is the shell's own window); a character's own
            // window is titled for the character, which is what tells two of them apart in a tab strip.
            window.Title = windowId == MainWindowId ? session.World.Name : SessionTitle(session);
        }

        var main = PaneContentFor(windowId, session.World.Name);
        foreach (var line in session.Scrollback.Snapshot())
        {
            main.AppendLine(_formatter.ToMarkup(line));
        }

        session.LinePrinted += (_, line) => OnUi(() => OnLine(windowId, line));
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

    /// <summary>
    /// Registers the window a session's output lands in, which is what
    /// <see cref="ReportPaneSizes"/> resolves that session's pane — and so its NAWS size — through.
    /// Re-registering a session forgets the size it was told, because the window it is being pointed
    /// at is a different rectangle until proven otherwise.
    /// <para>
    /// Internal as well as called from <see cref="BindSession"/>: it is the seam the NAWS tests attach
    /// a session over a fake telnet transport with, there being no other way to have two connected
    /// worlds in a headless frame.
    /// </para>
    /// </summary>
    internal void AttachSession(WorldSession session, string windowId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(windowId);
        _sessionWindows[session] = windowId;
        _sizeReports.Remove(session);
    }

    /// <summary>The session printing into a window, or null when the window belongs to no connection.</summary>
    private WorldSession? SessionFor(string windowId)
    {
        foreach (var (session, id) in _sessionWindows)
        {
            if (string.Equals(id, windowId, StringComparison.Ordinal))
            {
                return session;
            }
        }

        return null;
    }

    /// <summary>What a session's own window is called: its character, or the world for an anonymous one.</summary>
    private static string SessionTitle(WorldSession session) =>
        session.Character?.Name ?? session.World.Name;

    /// <summary>The window id a switched-to character's session prints into.</summary>
    private static string CharacterWindowId(string sessionKey) => $"char:{sessionKey}";

    /// <summary>The configured world + character a <c>world.character</c> session key names, or null.</summary>
    private (WorldDefinition World, CharacterDefinition Character)? FindCharacter(string sessionKey)
    {
        foreach (var world in _config.Worlds)
        {
            foreach (var character in world.Characters)
            {
                if (string.Equals($"{world.Name}.{character.Name}", sessionKey, StringComparison.Ordinal))
                {
                    return (world, character);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The command surface's <c>Switch to …</c>: makes a configured character the active session — the
    /// one the command line talks to, the one the status line and rail describe, and the one
    /// <c>Reconnect</c> acts on. It was the one entry the catalog offered and nothing implemented, so
    /// selecting a character fell through to the "isn't wired" arm — which, reporting through the
    /// active session, said nothing at all when there wasn't one.
    /// <para>
    /// A character with no session yet gets one opened here, as itself (its trigger sets, auto-login and
    /// log), and a window to print into: the main window while that is still free, otherwise a tab of
    /// its own, because two characters sharing one buffer would interleave their output.
    /// </para>
    /// <para>
    /// <strong>Switching does not connect.</strong> A character whose session exists but is offline is
    /// focused exactly like a connected one, and the status row says which it is and what connects it.
    /// Switching is navigation — the command that dials is <c>Reconnect</c>, and a "switch" that also
    /// opened a socket would make choosing a character to <em>look</em> at impossible.
    /// </para>
    /// </summary>
    private void SwitchToCharacter(string sessionKey)
    {
        var session = _sessions.Find(sessionKey);
        if (session is null)
        {
            if (FindCharacter(sessionKey) is not { } found)
            {
                Notice($"no character called {sessionKey} is configured — F5 adds one", key: "⌃P");
                return;
            }

            session = OpenSession(found.World, found.Character);
            BindSession(session, OpenSessionWindow(session));
        }
        else
        {
            _active = session;
        }

        // Its tab can have been closed since it was bound; OpenSessionWindow puts it back rather than
        // switching to a session with nowhere to print.
        Activate(OpenSessionWindow(session));
        UpdateStatus();
        UpdateInputChrome();

        var state = session.IsConnected
            ? "connected"
            : "offline — ⌃P ▸ Reconnect connects it";
        Notice($"switched to {SessionTitle(session)} · {state}", MessageSeverity.Info, "⌃P");
    }

    /// <summary>
    /// Ensures a session has a workspace window and returns its id: the main window while nothing else
    /// has claimed it (the shell starts there), otherwise a tab of the character's own. Realises the tab
    /// before anything tries to activate it.
    /// </summary>
    private string OpenSessionWindow(WorldSession session)
    {
        var windowId = _sessionWindows.TryGetValue(session, out var known)
            ? known
            : SessionFor(MainWindowId) is null ? MainWindowId : CharacterWindowId(session.SessionKey);

        if (_workspace.FindWindow(windowId) is null)
        {
            _workspace.OpenWindow(windowId, SessionTitle(session), WindowKind.Main, session.SessionKey);
            PaneContentFor(windowId, SessionTitle(session));
            RebuildPaneArea();
        }

        return windowId;
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

    /// <summary>
    /// Puts <paramref name="count"/> lines of a window's buffer, starting at <paramref name="from"/>,
    /// into one output control.
    /// <para>
    /// This and the <c>AppendLine</c> in <see cref="AppendWindowLine"/> are <b>the whole seam between the
    /// line buffer and the controls that draw it</b> — no other code hands pane content to a control.
    /// That is deliberate. Today every control is fed its region in full, which is what makes appending
    /// expensive: <c>MarkupControl</c>'s parse cache is keyed on a content version and <c>AppendLine</c>
    /// bumps it, so one arriving line re-parses everything the control holds (~50 ms at 5,000 lines).
    /// The fix is a <em>windowed</em> feed — give the control only the slice the viewport can show and
    /// re-feed it as the viewport moves — and when that lands it replaces these two methods and nothing
    /// else: a range feed is already a range, and the append becomes "re-window if the tail is visible".
    /// Keep new callers going through here rather than touching a control's content directly.
    /// </para>
    /// </summary>
    private static void FeedRange(MarkupControl control, List<string> buffer, int from, int count)
    {
        var start = Math.Clamp(from, 0, buffer.Count);
        var length = Math.Clamp(count, 0, buffer.Count - start);
        control.SetContent(buffer.GetRange(start, length));
    }

    /// <summary>The trigger pattern that routes to a spawn <paramref name="target"/>, for its capture line.</summary>
    private string? CaptureFor(string target) =>
        _config.TriggerSets.SelectMany(s => s.Triggers)
            .FirstOrDefault(t => string.Equals(t.Actions.SpawnTarget, target, StringComparison.Ordinal))
            ?.Pattern;

    /// <summary>
    /// Appends a line to a window's pane and badges it unread when the reader cannot see where it landed.
    /// <para>
    /// That is <see cref="Workspace.IsCaughtUp"/> and not <c>IsVisible</c>: a visible tab whose output the
    /// reader has scrolled back off is exactly as blind as a tab they are not looking at, and asking only
    /// about visibility meant new output arrived silently below the viewport — the one state in which a
    /// badge is the only thing that could tell them.
    /// </para>
    /// </summary>
    private void OnLine(string windowId, StyledLine line)
    {
        AppendWindowLine(windowId, _formatter.ToMarkup(line, Stamp()));

        if (!_workspace.IsCaughtUp(windowId))
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

    private void OnCommandEntered(InputBar bar, string command)
    {
        // The entered command clears this window's draft for the bar it came from and its unsent-input
        // marker, and joins that bar's draft-safe history so ↑/↓ can recall it without clobbering a
        // future draft. The bar has already emptied itself — ⏎ never moves the caret off it.
        HistoryFor(bar).Add(command);
        var windowId = ActiveWindowId();
        _drafts.Clear(windowId, bar);
        _workspace.SetUnsentInput(windowId, AnyBarHasText());
        RefreshTabTitles();

        // `/web <url>` opens the in-TUI web view; everything else goes to the world.
        if (command.StartsWith("/web ", StringComparison.OrdinalIgnoreCase))
        {
            OpenWeb(command[5..].Trim());
            return;
        }

        // `/graphics` reports where the degradation chain settled and, when it degraded, why — so a
        // missing picture is an explanation rather than a mystery — and then what the page in the web
        // view actually did with its images, which is the difference between "nothing arrived" and
        // "it arrived and looks wrong".
        if (command.Trim().Equals("/graphics", StringComparison.OrdinalIgnoreCase))
        {
            // Appended to the window rather than routed through the session, so it still answers
            // when nothing is connected — which is exactly when someone is checking their terminal.
            var report = InlineImagePolicy.Describe(_capabilities, WebGraphicsSurface());
            AppendWindowLine(windowId, $"[dim]*** Graphics: {Escape(report)}.[/]");
            foreach (var line in WebImageReport.Describe(_webPage, DecodedWebImages(), ResolveInlineImagePresentation()))
            {
                AppendWindowLine(windowId, $"[dim]*** {Escape(line)}[/]");
            }

            return;
        }

        _ = _active?.SendUserInputAsync(command);
    }

    /// <summary>Tracks the per-window input draft and the <c>✎</c> unsent-input marker as you type.</summary>
    private void OnInputChanged(InputBar bar, string text)
    {
        // A recall sets the bar's text without raising this, so a recalled draft is not re-recorded and
        // the history cursor survives. A genuine keystroke while recalling re-bases the recalled line.
        if (HistoryFor(bar).IsRecalling)
        {
            HistoryFor(bar).Rebase();
        }

        var windowId = ActiveWindowId();

        // The store decides whether to keep it — that is where F8's "keep per-tab drafts" lives.
        _drafts.Record(windowId, bar, text);

        _workspace.SetUnsentInput(windowId, AnyBarHasText());
        RefreshTabTitles();
        UpdateInputChrome();
    }

    /// <summary>The window id of the visible tab (the input line belongs to it).</summary>
    private string ActiveWindowId() => _workspace.Layout.FocusedPane.ActiveTab ?? MainWindowId;

    /// <summary>The bar ⏎ currently sends from — the armed one, and the only one with a caret.</summary>
    private InputBarControl ActiveBar() => _armed;

    /// <summary>Which of the two a bar is, for the draft store and the history lists.</summary>
    private InputBar BarKind(InputBarControl bar) =>
        ReferenceEquals(bar, _second) ? InputBar.Secondary : InputBar.Primary;

    /// <summary>The recall list belonging to a bar. Each keeps its own; see <see cref="_secondHistory"/>.</summary>
    private InputHistory HistoryFor(InputBar bar) => bar == InputBar.Secondary ? _secondHistory : _history;

    /// <summary>
    /// Whether a sent line must be kept out of history — the F8 switch over
    /// <see cref="HistorySecrets.LooksLikeCredential"/>. Passed to both <see cref="InputHistory"/>
    /// instances at construction and asked per line, so the setting takes effect on the next command.
    /// <para>
    /// Only hand-typed logins reach here. A configured character's connect string goes out through
    /// <c>WorldSession.SendLoginAsync</c> → <c>SendRawAsync</c>, which never touches the UI's history at
    /// all; the line this is for is the one someone types on a world they have not configured yet.
    /// </para>
    /// </summary>
    private bool IgnoreForHistory(string command) =>
        _config.Input.ExcludeCredentials && HistorySecrets.LooksLikeCredential(command);

    /// <summary>
    /// Which command line the ⌃R surface is showing the history of, in words — the second bar has its own
    /// list, and a surface that did not say which one it was showing would be indistinguishable from a
    /// surface showing the wrong one.
    /// </summary>
    private string HistoryBarLabel() =>
        BarKind(ActiveBar()) == InputBar.Secondary ? "second command line" : "command line";

    /// <summary>
    /// Puts a history entry on the armed command line — what ⏎ in the ⌃R surface does, and the only thing
    /// it does. It goes through <see cref="InputHistory.RecallAt"/> rather than assigning the text, so the
    /// draft it displaces is parked exactly as <c>↑</c> parks it and <c>↓</c> still walks back to it; and
    /// it records the line as this window's draft, for the same reason <see cref="TryRecallKey"/> does.
    /// <para>
    /// Nothing is sent. Assigning <c>Text</c> raises no change event (see <see cref="RecallDrafts"/>), so
    /// the inserted line is not re-recorded and the recall cursor survives — which is what makes the
    /// following <c>↓</c> mean "back to my draft".
    /// </para>
    /// </summary>
    private void InsertHistoryEntry(int index)
    {
        var bar = ActiveBar();
        var kind = BarKind(bar);
        if (HistoryFor(kind).RecallAt(index, bar.Text) is not { } text)
        {
            return;
        }

        bar.Text = text;
        _drafts.Record(ActiveWindowId(), kind, text);
        UpdateInputChrome();
    }

    /// <summary>
    /// Opens the ⌃R history surface, or closes it when the chord arrives again — the toggle every surface
    /// in this client is on. Ignored while another overlay owns the screen or a move is in progress, for
    /// <see cref="ArmPrefix"/>'s reason: a list floating over a surface the user is already answering
    /// would be a second question nobody asked.
    /// </summary>
    private void ToggleHistorySearch()
    {
        if (_historySearch.IsOpen)
        {
            _historySearch.Toggle();
            return;
        }

        if (AnyOverlayOpen || _moveMode)
        {
            return;
        }

        _historySearch.Toggle();
    }

    /// <summary>
    /// Whether any modal surface owns the screen. They are separate windows, so the framework already
    /// routes keys to them and the main window's handler is not raised at all — this is here because "the
    /// workspace does not act while a surface is up" is a rule of this app rather than a consequence of
    /// how the framework happens to dispatch, and the next surface may not be modal.
    /// </summary>
    private bool AnyOverlayOpen =>
        _palette.IsOpen || _settings.IsOpen || _quit.IsOpen || _messageLog.IsOpen || _historySearch.IsOpen;

    /// <summary>Whether either bar is holding unsent text — what the <c>✎</c> tab marker means.</summary>
    private bool AnyBarHasText() =>
        !_input.Buffer.IsEmpty || (_second.Visible && !_second.Buffer.IsEmpty);

    /// <summary>
    /// Wires one bar: its send, its draft recording, and the two ways the caret can move to it. Both
    /// bars are built the same way and differ only in which draft and which history they carry, which
    /// is the point — the second bar sends to the same window, it just holds a different line.
    /// </summary>
    private void SetUpBar(InputBarControl bar, InputBar kind)
    {
        bar.StickyPosition = StickyPosition.Bottom;
        bar.HorizontalAlignment = HorizontalAlignment.Stretch;
        bar.BandColor = ToColor(new Rgb(0x33, 0x39, 0x4c));
        bar.IdleBandColor = ToColor(new Rgb(0x26, 0x2b, 0x3a));
        bar.TextColor = ToColor(_theme.Resolve(TerminalColor.Default, isBackground: false));
        bar.HasSibling = () => _second.Visible;
        bar.Entered += text => OnCommandEntered(kind, text);
        bar.Changed += text => OnInputChanged(kind, text);
        bar.ActivationRequested += ArmBar;
        bar.CycleRequested += () => ArmBar(ReferenceEquals(bar, _input) ? _second : _input);
    }

    /// <summary>
    /// Makes one bar the one ⏎ sends from: it lights up, takes the caret and the keyboard focus, and
    /// the other dims. Nothing else in the app decides this, so "which bar is armed" and "where the
    /// caret is" cannot disagree.
    /// <para>
    /// A bar that is not on screen is answered with the one that always is. Arming an invisible control
    /// used to be a silent no-op, which left <see cref="_armed"/> pointing at the second command line
    /// after it had been hidden — ⏎ aimed at a bar the window no longer draws, and the caret with it.
    /// </para>
    /// </summary>
    private void ArmBar(InputBarControl bar)
    {
        _armed = bar.Visible ? bar : _input;
        _input.Armed = ReferenceEquals(_armed, _input);
        _second.Armed = ReferenceEquals(_armed, _second);
        _window.FocusControl(_armed);
        UpdateInputChrome();
    }

    /// <summary>
    /// Puts the window's keyboard focus back on the armed command line. See the constructor for why the
    /// app owns this rather than leaving it to the framework: paste and the terminal caret both follow
    /// framework focus, and everything else in this window that can take focus does nothing with it.
    /// </summary>
    private void PinFocusToArmedBar()
    {
        // An overlay owning the keyboard deactivates this window, and the framework clears its focus on
        // the way out and restores it on the way back (Window.SetIsActive). Re-arming mid-deactivation
        // would fight that and would put a caret on a window nobody is typing into. On the way back the
        // window is already active, so this still runs — and the armed bar, not whatever the framework
        // saved, is what the caret returns to.
        if (_pinningFocus || !_window.GetIsActive())
        {
            return;
        }

        var bar = ActiveBar();
        if (ReferenceEquals(_window.FocusManager.FocusedControl, bar))
        {
            return;
        }

        _pinningFocus = true;
        try
        {
            _window.FocusControl(bar);
        }
        finally
        {
            _pinningFocus = false;
        }
    }

    /// <summary>Guards <see cref="PinFocusToArmedBar"/> against re-entering itself through FocusChanged.</summary>
    private bool _pinningFocus;

    /// <summary>
    /// Shows or hides the active window's second command line (⌃B i, or the ⌃P surface). The answer is
    /// per window, so the bar follows the tab you are on; hiding the armed bar hands ⏎ back to the
    /// primary rather than leaving it pointed at something off screen.
    /// <para>
    /// Raising it also arms it. It is the line that was just asked for, and leaving ⏎ and the caret on
    /// the bar above while a new empty one appears below reads as the cursor being in the wrong window
    /// — which is how it was reported. Only the explicit toggle does this: <see cref="SyncInputBars"/>
    /// also raises the bar when a tab that keeps one becomes visible, and that is not a request to type
    /// into it.
    /// </para>
    /// </summary>
    private void ToggleSecondBar()
    {
        var shown = _secondBars.Toggle(ActiveWindowId());
        SyncInputBars();
        if (shown)
        {
            ArmBar(_second);
        }
    }

    /// <summary>
    /// Brings the input area in line with the active window and the current preferences: whether the
    /// second bar is up, and how tall the bars may grow. Called when the second bar is toggled, on every
    /// resize, and on every settings save, so F8's numbers take effect without a restart.
    /// <para>
    /// It deliberately leaves the text alone. Recalling here would empty both bars whenever
    /// <c>keep per-tab drafts</c> is off — the store hands back nothing in that mode by design — so
    /// raising the second bar, or saving a settings screen, would throw away the line being typed.
    /// The drafts are put back by <see cref="ChangeWindow"/>, which is where the window changed.
    /// </para>
    /// </summary>
    private void SyncInputBars()
    {
        var shown = _secondBars.IsShown(ActiveWindowId());
        SyncInputHeights(shown);

        if (_second.Visible != shown)
        {
            _second.Visible = shown;
            _window.ForceRebuildLayout();
        }

        // Re-assert the armed bar against the visibility that was just applied: a hidden bar cannot be
        // the one ⏎ sends from, and ArmBar answers a request for one with the primary.
        ArmBar(_armed);
    }

    /// <summary>
    /// Caps how tall each command line may grow. The configured heights are what the bars want; the
    /// window gets a veto. Two bars each grown to eight lines is most of a 24-row terminal, and an input
    /// area that leaves no output above it is not an input area — so the bars share a quarter of the
    /// window each, floor of one. The share is taken from what the chrome leaves rather than from the
    /// whole window: the framework reserves every sticky row before the workspace is measured at all,
    /// and it does not check that the two sticky bands fit, so rows promised to the header and the
    /// status line and then spent on a bar come out of the output area (see <see cref="InputLayout.Room"/>).
    /// <para>
    /// Split out of <see cref="SyncInputBars"/> because the chrome it measures changes without the input
    /// area changing at all — <see cref="SetStatus"/> calls it for exactly that reason — and because it
    /// touches nothing but the two caps, so calling it from there cannot recurse.
    /// </para>
    /// </summary>
    /// <param name="secondShown">Whether two command lines are sharing the room, or one.</param>
    private void SyncInputHeights(bool secondShown)
    {
        var room = InputLayout.Room(HeaderHeight(), ChromeRows(), secondShown ? 2 : 1);
        foreach (var bar in new[] { _input, _second })
        {
            bar.MinRows = Math.Min(_config.Input.Rows, room);
            bar.MaxRows = Math.Min(_config.Input.MaxRows, room);
        }
    }

    /// <summary>
    /// How many rows the header and the status line take between them — the chrome the input area has to
    /// leave alone. Both are single lines of markup that the window wraps, and both are ours, so the
    /// count is arithmetic on the text rather than a reading of the last frame: the veto has to be right
    /// on the first frame too, and nothing has been arranged yet when the window is built.
    /// </summary>
    private int ChromeRows() =>
        InputLayout.WrappedRows(MarkupWidth(_header.Text), HeaderWidth())
        + InputLayout.WrappedRows(MarkupWidth(_statusBar.Text), HeaderWidth());

    /// <summary>
    /// The input area following a change of visible window: the second bar's visibility, both drafts,
    /// and both history cursors are the new window's. This is the only path that replaces the text.
    /// </summary>
    private void ChangeWindow()
    {
        SyncInputBars();
        RecallDrafts(ActiveWindowId());
    }

    /// <summary>
    /// Puts a window's stored drafts back into both bars. Assigning <c>Text</c> is deliberately not a
    /// keystroke: it raises no change event, so recalling a draft neither re-records it nor resets the
    /// unsent-input marker of the window being left.
    /// </summary>
    private void RecallDrafts(string windowId)
    {
        _history.ResetCursor();
        _secondHistory.ResetCursor();
        _input.Text = _drafts.Recall(windowId, InputBar.Primary);
        _second.Text = _drafts.Recall(windowId, InputBar.Secondary);
    }

    /// <summary>
    /// Handles ↑/↓ as draft-safe history recall. A command line tall enough to have another row keeps
    /// the arrows for the caret — recall only happens where the caret has nowhere further to go, which
    /// is the single-row case it has always been plus the top and bottom of a grown one.
    /// </summary>
    private bool TryRecallKey(KeyPressedEventArgs e)
    {
        var bar = ActiveBar();
        var kind = BarKind(bar);
        var history = HistoryFor(kind);

        string? text;
        switch (e.KeyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                if (bar.TryMoveRow(-1))
                {
                    e.Handled = true;
                    return true;
                }

                text = history.Recall(bar.Text);
                break;
            case ConsoleKey.DownArrow:
                if (bar.TryMoveRow(1))
                {
                    e.Handled = true;
                    return true;
                }

                if (!history.IsRecalling)
                {
                    return false;
                }

                text = history.Forward();
                break;
            default:
                return false;
        }

        e.Handled = true;
        if (text is not null)
        {
            bar.Text = text;
            _drafts.Record(ActiveWindowId(), kind, text);
            UpdateInputChrome();
        }

        return true;
    }

    // --- Scrollback navigation ------------------------------------------------------------------

    /// <summary>
    /// Rows a page key keeps on screen. PgUp/PgDn move by the viewport <em>less</em> this, so the couple
    /// of lines you were reading at the edge are still there after the jump — the difference between
    /// paging through a transcript and losing your place twice a page.
    /// </summary>
    private const int PageOverlapRows = 2;

    /// <summary>
    /// The viewport the scrollback keys drive: the focused pane's active window.
    /// <para>
    /// A frozen pane hands them its <em>pinned</em> half. Freeze and scrollback are two ways of looking
    /// at the same history and this is where they compose: ⌃F holds a region still above the bar and
    /// keeps the tail live below it, and while that is up the region worth moving through is the pinned
    /// one — the live tail is by definition already showing its newest line. The tail keeps its own
    /// viewport regardless (see <see cref="BuildFrozenContent"/>), so a burst into a four-row tail still
    /// scrolls itself; it just is not what a page key aims at.
    /// </para>
    /// </summary>
    private ScrollablePanelControl? ScrollTarget()
    {
        var pane = _workspace.Layout.FocusedPane;
        var windowId = pane.ActiveTab ?? MainWindowId;
        return pane.Frozen && _paneScrolls.TryGetValue(FrozenRegionKey(windowId), out var frozen)
            ? frozen
            : _paneScrolls.GetValueOrDefault(windowId);
    }

    /// <summary>
    /// Handles the scrollback keys, or reports that this key is not one of them.
    /// <para>
    /// They are routed from here — the window's <c>PreviewKeyPressed</c> — rather than left to the
    /// panel's own <c>ProcessKey</c>, because the panel will never be handed a key. This app pins the
    /// keyboard focus to the armed command line (<see cref="PinFocusToArmedBar"/>) and routes typing
    /// explicitly, which is today's fix for a paste that followed framework focus and vanished;
    /// SharpConsoleUI hands keys to the focused control, and
    /// <c>ScrollablePanelControl.ProcessKey</c> returns false on its first line unless it has focus. So
    /// the honest options were to route the keys here or to weaken the pin, and weakening the pin would
    /// trade a fixed bug for this feature. Routing here also matches how every other key in this window
    /// reaches its destination, which means one place to read to find out what a keystroke does.
    /// </para>
    /// </summary>
    private bool TryScrollKey(KeyPressedEventArgs e)
    {
        var key = e.KeyInfo.Key;
        var ctrl = e.KeyInfo.Modifiers.HasFlag(ConsoleModifiers.Control);
        var shift = e.KeyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift);

        // Alt chords belong to macros and to the app, never to text or to scrolling.
        if (e.KeyInfo.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            return false;
        }

        Action<ScrollablePanelControl>? scroll = (key, ctrl, shift) switch
        {
            (ConsoleKey.PageUp, false, _) => panel => panel.ScrollVerticalBy(-PageRows(panel)),
            (ConsoleKey.PageDown, false, _) => panel => panel.ScrollVerticalBy(PageRows(panel)),
            (ConsoleKey.UpArrow, false, true) => panel => panel.ScrollVerticalBy(-1),
            (ConsoleKey.DownArrow, false, true) => panel => panel.ScrollVerticalBy(1),

            // ⌃Home/⌃End and not bare Home/End: those two are the command line's, and a caret that
            // stopped moving to the ends of the line you are typing would be a poor trade for a jump
            // there are two other ways to make. The bar ignores the Control modifier on them, so
            // nothing is taken away that plain Home/End does not still do.
            (ConsoleKey.Home, true, _) => ToOldest,
            (ConsoleKey.End, true, _) => BackToLive,
            _ => null,
        };

        if (scroll is null)
        {
            return false;
        }

        // Claimed whether or not this pane has anything to scroll. "PgUp does nothing here but types a
        // character over there" is the kind of key that gets reported as a corrupted command line.
        e.Handled = true;
        if (ScrollTarget() is { } panel)
        {
            scroll(panel);
            SyncScrollbackState();
        }

        return true;
    }

    /// <summary>
    /// Rows one page key moves. Read off the viewport the last frame arranged the panel at — the panel
    /// is the only thing that knows how tall its content box came out after the pane's chrome, and the
    /// figure it reports is the one it will clamp the scroll against.
    /// </summary>
    private static int PageRows(ScrollablePanelControl panel) =>
        Math.Max(1, panel.ViewportHeight - PageOverlapRows);

    /// <summary>
    /// Returns a viewport to the newest line <em>and re-arms auto-scroll</em>, so it stays there as
    /// output arrives. <c>ScrollToBottom</c> alone is a one-shot — it moves the offset and leaves the
    /// viewport detached, which would put the reader at the bottom of a transcript that then walked away
    /// from them again on the very next line.
    /// </summary>
    private static void BackToLive(ScrollablePanelControl panel)
    {
        panel.AutoScroll = true;
        panel.ScrollToBottom();
    }

    /// <summary>
    /// Jumps to the oldest line the buffer still holds, <em>and detaches auto-scroll</em>. Only
    /// <c>ScrollVerticalBy</c> detaches on its own (it is the one the framework treats as a user gesture);
    /// <c>ScrollToTop</c> is a bare offset write, so leaving auto-scroll armed would have the very next
    /// repaint pull the viewport back to the bottom — which is what ⌃Home did before this, visibly nothing.
    /// </summary>
    private static void ToOldest(ScrollablePanelControl panel)
    {
        // A pane whose content fits has nothing to jump to, and detaching it anyway would leave it
        // refusing to follow its own output the moment it did overflow — a keystroke that appears to do
        // nothing and then quietly breaks the thing it did nothing to.
        if (!Scrollable(panel))
        {
            return;
        }

        panel.AutoScroll = false;
        panel.ScrollToTop();
    }

    /// <summary>Whether a viewport has anything outside it, in either direction.</summary>
    private static bool Scrollable(ScrollablePanelControl panel) => panel.CanScrollUp || panel.CanScrollDown;

    /// <summary>
    /// Runs one scrollback move on the focused pane for the command surface, and says so on the status
    /// row when there was nothing for it to do — the same rule the ⌃B commands follow, and for the same
    /// reason: a palette entry that changes nothing and reports nothing reads as a broken client.
    /// </summary>
    private void ScrollFocusedPane(Action<ScrollablePanelControl> move)
    {
        if (ScrollTarget() is not { } panel || !Scrollable(panel))
        {
            RefuseCommand("nothing to scroll — this window fits in its pane");
            return;
        }

        move(panel);
        SyncScrollbackState();
    }

    /// <summary>
    /// Publishes the focused pane's scroll position into the state that renders from it: the window's
    /// <see cref="WorkspaceWindow.ScrolledBack"/> flag (which is what makes unread badging count lines
    /// arriving below the viewport) and the status row's scrollback segment.
    /// <para>
    /// <see cref="ScrollablePanelControl.AutoScroll"/> <em>is</em> the "showing the live tail" bit —
    /// the framework detaches it when the reader scrolls up and re-attaches it when they reach the
    /// bottom again — so this mirrors that one fact rather than inventing a second one to keep in step
    /// with it. The frozen half is deliberately not consulted: while a pane is frozen its live tail is
    /// still live, so lines are still landing where the reader can see them and nothing is unread.
    /// </para>
    /// </summary>
    /// <param name="windowId">
    /// The window whose viewport moved. Defaults to the focused pane's, which is what every keyboard
    /// route means; the wheel passes one explicitly because it scrolls whatever the pointer is over, and
    /// that need not be the focused pane.
    /// </param>
    private void SyncScrollbackState(string? windowId = null)
    {
        windowId ??= ActiveWindowId();
        if (_paneScrolls.GetValueOrDefault(windowId) is { } live
            && _workspace.SetScrolledBack(windowId, !live.AutoScroll))
        {
            RefreshTabTitles();
        }

        RefreshStatusRow();
    }

    /// <summary>Raised by any viewport that moved — the wheel and the scrollbar get here too, not just keys.</summary>
    private void OnPaneScrolled() => SyncScrollbackState();

    /// <summary>
    /// The status row's scrollback segment: shown exactly while the focused pane has output below its
    /// viewport, and carrying the key that gets back to it. Empty otherwise — this is the only visible
    /// sign that a pane is not showing its newest line, because the panes deliberately carry no
    /// scrollbar (see <see cref="ScrollViewFor"/>), so it has to be right rather than decorative.
    /// </summary>
    private string ScrollbackStatus()
    {
        if (ScrollTarget() is not { CanScrollDown: true } panel)
        {
            return string.Empty;
        }

        // Kept short on purpose. The row right-aligns a cluster of five segments and only guarantees a
        // three-cell gap, so a wordier phrasing wrapped the status line onto a second row at 120 columns
        // — and a status line that grows a row takes one off the workspace (SyncInputHeights counts the
        // chrome), which is a pane getting shorter because you scrolled it.
        var below = Math.Max(1, panel.TotalContentHeight - panel.ViewportHeight - panel.VerticalScrollOffset);
        return $"[#e5c07b]{Glyphs.Scrollback} scrollback[/] [dim]{below} · ⌃End live[/]";
    }

    /// <summary>
    /// Repaints the resting status row for whichever state the client is in.
    /// <see cref="RefreshStatusBar"/> covers the connected case only and returns early otherwise — by
    /// design, since it runs off every chrome refresh — but a scroll changes the row on a client with
    /// nothing connected too, and that is exactly the client someone reads their scrollback on.
    /// </summary>
    private void RefreshStatusRow()
    {
        if (_moveMode)
        {
            return;
        }

        SetStatus(_statusIdentity is { } id
            ? StatusBarMarkup(id.Character, id.Host, id.Port, id.State)
            : NotConnectedMarkup());
    }

    /// <summary>
    /// Refreshes the input region: each bar's character-bound prompt (<c>Corvid@Aetherfall ›</c>) and
    /// the status bar (which carries the live character count now that the gutter is gone). The armed
    /// bar's prompt is the bright one and ends in <c>›</c>; the other is dimmed and ends in <c>·</c>,
    /// so which line ⏎ will send is readable without hunting for the caret.
    /// </summary>
    private void UpdateInputChrome()
    {
        var session = _active;
        var character = session?.Character?.Name ?? (ActiveWorld() is { } aw ? aw.Character : null);
        var world = session?.World.Name ?? ActiveWorld()?.World.Name;
        var label = StatusFormatter.CharacterPrompt(character, world);
        _input.Prompt = PromptMarkup(label, _input.Armed);
        _second.Prompt = PromptMarkup(SecondPromptLabel(label), _second.Armed);
        RefreshStatusBar();
    }

    /// <summary>
    /// The second bar's label: the same character prompt with its trailing <c>›</c> replaced by a
    /// second-line marker, so the two bars read as one identity on two lines rather than as two
    /// connections.
    /// </summary>
    private static string SecondPromptLabel(string label) =>
        label.EndsWith("› ", StringComparison.Ordinal) ? label[..^2] + "» " : label;

    /// <summary>
    /// The input band's background hex — shared by the bar's own fill (<see cref="InputBarControl"/>)
    /// and the prompt cells (<see cref="PromptMarkup"/>) so the input row reads as one solid full-width
    /// band. Keep in sync with the <c>BandColor</c> RGB in <see cref="SetUpBar"/>.
    /// </summary>
    private const string InputBandHex = "#33394c";

    /// <summary>The band behind the bar ⏎ will not send from. Keep in sync with <c>IdleBandColor</c>.</summary>
    private const string IdleBandHex = "#262b3a";

    /// <summary>
    /// Wraps a prompt label so its cells carry the band background and its brightness says whether this
    /// is the bar ⏎ sends from. The bar already fills its row with the same colour, so painting the
    /// label to match makes the whole row a continuous band with no gap at the prompt. Brackets in names
    /// are escaped to block injection.
    /// </summary>
    private static string PromptMarkup(string prompt, bool armed = true)
    {
        var text = prompt.Replace("[", "[[").Replace("]", "]]");
        return armed
            ? $"[on {InputBandHex}]{text}[/]"
            : $"[dim on {IdleBandHex}]{text}[/]";
    }

    /// <summary>
    /// Pins each bar's width to the window so its band (and the wrap width derived from it) runs to the
    /// right edge — otherwise a bar measures to its content and the row stops mid-screen.
    /// </summary>
    private void SyncInputWidth()
    {
        _input.Width = HeaderWidth();
        _second.Width = HeaderWidth();
    }

    /// <summary>The active connection's status-bar identity (character/host/port/state), or null.</summary>
    private (string Character, string Host, int Port, string State)? _statusIdentity;

    /// <summary>Repaints the connection status bar from the stored identity, folding in the live char
    /// count. A no-op while the move-mode prompt owns the bar or nothing's connected — a <see cref="Notice"/>
    /// is displaced instead, so typing after a refusal puts the connection line back at once.</summary>
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
    /// One settings screen: the F-key that toggles it, what it calls itself (the title its own header
    /// draws, so the command surface and the screen can't disagree), the <c>--view</c> names that
    /// select it for a snapshot, and the factory that opens it — a fresh <see cref="SettingsSession"/>
    /// (its own cursor and undo log) plus the control factory that renders that session.
    /// </summary>
    private readonly record struct SettingsScreen(ConsoleKey Key, string Title, string[] Views, Func<ScreenBinding> Open);

    /// <summary>
    /// The F2–F9 settings screens, in F-key order. The global shortcuts, the <c>--view</c> snapshot
    /// lookup and the command surface's SETTINGS group all read this one table, so a screen can't be
    /// bound to a key without also being reachable by name and offered in the palette. Each control is
    /// built on demand from live config by its pure renderer, so re-opening always reflects current
    /// state, and every screen hands back a composed tree of real panels.
    /// <para>
    /// The first <c>--view</c> name is also the screen's command id (<c>screen:worlds</c>), because it
    /// is already the stable name a snapshot addresses the screen by; giving the palette a second set
    /// of names would be two spellings of one thing.
    /// </para>
    /// </summary>
    private IReadOnlyList<SettingsScreen> SettingsScreens() => new SettingsScreen[]
    {
        new(ConsoleKey.F2, "Triggers & spawn routing", new[] { "triggers", "route", "highlight", "set" }, TriggersScreen),
        new(ConsoleKey.F3, "Aliases", new[] { "aliases" }, AliasesScreen),
        new(ConsoleKey.F4, "Keypad & hotkeys", new[] { "keypad" }, KeypadScreen),
        new(ConsoleKey.F5, "Worlds & Characters", new[] { "worlds", "settings" }, WorldsScreen),
        new(ConsoleKey.F6, "Timers", new[] { "timers" }, TimersScreen),
        new(ConsoleKey.F7, "Text & ANSI", new[] { "textansi" }, TextAnsiScreen),
        new(ConsoleKey.F8, "Input", new[] { "input" }, InputScreen),
        new(ConsoleKey.F9, "Character logging", new[] { "logging" }, CharacterLoggingScreen),
    };

    /// <summary>
    /// The SETTINGS half of the ⌃P catalog: every screen in <see cref="SettingsScreens"/>, each
    /// carrying the F-key it is registered on. Derived from that table rather than written out, for the
    /// same reason <see cref="RegisterGlobalShortcuts"/> derives from <see cref="MacroKeys.AppShortcuts"/>
    /// — a palette row that named a key nothing was bound to would be a lie the compiler can't catch.
    /// </summary>
    private IReadOnlyList<SettingsEntry> SettingsCommands() => SettingsScreens()
        .Select(s => new SettingsEntry(s.Title, ScreenCommandPrefix + s.Views[0], s.Key.ToString()))
        .ToList();

    /// <summary>The command-surface id prefix for "open this settings screen".</summary>
    private const string ScreenCommandPrefix = "screen:";

    /// <summary>
    /// Binds every chord the app claims globally: the window/pane commands, and each settings screen's
    /// F-key to the full-screen overlay (Esc / the same F-key closes it).
    /// <para>
    /// The chords come from <see cref="MacroKeys.AppShortcuts"/> rather than being written out here,
    /// because F4 has to tell a user that a macro on <c>Ctrl+Q</c> will never fire, and it can only say
    /// so honestly if the list it reads is the list that was registered. Registering <em>from</em> that
    /// table makes the two the same list. Both directions are checked as it goes: a claim with no action
    /// and a screen with no claim are both startup failures rather than a key that silently does nothing.
    /// </para>
    /// </summary>
    private void RegisterGlobalShortcuts()
    {
        var screens = SettingsScreens().ToDictionary(s => s.Key, s => s.Open);
        foreach (var claim in MacroKeys.AppShortcuts)
        {
            var action = ShortcutAction(claim, screens)
                ?? throw new InvalidOperationException(
                    $"MacroKeys.AppShortcuts claims {claim.Modifiers}+{claim.Key} but nothing runs on it");
            _system.RegisterGlobalShortcut(claim.Modifiers, claim.Key, action);
            _shortcuts[(claim.Modifiers, claim.Key)] = action;
        }

        foreach (var key in screens.Keys)
        {
            if (!MacroKeys.AppShortcuts.Any(c => c.Modifiers == (ConsoleModifiers)0 && c.Key == key))
            {
                throw new InvalidOperationException(
                    $"the {key} settings screen is not claimed in MacroKeys.AppShortcuts");
            }
        }
    }

    /// <summary>
    /// What a claimed chord runs, or null when nothing does. Every one returns true: these are the keys
    /// the app takes outright, and a global shortcut that returned false would hand the key back to the
    /// window underneath — which is exactly what the keypad screen has just told the user does not happen.
    /// </summary>
    private Func<bool>? ShortcutAction(
        AppShortcut claim, IReadOnlyDictionary<ConsoleKey, Func<ScreenBinding>> screens)
    {
        if (claim.Modifiers == (ConsoleModifiers)0)
        {
            if (!screens.TryGetValue(claim.Key, out var open))
            {
                return null;
            }

            var key = claim.Key;
            return () => { _settings.Toggle(key, open); return true; };
        }

        if (claim.Modifiers != ConsoleModifiers.Control)
        {
            return null;
        }

        return claim.Key switch
        {
            // ⌃Q asks first. A second ⌃Q dismisses the question rather than answering it — the same
            // toggle every other surface in this client is on, and the only reading under which a held
            // or twice-fumbled chord cannot quit on its own. See QuitPrompt.
            ConsoleKey.Q => () => { _quit.Toggle(); return true; },
            // Next window (Ctrl+N, plus Ctrl+Tab where the terminal reports it) and close window (Ctrl+W).
            ConsoleKey.N or ConsoleKey.Tab => () => { NextWindow(); return true; },
            ConsoleKey.W => () => { CloseActiveWindow(); return true; },
            ConsoleKey.O => () => { CyclePane(); return true; },
            ConsoleKey.P => () => { ToggleMenu(); return true; },
            ConsoleKey.B => () => { ArmPrefix(); return true; },
            ConsoleKey.F => () => { ToggleFreeze(); return true; },
            // ⌃R is the readline/bash/zsh/fish reverse-history-search chord, which is why the surface is
            // on it. ⌃H — what a user reaching for "history" tries first — cannot be bound at all: the
            // framework's parser turns byte 0x08 into Backspace with no Control modifier, so binding it
            // would take the command line's erase key and the app could not even tell the two apart.
            ConsoleKey.R => () => { ToggleHistorySearch(); return true; },
            _ => null,
        };
    }

    /// <summary>Ends the UI loop. The one caller is a confirmed <see cref="QuitOverlay"/>.</summary>
    private void Quit()
    {
        _exiting = true;
        _system.RequestExit(0);
    }

    /// <summary>
    /// What a quit would end, as of the keystroke asking: the worlds it disconnects, the lines it throws
    /// away unsent, and the settings edits that were never saved. Gathered here because only the app can
    /// see any of it; <see cref="QuitPrompt"/> turns it into the question.
    /// <para>
    /// Drafts are counted per command line, not per window. A window says only that it is holding
    /// something (<see cref="WorkspaceWindow.HasUnsentInput"/>, the same fact its tab's ✎ is drawn from),
    /// which is one draft — but the active window's two bars are right here to be read, and a second bar
    /// holding an OOC line is exactly the draft a per-window count would hide.
    /// </para>
    /// </summary>
    private QuitFacts QuitFactsNow()
    {
        var activeId = ActiveWindowId();
        var holding = _workspace.Windows.Where(w => w.HasUnsentInput).ToList();
        var bars = (_input.Buffer.IsEmpty ? 0 : 1) + (_second.Visible && !_second.Buffer.IsEmpty ? 1 : 0);
        var drafts = holding.Count(w => w.Id != activeId) + bars;

        // A screen with nothing typed into it costs nothing to close, so the edit count travels with the
        // title and QuitPrompt drops the line when it is zero.
        var screen = _settings.OpenKey is { } key
            ? SettingsScreens().FirstOrDefault(s => s.Key == key).Title
            : null;

        return new QuitFacts(
            ConnectedWorlds(),
            drafts,
            holding.Select(w => w.Title).ToList(),
            screen,
            _settings.PendingEdits);
    }

    /// <summary>
    /// The worlds a quit would disconnect. Live sessions are the truth; the demo scene opens no sockets
    /// at all, so when there is no session to ask, the rail's connected keys — which are what the header
    /// counts and the snapshot frame shows — answer instead.
    /// </summary>
    private IReadOnlyList<string> ConnectedWorlds() =>
        _sessions.Sessions.Count > 0
            ? _sessions.Sessions.Where(s => s.IsConnected)
                .Select(s => s.World.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : _config.Worlds
                .Where(w => _connectedKeys.Contains(w.Name)
                    || w.Characters.Any(c => _connectedKeys.Contains($"{w.Name}.{c.Name}")))
                .Select(w => w.Name)
                .ToList();

    /// <summary>
    /// Persists the configuration the settings screens edit — the ⏎ Save action. The workspace layout
    /// is captured alongside it so a save never rolls back the resumed session; a failed write is
    /// swallowed for the same reason startup's is (the config is a convenience, not the session).
    /// </summary>
    private void SaveConfiguration()
    {
        // F8 edits the live InputSettings, so a saved height applies to the bars on the way out of the
        // screen rather than at the next launch.
        SyncInputBars();

        try
        {
            _config.LastSession = CaptureSession();
            ConfigurationStore.Save(ConfigurationStore.DefaultPath, _config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Notice($"could not save settings: {ex.Message}", MessageSeverity.Error);
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
            selection.SelectionIn(WorldsScreenRenderer.CharactersPane),
            selection.SelectionIn(WorldsScreenRenderer.TriggerSetsPane)));
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
            fkey,
            session.Selection.SelectionIn(WorldsScreenRenderer.TriggerSetsPane),
            _system.DesktopDimensions.Height));
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
            session.Focus(),
            _system.DesktopDimensions.Height));
    }

    /// <summary>Opens the F3 Aliases screen: the alias list, then the alias's toggles.</summary>
    private ScreenBinding AliasesScreen()
    {
        var session = new SettingsSession(selection =>
            AliasesScreenRenderer.Model(_config.TriggerSets, selection.SelectionIn(0)));

        return new ScreenBinding(session, () => AliasesScreenView.Build(
            _config.TriggerSets,
            session.Selection.SelectionIn(0),
            _system.DesktopDimensions.Width,
            session.Focus(),
            _system.DesktopDimensions.Height));
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
            session.Focus(),
            _system.DesktopDimensions.Height));
    }

    /// <summary>Opens the F6 Timers screen: the timer list, then the timer's toggles.</summary>
    private ScreenBinding TimersScreen()
    {
        var session = new SettingsSession(selection =>
            TimersScreenRenderer.Model(_config.TriggerSets, selection.SelectionIn(0)));

        return new ScreenBinding(session, () => TimersScreenView.Build(
            _config.TriggerSets,
            session.Selection.SelectionIn(0),
            _system.DesktopDimensions.Width,
            session.Focus(),
            _system.DesktopDimensions.Height));
    }

    /// <summary>Opens the F7 Text &amp; ANSI screen, bound to the app's text preferences.</summary>
    private ScreenBinding TextAnsiScreen() =>
        OptionsScreen(() => OptionsScreenRenderer.TextAnsiScreen(_config.Text));

    /// <summary>Opens the F8 Input screen, bound to the app's input preferences.</summary>
    private ScreenBinding InputScreen() =>
        OptionsScreen(() => OptionsScreenRenderer.InputScreen(_config.Input));

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
    /// and steps to the next, and the rest is typing. Several views walk further than the first field,
    /// because a still frame should land on the thing that screen's editing actually added: F5 rewrites
    /// a host's suffix ("no way to change a host" is the gap the whole mode closes), and F2 steps on to
    /// its route and moves the mark, which is the only way to see that the dropdown is live rather than
    /// a report. The <c>logging</c> view opens F5 on the character pane, so it steps twice more to
    /// reach the log format — past the name and the on-connect line — because the character's log is
    /// the whole reason that view exists, and it is also this app's one <em>closed</em> list, so it is
    /// what the closed presentation is checked against. <c>keypad</c> steps twice in the other
    /// direction, onto the binding's <em>key capture</em>: that is the one state on these screens no
    /// amount of typing can reach, so a still frame is the only way to look at it.
    /// <para>
    /// <c>route</c> and <c>highlight</c> are F2 again, stopped at the two states a single frame of
    /// <c>triggers-edit</c> cannot also show: a buffer that has <em>narrowed</em> the list (<c>pa</c> →
    /// <c>pages</c>), and a list longer than the pane can hold (seventeen colour names capped to six).
    /// Both are drawn chrome with no state of their own, so a snapshot is the only place they can be
    /// looked at rather than merely asserted.
    /// </para>
    /// <para>
    /// <c>textansi</c> and <c>input</c> have no <c>-edit</c> state to script any more, and their
    /// scripts are empty rather than "press ⏎": every row F7 and F8 still draw is a checkbox, since
    /// the three value rows those screens carried (<c>ambiguous width</c>, <c>newline key</c>,
    /// <c>dictionary</c>) named features that do not exist and went with them. ⏎ on a row with
    /// nothing to open <em>saves and closes</em>, so driving one would snapshot the workspace with no
    /// screen on it — a frame that silently isn't of the thing it is named after.
    /// </para>
    /// </summary>
    private static IEnumerable<ConsoleKeyInfo> EditSnapshotKeys(string view)
    {
        if (string.Equals(view, "textansi", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(view, "input", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        yield return Stroke('\r', ConsoleKey.Enter);

        if (string.Equals(view, "keypad", StringComparison.OrdinalIgnoreCase))
        {
            // name → command → key, which is where the keyboard stops being a text buffer: the frame
            // shows an armed capture, the one screen state that cannot be reached by typing anything.
            yield return Stroke('\t', ConsoleKey.Tab);
            yield return Stroke('\t', ConsoleKey.Tab);
            yield break;
        }

        if (string.Equals(view, "logging", StringComparison.OrdinalIgnoreCase))
        {
            // name → on connect → log: the character row's fields, in order.
            yield return Stroke('\t', ConsoleKey.Tab);
            yield return Stroke('\t', ConsoleKey.Tab);
            yield break;
        }

        if (string.Equals(view, "set", StringComparison.OrdinalIgnoreCase))
        {
            // Straight to the rule's last field, the set that owns it — the one edit on these screens
            // that moves the row it is made on. A still frame is the only way to see the closed list of
            // sets over a pane whose rows are flattened across all of them.
            for (var i = 0; i < TriggersScreenRenderer.SetField; i++)
            {
                yield return Stroke('\t', ConsoleKey.Tab);
            }

            yield break;
        }

        if (string.Equals(view, "triggers", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(view, "route", StringComparison.OrdinalIgnoreCase))
        {
            // name → pattern → route: two steps, because the name now leads the row's fields.
            yield return Stroke('\t', ConsoleKey.Tab);
            yield return Stroke('\t', ConsoleKey.Tab);

            if (string.Equals(view, "triggers", StringComparison.OrdinalIgnoreCase))
            {
                yield return Stroke('\0', ConsoleKey.DownArrow);
                yield break;
            }

            // Clear the opened value, then type a fragment of another window: the list narrows to it,
            // and the frame shows a filter rather than a menu.
            for (var i = 0; i < 4; i++)
            {
                yield return Stroke('\b', ConsoleKey.Backspace);
            }

            foreach (var c in "pa")
            {
                yield return Stroke(c, ConsoleKey.NoName);
            }

            yield break;
        }

        if (string.Equals(view, "highlight", StringComparison.OrdinalIgnoreCase))
        {
            // name → pattern → route → highlight fg, then clear the buffer: an empty one narrows
            // nothing, so the whole seventeen-name palette is offered and the list is drawn at its cap.
            for (var i = 0; i < 3; i++)
            {
                yield return Stroke('\t', ConsoleKey.Tab);
            }

            for (var i = 0; i < 12; i++)
            {
                yield return Stroke('\b', ConsoleKey.Backspace);
            }

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

    /// <summary>
    /// Builds the ⌃P command catalog from live config + workspace state. Internal so a headless test
    /// can check the surface against <see cref="SettingsScreens"/> itself — the whole point of deriving
    /// the SETTINGS group from that table is that the two cannot disagree, and only a test that reads
    /// both can say so.
    /// </summary>
    internal IReadOnlyList<CommandItem> BuildCatalog()
    {
        var context = new CommandContext(
            LoggingOn: _active?.IsLogging == true,
            Zoomed: _workspace.Layout.ZoomedPaneId is not null,
            Frozen: _workspace.Layout.FocusedPane.Frozen,
            TimestampsOn: _showTimestamps,
            SecondInputOn: _secondBars.IsShown(ActiveWindowId()),
            ScrolledBack: ScrollTarget() is { CanScrollDown: true });
        return CommandCatalog.Build(
            _workspace, BuildCharacterRefs(), _active?.SessionKey, context, SettingsCommands());
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

    /// <summary>The F-key of the settings screen currently open over the workspace, or null when none is.</summary>
    internal ConsoleKey? OpenSettingsKey => _settings.OpenKey;

    /// <summary>
    /// The <c>world.character</c> key of the session commands act on, or null when there is none —
    /// which is the state the reported bug lived in. Internal so a headless test can assert that
    /// <c>Switch to …</c> actually switched.
    /// </summary>
    internal string? ActiveSessionKey => _active?.SessionKey;

    /// <summary>The open session with this key, or null. Internal for the same reason.</summary>
    internal WorldSession? FindSession(string sessionKey) => _sessions.Find(sessionKey);

    /// <summary>Every window in the workspace, by id — a test's view of which tabs a switch created.</summary>
    internal IReadOnlyList<string> WindowIds() => _workspace.Windows.Select(w => w.Id).ToArray();

    /// <summary>The live configuration this app is running on.</summary>
    internal AppConfiguration Configuration => _config;

    /// <summary>Whether the ⌃P client message viewer is up.</summary>
    internal bool MessageLogIsOpen => _messageLog.IsOpen;

    /// <summary>
    /// Runs a command-surface entry by its id, doing what the current shell supports. Internal so a
    /// headless test can dispatch an id the way the palette does, without opening the surface and
    /// typing at it.
    /// <para>
    /// Returns <see langword="false"/> for an id nothing here implements. That is not a nicety: the
    /// catalog is generated from live state and this is a hand-written switch, so the two drift in
    /// silence — <c>char:</c> was offered by the surface and implemented nowhere for as long as the
    /// surface has existed. <c>CommandDispatchTests</c> dispatches every id the catalog can emit and
    /// fails on a false, which is the only thing that makes them stay in step.
    /// </para>
    /// </summary>
    internal bool DispatchCommand(string id)
    {
        if (id.StartsWith(CharacterCommandPrefix, StringComparison.Ordinal))
        {
            SwitchToCharacter(id[CharacterCommandPrefix.Length..]);
            return true;
        }

        if (id.StartsWith("win:", StringComparison.Ordinal))
        {
            var windowId = id["win:".Length..];
            if (!Activate(windowId))
            {
                RefuseCommand($"{windowId} is not open any more");
            }

            return true;
        }

        // A settings entry opens the very screen its F-key opens, through the same Toggle: the palette
        // is another door onto that key, not a second way of building the screen.
        if (id.StartsWith(ScreenCommandPrefix, StringComparison.Ordinal))
        {
            var view = id[ScreenCommandPrefix.Length..];
            if (SettingsScreens().FirstOrDefault(s => s.Views[0] == view) is { Open: not null } screen)
            {
                _settings.Toggle(screen.Key, screen.Open);
                return true;
            }

            RefuseCommand($"there is no settings screen called {view}");
            return false;
        }

        switch (id)
        {
            case "layout:zoom":
            case "layout:unzoom":
                _workspace.Layout.ToggleZoom();
                RebuildPaneArea(); // zoom collapses the tree to one pane (or restores it)
                return true;
            case "layout:close":
                CloseActiveWindow(); // rebuilds the pane area itself
                return true;
            case "layout:split-right":
                SplitFocusedPane(PaneCommand.SplitRight); // reports when the pane has nothing to split
                return true;
            case "layout:split-down":
                SplitFocusedPane(PaneCommand.SplitDown);
                return true;
            case "term:freeze":
            case "term:unfreeze":
                ToggleFreeze();
                return true;
            case "term:input2-on":
            case "term:input2-off":
                ToggleSecondBar();
                return true;
            case "term:messages":
                _messageLog.Toggle();
                return true;
            case "term:history":
                ToggleHistorySearch();
                return true;
            case "term:log-on":
                StartLogging();
                break;
            case "term:log-off":
                StopLogging();
                break;
            case "term:clear":
                if (_panes.TryGetValue(ActiveWindowId(), out var pane))
                {
                    pane.SetContent(new List<string>());
                }

                break;
            case "term:scroll-oldest":
                ScrollFocusedPane(ToOldest);
                break;
            case "term:scroll-live":
                ScrollFocusedPane(BackToLive);
                break;
            case "term:timestamps-on":
            case "term:timestamps-off":
                _showTimestamps = !_showTimestamps;
                break;
            case "world:reconnect":
                Reconnect();
                break;
            case "world:disconnect":
                Disconnect();
                break;
            default:
                // On the status line, never through _active: an unwired command is exactly what you hit
                // while nothing is connected, and a message that needs a session to be seen cannot
                // report in the state it is being read in. That was the whole defect — the entry did
                // nothing and said nothing.
                RefuseCommand($"{id} isn't wired in this build yet");
                RefreshTabTitles();
                return false;
        }

        RefreshTabTitles();
        return true;
    }

    /// <summary>The command id prefix carrying a character's <c>world.character</c> session key.</summary>
    private const string CharacterCommandPrefix = "char:";

    /// <summary>
    /// Says on the status line why a command surface entry did not do what its label promised. It goes
    /// through <see cref="Notice"/>, so it is visible with nothing connected and clears itself
    /// afterwards, and it is echoed into the output window so a missed one is still findable.
    /// </summary>
    private void RefuseCommand(string reason) => Notice(reason, MessageSeverity.Warning, "⌃P");

    /// <summary>
    /// The task the last dispatched command started, or a completed one when it ran synchronously.
    /// Internal so a headless test can await a connect it dispatched rather than poll for it; the app
    /// itself never waits on a command — a UI that blocked on a socket would be the bug next door.
    /// </summary>
    internal Task LastCommand { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The command surface's <c>Reconnect</c>: drops the active session's connection if it has one and
    /// dials it again.
    /// <para>
    /// It was <c>_ = _active?.ConnectAsync()</c> — three failures in one line. With no active session it
    /// did nothing, silently, which is precisely the state someone reaching for <em>Reconnect</em> is
    /// in; already connected it hit <c>ConnectAsync</c>'s early return and so did nothing there either,
    /// while being labelled "Reconnect"; and fire-and-forget meant a refused connection threw into a
    /// task nobody read. Now: it says when there is nothing to connect, it really does drop and redial
    /// an open connection, and a failure is reported on the status line as well as printed in the
    /// session's own window.
    /// </para>
    /// </summary>
    private void Reconnect()
    {
        if (_active is not { } session)
        {
            RefuseCommand("nothing to reconnect — pick a character first (⌃P ▸ Switch to …)");
            return;
        }

        LastCommand = ReconnectAsync(session);
    }

    private async Task ReconnectAsync(WorldSession session)
    {
        try
        {
            if (session.State is ConnectionState.Connected or ConnectionState.Connecting)
            {
                session.PrintSystem("*** Reconnecting...");
                await session.DisconnectAsync().ConfigureAwait(false);
            }

            // ConnectAsync returns silently when the session still believes it is connected, so a
            // transport that did not report its own disconnection would otherwise reproduce the exact
            // bug this method exists to fix: a Reconnect that does nothing and says nothing.
            if (session.State is ConnectionState.Connected or ConnectionState.Connecting)
            {
                OnUi(() => RefuseCommand(
                    $"{SessionTitle(session)} would not drop its connection — Disconnect, then Reconnect"));
                return;
            }

            await session.ConnectAsync().ConfigureAwait(false);

            // A freshly connected session has never been told a size, and there is no guarantee of
            // another frame soon enough to matter (see StartAsync).
            OnUiThread(ReportPaneSizes);
        }
        catch (Exception ex)
        {
            // WorldSession has already printed the failure into its own window (and logged it); the
            // status line says it too, because a fire-and-forget connect that only threw is exactly how
            // this used to fail in silence.
            OnUi(() => Notice(
                $"could not connect to {session.World.Host}:{session.World.Port} — {ex.Message}",
                MessageSeverity.Error,
                "⌃P"));
        }
    }

    /// <summary>
    /// The command surface's <c>Disconnect</c>. Same treatment as <see cref="Reconnect"/>: nothing to
    /// disconnect, or a connection that would not close, is said out loud rather than dropped.
    /// </summary>
    private void Disconnect()
    {
        if (_active is not { } session)
        {
            RefuseCommand("nothing to disconnect — pick a character first (⌃P ▸ Switch to …)");
            return;
        }

        if (!session.IsConnected)
        {
            RefuseCommand($"{SessionTitle(session)} is not connected");
            return;
        }

        LastCommand = DisconnectAsync(session);
    }

    private async Task DisconnectAsync(WorldSession session)
    {
        try
        {
            await session.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OnUi(() => RefuseCommand($"could not disconnect {SessionTitle(session)} — {ex.Message}"));
        }
    }

    /// <summary>
    /// The command surface's <c>Start logging</c>. Uses the character's own format and folder (F5); a
    /// character still on <see cref="LogFormat.None"/> — the default — gets a plain-text log, because
    /// asking for one is what the command means. It was emitted by the catalog and handled nowhere, so
    /// it did nothing at all, and the entry's label never flipped to <c>Pause logging</c> either.
    /// </summary>
    private void StartLogging()
    {
        if (_active is not { } session)
        {
            RefuseCommand("nothing to log — pick a character first (⌃P ▸ Switch to …)");
            return;
        }

        if (session.IsLogging)
        {
            RefuseCommand($"{SessionTitle(session)} is already logging");
            return;
        }

        var configured = session.Character?.Logging.Format ?? LogFormat.None;
        var format = configured == LogFormat.None ? LogFormat.Plain : configured;
        if (OpenLog(session.World, session.Character, format) is not { } sink)
        {
            // OpenLog has already said why on the status line.
            return;
        }

        session.AttachLog(sink);
        session.PrintSystem($"*** Logging to {LogFolder(session.Character)} ({format.ToString().ToLowerInvariant()}).");
    }

    /// <summary>The command surface's <c>Pause logging</c>: flushes and closes the sink.</summary>
    private void StopLogging()
    {
        if (_active is not { } session)
        {
            RefuseCommand("nothing to stop — pick a character first (⌃P ▸ Switch to …)");
            return;
        }

        if (!session.IsLogging)
        {
            RefuseCommand($"{SessionTitle(session)} is not logging");
            return;
        }

        session.DetachLog();
        session.PrintSystem("*** Logging paused.");
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
                FeedRange(control, buf, 0, buf.Count);
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
            ActiveBar().SetAndNotify(Uri.UnescapeDataString(url[MarkupFormatter.PromptScheme.Length..]));
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

            // Both drafts, the second bar's own visibility, and the history cursors all belong to the
            // window now showing — the input area follows the tab.
            ChangeWindow();
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
        _webImageControls.Clear();

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
        _webImageLoad = LoadWebImagesAsync(page, WebImageColumns(), cts.Token);
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

    /// <summary>
    /// A 64×48 PNG as a <c>data:</c> URI — four flat quadrants crossed by a dark diagonal, chosen so
    /// the snapshot shows at a glance whether the picture is the right size, the right way up, and in
    /// the right place. Small enough to live in source; no network, no asset file.
    /// </summary>
    private const string DemoImageDataUri =
        "data:image/png;base64," +
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAAAwCAIAAAAuKetIAAAA60lEQVR42tXPsQ3DQAxD0RvCg12dSTJEBvMYrlJn" +
        "hBRGgMCGD/JJlEji98Rry29b74j6+oTWlr/JAxAGOGC/wTGSADhDHgBkSAUgGAWAWEMNINBQBohiFAP8hnqA00AB" +
        "8DCIAHMGLsCEgQ5wl0EKsBt4AUYDNcDCEACMDRqAgUEGcMUQA5wNeoBkQ+uvN6gD47M+EAEBOQYsIMEAB+w3OEYS" +
        "AGfIA4AMqQAEowAQa6gBBBrKAFGMYoDfUA9wGigAHgYRYM7ABZgw0AHuMkgBdgMvwGigBlgYAoCxQQMwMMgArhhi" +
        "gLNBD3AwfAH2qz9wsoUh7AAAAABJRU5ErkJggg==";

    /// <summary>
    /// Drives the web view's real path for the <c>web</c> snapshot: HTML → styled lines + image index
    /// → the degradation chain's verdict → fetch/decode → composed blocks, and waits for the pictures
    /// so the single rendered frame contains them.
    ///
    /// <para>Nothing here forces graphics on. The frame shows whatever this host actually settles on,
    /// which is the point: with no graphics it is the <c>[image: …]</c> placeholder, with
    /// <c>SHARPMUTERM_GRAPHICS=halfblock</c> it is a real decoded picture drawn as half-block cells.
    /// Kitty output still needs a Kitty terminal — a snapshot is a plain-text sink.</para>
    /// </summary>
    private void ShowDemoWebPage()
    {
        const string url = "https://sharpmuterm.invalid/room";
        var html =
            "<html><head><title>The Cartographer's Study</title></head><body>" +
            "<h1>The Cartographer's Study</h1>" +
            "<p>Charts of the northern reaches cover every surface. A brass orrery ticks in the corner, " +
            "and the survey map of the coast road is pinned above the desk.</p>" +
            $"<img src=\"{DemoImageDataUri}\" alt=\"survey map of the coast road\">" +
            "<p>Exits lead <a href=\"https://sharpmuterm.invalid/hall\">north to the hall</a> and south " +
            "to the stair.</p>" +
            "</body></html>";

        var rendered = new SharpMUTerm.Web.HtmlStyledRenderer(url)
            .RenderDocument(html, Math.Max(20, _window.Width - 4));

        ShowWeb(new SharpMUTerm.Web.WebPage(
            url, SharpMUTerm.Web.HtmlStyledRenderer.GetTitle(html), rendered.Lines, rendered.Images));

        // ShowWeb kicks the load off in the background; a snapshot renders exactly one frame, so wait.
        _webImageLoad.GetAwaiter().GetResult();
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

    /// <summary>The decoded page images as plain sizes, for <see cref="WebImageReport"/>.</summary>
    private Dictionary<int, WebImageReport.Decoded> DecodedWebImages() =>
        _webImages.ToDictionary(e => e.Key, e => new WebImageReport.Decoded(e.Value.Width, e.Value.Height));

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
            // A text-only page still needs a viewport: it is one markup control, and a control taller
            // than its box paints only the rows the box has (the same defect the output panes had). Not
            // auto-scrolling — a page is a document, and its top is where you start reading.
            return ScrollViewFor(WebWindowId, live, autoScroll: false);
        }

        var boxes = new Dictionary<int, WebImageLayout.CellBox>();
        foreach (var (index, buffer) in _webImages)
        {
            boxes[index] = new WebImageLayout.CellBox(buffer.Width, Math.Max(1, buffer.Height / WebImageLayout.PixelsPerCell));
        }

        var blocks = WebViewComposer.Compose(_webMarkup, _webPage.Images, boxes);
        var panel = new List<IWindowControl>();

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
                        panel.Add(live);
                    }
                    else
                    {
                        // Later runs mirror PaneContentFor's plain control, link routing included,
                        // so a link reads the same wherever on the page it sits.
                        var markup = new MarkupControl(text.Lines.ToList());
                        markup.LinkClicked += (_, e) => OnLinkClicked(e.Url);
                        panel.Add(markup);
                    }

                    break;

                case WebImageBlock image:
                    panel.Add(WebImageControlFor(image));
                    break;
            }
        }

        if (!usedLiveControl)
        {
            // An all-image page: the window still needs its own control in the tree.
            live.SetContent(new List<string>());
            panel.Add(live);
        }

        // The same kept viewport the text-only page uses, so a page whose images arrive one at a time
        // (every arrival rebuilds the pane) does not throw the reader back to the top each time.
        return ScrollViewFor(WebWindowId, panel, autoScroll: false);
    }

    /// <summary>
    /// The control drawing one decoded page image, created once per image and reused for the life of
    /// the page. See <see cref="_webImageControls"/> for why a fresh control per rebuild is wrong.
    /// </summary>
    private ImageControl WebImageControlFor(WebImageBlock block)
    {
        var source = _webImages[block.Index];
        if (_webImageControls.TryGetValue(block.Index, out var control))
        {
            // Guard against a stale control if a page ever re-decodes the same slot.
            if (!ReferenceEquals(control.Source, source))
            {
                control.Source = source;
            }

            control.MinimumHeight = block.Box.Rows;
            return control;
        }

        control = new ImageControl
        {
            Source = source,
            ScaleMode = ImageScaleMode.Fit,
            MinimumHeight = block.Box.Rows,
        };
        _webImageControls[block.Index] = control;
        return control;
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
    /// A window's output as the pane actually shows it: its markup control inside a scroll viewport.
    /// <para>
    /// This is the fix for the defect that a pane could not scroll at all. A bare
    /// <see cref="MarkupControl"/> paints its rows from index 0 down until it runs out of box
    /// (<c>MarkupControl.PaintDOM</c>) — it has no scroll offset and no bottom anchoring — so a window
    /// with more lines than rows showed its <em>oldest</em> screenful for ever and every new line landed
    /// off the bottom of the box, invisible. Scrolling in SharpConsoleUI lives in
    /// <see cref="ScrollablePanelControl"/>, whose <see cref="ScrollablePanelControl.AutoScroll"/> is
    /// exactly terminal behaviour: it pins the viewport to the bottom on any repaint while enabled,
    /// detaches when the reader scrolls up, and re-attaches when they come back down. Nothing here
    /// reimplements any of that.
    /// </para>
    /// </summary>
    private ScrollablePanelControl OutputViewFor(string id, string title) =>
        ScrollViewFor(id, PaneContentFor(id, title));

    /// <summary>
    /// The scroll viewport for one output region, created on first use and kept for the life of the
    /// window under <paramref name="key"/> — so the reader's scroll position survives the pane-area
    /// rebuilds a split, a tab change or a freeze all trigger.
    /// <para>
    /// It refills itself rather than trusting that it still holds its child.
    /// <see cref="RebuildPaneArea"/> hands the old workspace row to <c>Window.RemoveContent</c>, which
    /// disposes it, and a disposed grid disposes its children all the way down — and
    /// <c>ScrollablePanelControl.OnDisposing</c> <em>clears its child list</em>. The markup controls
    /// themselves survive that (they hold nothing and override nothing, which is why
    /// <see cref="PaneContentFor"/> can re-parent the same control for the life of the app), so the
    /// repair is to put the child back, not to keep a second copy of it.
    /// </para>
    /// </summary>
    /// <param name="autoScroll">
    /// True for a live tail — output whose newest line is the interesting one. False for a document
    /// whose top is where you start reading, which is what the web view is.
    /// </param>
    private ScrollablePanelControl ScrollViewFor(string key, IWindowControl content, bool autoScroll = true) =>
        ScrollViewFor(key, new[] { content }, autoScroll);

    /// <inheritdoc cref="ScrollViewFor(string, IWindowControl, bool)"/>
    private ScrollablePanelControl ScrollViewFor(string key, IReadOnlyList<IWindowControl> content, bool autoScroll = true)
    {
        if (!_paneScrolls.TryGetValue(key, out var panel))
        {
            panel = new ScrollablePanelControl
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Fill,
                VerticalScrollMode = ScrollMode.Scroll,

                // The markup control wraps, so there is never anything to the right to reach.
                HorizontalScrollMode = ScrollMode.None,

                // No scrollbar. It would cost two columns of every output pane the moment the buffer
                // passed one screenful — permanently, in a client whose entire job is columns of text —
                // and it would cost them somewhere the app does not currently look: per-pane NAWS is
                // derived from the pane's own rectangle (see PaneOutputRects), so a reserved gutter
                // would silently make every server's reported width two columns too wide. The scroll
                // position is reported on the status row instead (ScrollbackStatus), which is where
                // this app puts transient state and which costs no output width at all.
                ShowScrollbar = false,
                AutoScroll = autoScroll,
            };
            panel.Scrolled += (_, _) => OnPaneScrolled();
            _paneScrolls[key] = panel;
        }

        var children = panel.Children;
        if (!children.SequenceEqual(content, ReferenceEqualityComparer.Instance))
        {
            foreach (var child in children)
            {
                panel.RemoveControl(child); // RemoveControl, not ClearContents: that one disposes them
            }

            foreach (var child in content)
            {
                panel.AddControl(child);
            }
        }

        return panel;
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
            ? OnSurface(BuildPaneTabs(zoomed))
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

    /// <summary>
    /// The one-cell hairline beside the rail and between two split panes. A <see cref="MarkupControl"/>
    /// with no lines measures to nothing and never paints its background — which is what this was, and
    /// why the dividers have never actually been drawn — so it is an empty grid instead, whose
    /// background covers its whole arranged area. (<see cref="ScreenChrome.VerticalRule"/> is the same
    /// trick; the settings screens found it first.) It matters more now that the panes carry a surface:
    /// a hairline is what keeps two adjacent surfaces from reading as one.
    /// </summary>
    private IWindowControl Divider()
    {
        var rule = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Column(c => c.Flex(1).Add(new MarkupControl(new List<string>())))
            .Build();
        rule.BackgroundColor = ToColor(WorkspacePalette.Rule(_theme));
        return rule;
    }

    /// <summary>
    /// Paints a pane's whole rectangle — tab strip, output and the empty rows below it — on the
    /// workspace surface. A <see cref="MarkupControl"/> only backgrounds the rows it has content for
    /// (its paint fills everything past the last line transparently), so an empty pane would stay the
    /// backdrop's colour and go on reading as a hole; a grid's background covers the area it is
    /// arranged in, however little is in it.
    /// </summary>
    private IWindowControl OnSurface(IWindowControl content)
    {
        var surface = Controls.Grid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill);
        surface.Rows(GridLength.Star(1)).Columns(GridLength.Star(1));
        surface.Place(content, 0, 0, 1, 1);
        var built = surface.Build();
        built.BackgroundColor = ToColor(WorkspacePalette.Surface(_theme));
        return built;
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
                return OnSurface(BuildDragPane(pane));
            }

            return OnSurface(_moveMode && _moveLetters.TryGetValue(pane.Id, out var letter)
                ? BuildMovePane(pane, letter)
                : BuildPaneTabs(pane));
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

            builder.AddTab(TabTitles.For(window, ActiveCharacterKey()), BuildTabContent(pane, windowId, window));
            ids.Add(windowId);
        }

        // Stretch so the tab strip + content fill their pane column to the right edge; the control
        // default is Left, which self-sizes to content and leaves the pane short (docs/patterns.md §12).
        var tabs = builder.Fill().WithAlignment(HorizontalAlignment.Stretch).Build();
        for (var i = 0; i < ids.Count; i++)
        {
            tabs.TabPages[i].Tag = ids[i];
            tabs.TabPages[i].IsClosable = CanCloseTab(ids[i], pane.ActiveTab);
        }

        if (pane.ActiveIndex >= 0 && pane.ActiveIndex < tabs.TabCount)
        {
            tabs.ActiveTabIndex = pane.ActiveIndex;
        }

        var paneId = pane.Id;
        tabs.TabChanged += (_, e) => OnTabChanged(paneId, e.NewTab);
        tabs.TabCloseRequested += (_, e) => OnTabCloseRequested(e.TabPage);
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

        return OutputViewFor(windowId, window.Title);
    }

    /// <summary>Wraps a spawn window's output under a dim capture line naming its trigger pattern.</summary>
    private IWindowControl BuildSpawnContent(string windowId, WorkspaceWindow window)
    {
        var header = new MarkupControl(new List<string> { CaptureLineRenderer.Line(window.CapturePattern!) });
        var output = OutputViewFor(windowId, window.Title);

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
    /// <para>
    /// <b>Both halves get their own scroll viewport.</b> The pinned half is the one a reader most wants
    /// to move through — it is the history freeze exists to hold still — and before this it could show
    /// only the oldest screenful of it, which made ⌃F a way of pinning text you could not read. The
    /// live tail gets one too, for the same reason every other pane does: it is a tail, and a burst of
    /// output past its few rows would otherwise vanish under the bar.
    /// </para>
    /// <para>
    /// Both are ordinary <see cref="ScrollablePanelControl.AutoScroll"/> viewports, with no
    /// freeze-specific rule. On the pinned half "the bottom" is the freeze point — the last line that was
    /// on screen when ⌃F was pressed — so auto-scroll opens it exactly where the reader left off and
    /// detaches the moment they scroll up, which is the behaviour a special case would have had to
    /// reproduce. It also keeps the half honest when the scrollback cap trims the buffer and
    /// <see cref="AppendWindowLine"/> walks the freeze point down: the pinned region shrinks and the
    /// viewport re-clamps to its new end instead of drifting off the content.
    /// </para>
    /// </summary>
    private IWindowControl BuildFrozenContent(string windowId, string title)
    {
        var buffer = _lines.TryGetValue(windowId, out var b) ? b : new List<string>();
        var split = _freezePoints.TryGetValue(windowId, out var p) ? Math.Clamp(p, 0, buffer.Count) : buffer.Count;

        var frozen = FrozenContentFor(windowId);
        FeedRange(frozen, buffer, 0, split);

        var bar = new MarkupControl(new List<string> { FreezeBarRenderer.Bar(FrozenAccentHex()) });

        var live = PaneContentFor(windowId, title);
        FeedRange(live, buffer, split, buffer.Count - split);

        // Pinned scrollback gets the lion's share; a single "❄ FROZEN ⌃F ───" line is both label and
        // border, with the live tail a few rows below it.
        var grid = Controls.Grid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill);
        grid.Rows(GridLength.Star(3), GridLength.Cells(1), GridLength.Star(1)).Columns(GridLength.Star(1));
        grid.Place(ScrollViewFor(FrozenRegionKey(windowId), frozen), 0, 0, 1, 1);
        grid.Place(bar, 1, 0, 1, 1);
        grid.Place(ScrollViewFor(windowId, live), 2, 0, 1, 1);
        return grid.Build();
    }

    /// <summary>The <see cref="_paneScrolls"/> key of a window's pinned-scrollback half while frozen.</summary>
    private static string FrozenRegionKey(string windowId) => $"frozen:{windowId}";

    /// <summary>A window's pinned-scrollback control, created (with link routing) on first freeze.</summary>
    private MarkupControl FrozenContentFor(string windowId)
    {
        if (_frozenPanes.TryGetValue(windowId, out var existing))
        {
            return existing;
        }

        var control = new MarkupControl(new List<string>());
        control.LinkClicked += (_, e) => OnLinkClicked(e.Url);
        _frozenPanes[windowId] = control;
        return control;
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
        if (AnyOverlayOpen)
        {
            return;
        }

        _prefixArmed = true;
        _header.SetContent(new List<string> { HeaderMarkup() });
    }

    /// <summary>Consumes the key after ⌃B and runs the matching pane command (tmux-style).</summary>
    private void OnWindowKey(object? sender, KeyPressedEventArgs e) => HandleWindowKey(e);

    /// <summary>
    /// Feeds one key to the very handler the main window's <c>PreviewKeyPressed</c> raises, and reports
    /// the macro command it sent. It exists for the same reason
    /// <see cref="SettingsOverlay.SimulateKey"/> does — the framework only pumps keys inside
    /// <c>Run()</c>, which a headless test never enters — and it goes <em>through</em>
    /// <see cref="HandleWindowKey"/> rather than around it, so what it proves is what a keystroke does.
    /// </summary>
    internal string? SimulateKey(ConsoleKeyInfo key)
    {
        // The framework runs a global shortcut before the window sees the key at all, so the harness
        // does too — otherwise a test pressing ⌃B would find it typed into the command line, which is
        // the opposite of what the running app does with it.
        if (_shortcuts.TryGetValue((key.Modifiers, key.Key), out var shortcut))
        {
            shortcut();
            return null;
        }

        return HandleWindowKey(new KeyPressedEventArgs(key, false));
    }

    /// <summary>Switches the visible window the way the tab strip does, so the tab-changed path runs.</summary>
    internal void SimulateWindowChange(string windowId) => Activate(windowId);

    /// <summary>
    /// Delivers one paste by the framework's rule: to the window's focused control, and only when that
    /// control accepts paste. It deliberately does not call <see cref="InputBarControl.Paste"/> — the
    /// bar's own paste was never broken, the routing to it was, so a test that pasted into the bar
    /// directly would pass either way. The rule is restated here rather than driven through
    /// <c>WindowEventDispatcher.ProcessPaste</c> because that type is internal to SharpConsoleUI; it is
    /// the same two lines, and the pty run behind <c>APasteAfterFocusIsTakenOffTheBar…</c> is what keeps
    /// the restatement honest.
    /// </summary>
    internal void SimulatePaste(string text)
    {
        if (_window.FocusManager.FocusedControl is IPasteTarget target)
        {
            target.Paste(text);
        }
    }

    /// <summary>
    /// Takes the keyboard focus off the command line the way the framework does when nothing asks it
    /// not to: ⇥ with no sibling bar to hand the caret to walks focus to the next control in the window,
    /// and a mouse click focuses whatever the pointer hit. Neither goes through the app, which is the
    /// point — it is the state a paste used to disappear into, and it is reachable without the app's
    /// consent.
    /// </summary>
    internal void SimulateFocusSteal() => _window.FocusManager.MoveFocus(backward: false);

    /// <summary>
    /// How wide the header markup is, in cells. This is the number the very first frame is laid out
    /// against and the one a test has to read: the status cluster is right-aligned by padding the row
    /// out to the width the header was built for, so a header built for a wider terminal than the one
    /// it is on is a header that wraps. Readable before anything has been rendered, which is the whole
    /// point — every render path in this app rebuilds the header on the way past, and by then the
    /// window exists and the width is no longer a guess.
    /// </summary>
    internal int HeaderMarkupWidth => MarkupWidth(_header.Text);

    /// <summary>Whether the framework's keyboard focus is on the bar ⏎ sends from — the app's one rule.</summary>
    internal bool ArmedBarHasFocus => ReferenceEquals(_window.FocusManager.FocusedControl, ActiveBar());

    /// <summary>Whether the control holding the keyboard focus is one the window still draws.</summary>
    internal bool FocusIsOnAVisibleControl =>
        _window.FocusManager.FocusedControl is IWindowControl { Visible: true };

    /// <summary>Whether each bar is reporting a caret for the terminal to sit on — armed and focused.</summary>
    internal (bool Primary, bool Second) CaretReported => (
        _input.GetLogicalCursorPosition() is not null,
        _second.GetLogicalCursorPosition() is not null);

    /// <summary>What the command line ⏎ sends from is holding — the armed bar's text.</summary>
    internal string ArmedInputText => ActiveBar().Text;

    /// <summary>What each bar is holding, whichever is armed.</summary>
    internal string PrimaryInputText => _input.Text;

    /// <summary>What the second bar is holding, whether or not it is on screen.</summary>
    internal string SecondaryInputText => _second.Text;

    /// <summary>Whether the active window is showing its second command line.</summary>
    internal bool SecondBarShown => _second.Visible;

    /// <summary>Whether ⏎ would send from the second bar rather than the first.</summary>
    internal bool SecondBarArmed => ReferenceEquals(_armed, _second);

    /// <summary>How many rows tall the primary command line currently is.</summary>
    internal int PrimaryInputRows => _input.Rows();

    /// <summary>
    /// What the last frame actually gave each band of the window: the header, the workspace, each
    /// command line, and the status line, as the framework arranged them. Deliberately read from the
    /// arranged bounds rather than from <see cref="InputBarControl.Rows"/> — a bar asking for three
    /// rows and a window handing it none agree on the arithmetic and disagree on the screen, and only
    /// these numbers can tell the two apart. Zero until a frame has been rendered.
    /// </summary>
    internal (int Header, int Workspace, int Primary, int Second, int Status) LaidOutRows => (
        _header.ActualHeight,
        _workspaceRow.ActualHeight,
        _input.ActualHeight,
        _second.Visible ? _second.ActualHeight : 0,
        _statusBar.ActualHeight);

    /// <summary>The two band colours a command line paints itself in — armed, and the one ⏎ ignores.</summary>
    internal (Color Armed, Color Idle) InputBandColors => (_input.BandColor, _input.IdleBandColor);

    /// <summary>
    /// One output viewport's scroll state, as the framework reports it after a real frame. Internal so a
    /// test can assert what the panel believes beside what the frame actually painted — the two together
    /// are what say the pane scrolls, rather than either alone.
    /// </summary>
    /// <param name="ContentRows">Total rows of content in the viewport, which may far exceed <paramref name="ViewportRows"/>.</param>
    /// <param name="ViewportRows">Rows the viewport shows at once.</param>
    /// <param name="Offset">Content rows hidden above the viewport.</param>
    /// <param name="AutoScroll">Whether the viewport is still pinned to the newest line.</param>
    internal readonly record struct ScrollbackView(
        int ContentRows, int ViewportRows, int Offset, bool AutoScroll, bool CanScrollUp, bool CanScrollDown);

    /// <summary>A window's live-output viewport state, or null before one has been built.</summary>
    internal ScrollbackView? ScrollbackOf(string windowId) => ViewOf(_paneScrolls.GetValueOrDefault(windowId));

    /// <summary>A window's pinned-scrollback viewport state while its pane is frozen, or null.</summary>
    internal ScrollbackView? FrozenScrollbackOf(string windowId) =>
        ViewOf(_paneScrolls.GetValueOrDefault(FrozenRegionKey(windowId)));

    /// <summary>The viewport the scrollback keys are currently aimed at, or null when no pane is realised.</summary>
    internal ScrollbackView? ScrollTargetView => ViewOf(ScrollTarget());

    private static ScrollbackView? ViewOf(ScrollablePanelControl? panel) => panel is null
        ? null
        : new ScrollbackView(
            panel.TotalContentHeight, panel.ViewportHeight, panel.VerticalScrollOffset,
            panel.AutoScroll, panel.CanScrollUp, panel.CanScrollDown);

    /// <summary>
    /// Where the framework actually arranged a window's output control, in desktop cells. This is the
    /// reading that separates "the pane scrolls" from "the pane is showing the top and the numbers happen
    /// to add up": a scrolled viewport arranges its child at its full content height and a
    /// <em>negative</em> top, and clips it to the viewport. Null until a frame has laid the pane out.
    /// </summary>
    internal PaneRect? OutputContentBounds(string windowId)
    {
        if (_panes.GetValueOrDefault(windowId) is not { } control ||
            _window.GetLayoutNode(control) is not { } node)
        {
            return null;
        }

        var origin = ContentOrigin();
        var bounds = node.AbsoluteBounds;
        return new PaneRect(origin.X + bounds.X, origin.Y + bounds.Y, bounds.Width, bounds.Height);
    }

    /// <summary>Feeds one key straight to the scrollback handler, reporting whether it was a scrollback key.</summary>
    internal bool SimulateScrollKey(ConsoleKeyInfo key) => TryScrollKey(new KeyPressedEventArgs(key, false));

    /// <summary>A window's unread badge count, as the tab strip and the rail render it.</summary>
    internal int UnreadOf(string windowId) => _workspace.FindWindow(windowId)?.Unread ?? 0;

    /// <summary>
    /// Hands a key to the armed command line and reports whether it took it. Focus is put back on that
    /// bar first: it is where the caret belongs, and where the framework delivers a paste.
    /// </summary>
    private bool RouteToInput(ConsoleKeyInfo key)
    {
        var bar = ActiveBar();
        if (!bar.HasFocus)
        {
            _window.FocusControl(bar);
        }

        return bar.ProcessKey(key);
    }

    /// <summary>
    /// Arms the ⌃B prefix and then feeds one key, as pressing the chord and the key would. The arming
    /// goes through the method the shortcut itself runs, because ⌃B <em>is</em> a global shortcut and
    /// the framework dispatches those only inside <c>Run()</c> — the loop a headless test never enters —
    /// so there is no pair of keystrokes a test could send instead. Everything after that is the real
    /// handler.
    /// </summary>
    internal string? SimulatePrefixedKey(ConsoleKeyInfo key)
    {
        ArmPrefix();
        return SimulateKey(key);
    }

    /// <summary>The status line's current markup — where a pane command that had nothing to do says so.</summary>
    internal string StatusMarkup => _statusBar.Text;

    /// <summary>Whether the ⌃Q confirmation is up.</summary>
    internal bool QuitPromptOpen => _quit.IsOpen;

    /// <summary>What the ⌃Q confirmation is asking — the rendered markup, for a headless test to read.</summary>
    internal IReadOnlyList<string> QuitPromptLines => _quit.Lines;

    /// <summary>
    /// Feeds one key to the open confirmation, through the handler its <c>PreviewKeyPressed</c> raises.
    /// The modal's keys cannot go in through <see cref="SimulateKey"/>: a modal window owns the keyboard
    /// while it is up, and the framework's routing to it only exists inside <c>Run()</c>.
    /// </summary>
    internal void SimulateQuitKey(ConsoleKeyInfo key) => _quit.SimulateKey(key);

    /// <summary>Feeds one key to the open settings screen, the way the <c>-edit</c> snapshot views do.</summary>
    internal void SimulateSettingsKey(ConsoleKeyInfo key) => _settings.SimulateKey(key);

    /// <summary>Whether the ⌃R history surface is up.</summary>
    internal bool HistorySearchOpen => _historySearch.IsOpen;

    /// <summary>What the ⌃R surface is showing — the rendered markup, for a headless test to read.</summary>
    internal IReadOnlyList<string> HistorySearchLines => _historySearch.Lines;

    /// <summary>The rows the ⌃R surface is currently listing, newest first.</summary>
    internal IReadOnlyList<string> HistorySearchRows =>
        _historySearch.Matches.Select(m => m.Text).ToArray();

    /// <summary>The entry ⏎ would insert from the ⌃R surface, or null when nothing is listed.</summary>
    internal string? HistorySearchSelection => _historySearch.Selected;

    /// <summary>
    /// Feeds one key to the open ⌃R surface, through the handler its <c>PreviewKeyPressed</c> raises. Like
    /// <see cref="SimulateQuitKey"/>, it cannot go through <see cref="SimulateKey"/>: a modal window owns
    /// the keyboard while it is up, and the framework's routing to it only exists inside <c>Run()</c>.
    /// </summary>
    internal void SimulateHistorySearchKey(ConsoleKeyInfo key) => _historySearch.SimulateKey(key);

    /// <summary>Types a filter into the open ⌃R surface, one real keystroke at a time.</summary>
    internal void SimulateHistorySearchTyping(string text) => _historySearch.SimulateTyping(text);

    /// <summary>What a command line has recorded, oldest first — the list the ⌃R surface reads.</summary>
    internal IReadOnlyList<string> HistoryEntries(InputBar bar) => HistoryFor(bar).Entries;

    /// <summary>Whether a confirmed quit has ended the loop — what <c>RequestExit</c> did, observably.</summary>
    internal bool ExitRequested => _exiting;

    /// <summary>
    /// The framework's own quit-from-anywhere key, which this app turns off so the confirmation is the
    /// only way out. Read back by test, because the default is the exact chord we intercept.
    /// </summary>
    internal ConsoleKey? FrameworkExitKey => _system.Options.ExitKey;

    /// <summary>
    /// The main window's key handler: move mode, the drag escape, the ⌃B prefix, then a bound macro,
    /// then draft-safe history recall. Returns the macro command it dispatched, or null.
    /// </summary>
    private string? HandleWindowKey(KeyPressedEventArgs e)
    {
        if (_moveMode)
        {
            HandleMoveKey(e);
            return null;
        }

        // Escape abandons a mouse drag. A terminal that loses the button-up (the pointer left the
        // window, the terminal dropped a frame) would otherwise strand the preview over the panes.
        if (_dragActive && e.KeyInfo.Key == ConsoleKey.Escape)
        {
            e.Handled = true;
            _paneDrag.Reset(); // no mouse frame ends this one, so the gesture has to be dropped here
            EndDrag();
            return null;
        }

        if (!_prefixArmed)
        {
            // A modal surface owns the keyboard while it is up. Both are separate windows, so the
            // framework already routes keys to them and this handler is not raised at all — the guard is
            // here because "a macro must not fire while a screen is open" is a rule of this app, not a
            // consequence of how the framework happens to dispatch, and the next surface may not be modal.
            if (AnyOverlayOpen)
            {
                return null;
            }

            if (DispatchMacro(e.KeyInfo) is { } sent)
            {
                e.Handled = true;
                return sent;
            }

            // Scrollback: PgUp/PgDn, Shift+↑/↓, ⌃Home/⌃End. After the macros for the same reason recall
            // is — a key the user has explicitly bound to a command is theirs — and before recall,
            // because Shift+↑ used to fall into it (TryRecallKey does not look at modifiers).
            if (TryScrollKey(e))
            {
                return null;
            }

            // Draft-safe history recall on ↑/↓ — our own, so a half-typed draft survives (see InputHistory).
            // It asks the command line first, so the arrows only recall where the caret cannot move.
            if (TryRecallKey(e))
            {
                return null;
            }

            // Everything else that is typing goes to the command line, focused or not. This is the app's
            // focus policy, not the framework's: SharpConsoleUI routes a key to whichever control holds
            // focus, and a client whose typing lands in a tab strip because the last click did is not one
            // anybody wants. Routing here also keeps the keyboard focus on the bar, so paste (which the
            // window sends to the focused IPasteTarget) and the terminal caret follow the same rule.
            if (RouteToInput(e.KeyInfo))
            {
                e.Handled = true;
            }

            return null;
        }

        _prefixArmed = false;
        e.Handled = true;
        RunPrefixCommand(PrefixKey(e.KeyInfo));
        _header.SetContent(new List<string> { HeaderMarkup() });
        return null;
    }

    /// <summary>
    /// Which pane command a key pressed after ⌃B names. The keymap is literal characters — <c>&lt;</c>
    /// and <c>&gt;</c> reorder the active tab — but a bare pair of angle brackets on the armed strip
    /// reads as a direction, and reaching for ← and → is what that label invites; so the arrows are
    /// accepted as the same two commands. Nothing competes for them here: their other job, draft-safe
    /// history recall, only runs while the prefix is <em>not</em> armed.
    /// </summary>
    private static char PrefixKey(ConsoleKeyInfo key) => key.Key switch
    {
        ConsoleKey.LeftArrow => '<',
        ConsoleKey.RightArrow => '>',
        _ => char.ToLowerInvariant(key.KeyChar),
    };

    /// <summary>
    /// Runs the pane command a ⌃B key names, and says on the status line when the command had nothing
    /// to do.
    /// <para>
    /// The reporting is the point. On a fresh workspace — one pane holding one window — every key on
    /// the strip is a legitimate no-op: a split moves the pane's <em>other</em> tabs across and there
    /// are none, reordering needs a second tab, and zoom and cycle need a second pane. A keystroke that
    /// changes nothing and says nothing is indistinguishable from a prefix that never fired, which is
    /// exactly how the whole feature read from the outside.
    /// </para>
    /// </summary>
    private void RunPrefixCommand(char key)
    {
        switch (key)
        {
            case '|':
                SplitFocusedPane(PaneCommand.SplitRight);
                break;

            case '-':
                SplitFocusedPane(PaneCommand.SplitDown);
                break;

            case 'z':
                if (_workspace.Layout.Panes.Count <= 1)
                {
                    RefusePrefix("nothing to zoom — the workspace has one pane");
                    break;
                }

                _workspace.Layout.ToggleZoom();
                RebuildPaneArea();
                break;

            case 'o':
                if (_workspace.Layout.Panes.Count <= 1)
                {
                    RefusePrefix("nowhere to cycle to — the workspace has one pane");
                    break;
                }

                CyclePane();
                break;

            case 'x':
                // CloseActiveWindow refuses the main window; it is the session, not a closable tab.
                if (ActiveWindowId() == MainWindowId)
                {
                    RefusePrefix("the main window stays open — ⌃B x closes a spawn or web tab");
                    break;
                }

                CloseActiveWindow();
                break;

            case 'b':
                _railCollapsed = !_railCollapsed;
                RebuildPaneArea();
                break;

            case '<':
            case '>':
                var tabs = _workspace.Layout.FocusedPane.Tabs.Count;
                if (_workspace.Layout.ReorderActiveTab(key == '<' ? -1 : 1))
                {
                    RefreshTabTitles();
                    break;
                }

                RefusePrefix(tabs > 1
                    ? "the tab is already at that end of the strip"
                    : "nothing to reorder — this pane has one tab");
                break;

            case 'm':
                EnterMoveMode();
                break;

            case 'i':
                ToggleSecondBar();
                break;

            default:
                break; // any other key just disarms
        }
    }

    /// <summary>
    /// Splits the focused pane, or reports why it can't. Shared by <c>⌃B |</c> / <c>⌃B -</c> and the
    /// command surface's split entries, which refused just as quietly.
    /// </summary>
    private void SplitFocusedPane(PaneCommand command)
    {
        if (PaneCommands.Apply(_workspace.Layout, command))
        {
            RebuildPaneArea();
            return;
        }

        RefusePrefix("nothing to split — a split moves this pane's other tabs across, and it has none");
    }

    /// <summary>
    /// Says on the status line that a pane command had nothing to do. It goes through
    /// <see cref="Notice"/>, which is what makes it go away again: it used to be a bare
    /// <see cref="SetStatus"/> whose comment claimed "the next <see cref="UpdateStatus"/> puts the
    /// connection line back" — true only while something was connected, because nothing else calls
    /// that. On a fresh client the refusal sat on the row for the rest of the session.
    /// </summary>
    private void RefusePrefix(string reason) => Notice(reason, MessageSeverity.Warning, "⌃B");

    /// <summary>
    /// Runs the macro bound to a keystroke and returns the command it sent, or null when this key is not
    /// one the app acts on. This is the wire the F4 screen has always drawn and nothing ever connected:
    /// <see cref="MacroEngine"/> and <see cref="WorldSession.HandleKeyAsync"/> were written and tested,
    /// and no key press had ever reached either of them.
    /// <para>
    /// It sits on the main window's <c>PreviewKeyPressed</c>, which is the one place with all three
    /// properties a macro needs: it runs <em>before</em> the focused control, so a binding beats the
    /// prompt; it is not raised while a modal (a settings screen, the command surface) holds the
    /// keyboard; and it runs <em>after</em> the global shortcuts, so the chords the app claims for
    /// itself never arrive here — which is why <see cref="MacroKeys.Verdict"/> reports those as taken
    /// rather than the screen pretending a macro could outrank them.
    /// </para>
    /// <para>
    /// The macro is resolved before it is sent because the answer decides whether the keystroke is
    /// swallowed, and <see cref="WorldSession.HandleKeyAsync"/> only reports that after it has already
    /// sent. The send itself still goes through that method: it is the one path from a key to the wire,
    /// and a second one here would be a second thing to keep in step. Nothing connected means nothing to
    /// send to, so the key falls through to whatever would have had it.
    /// </para>
    /// </summary>
    private string? DispatchMacro(ConsoleKeyInfo key)
    {
        if (_active is not { } session || MacroKeys.Descriptor(key) is not { } descriptor)
        {
            return null;
        }

        if (session.Macros.Resolve(descriptor) is not { Command.Length: > 0 } macro)
        {
            return null;
        }

        _ = session.HandleKeyAsync(descriptor);
        return macro.Command;
    }

    /// <summary>
    /// Opens the session for a world and binds it <em>without connecting</em> — the pair of calls
    /// <see cref="StartAsync"/> makes before it dials. It exists so the key → macro → command path can be
    /// driven end to end without a socket: <see cref="WorldSession.HandleKeyAsync"/> resolves and reports
    /// a binding whether or not there is a transport under it to write to.
    /// </summary>
    internal WorldSession BindWorldWithoutConnecting(WorldDefinition world)
    {
        var session = OpenSession(world);
        BindSession(session);
        return session;
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
        SetStatus(MovePromptMarkup(), displace: true);
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
            SetStatus(MovePromptMarkup(), displace: true);
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
                SetStatus(MovePromptMarkup(), displace: true);
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
        if (AnyOverlayOpen || _moveMode)
        {
            return;
        }

        if (WheelLines(flags) is { } lines)
        {
            OnUiThread(() => ScrollPaneUnderPointer(point, lines));
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
    /// How many lines a mouse frame asks the scrollback to move, or null when it is not a wheel frame.
    /// Three, the customary notch: one line makes a wheel useless on a transcript and a whole page makes
    /// it unusable for reading one.
    /// </summary>
    private static int? WheelLines(List<MouseFlags> flags)
    {
        if (flags.Contains(MouseFlags.WheeledUp))
        {
            return -WheelNotchRows;
        }

        return flags.Contains(MouseFlags.WheeledDown) ? WheelNotchRows : null;
    }

    /// <summary>Lines one wheel notch moves the scrollback.</summary>
    private const int WheelNotchRows = 3;

    /// <summary>
    /// Scrolls whichever pane the pointer is over.
    /// <para>
    /// Routed from the driver rather than left to the panel's own <c>IMouseAwareControl</c> handling, for
    /// the reason the whole mouse layer of this app is: the framework delivers a mouse frame to the
    /// control it hit-tests, and this app's hit-testing of the pane area already lives here because a
    /// drag has to see every pane rather than only the one that was pressed
    /// (<see cref="OnDriverMouseEvent"/>). Doing it here also means the wheel is a thing a headless frame
    /// can prove, which a route through the framework's input pump — reachable only from inside
    /// <c>Run()</c> — is not.
    /// </para>
    /// <para>
    /// The pointer, not the focus: a wheel scrolls what it is over, which is the one mouse convention no
    /// user checks first. That makes it the only scrollback route that reads a pane other than the
    /// focused one, so the state sync is done against <em>that</em> pane's window.
    /// </para>
    /// </summary>
    private void ScrollPaneUnderPointer(System.Drawing.Point point, int lines)
    {
        foreach (var (paneId, rect) in PaneOutputRects())
        {
            if (point.X < rect.X || point.X >= rect.X + rect.Width ||
                point.Y < rect.Y || point.Y >= rect.Y + rect.Height)
            {
                continue;
            }

            if (_workspace.Layout.FindPane(paneId) is not { ActiveTab: { } windowId } pane)
            {
                return;
            }

            // A frozen pane is two regions in one rectangle; the wheel takes the one the pointer is in.
            // The pinned half occupies the top of the pane, so its arranged height is the boundary.
            var frozen = pane.Frozen ? _paneScrolls.GetValueOrDefault(FrozenRegionKey(windowId)) : null;
            var overFrozen = frozen is { ActualHeight: > 0 } && point.Y < rect.Y + frozen.ActualHeight;

            if ((overFrozen ? frozen : _paneScrolls.GetValueOrDefault(windowId)) is not { } over)
            {
                return;
            }

            over.ScrollVerticalBy(lines);
            SyncScrollbackState(windowId);
            return;
        }
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
        var rects = new Dictionary<string, PaneRect>(StringComparer.Ordinal);
        var windows = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (paneId, _, rect) in RealisedPanes())
        {
            rects[paneId] = rect;

            if (_workspace.Layout.FindPane(paneId)?.ActiveTab is { } windowId)
            {
                windows[paneId] = windowId;
            }
        }

        return new PaneDragSurface(rects, windows);
    }

    /// <summary>
    /// Every pane that currently has an arranged tab control, with its whole rectangle in desktop
    /// cells — tab strip included. A layout node exists only while the arranged layout does, so a pane
    /// rebuilt since the last frame is simply absent (see <see cref="RebuildPaneArea"/>).
    /// </summary>
    private List<(string PaneId, TabControl Tabs, PaneRect Rect)> RealisedPanes()
    {
        var origin = ContentOrigin();

        KeyValuePair<string, TabControl>[] realised;
        lock (_paneTabsLock)
        {
            realised = _paneTabs.ToArray();
        }

        var panes = new List<(string, TabControl, PaneRect)>(realised.Length);
        foreach (var (paneId, tabs) in realised)
        {
            if (_window.GetLayoutNode(tabs) is not { } node)
            {
                continue;
            }

            var bounds = node.AbsoluteBounds;
            panes.Add((paneId, tabs, new PaneRect(origin.X + bounds.X, origin.Y + bounds.Y, bounds.Width, bounds.Height)));
        }

        return panes;
    }

    /// <summary>
    /// Each realised pane's <em>output</em> rectangle: the pane less its tab strip and the tab
    /// control's own margins — the cells a window's text is actually arranged into, which is what a
    /// server needs for NAWS. The arithmetic mirrors the framework's
    /// <c>TabLayout.ArrangeChildren</c> and reads the strip's depth off the live control
    /// (<see cref="TabControl.TabHeaderHeight"/>: one row for the classic header, two for the
    /// separator styles) rather than assuming a row.
    /// <para>
    /// Internal because the NAWS tests read it back beside <see cref="PaneSnapshot"/> — the pair is
    /// the claim that the reported rows exclude the chrome.
    /// </para>
    /// </summary>
    internal IReadOnlyDictionary<string, PaneRect> PaneOutputRects()
    {
        var rects = new Dictionary<string, PaneRect>(StringComparer.Ordinal);
        foreach (var (paneId, tabs, rect) in RealisedPanes())
        {
            var margin = tabs.Margin;
            var top = margin.Top + tabs.TabHeaderHeight;
            rects[paneId] = new PaneRect(
                rect.X + margin.Left,
                rect.Y + top,
                Math.Max(0, rect.Width - margin.Left - margin.Right),
                Math.Max(0, rect.Height - top - margin.Bottom));
        }

        return rects;
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
                SetStatus(DragPromptMarkup(result.WindowId, result.TargetPaneId, result.Edge), displace: true);
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

        // The input area follows pane focus the same way it follows a tab change: both drafts, the
        // second bar's visibility, and the history cursors are the newly focused window's. The keyboard
        // stays on the command line rather than moving to the pane — typing belongs to the input.
        ChangeWindow();
        RefreshTabTitles();
        UpdateInputChrome();
    }

    /// <summary>Closes the focused pane's active window (Ctrl+W). The main window can't be closed.</summary>
    private void CloseActiveWindow() => CloseWindow(ActiveWindowId());

    /// <summary>
    /// Whether a tab carries the framework's <c>×</c> close button. Only the pane's <em>active</em>
    /// tab does — the design shows one close affordance per pane, not one per tab — and never the
    /// main window, which <see cref="CloseWindow"/> refuses to close anyway.
    /// </summary>
    private static bool CanCloseTab(string windowId, string? activeTab) =>
        windowId != MainWindowId && string.Equals(windowId, activeTab, StringComparison.Ordinal);

    /// <summary>
    /// Closes the tab whose <c>×</c> the user clicked. The framework raises this from its own hit
    /// test on the close cell, which is why the glyph has to be <see cref="TabPage.IsClosable"/>
    /// rather than a <c>✕</c> written into the title: a title is drawn as plain text, so a click
    /// anywhere in it — the <c>✕</c> included — only ever selects the tab.
    /// </summary>
    /// <remarks>
    /// Raised on the driver's <em>input</em> thread: the framework dispatches mouse frames straight
    /// from the driver event (<c>InputCoordinator.HandleMouseEvent</c>) rather than queueing them the
    /// way it queues keys, so ⌃W and this arrive on different threads. Closing rebuilds the whole
    /// pane area, so it is marshalled the same way the drag adapter marshals a drop. Closes the tab
    /// by its own id rather than the focused pane's active one, so the <c>×</c> of an unfocused pane
    /// closes that pane's tab.
    /// </remarks>
    private void OnTabCloseRequested(TabPage tab)
    {
        if (tab.Tag is string windowId)
        {
            OnUiThread(() => CloseWindow(windowId));
        }
    }

    /// <summary>
    /// Drives a primary-button click into a pane's tab strip through the framework's own
    /// <see cref="TabControl.ProcessMouseEvent"/> — the hit test that decides whether a click landed
    /// on a tab, on its <c>×</c> close button, or on neither. It exists for the same reason
    /// <see cref="SimulateKey"/> does: SharpConsoleUI subscribes its mouse dispatch only inside
    /// <c>Run()</c>, which a headless test never enters, so there is otherwise no way to prove that
    /// clicking the <c>×</c> closes a tab.
    /// </summary>
    /// <param name="paneId">The pane whose tab strip receives the click.</param>
    /// <param name="x">Column relative to the pane's own origin — the space the dispatcher would
    /// hand the control. Translating desktop cells into it is
    /// <see cref="PaneSnapshot"/>'s job and is covered by the pane-drag tests.</param>
    /// <param name="y">Row relative to the pane's origin; 0 is the tab header row.</param>
    /// <returns>True when the tab strip consumed the click.</returns>
    internal bool SimulateTabStripClick(string paneId, int x, int y)
    {
        TabControl? tabs;
        lock (_paneTabsLock)
        {
            if (!_paneTabs.TryGetValue(paneId, out tabs))
            {
                return false;
            }
        }

        var point = new System.Drawing.Point(x, y);
        return tabs.ProcessMouseEvent(new MouseEventArgs(
            // A real terminal reports the end of a click as released + clicked together; the framework
            // acts on the clicked bit (see NetConsoleDriver.ParseMouseSequence / SequenceHelper).
            new List<MouseFlags> { MouseFlags.Button1Released, MouseFlags.Button1Clicked },
            point,
            point,
            point));
    }

    /// <summary>
    /// Closes one window: drops its control, draft, scrollback and freeze point, removes it from the
    /// workspace, and rebuilds the pane area. The main window is never closed — it is the session.
    /// </summary>
    private void CloseWindow(string id)
    {
        if (id == MainWindowId || _workspace.FindWindow(id) is null)
        {
            return;
        }

        _panes.Remove(id);
        _paneScrolls.Remove(id);              // and its scroll position: a reopened window starts live
        _paneScrolls.Remove(FrozenRegionKey(id));
        _frozenPanes.Remove(id);
        _drafts.Forget(id);        // both bars: a closed window keeps neither of its two drafts
        _secondBars.Forget(id);    // and a same-id window later starts from F8's default again
        _lines.Remove(id);         // don't resurrect old scrollback if a same-id spawn reopens
        _freezePoints.Remove(id);
        _workspace.CloseWindow(id);
        RebuildPaneArea();
    }

    /// <summary>
    /// Makes a window active in its hosting pane (model + view) and focuses that pane. Returns false
    /// when the window is not placed in any pane, so a caller acting on a user's request can say so
    /// instead of appearing to do nothing.
    /// <para>
    /// A window that belongs to a session makes that session the active one. The command line talks to
    /// whichever character you are looking at, which is the same rule <see cref="SwitchToCharacter"/>
    /// applies — a tab that showed one character's output while ⏎ sent to another would be a worse
    /// version of the bug this came from. Windows with no session of their own (spawns, the web view)
    /// leave the active session alone.
    /// </para>
    /// </summary>
    private bool Activate(string id)
    {
        if (!_workspace.ActivateWindow(id))
        {
            return false;
        }

        if (SessionFor(id) is { } session && !ReferenceEquals(session, _active))
        {
            _active = session;
            UpdateStatus();
            UpdateInputChrome();
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
        return true;
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
                    page.Title = TabTitles.For(window, focusedCharacter);
                    // The × follows the active tab, so keep it in step with every title refresh.
                    page.IsClosable = CanCloseTab(id, activeTab);
                }
            }
        }

        RefreshRail();
        UpdateInputChrome();
    }

    /// <summary>
    /// Tells every connected session, over NAWS, how big its own output area is — the pane its window
    /// lives in, less that pane's tab strip. Not the terminal: this client is built around splits, so
    /// a world sharing the screen with another has perhaps half the columns the window has, and a
    /// server told the window's width wraps to a width that does not exist. Nor only the focused
    /// world: a session nobody is looking at is still receiving text, and a size it was told once and
    /// never again is wrong from the first split onwards.
    /// <para>
    /// A window that is not its pane's visible tab is still reported, at its pane's size. It is the
    /// size that window will be shown at the moment its tab is picked, and the lines arriving into its
    /// buffer meanwhile are wrapped by the server at whatever it was last told — so the choice is
    /// between the size the text will be read at and a stale one. Reporting nothing is the same stale
    /// answer with less code.
    /// </para>
    /// <para>
    /// A session is told only when the answer has changed since the last thing we told it — an
    /// unchanged size is never re-sent, rate limit or no — and no more than once per
    /// <see cref="WindowSizeReportInterval"/>. What the interval holds back is coalesced to the
    /// newest size and delivered by <see cref="FlushPendingSizes"/>; see
    /// <see cref="OfferWindowSize"/> for the shape of the throttle.
    /// A session that disconnects forgets what it was told <em>and</em> when, so a reconnect (which
    /// resets the server's idea of NAWS along with everything else) announces at once rather than
    /// serving out an interval belonging to the previous connection.
    /// </para>
    /// </summary>
    private void ReportPaneSizes()
    {
        if (_sessionWindows.Count == 0)
        {
            return;
        }

        var rects = PaneOutputRects();
        var now = _time.GetUtcNow();

        // Enumerated in place: this runs on the UI thread, which is also the only thread that
        // registers a session (see AttachSession), and nothing in the loop registers one. The
        // dictionary being written to inside it is the other one.
        foreach (var (session, windowId) in _sessionWindows)
        {
            if (!session.IsConnected)
            {
                _sizeReports.Remove(session);
                continue;
            }

            // FindWindow resolves the pane hosting the window whether or not it is the visible tab.
            if (_workspace.Layout.FindWindow(windowId) is not { } pane ||
                !rects.TryGetValue(pane.Id, out var rect) ||
                rect.IsEmpty)
            {
                continue;
            }

            OfferWindowSize(session, (Math.Max(1, rect.Width), Math.Max(1, rect.Height)), now);
        }

        ArmSizeFlush(now);
    }

    /// <summary>
    /// Offers one session a size, and decides whether it goes out now or waits.
    /// <list type="bullet">
    /// <item>The size the server already has is dropped outright, and cancels anything waiting: a
    /// drag that ends where it started owes the server nothing.</item>
    /// <item>A session that has been told nothing, or nothing within
    /// <see cref="WindowSizeReportInterval"/>, is told immediately. So a single discrete change — a
    /// split, a zoom, a closed tab, a connect — carries no added latency at all; the limit only
    /// engages while sizes are arriving faster than that.</item>
    /// <item>Anything else becomes the pending size, <em>replacing</em> whatever was pending rather
    /// than queueing behind it. Only where a drag ended matters, and a server made to re-wrap through
    /// every intermediate width would be doing work the user never sees.</item>
    /// </list>
    /// </summary>
    private void OfferWindowSize(WorldSession session, (int Width, int Height) size, DateTimeOffset now)
    {
        if (!_sizeReports.TryGetValue(session, out var report))
        {
            _sizeReports[session] = report = new SizeReport();
        }

        if (report.Sent == size)
        {
            report.Pending = null;
            return;
        }

        if (report.Sent is null || now - report.SentAt >= WindowSizeReportInterval)
        {
            SendWindowSize(session, report, size, now);
            return;
        }

        report.Pending = size;
    }

    /// <summary>Writes a size to a session and records what was sent, and when.</summary>
    private void SendWindowSize(
        WorldSession session,
        SizeReport report,
        (int Width, int Height) size,
        DateTimeOffset now)
    {
        report.Sent = size;
        report.SentAt = now;
        report.Pending = null;
        _ = AnnounceWindowSizeAsync(session, size.Width, size.Height);
    }

    /// <summary>
    /// Delivers the sizes the interval held back. This is the half of the rate limit that makes it
    /// safe: reports ride the frame, and the frames stop the instant a drag-resize ends, so a limiter
    /// that only ever dropped would lose the one size that matters — the one the drag settled on.
    /// Runs on the UI thread (the timer callback marshals through <see cref="OnUiThread"/>), so it
    /// shares the report bookkeeping with the frame path rather than locking against it.
    /// </summary>
    private void FlushPendingSizes()
    {
        _sizeFlushDueAt = null; // whatever the timer was armed for has now happened
        var now = _time.GetUtcNow();

        foreach (var (session, report) in _sizeReports)
        {
            if (report.Pending is not { } pending)
            {
                continue;
            }

            if (!session.IsConnected)
            {
                report.Pending = null;
                continue;
            }

            // Re-armed rather than sent early: another session's earlier deadline can wake this up.
            if (report.Sent is not null && now - report.SentAt < WindowSizeReportInterval)
            {
                continue;
            }

            SendWindowSize(session, report, pending, now);
        }

        ArmSizeFlush(now);
    }

    /// <summary>
    /// Arms the one-shot trailing flush for the earliest moment a held-back size may go out, or
    /// disarms it when nothing is waiting. A timer is used rather than the render loop precisely
    /// because the render loop is what stops: the last frame of a resize is followed by silence, and
    /// the settled size has to arrive out of that silence. Its callback does nothing but marshal onto
    /// the UI thread, where the main loop drains queued actions every iteration whether or not
    /// anything is dirty.
    /// </summary>
    private void ArmSizeFlush(DateTimeOffset now)
    {
        DateTimeOffset? due = null;
        foreach (var report in _sizeReports.Values)
        {
            if (report.Pending is null)
            {
                continue;
            }

            var ready = report.Sent is null ? now : report.SentAt + WindowSizeReportInterval;
            if (due is null || ready < due)
            {
                due = ready;
            }
        }

        if (due is null)
        {
            _sizeFlushDueAt = null;
            _sizeFlushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        // An earlier wake-up is already booked; it will re-arm for anything still waiting after it.
        if (_sizeFlushDueAt is { } armed && armed <= due)
        {
            return;
        }

        _sizeFlushDueAt = due;
        _sizeFlushTimer ??= _time.CreateTimer(
            _ => OnUiThread(FlushPendingSizes),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _sizeFlushTimer.Change(
            due.Value > now ? due.Value - now : TimeSpan.Zero,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Sends one session's NAWS report. The callers cannot await it — a paint callback, a timer and a
    /// connect continuation — but the failure is not swallowed the way the old fire-and-forget was: a
    /// write that throws says so in that world's own output, and the record of what it was told is
    /// dropped so the next frame tries again rather than believing the server knows a size it was
    /// never sent.
    /// </summary>
    private async Task AnnounceWindowSizeAsync(WorldSession session, int width, int height)
    {
        try
        {
            await session.SetWindowSizeAsync(width, height).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Deliberately broad: this task is nobody's to observe, so anything escaping it would be
            // an unobserved exception at a finalizer instead of a line the user can read.
            OnUiThread(() =>
            {
                _sizeReports.Remove(session);
                session.PrintSystem($"*** Could not report the window size: {ex.Message}");
            });
        }
    }

    /// <summary>
    /// Writes one line of markup onto the status row. The only thing that paints it — and therefore the
    /// one place the input-height veto can be re-derived against what the row actually says.
    /// <para>
    /// The veto counts the rows the header and status line wrap to, and the status line is the piece of
    /// chrome whose length changes at runtime: <c>not connected</c> at construction, a full identity +
    /// keepalive + host cluster once a session is up, and — laid over either — a <see cref="Notice"/>,
    /// which is a whole sentence ("nothing to split — a split moves this pane's other tabs across, and it
    /// has none" is eighty characters). On a narrow terminal any of those is a second row the input bars
    /// were promised. Left uncounted, an 80×6 window handed the input area a row the status line then
    /// took. Re-deriving here rather than in <see cref="SetStatus"/> is what covers the notice case too.
    /// </para>
    /// </summary>
    private void PaintStatus(string markup)
    {
        _statusBar.SetContent(new List<string> { markup });
        SyncInputHeights(_second.Visible);
    }

    /// <summary>
    /// Sets what the status row says <em>at rest</em>: the connection identity, a mode prompt, the
    /// not-connected line. Remembered as well as painted, so a <see cref="Notice"/> laid over the top of
    /// it can be lifted off again without anyone recomputing what was underneath — the dependency that
    /// made a ⌃B refusal permanent on a client with nothing connected.
    /// <para>
    /// While a notice is up this <em>records without painting</em>, so the row keeps the message for its
    /// few seconds and then reveals the latest state. That matters more than it sounds: the resting line
    /// is repainted by every chrome refresh — <see cref="RefreshTabTitles"/> ends in
    /// <see cref="UpdateInputChrome"/> — so a notice raised by a command would otherwise be wiped by the
    /// same dispatch that raised it, before a single frame carried it.
    /// </para>
    /// </summary>
    /// <param name="displace">
    /// True for a line that must be seen <em>now</em> — the move and drag prompts, which are a mode the
    /// user just entered rather than a background repaint.
    /// </param>
    private void SetStatus(string markup, bool displace = false)
    {
        _restingStatus = markup;
        if (displace)
        {
            ForgetNotice();
        }

        if (_notice is null)
        {
            PaintStatus(markup);
        }
    }

    /// <summary>What the status row says with no notice over it. Kept in step by <see cref="SetStatus"/>.</summary>
    private string _restingStatus = "[dim]not connected[/]";

    /// <summary>
    /// How long a <see cref="Notice"/> holds the status row. tmux's <c>display-time</c> is well under a
    /// second, which suits a word or two; ours are sentences ("nothing to split — a split moves this
    /// pane's other tabs across, and it has none" is eighty characters), and eighty characters is about
    /// five seconds of unhurried reading. Six gives that a margin without a refusal outstaying the thing
    /// it refused. Nothing is lost to it either way: every notice is echoed into the output window.
    /// </summary>
    internal static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(6);

    /// <summary>The transient message currently laid over the resting row, or null when there is none.</summary>
    private string? _notice;
    private ITimer? _noticeTimer;

    /// <summary>
    /// Says something on the status row that is <em>news</em> rather than state: a refused pane command,
    /// a command surface entry this build cannot run, a connection that would not open. tmux's
    /// <c>display-message</c> model — it holds the row for <see cref="NoticeDuration"/> and then the
    /// resting line comes back on its own.
    /// <para>
    /// Two rules, both learned from bugs of exactly this shape. It must be sayable with <em>nothing
    /// connected</em> — which is why it is here and not <c>WorldSession.PrintSystem</c>: a message
    /// reported through the active session says nothing at all in the one state a user meets these
    /// messages in. And it must retire <em>itself</em>: the refusals used to wait for the next
    /// <see cref="UpdateStatus"/> to displace them, and that only ever runs off a session event, so on a
    /// client with no connection they stayed on the row for the rest of the run. The timer runs on the
    /// injected clock, so a test advances it instead of sleeping for it — and it is a timer rather than
    /// anything hung off a frame for the reason the NAWS flush is: repaints stop, clocks don't.
    /// </para>
    /// <para>
    /// Every notice is also recorded in <see cref="_messages"/> — a capped, in-memory client message log
    /// read by ⌃P ▸ <c>Show client messages</c> — because a message that dismisses itself needs
    /// somewhere it can be found again. It is <em>not</em> the output window: that is the server's
    /// stream, and <c>WorldSession.PrintSystem</c> writes what it prints into the character's log sink,
    /// so a UI refusal about pane splits would land in a transcript someone keeps for roleplay or
    /// evidence. Client chrome gets its own surface. Recording happens here, before anything touches a
    /// session or a window, so a notice raised with nothing connected and nothing focused is still kept.
    /// </para>
    /// </summary>
    /// <param name="text">The message, as plain text — the log keeps this, and the row's markup is built from it.</param>
    /// <param name="severity">How loud it is; the viewer colours and labels rows by this.</param>
    /// <param name="key">An optional leading chip naming the surface that refused (<c>⌃B</c>, <c>⌃P</c>).</param>
    private void Notice(string text, MessageSeverity severity = MessageSeverity.Warning, string? key = null)
    {
        // Through the logger, not straight into the buffer: the client's own messages and the telnet
        // stack's diagnostics then share one ordered history (and one rolling file), which is the point
        // of there being a pipeline at all.
        _diagnostics.Logger.Log(
            severity switch
            {
                MessageSeverity.Error => LogLevel.Error,
                MessageSeverity.Warning => LogLevel.Warning,
                _ => LogLevel.Information,
            },
            "{Notice:l}", // :l renders the string literally; the default quotes it, and a quoted sentence reads as data
            key is null ? text : $"{key} {text}");

        var body = severity == MessageSeverity.Error
            ? $"[{ScreenPalette.Warn}]{Escape(text)}[/]"
            : $"[dim]{Escape(text)}[/]";
        var markup = key is null ? body : $"[#e5c07b]{Escape(key)}[/] {body}";

        _notice = markup;
        PaintStatus(markup);
        _noticeTimer ??= _time.CreateTimer(
            _ => OnUiThread(ClearNotice), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _noticeTimer.Change(NoticeDuration, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Everything <see cref="Notice"/> has said this run, capped. Internal so a headless test can assert
    /// that a message which dismissed itself was still recorded.
    /// </summary>
    internal ClientMessageLog Messages => _messages;

    /// <summary>Lifts the notice off the row, putting the resting line back under it.</summary>
    private void ClearNotice()
    {
        if (_notice is null)
        {
            return;
        }

        ForgetNotice();
        PaintStatus(_restingStatus);
    }

    /// <summary>Forgets the notice and disarms its timer, without painting — for callers about to paint.</summary>
    private void ForgetNotice()
    {
        _notice = null;
        _noticeTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The resting status row of a client with nothing connected.</summary>
    private string NotConnectedMarkup()
    {
        var scrollback = ScrollbackStatus();
        var suffix = scrollback.Length == 0 ? string.Empty : $"   {scrollback}";
        return $"[dim]not connected · Graphics {Escape(_capabilities.Protocol.ToString())} · ⌃P palette · ⌃Q quit[/]{suffix}";
    }

    private void UpdateStatus()
    {
        var session = _active;
        if (session is null)
        {
            // Clear the identity too: leaving a stale one behind means the next RefreshStatusBar
            // repaints a connection that is no longer there.
            _statusIdentity = null;
            SetStatus(NotConnectedMarkup());
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

        // The ⌃B prefix indicator shows only while armed (design: "⌃B — awaiting | - z o x b m < > ← →").
        // The reorder pair lists both spellings: the keymap is the literal < and >, but bare angle
        // brackets read as a direction, and the arrows are what a reader reaches for — so they work too
        // (see PrefixKey) and the strip says so rather than leaving the guess to be discovered.
        if (_prefixArmed)
        {
            return $"{leftBar}  [#e5c07b]⌃B — awaiting[/]  [dim]| - z o x b m i < > ← →[/]";
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

    /// <summary>
    /// The header width to lay out against: the live window width once there is a window, and the
    /// terminal's own width until then. The distinction is not cosmetic. The first header markup is
    /// built before the window exists, and the same number right-aligns that header's status cluster,
    /// pins each command line's band to the full row, and counts the chrome rows the input-height veto
    /// has to leave alone — so a guess here lays the whole first frame out for a terminal nobody has.
    /// The app is handed a driver that knows the answer, which is why it is asked instead.
    /// </summary>
    private int HeaderWidth() => _window is { Width: > 0 } ? _window.Width : DriverSize().Width;

    /// <summary>The window's height in rows, or the terminal's own before the window exists.</summary>
    private int HeaderHeight() => _window is { Height: > 0 } ? _window.Height : DriverSize().Height;

    /// <summary>
    /// The terminal's size as the console driver reports it — the size to lay out against before the
    /// window exists. Only a driver with no console to measure (a redirected stdout on Windows) fails
    /// to answer, and the old literals survive as that last resort rather than as the first guess.
    /// </summary>
    private (int Width, int Height) DriverSize()
    {
        try
        {
            var size = _system.ConsoleDriver.ScreenSize;
            return (size.Width > 0 ? size.Width : 160, size.Height > 0 ? size.Height : 48);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            return (160, 48);
        }
    }

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

        // Leftmost of the right cluster while it is there at all: a pane that is not showing its newest
        // line is the most important thing the row can say, because nothing else on screen says it.
        if (ScrollbackStatus() is { Length: > 0 } scrollback)
        {
            right.Add(scrollback);
        }

        // Keepalive latency sparkline + last ack (compact), per the design status bar.
        var spark = Meters.Sparkline(new[] { 38, 44, 41, 47, 40, 43 });
        var ackMs = ActiveWorld() is { } w && w.World.KeepaliveSeconds > 0 ? 41 : 0;
        right.Add($"[dim]{Glyphs.Heartbeat}[/] [#98c379]{spark}[/] [dim]{ackMs}ms[/]");

        var encoding = ActiveWorld() is { } enc ? enc.World.Encoding : "UTF-8";
        right.Add($"[dim]{Escape($"{host}:{port}")}  {Escape(encoding)}[/]");

        // The character count lives at the bottom now (the input gutter is gone); while recalling
        // history it becomes the "back to draft" hint instead. Both read the armed bar, so a count that
        // moves is always the line ⏎ would send.
        right.Add(HistoryFor(BarKind(ActiveBar())).IsRecalling
            ? $"[{AccentHex(AccentPalette[0])}]history[/] [dim]· ↓ back to draft[/]"
            : $"[dim]{ActiveBar().Text.Length} chars[/]");

        right.Add("[dim]⌃P palette[/]");
        var rightBar = string.Join("   ", right);

        // Right-align the cluster to the far edge; identity stays pinned left.
        var gap = Math.Max(3, HeaderWidth() - MarkupWidth(left) - MarkupWidth(rightBar));
        return $"{left}{new string(' ', gap)}{rightBar}";
    }

    /// <summary>
    /// Marshals an action onto the UI thread (session events and web fetches fire on background
    /// threads). Shares <see cref="OnUiThread"/>'s headless handling: a snapshot or test run has no
    /// main loop to drain the queue, so an action posted there would otherwise be dropped.
    /// </summary>
    private void OnUi(Action action) => OnUiThread(action);

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
        _sizeFlushTimer?.Dispose(); // nothing left to tell a server we are shutting down to
        _noticeTimer?.Dispose();    // and no row left to put a notice back on
        _webImageCts?.Cancel();
        _webImageCts?.Dispose();
        _imageLoader.Dispose();
        _fetcher.Dispose();
        await _sessions.DisposeAsync().ConfigureAwait(false);
        if (_ownsDiagnostics)
        {
            _diagnostics.Dispose(); // only the pipeline this app built; a supplied one outlives it
        }
    }
}
