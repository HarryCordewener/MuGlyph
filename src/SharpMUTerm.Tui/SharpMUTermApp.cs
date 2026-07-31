using System.Text;
using Microsoft.Extensions.Logging;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Diagnostics;
using SharpMUTerm.Core.Input;
using SharpMUTerm.Core.Logging;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Telnet;
using SharpMUTerm.Core.Telnet.Mssp;
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
    internal const string MainWindowId = "main";
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
    private readonly Dictionary<string, List<PaneLine>> _lines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _freezePoints = new(StringComparer.Ordinal);

    /// <summary>
    /// Connections the <em>demo scene</em> declares live, by session key. It opens no sockets at all, so
    /// there is nothing for <see cref="ConnectedCharacters"/> to ask and this is the answer it falls back
    /// to. It is not a cache of live state: it used to be one, maintained for
    /// <see cref="_active"/> alone inside <see cref="UpdateStatus"/>, so a background world connecting or
    /// dropping never reached the rail's dots and never reached the header's count either.
    /// </summary>
    private readonly HashSet<string> _demoConnectedKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// The encoding the <em>demo scene</em> declares its connection settled on. It exists for the same
    /// reason <see cref="_demoConnectedKeys"/> does: the status row's encoding cell now reports what a
    /// live session is decoding with, and the demo opens no sockets, so there is no session to ask —
    /// but the demo <em>does</em> build the connected row (see <see cref="LoadDemoScene"/>), so without
    /// this the cell would silently vanish from every snapshot. Held in the type the live path produces,
    /// and <c>StatusEncodingTests</c> pins that what it declares is a state the live writer can reach.
    /// </summary>
    private SessionEncoding? _demoEncoding;

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
    /// Each realised pane's surface grid, by pane id — the control whose background says whether the pane
    /// holds the focus. Kept so <see cref="RefreshPaneFocus"/> can repaint the indicator without
    /// rebuilding the pane area. Rebuilt with it (see <see cref="BuildWorkspaceRow"/>), and touched only
    /// on the UI thread: unlike <see cref="_paneTabs"/> no mouse frame reads it.
    /// </summary>
    private readonly Dictionary<string, SharpConsoleUI.Controls.GridControl> _paneSurfaces =
        new(StringComparer.Ordinal);

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

    /// <summary>
    /// The settings overlay, so a headless test can drive a key into an open screen and ask what
    /// happened. It is the same seam <c>SimulateKey</c> exists for and for the same reason: the
    /// framework only pumps input inside <c>Run()</c>, which no test enters.
    /// </summary>
    internal SettingsOverlay Settings => _settings;

    /// <summary>The ⌃P ▸ <c>Show client messages</c> viewer over the diagnostics log.</summary>
    private readonly MessageLogOverlay _messageLog;

    /// <summary>The ⌃Q confirmation. Nothing ends the loop except a yes it collected.</summary>
    private readonly QuitOverlay _quit;

    /// <summary>
    /// The ⌃B which-key panel: the keymap explained, shown a short moment after the prefix is armed and
    /// only when nothing has been pressed by then.
    /// </summary>
    private readonly PrefixOverlay _prefixPanel;

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
    private string? _demoActiveKey;
    private readonly bool _headless;
    private bool _railCollapsed;
    private bool _prefixArmed;

    /// <summary>
    /// The which-key timer: armed by <see cref="ArmPrefix"/>, and if it fires before a key arrives it
    /// opens <see cref="_prefixPanel"/>. It is a timer rather than anything hung off a frame for the
    /// reason the NAWS flush and the notice are: an armed prefix changes nothing, so repaints stop —
    /// clocks don't.
    /// </summary>
    private ITimer? _prefixTimer;

    /// <summary>
    /// How long the terse strip stands alone before the panel explains it. Short enough that someone who
    /// does not know the keymap is told without having asked; long enough that someone who does never
    /// sees the panel at all, because their second keystroke has already landed. It runs on the injected
    /// clock, so a test advances it rather than sleeping for it.
    /// </summary>
    private static readonly TimeSpan PrefixPanelDelay = TimeSpan.FromMilliseconds(400);

    private bool _moveMode;
    private string? _moveWindowId;
    private string? _moveTargetPaneId;
    private Edge? _moveEdge;
    /// <summary>
    /// The digit that targets each pane in move mode, which is the pane's own ordinal — the number
    /// <see cref="PaneLabel"/> spells and ⌥N jumps to. It was a separate a–j alphabet, so the badge on
    /// a pane and the prompt beside it named the same pane two ways (<c>MOVE Corvid → split pane 2
    /// left</c> under a badge reading <c>B</c>). Only panes 1–9 get an entry: there is no tenth digit,
    /// and a badge whose key does not exist is worse than no badge. A tenth pane is still a drop target
    /// for the mouse.
    /// </summary>
    private readonly Dictionary<string, int> _moveOrdinals = new(StringComparer.Ordinal);

    /// <summary>
    /// The chords the app claims globally, by the action each runs. It is the same delegate
    /// <c>RegisterGlobalShortcut</c> was handed, kept so <see cref="SimulateKey"/> can run the shortcuts
    /// in the order the framework does — a headless test never enters <c>Run()</c>, where that ordering
    /// otherwise lives.
    /// </summary>
    private readonly Dictionary<(ConsoleModifiers Modifiers, ConsoleKey Key), Func<bool>> _shortcuts = new();

    /// <summary>Assembles pane drag-and-drop out of the driver's raw mouse frames (see PaneDragTracker).</summary>
    private readonly PaneDragTracker _paneDrag = new();

    /// <summary>How the configuration is written back, or null for an app that owns no file.</summary>
    private readonly Action<AppConfiguration>? _save;

    /// <summary>
    /// The directory session transcripts are written under, or null for an app that owns no log
    /// directory — which is the default, and is what every test and every snapshot gets. See the
    /// <c>logRoot</c> constructor parameter for why it is handed in rather than resolved here.
    /// </summary>
    private readonly string? _logRoot;

    /// <summary>
    /// Where each pane's recent content is kept so a restart refills it, or null for an app that owns
    /// no restore log — which is the default, and is what every test and every snapshot gets. Third of
    /// the same family as <see cref="_save"/> and <see cref="_logRoot"/>, for the same reason.
    /// </summary>
    private readonly RestoreLog? _restore;

    /// <summary>
    /// Every server's last MSSP report, which is what the F5 ▸ <c>i</c> INFO screen reads. Never null,
    /// and that is the difference from the three above rather than an inconsistency with them: a cache
    /// built with no path is memory-only <em>by construction</em> — it reads nothing and writes nothing —
    /// so the guarantee those three get from a null check, this one gets from the object it is handed.
    /// A structural guarantee beats a check at every use site, and there is exactly one use site here
    /// that a check could be forgotten at anyway. Only <c>Program</c> hands one a path.
    /// </summary>
    private readonly MsspCache _mssp;

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
    /// <param name="save">
    /// How to write the configuration back, or null for an app that must never write one — which is the
    /// default, and is what every test and every snapshot gets.
    /// <para>
    /// It is a parameter rather than a hardcoded <c>ConfigurationStore.Save(DefaultPath, …)</c> because
    /// the settings screens now persist each change as it is committed. That makes "which file does this
    /// app own" a question with real consequences: a <c>--demo-config</c> snapshot that drove a key into
    /// a field would otherwise have written the demo worlds over the developer's own configuration, and
    /// the suites drive those keys constantly. Only the entry point knows it loaded from disk, so only
    /// the entry point supplies the way back to it.
    /// </para>
    /// </param>
    /// <param name="logRoot">
    /// Where this app writes session transcripts, or null for an app that owns no log directory — which
    /// is the default, and is what every test and every snapshot gets. It is the exact counterpart of
    /// <paramref name="save"/>, and it exists for the same reason.
    /// <para>
    /// It used to be resolved here, unconditionally, from the directory of
    /// <see cref="ConfigurationStore.DefaultPath"/> — so <em>any</em> app that opened a session for a
    /// character whose <see cref="LoggingSettings.Format"/> was not <see cref="LogFormat.None"/> created a
    /// real file under the developer's own <c>~/.config/SharpMUTerm/logs</c>. The demo scene's
    /// <c>Aetherfall.Corvid</c> is exactly such a character, so the suites did it several times a run and
    /// had left 277 empty transcripts beside genuine ones. Only the entry point knows it is the live
    /// client, so only the entry point supplies the directory.
    /// </para>
    /// <para>
    /// Null means <em>no logging at all</em>, not "no default location": a character carrying an explicit
    /// <see cref="LoggingSettings.Directory"/> is refused too. An app that owns no log directory owns none,
    /// and a fixture naming an absolute path is still a test reaching outside itself.
    /// </para>
    /// </param>
    /// <param name="restore">
    /// The <see cref="RestoreLog"/> this app reads its panes' previous content out of and writes their
    /// new content into, or null for an app that owns no restore log — which is the default, and is
    /// what every test and every snapshot gets. The third member of the <paramref name="save"/> /
    /// <paramref name="logRoot"/> family, handed in for exactly their reason: this one writes a file
    /// per window under the user's configuration directory, and an app that is not the live entry point
    /// has no business creating those. It is also what keeps the demo scene honest — a
    /// <c>--demo-config</c> snapshot would otherwise restore <em>your</em> panes into the demo's.
    /// </param>
    /// <param name="mssp">
    /// Where each server's MSSP report is kept between launches, or null for a memory-only cache — which
    /// is the default, and is what every test and every snapshot gets. It differs from the three
    /// parameters above in one way that matters: the *field* is never null, because a memory-only
    /// <see cref="MsspCache"/> is a working cache that happens to own no file. The INFO screen therefore
    /// needs no "is there a cache" branch, and a snapshot can seed a report through the same writer a
    /// live session uses without any of it reaching disk.
    /// </param>
    public SharpMUTermApp(
        AppConfiguration config,
        TerminalCapabilities capabilities,
        IConsoleDriver? driver = null,
        TimeProvider? time = null,
        ClientDiagnostics? diagnostics = null,
        Action<AppConfiguration>? save = null,
        string? logRoot = null,
        RestoreLog? restore = null,
        MsspCache? mssp = null)
    {
        _config = config;
        _save = save;
        _logRoot = string.IsNullOrWhiteSpace(logRoot) ? null : logRoot;
        _restore = restore;
        _mssp = mssp ?? new MsspCache();
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
        _header.LinkClicked += (_, e) => OnChromeLinkClicked(e.Url);
        _header.BackgroundColor = ToColor(_theme.StatusBackground); // the menu bar is a distinct chrome band
        // Keep the clickable brand button on-brand (violet) instead of the driver's default link highlight.
        var brand = AccentPalette[2];
        _header.FocusedLinkBackgroundColor = ToColor(new Rgb(brand.R, brand.G, brand.B));
        _header.FocusedLinkForegroundColor = ToColor(_theme.Resolve(TerminalColor.Default, isBackground: true));

        var main = new MarkupControl(new List<string>());
        main.LinkClicked += (_, e) => OnLinkClicked(MainWindowId, e.Url);
        _panes[MainWindowId] = main;

        // The connection rail (worlds → characters → windows) sits left of the pane area, joined by
        // a splitter. RailModel/RailRenderer keep the projection + markup tested; this just hosts it.
        _rail = new MarkupControl(new List<string>());
        _rail.LinkClicked += (_, e) => DispatchRailTarget(e.Url);

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
            // Neither movable nor resizable, and this is not cosmetic. Both default to true, and the
            // framework treats an unhandled chord on a movable window as window management: any Ctrl+key
            // we do not claim falls into InputCoordinator.HandleMoveInput (Input/InputCoordinator.cs:837,
            // reached at :165), where `case ConsoleKey.X` calls CloseWindow — and this is the only window
            // there is, so ⌃X blanked the client. Shift+arrows reach HandleResizeInput the same way.
            // A window that fills the desktop has nowhere to move to and no size to be but this one, so
            // the honest fix is to decline the whole category rather than to claim every chord it eats.
            // Same shape as ExitKey: the framework's built-in bindings win anything we leave unhandled.
            .Movable(false)
            .Resizable(false)
            .WithColors(fg, bg)
            .AddControl(_header)
            .AddControl(_workspaceRow)
            .AddControl(_input)
            .AddControl(_second)
            .AddControl(_statusBar)
            .Build();

        _palette = new CommandPalette(_system, BuildCatalog, () => _active?.SessionKey, id => DispatchCommand(id));
        _messageLog = new MessageLogOverlay(_system, _diagnostics);
        _settings = new SettingsOverlay(_system);
        _quit = new QuitOverlay(_system, QuitFactsNow, Quit);

        // The ⌃B which-key panel. Its facts are read at the moment it opens, so it explains the workspace
        // the user is actually looking at, and the key that ends it goes back through the very consumer a
        // key pressed before it appeared goes through — see ArmPrefix.
        _prefixPanel = new PrefixOverlay(_system, PrefixFactsNow, ConsumePrefixKey);

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

        // Before the window is shown, so the first frame already has the panes' previous content in
        // them — a pane that filled itself in a frame or two later would read as a glitch, and this is
        // also the last moment at which no live line has arrived to be restored *above*.
        RestorePreviousSession();
        _system.AddWindow(_window);
    }

    /// <summary>Captures the current workspace (panes/windows/focus) so it can be persisted and resumed.</summary>
    public WorkspaceState CaptureSession() => WorkspaceState.Capture(_workspace);

    /// <summary>
    /// Runs the UI loop, opening <paramref name="startup"/> once the window is shown. An empty list is a
    /// supported launch and not an error — see <see cref="StartAsync"/>.
    /// </summary>
    public int Run(IReadOnlyList<StartupConnection> startup)
    {
        ScheduleStartup(startup ?? Array.Empty<StartupConnection>());
        return _system.Run();
    }

    /// <summary>
    /// Schedules the startup connections onto the UI thread. Internal because it is the half of
    /// <see cref="Run"/> a headless test can run — <c>_system.Run()</c> is the blocking main loop and no
    /// test enters it, so a bug in this scheduling is invisible to every test that stops short of it.
    /// One did live here.
    /// <para>
    /// <b>It is deliberately not <c>Window.OnShown</c>.</b> That event is raised by <c>AddWindow</c>
    /// (<c>WindowStateService</c> → <c>Window.WindowIsAdded</c>), and <c>AddWindow</c> is the last line of
    /// this app's constructor — so by the time <see cref="Run"/> was reached it had already fired, and it
    /// is never raised again. <see cref="StartAsync"/> therefore never ran at all: nothing threw and
    /// nothing logged, the client came up looking entirely normal, and it simply never dialled.
    /// <c>sharpmuterm host port</c> connected to nothing, and every character marked <em>at start</em> on
    /// F5 was ignored, which left that whole setting dead on arrival.
    /// </para>
    /// <para>
    /// <see cref="OnUiThread"/> is the right hook because it cannot be missed by ordering: before
    /// <c>_system.Run()</c> the system has captured no UI thread (<c>_uiThreadId</c> is −1), so this
    /// queues, and <c>DrainUIActionQueue</c> runs it at the top of the loop's first pass — driver started,
    /// window added, which is all <c>OnShown</c> was reaching for. Headless it runs inline, which is what
    /// makes it checkable.
    /// </para>
    /// </summary>
    internal void ScheduleStartup(IReadOnlyList<StartupConnection> startup) =>
        OnUiThread(() => LastCommand = StartAsync(startup));

    /// <summary>
    /// Subscribes to the main window's <c>OnShown</c>. Exists for one test — the one that pins that the
    /// event has <em>already</em> fired by the time anyone outside the constructor can reach it, which is
    /// why <see cref="ScheduleStartup"/> does not use it.
    /// </summary>
    internal void OnMainWindowShown(EventHandler handler) => _window.OnShown += handler;

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
            // Set before the scene loads, so this frame is the column *on* rather than a live toggle.
            // The `timestamps-toggled` view below is the other half of the pair and the one that
            // exercises the reported bug: same scene, column turned on after the output is already there.
            ShowTimestamps = true;
        }

        LoadDemoScene();

        // The reported bug, as a frame: the scene is already on screen with the column off, and *then*
        // the real ⌃P entry is dispatched. Under the old append-time gutter this frame was identical to
        // the default workspace — which is exactly what "the button seems to not do anything" was.
        // It splits first so one frame carries a *session* window beside a *spawn* window. That pair is
        // the reason the gutter moved out of the line's markup: a session window could in principle be
        // rebuilt from its WorldSession.Scrollback, and a spawn window could not — its history is markup
        // in the line buffer and nothing else. Both have to gain the column, or the same command visibly
        // does different things in different tabs.
        if (string.Equals(view, "timestamps-toggled", StringComparison.OrdinalIgnoreCase))
        {
            PaneCommands.Apply(_workspace.Layout, PaneCommand.SplitRight);
            RebuildPaneArea();
            DispatchCommand("term:timestamps-on");
        }

        // Bring the Chat spawn window to the front, so one frame shows a *routed* window as the active tab
        // with the character's own window sitting behind it in the same strip. It used to exist for the dim
        // "⇱ capture …" header a spawn pane drew over its output; that header is gone (the user asked for
        // it to go) and the view is not, because two other suites drive it — a spawn tab being closable
        // (PaneTabCloseTests) and the timestamp gutter reaching a window whose history is markup and
        // nothing else (TimestampGutterTests) are both claims about a spawn window in front.
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

        // The focus indication, in the one geometry that shows all of it at once: two panes and two
        // command lines, so a frame carries a focused pane beside an unfocused one *and* an armed bar
        // above an idle one. `focus` leaves the focus where a split leaves it (the left pane, primary bar
        // armed); `focus-moved` then drives the real ⌃→ and ⌃↓ handlers, so the pair of frames is the
        // before and after of the keys rather than two hand-posed states.
        if (view is not null && view.StartsWith("focus", StringComparison.OrdinalIgnoreCase))
        {
            PaneCommands.Apply(_workspace.Layout, PaneCommand.SplitRight);
            RebuildPaneArea();
            ToggleSecondBar();
            ArmBar(_input);

            if (string.Equals(view, "focus-moved", StringComparison.OrdinalIgnoreCase))
            {
                RenderFrame(); // the panes need real arranged bounds before a directional move can read them
                SimulateKey(Stroke('\0', ConsoleKey.RightArrow, ctrl: true));

                // That frame left the driver's front buffer populated, so the closing render would emit
                // only the cells that changed — see the `drag` view, which hits the same thing.
                ReArmWholeFrame();
            }
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
                    AppendWindowLine(MainWindowId, _formatter.ToMarkup(line), StampNow());
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

        // A rail row that outgrows the sidebar *after* the pane area was built — the shape behind the
        // reported two-line rail row. The report's own route was the startup retitle (the main window is
        // created as "Main" and a session renames it), which a resumed demo cannot show because its titles
        // are known before the first frame; this is the other route to the same state and the one a world can
        // reach on its own: a second page loaded into the web view, whose title is whatever the page says.
        // The row is elided to the column rather than wrapped, and the sidebar is as wide as its widest row.
        if (string.Equals(view, "rail-long", StringComparison.OrdinalIgnoreCase))
        {
            ShowDemoWebPage();
            ShowDemoWebPage("A Survey of the Coast Road and the Northern Watch, with Appendices");
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

        // The ⌃B which-key panel over the demo workspace: what the arming timer opens when no key has
        // arrived by the time it fires. `prefix` is the terse strip on its own — the first thing an armed
        // prefix shows — and this is what follows it, so the two views are the whole of the display. The
        // demo pane holds two tabs and one pane, so the frame carries live rows *and* dimmed ones with
        // their reasons, which is the half of the panel that explains the workspace rather than the keymap.
        if (string.Equals(view, "prefix-panel", StringComparison.OrdinalIgnoreCase))
        {
            _prefixArmed = true;
            _header.SetContent(new List<string> { HeaderMarkup() });
            _prefixPanel.Open();
        }

        // The client message viewer, over a demo scene that has said a few things. It is the only way to
        // look at the surface without a terminal, and the messages are seeded here rather than faked in
        // the view: what the snapshot shows is what Notice actually records.
        if (string.Equals(view, "messages", StringComparison.OrdinalIgnoreCase))
        {
            RefusePrefix(PrefixPanel.NoSplitRefusal);
            RefuseCommand("nothing to reconnect — pick a character first (⌃P ▸ Switch to …)");
            Notice("switched to Corvid · offline — Alt+R (⌃P ▸ Reconnect) connects it", MessageSeverity.Info, "⌃P");
            Notice("could not connect to aetherfall.mux:4201 — no route to host", MessageSeverity.Error, "⌃P");
            _messageLog.Toggle();
        }

        // The ⌃Q confirmation, over a workspace that has something to lose: a second world marked
        // connected (the demo scene's own way of saying so, and what the header's "n/m characters" reads),
        // a line typed into the command line through the bar's real change notification, and then the
        // registered shortcut itself — so the frame shows what pressing ⌃Q does, not an impression of it.
        if (string.Equals(view, "quit", StringComparison.OrdinalIgnoreCase))
        {
            if (_config.Worlds.ElementAtOrDefault(1) is { Characters.Count: > 0 } second)
            {
                _demoConnectedKeys.Add($"{second.Name}.{second.Characters[0].Name}");
                _header.SetContent(new List<string> { HeaderMarkup() }); // the band counts them: 2/3
            }

            _input.SetAndNotify("say back in a moment — kettle's on");
            _shortcuts[(ConsoleModifiers.Control, ConsoleKey.Q)]();
        }

        // The same confirmation with the shape that hid two counting bugs: two connections on *one* world.
        // Every other view has at most one character connected per world, so a header dividing connections
        // by worlds and a prompt reducing connections to distinct world names both looked right — the first
        // read 1/2 where the truth was 2/3, the second said "1 world connected" over two live characters.
        // This is the one frame where both halves of the fraction and both ends of the sentence are visible
        // and wrong if either reverts.
        if (string.Equals(view, "connections", StringComparison.OrdinalIgnoreCase))
        {
            if (_config.Worlds.FirstOrDefault() is { Characters.Count: > 1 } first)
            {
                foreach (var character in first.Characters)
                {
                    _demoConnectedKeys.Add($"{first.Name}.{character.Name}");
                }

                // Both surfaces, explicitly. The `quit` view gets its rail repaint by accident — its
                // SetAndNotify runs RefreshTabTitles, which refreshes the rail on the way past — and a
                // frame where the header counts a connection the rail draws as offline is unreadable.
                RefreshHeader();
                RefreshRail();
            }

            _shortcuts[(ConsoleModifiers.Control, ConsoleKey.Q)]();
        }

        // The deletion review, reached the only way a user can reach it: open F5, take the selected world
        // out with Delete, then leave with Esc. Everything in the frame — the wording, the count of
        // characters going with the world, which button ⏎ is standing on — is what the real keys produce.
        if (string.Equals(view, "deletions", StringComparison.OrdinalIgnoreCase))
        {
            _settings.OpenForSnapshot(ConsoleKey.F5, WorldsScreen());
            _settings.SimulateKey(Stroke('\0', ConsoleKey.Delete));
            _settings.SimulateKey(Stroke('\0', ConsoleKey.Escape));
        }

        // The MSSP report, reached the only way a user can reach it: open F5 and press `i` on the
        // selected world. Nothing about the screen is faked here — the key runs the real button, which
        // opens the real binding over the real overlay.
        //
        // Three views because the screen has three states and two of them are empty ones that must not
        // look alike. `mssp` seeds a report through the live writer; `mssp-none` only records a
        // connection, which is the "connected and this server publishes no MSSP" arm; `mssp-never`
        // seeds nothing at all, which is the arm a world you have not dialled is in.
        if (view is not null && view.StartsWith(MsspScreenRenderer.View, StringComparison.OrdinalIgnoreCase))
        {
            var world = _config.Worlds.Count > 0 ? _config.Worlds[0] : null;
            if (world is not null && !view.EndsWith("-never", StringComparison.OrdinalIgnoreCase))
            {
                _mssp.RecordConnection(world.Host, world.Port, _time.GetUtcNow());
                if (!view.EndsWith("-none", StringComparison.OrdinalIgnoreCase))
                {
                    CaptureMssp(world, DemoScene.MsspReport());
                }
            }

            _settings.OpenForSnapshot(ConsoleKey.F5, WorldsScreen());
            _settings.SimulateKey(Stroke('i', ConsoleKey.I));
            SyncInputWidth();
            return RenderFrame();
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
                AppendWindowLine(windowId, _formatter.ToMarkup(line), StampNow());
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
        AppendWindowLine(MainWindowId, _formatter.ToMarkup(exits), StampNow());

        // A trigger-highlighted line: carries a left-rule colour, so it gets the 2-col rule treatment.
        var highlighted = new StyledLine(
            new[] { new StyledSpan("[public] Rivane: to the crypt, then!", TextStyle.Default) },
            TerminalColor.FromRgb(0x00, 0xf5, 0xb7));
        AppendWindowLine(MainWindowId, _formatter.ToMarkup(highlighted), StampNow());

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
                AppendWindowLine(chatId, _formatter.ToMarkup(line), StampNow());
            }

            _workspace.NoteActivity(chatId); // each line accrues unread while Chat is in the background
        }

        _statusIdentity = ("Corvid", "aetherfall.mux", 4201, "connected");

        // The ordinary case, and so the one the snapshots should show: Aetherfall is on `auto` and its
        // server agreed to UTF-8, which the row draws unqualified. A world that pinned an encoding
        // would read "utf-8 forced" and a server that never negotiated "utf-8 assumed".
        _demoEncoding = new SessionEncoding(Encoding.UTF8, EncodingSource.Negotiated);
        _statusBar.SetContent(new List<string> { StatusBarMarkup("Corvid", "connected") });
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

    /// <summary>
    /// Refills every pane with what it was showing when the client last ran, and marks where that ends.
    /// <para>
    /// <b>It is the content half of <see cref="ResumeOrNew"/>.</b> That rebuilds the workspace — which
    /// windows exist, which pane each sits in, which was focused — from
    /// <see cref="AppConfiguration.LastSession"/>; this refills those windows from the restore log,
    /// keyed by the same window ids. Both halves are needed and neither implies the other: a client that
    /// resumed its layout and showed every pane empty is what the whole feature is about.
    /// </para>
    /// <para>
    /// <b>The two halves are joined loosely, on purpose.</b> A window in the log that the saved
    /// workspace no longer holds — a spawn window whose pane was closed — is still buffered, because
    /// <see cref="_lines"/> is keyed by window and not by anything the workspace has to agree with; if
    /// that channel speaks again its pane reopens with its history already in it
    /// (<see cref="PaneContentFor"/>). A window in the saved workspace with no log simply starts empty.
    /// Neither is an error and neither throws, which is the property that matters most here: this runs
    /// before the first frame, and a client that will not start because of a cache of old chat lines
    /// would be a worse client than one that starts with an empty pane.
    /// </para>
    /// <para>
    /// <b>Nothing restored is written back.</b> The log is fed from <see cref="OnLine"/> and
    /// <see cref="OnSpawnLine"/> — the two places a <em>world's</em> output reaches a pane — and this
    /// replay goes straight to <see cref="AppendWindowLine"/>, so a restart cannot echo its own history
    /// into the log and double every pane.
    /// </para>
    /// </summary>
    private void RestorePreviousSession()
    {
        if (_restore is null || !_config.RestoreLog.Enabled)
        {
            return;
        }

        var restoredWindows = 0;
        var restoredLines = 0;
        foreach (var window in _restore.Read())
        {
            // A character who opted out gets their content dropped rather than merely un-drawn: an
            // opt-out that left the last session's text lying in the config directory would be
            // answering a different question from the one it was asked.
            if (!RestoreLogWanted(_workspace.FindWindow(window.WindowId)?.SessionKey))
            {
                _restore.Forget(window.WindowId);
                continue;
            }

            if (window.Lines.Count == 0)
            {
                continue;
            }

            foreach (var line in window.Lines)
            {
                AppendWindowLine(window.WindowId, _formatter.ToMarkup(line.Line), line.Stamp);
            }

            // The boundary marker carries no stamp: it did not arrive, it was drawn, and a timestamp
            // gutter beside it would be claiming a time for a row the game never sent.
            AppendWindowLine(
                window.WindowId,
                RestoreBarRenderer.Bar(window.Lines.Count, window.LastWritten, FrozenAccentHex()));

            restoredWindows++;
            restoredLines += window.Lines.Count;
        }

        if (restoredWindows == 0)
        {
            return;
        }

        _diagnostics.Logger.LogInformation(
            "Restored {Lines} line(s) into {Windows} pane(s) from the previous session",
            restoredLines,
            restoredWindows);
    }

    /// <summary>
    /// Whether a window owned by <paramref name="sessionKey"/> takes part in the restore log. An
    /// unowned window — the main window before any session adopts it, the web view — is allowed: no
    /// character has said otherwise, and the app-wide switch is checked by the caller.
    /// </summary>
    private bool RestoreLogWanted(string? sessionKey) =>
        sessionKey is null || CharacterFor(sessionKey)?.Logging.RestoreLog != false;

    /// <summary>
    /// The configured character behind a <c>world.character</c> session key, or null when the key names
    /// no character this configuration still holds (an anonymous connection, or one whose character has
    /// since been renamed or deleted).
    /// </summary>
    private CharacterDefinition? CharacterFor(string sessionKey) => _config.Worlds
        .SelectMany(world => world.Characters.Select(character => (Key: $"{world.Name}.{character.Name}", character)))
        .FirstOrDefault(pair => string.Equals(pair.Key, sessionKey, StringComparison.Ordinal))
        .character;

    /// <summary>
    /// Sets the demo's focused/connected character from the resumed config (snapshot chrome). It asks
    /// <see cref="StartupConnections"/> rather than reaching for the first world's first character,
    /// because that is now what a launch actually does — and the demo faking a connection the live
    /// startup path would not open is the exact class of divergence that has hidden three bugs in this
    /// file already. The first connection is the focused one here for the same reason it is in
    /// <see cref="StartAsync"/>.
    /// </summary>
    private void InitDemoRuntimeState()
    {
        foreach (var (world, character) in StartupConnections.Resolve(_config))
        {
            var key = $"{world.Name}.{character?.Name ?? world.Name}";
            _demoActiveKey ??= key;
            _demoConnectedKeys.Add(key);
        }
    }

    private static StyledSpan Link(string command) => new(
        command,
        new TextStyle(TerminalColor.FromIndex(11), TerminalColor.Default, TextAttributes.Underline),
        SpanInteraction.Command(command));

    /// <summary>
    /// What a launch does: open a window per <see cref="StartupConnection"/>, then dial them.
    /// <para>
    /// <b>Windows first, in order; sockets afterwards, together.</b> The two halves are separated on
    /// purpose. Creating the sessions and their windows runs sequentially on the UI thread in
    /// configuration order, so the tab strip, the rail and — decisively — <em>which character the
    /// command line is pointed at</em> are settled by the configuration and not by which server answered
    /// first. Dialling then happens concurrently, because a world with a black-holed port would
    /// otherwise hold every later world's window hostage for the length of a TCP timeout. Focus is taken
    /// before the first packet leaves, so three auto-connects land you on the first one, every time.
    /// </para>
    /// <para>
    /// <b>Nothing marked is a launch, not a failure.</b> The client opens with no connection, and says
    /// which of the two reasons it is and which keys change it — an empty workspace that explains itself
    /// rather than one that looks broken. This used to be unreachable (the first configured world was
    /// dialled unconditionally), which is exactly why the state is worth a sentence.
    /// </para>
    /// <para>
    /// <b>One refusal does not stop the others.</b> Each dial is awaited on its own and its failure is
    /// reported where the user can find it: <see cref="WorldSession"/> prints it as a system line in that
    /// character's own window — which may be a background tab — so it is also raised as a
    /// <see cref="Notice"/>, and every notice is kept in the ⌃P client message log after it retires
    /// itself. Several failures therefore leave several rows there, not one survivor.
    /// </para>
    /// </summary>
    internal async Task StartAsync(IReadOnlyList<StartupConnection> startup)
    {
        ArgumentNullException.ThrowIfNull(startup);

        if (startup.Count == 0)
        {
            // Two different states, and telling them apart is the whole value of the message: nothing
            // configured is a client with nothing to do yet, while worlds configured and none marked is
            // a client doing exactly what it was told. The second is Info for that reason — and neither
            // carries a key chip, because no surface refused anything; the text names the keys instead.
            var empty = _config.Worlds.Count == 0;
            Notice(
                empty ? NoWorldsNotice : NothingAtStartNotice,
                empty ? MessageSeverity.Warning : MessageSeverity.Info);
            return;
        }

        var opened = new List<(WorldSession Session, string WindowId)>(startup.Count);
        foreach (var (world, character) in startup)
        {
            var session = OpenSession(world, character);

            // The same pair SwitchToCharacter uses, and for the same reason: the first session finds the
            // main window free and takes it, every later one gets a tab of its own, because two
            // characters sharing one buffer would interleave their output.
            var windowId = OpenSessionWindow(session);
            BindSession(session, windowId);
            session.PrintSystem($"*** SharpMUTerm — theme '{_theme.Name}', graphics: {_capabilities.Protocol}.");
            opened.Add((session, windowId));
        }

        // BindSession leaves _active on whichever session was bound last. The rule is the first one, so
        // it is claimed back here — through Activate, the one activation path, so the tab, the drafts and
        // the pane indicator agree with it — and this happens before any dial, so no race can move it.
        Activate(opened[0].WindowId);

        await Task.WhenAll(opened.Select(o => DialAtStartupAsync(o.Session))).ConfigureAwait(false);
    }

    /// <summary>The empty-workspace message when there is nothing to connect because there is nothing at all.</summary>
    internal const string NoWorldsNotice =
        "No world configured. Pass a host/port on the command line, or add one on F5.";

    /// <summary>
    /// And when there are worlds but none of their characters is marked. It is the ordinary state of a
    /// fresh install and of every config upgraded from before the mark existed, so it names the two ways
    /// out rather than reading as a fault: connect one now, or mark it and stop being asked.
    /// </summary>
    internal const string NothingAtStartNotice =
        "nothing connects at start — ⌃P ▸ Switch to …, then Reconnect · F5 ▸ at start marks one";

    /// <summary>
    /// One startup dial, whose failure is this connection's own business. Awaited separately from its
    /// siblings so a refused world cannot cancel them, and reported rather than swallowed: the session
    /// prints the reason into its own window, which is a background tab for all but the first, so the
    /// status row says which connection it was.
    /// </summary>
    private async Task DialAtStartupAsync(WorldSession session)
    {
        try
        {
            await session.ConnectAsync().ConfigureAwait(false);

            // A freshly connected session has never been told anything, and there is no guarantee of
            // another frame soon enough to matter — so announce now, on the UI thread, where the pane
            // geometry and the report bookkeeping live.
            OnUiThread(ReportPaneSizes);
        }
        catch (Exception ex)
        {
            // Through Snippet, because the reason can carry a host name straight off the command line.
            OnUi(() => Notice(
                $"could not connect {SessionTitle(session)} — {Snippet(ex.Message)}",
                MessageSeverity.Error,
                "⌃P"));
        }
    }

    /// <summary>
    /// Builds the session for a world as a given <paramref name="character"/> — or, with none named, as
    /// its <em>first configured character</em> — so the character's trigger sets, login line, on-connect
    /// lines and log actually reach the runtime. A world with no characters still connects, anonymously,
    /// which is what a host typed on the command line is.
    /// <para>
    /// This is the seam the F2/F3/F5/F6 screens all hang off: the session holds the <em>same</em>
    /// <see cref="Trigger"/>/<see cref="Alias"/>/<see cref="TimerDefinition"/> objects the screens
    /// edit, so editing one is seen by the next line without a reload. <strong>Adding or removing a rule,
    /// and assigning or unassigning a whole set, are live too</strong> — see
    /// <see cref="ReloadAutomation"/>, which every committed settings change runs. That used to need a
    /// reconnect, and it is the defect the reported "captures never fire" was: a session opened before its
    /// character had the capture set assigned ran an empty trigger engine for the rest of its life. The one
    /// thing still deferred to the next connect is a <em>timer's period</em>, for the reason
    /// <see cref="WorldSession.ReloadAutomation"/> gives.
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
                _config.ScrollbackSpill,
                _config.CharsetOrder)
            : _sessions.Open(
                world,
                character,
                _config.ResolveTriggerSets(character),
                _config.ScrollbackLines,
                OpenLog(world, character),
                _config.Text,
                _config.Input,
                TelnetFactory,
                _config.ScrollbackSpill,
                _config.CharsetOrder);
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
    /// Where a character's session log is written: its own configured folder, or this app's log root —
    /// the same folder the client's diagnostics file lives in, and deliberately not the same file, since
    /// one is a transcript someone keeps and the other is client chrome.
    /// <para>
    /// <b>Null when this app owns no log root</b> (see the <c>logRoot</c> constructor parameter), and null
    /// then whatever the character says. An explicit <see cref="LoggingSettings.Directory"/> is a stronger
    /// statement of intent than a default, and it is still overruled: the root is the app's answer to
    /// "may I write transcripts at all", the character's directory only ever chose <em>where</em> within
    /// that. Reading it the other way would leave every fixture that names a path free to write outside
    /// itself — which is how the defect this gate closes stayed invisible.
    /// </para>
    /// </summary>
    private string? LogFolder(CharacterDefinition? character) =>
        _logRoot is null
            ? null
            : string.IsNullOrWhiteSpace(character?.Logging.Directory)
                ? _logRoot
                : character!.Logging.Directory!;

    private ILogSink? OpenLog(WorldDefinition world, CharacterDefinition? character, LogFormat? forceFormat = null)
    {
        var format = forceFormat ?? character?.Logging.Format ?? LogFormat.None;
        if (format == LogFormat.None)
        {
            return null;
        }

        // No log root, no transcript — silently, because this arm is every snapshot and every test, and a
        // connect is not the moment to complain that the client was built without a place to write. What
        // must never happen is the *reporting* surfaces claiming a log that does not exist; that is why
        // HeaderLogFormat and StartLogging both consult the same gate rather than the character's setting.
        if (LogFolder(character) is not { } folder)
        {
            return null;
        }

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
            // Every session's own window is titled for the session — its character, or its world when it has
            // none (a host typed on the command line). The main window used to be special-cased to the
            // world's name on the grounds that it is "the shell's own window", and it is not: it holds one
            // character's transcript exactly like a switched-to character's tab does. That special case is
            // what put the world's name on a row already nested under the world in the rail, and on the quit
            // prompt's draft line directly under "1 world connected — <that same world>". A tab strip of
            // "Aetherfall" beside "Rookery" was the same inconsistency: one names a world, the other a
            // character, and they sit in one row.
            window.Title = SessionTitle(session);

            // And it is recorded as this session's, for the same reason <see cref="OpenSessionWindow"/>
            // does it on the adoption path: the main window is built before any session exists, so the
            // first session adopts one whose owner is nobody. Ownership is not cosmetic — the rail lists a
            // character's windows by it and <see cref="WindowSession"/> resolves what a window's typing
            // and its links act on by it — and the startup path is the one that reaches this window.
            _workspace.SetWindowOwner(windowId, session.SessionKey);
        }

        // Any history the session already holds is replayed *through the line buffer*, not straight onto
        // the control. Painting it directly made this a third place pane content reached a control, and
        // the two disagreed: a line only the control knew about was dropped by the next thing to re-feed
        // that pane from the buffer (freezing, resuming, or now toggling the timestamp column). It
        // carries no stamp because a StyledLine records no arrival time — nothing in this replay knows
        // when these lines came in, and inventing one would be worse than leaving the gutter off them.
        PaneContentFor(windowId, session.World.Name);
        foreach (var line in session.Scrollback.Snapshot())
        {
            AppendWindowLine(windowId, _formatter.ToMarkup(line));
        }

        session.LinePrinted += (_, line) => OnUi(() => OnLine(session, windowId, line));
        session.PromptChanged += (_, _) => OnUi(UpdateStatus);

        // Every connect reports the automation it is running — see ReportAutomation. Hung off the state
        // change rather than off the two call sites that dial (StartAsync and ReconnectAsync), for the
        // reason this whole block is per-session and in one place: a third path would otherwise be a
        // third thing to remember, which is how a session that printed fine came to route no captures.
        session.StateChanged += (_, e) => OnUi(() =>
        {
            if (e.State == ConnectionState.Connected)
            {
                ReportAutomation(session);

                // Recorded on the *connection*, not on the report — because "we reached this server and
                // it published nothing" is a fact the INFO screen has to be able to state, and it is
                // only ever knowable from the connection having happened. MSSP arrives on the read loop
                // after this transition, so a server that does publish overwrites nothing: it adds a
                // report beside a connection time that is already here.
                _mssp.RecordConnection(session.World.Host, session.World.Port, _time.GetUtcNow());
            }

            UpdateStatus();
        });

        // MSSP is captured per world and kept, so the report is readable while nothing is connected —
        // which is when it is wanted, since the question the screen answers is "what is this world"
        // asked before or between sessions. It goes through CaptureMssp rather than straight into the
        // cache so the snapshot's demo report is written by the same code the wire is (see DemoScene's
        // remarks on state a live session writes).
        session.MsspReceived += (_, e) => OnUi(() => CaptureMssp(session.World, e.Data));
        session.GmcpReceived += (_, e) => OnUi(() =>
        {
            if (_stats.Update(e.Package, e.Json))
            {
                UpdateStatus();
            }
        });
        session.SpawnLine += (_, e) => OnUi(() => OnSpawnLine(session, e.Target, e.Line));

        // The status row's encoding cell is live, so it has to be repainted when the thing it reports
        // changes. WorldSession has already put the change in the client message log by the time this
        // runs (see WorldSession.OnEncodingChanged) — this is only the repaint.
        session.EncodingChanged += (_, _) => OnUi(UpdateStatus);
        RefreshTabTitles();
        UpdateStatus();
    }

    /// <summary>
    /// Records what automation a connection came up with — which sets resolved for its character and how
    /// many rules they hold — and warns about a set it is assigned that does not exist.
    /// <para>
    /// It is here because the client used to say nothing about this anywhere, which is what made "my
    /// captures do not work" unanswerable from the screen: an empty trigger engine and a full one that
    /// happens not to match look identical, and
    /// <see cref="AppConfiguration.ResolveTriggerSets"/> skips a name it cannot find without a word. Three
    /// surfaces now cover it — this one at connect, the <see cref="Notice"/> in
    /// <see cref="ReloadAutomation"/> when an edit changes what is live, and <c>/triggers</c> for the
    /// detail including how often each rule has actually fired.
    /// </para>
    /// <para>
    /// The summary goes to the client message log (⌃P ▸ <c>Show client messages</c>) and <em>not</em> to the
    /// output pane, for the reason <see cref="Notice"/> gives: the pane is the server's stream and
    /// <see cref="WorldSession.PrintSystem"/> writes into the character's transcript, so client chrome
    /// about rule counts would land in a log someone keeps. It is also not a status-row notice, because a
    /// character connecting with the automation it was configured with is not news. Only the misnamed set
    /// is, and that one is loud.
    /// </para>
    /// </summary>
    private void ReportAutomation(WorldSession session)
    {
        if (session.Character is not { } character)
        {
            return; // anonymous: no character, so no trigger sets to resolve or misname
        }

        var who = SessionTitle(session);
        var sets = _config.ResolveTriggerSets(character);
        _diagnostics.Logger.LogInformation(
            "{Notice:l}",
            $"{who}: {TriggerReport.Summary(who, character.TriggerSets, sets)} (/triggers for detail)");

        foreach (var orphan in TriggerReport.Orphans(character.TriggerSets, sets))
        {
            Notice(
                $"{who} is assigned a trigger set called {orphan}, and no such set exists — F2 defines sets, F5 assigns them",
                MessageSeverity.Warning,
                "⌃P");
        }
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
    /// A character with no session yet gets one opened here, as itself (its trigger sets, login line and
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
            : "offline — Alt+R (⌃P ▸ Reconnect) connects it";
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
        else
        {
            // Adopting a window that already exists — the main one, which is built before any session.
            // Its recorded owner would otherwise still name whoever held it before, and the rail reads
            // ownership to decide whose windows to list.
            _workspace.SetWindowOwner(windowId, session.SessionKey);
        }

        return windowId;
    }

    /// <summary>
    /// Whether the output views draw the timestamp gutter. Read straight off the live
    /// <see cref="TextSettings"/> rather than mirrored into a field of its own, so the ⌃P entry, the
    /// catalog's idea of which label to offer, and what the panes draw are one fact and cannot desync —
    /// which is precisely what let <c>term:timestamps-on</c> and <c>term:timestamps-off</c> both run a
    /// plain <c>!</c> flip and mean the opposite of what they said.
    /// </summary>
    private bool ShowTimestamps
    {
        get => _config.Text.ShowTimestamps;
        set => _config.Text.ShowTimestamps = value;
    }

    /// <summary>
    /// When a line arriving now should say it arrived. Recorded on every session line whether or not
    /// the gutter is currently drawn — see <see cref="PaneLine"/>. Headless snapshots use a fixed clock
    /// so golden frames stay stable.
    /// </summary>
    private string StampNow() => _headless ? "09:24" : DateTime.Now.ToString("HH:mm");

    /// <summary>
    /// One buffered line as a control should draw it right now: its own markup, with the timestamp
    /// gutter glued on only while the column is on and the line has a stamp to show. This is the whole
    /// of the "show timestamps" decision, and it is made here — on the way to a control — rather than
    /// on the way into the buffer.
    /// </summary>
    private string Compose(PaneLine line) =>
        MarkupFormatter.WithTimestamp(line.Markup, ShowTimestamps ? line.Stamp : null);

    /// <summary>
    /// Appends one already-formatted markup line to a window: records it in the scrollback buffer and,
    /// if the window has a live control, paints it. A frozen pane's live control is its tail region, so
    /// new lines land below the <c>▲ FROZEN ⌃F</c> bar while the pinned scrollback stays put.
    /// <para>
    /// <paramref name="stamp"/> is when the line arrived, and defaults to none: only a world's output
    /// passes one, because only a world's output is what the timestamp column describes.
    /// </para>
    /// </summary>
    private void AppendWindowLine(string windowId, string markup, string? stamp = null)
    {
        if (!_lines.TryGetValue(windowId, out var buffer))
        {
            _lines[windowId] = buffer = new List<PaneLine>();
        }

        buffer.Add(new PaneLine(markup, stamp));


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
            control.AppendLine(Compose(buffer[^1]));
        }
    }

    /// <summary>
    /// Records one of a world's lines in the restore log, so the pane it landed in comes back holding
    /// it next launch.
    /// <para>
    /// It is called from <see cref="OnLine"/> and <see cref="OnSpawnLine"/> and from nowhere else, and
    /// deliberately <em>not</em> from <see cref="AppendWindowLine"/> even though that is the seam every
    /// line goes through. Two reasons, and both are bugs avoided rather than tidiness. That seam also
    /// carries the client's own chrome — <c>/graphics</c>, <c>/triggers</c>, the restore bar itself —
    /// which is not session content and has no business surviving into a later run. And it carries the
    /// restore <em>replay</em>, so logging there would have each launch re-record its own history and
    /// double every pane against the bound.
    /// </para>
    /// <para>
    /// The <see cref="StyledLine"/> is what is stored rather than the markup it was rendered to, because
    /// markup is the theme's answer and not the world's: a line logged as markup would come back frozen
    /// in whatever colours were configured the day it arrived, and would have lost the palette indices,
    /// the rule colour and each span's interaction on the way (see <see cref="StyledLineCodec"/>).
    /// </para>
    /// </summary>
    private void RecordForRestore(WorldSession session, string windowId, string title, StyledLine line, string? stamp)
    {
        if (_restore is null || !_config.RestoreLog.Enabled)
        {
            return;
        }

        // Read off the session rather than the window's recorded owner: it is the character whose line
        // this is, it cannot be stale, and a session with no character (a host typed on the command
        // line) has nobody to have opted out.
        if (session.Character?.Logging.RestoreLog == false)
        {
            return;
        }

        _restore.Append(windowId, title, line, stamp);
    }

    /// <summary>A window's title, or its id when the workspace does not (yet) know it.</summary>
    private string WindowTitle(string windowId) => _workspace.FindWindow(windowId)?.Title ?? windowId;

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
    private void FeedRange(MarkupControl control, List<PaneLine> buffer, int from, int count)
    {
        var start = Math.Clamp(from, 0, buffer.Count);
        var length = Math.Clamp(count, 0, buffer.Count - start);
        var markup = new List<string>(length);
        for (var i = 0; i < length; i++)
        {
            markup.Add(Compose(buffer[start + i]));
        }

        control.SetContent(markup);
    }

    /// <summary>
    /// Turns the output views' timestamp column on or off and repaints what is already on screen, which
    /// is the whole point of the setting: it describes lines that have <em>arrived</em>.
    /// <para>
    /// It takes the state it is asked for rather than flipping. Both catalog ids used to run one
    /// <c>!</c>, so the entry labelled <c>Show timestamps</c> turned them <em>off</em> whenever the
    /// catalog's idea of the state and the app's had drifted apart. They can no longer drift — the
    /// catalog reads <see cref="ShowTimestamps"/> and so does every pane — but a command that says which
    /// state it wants should ask for it, not for the other one.
    /// </para>
    /// <para>
    /// Asking for the state it is already in does nothing at all: it must not flip, and it must not
    /// spend a full re-feed of every pane to arrive where it already was.
    /// </para>
    /// </summary>
    private void SetTimestamps(bool on)
    {
        if (ShowTimestamps == on)
        {
            return;
        }

        ShowTimestamps = on;
        RepaintPanes();
        PersistConfiguration();
    }

    /// <summary>
    /// Re-draws every output pane from its line buffer, so a change to how a <em>buffered</em> line
    /// renders reaches the text already on screen. Toggling the timestamp gutter is the one caller: the
    /// setting names something about lines that have already arrived, so a version of it that only
    /// applied to the next line would be the reported bug in a politer form.
    /// <para>
    /// Feeding whole buffers is the expensive operation this file warns about (<see cref="FeedRange"/>),
    /// and that is the right trade here and only here: it is bounded by one deliberate keystroke, not by
    /// the wire. A frozen window's two halves are re-fed at its split point, exactly as
    /// <see cref="BuildFrozenContent"/> lays them out, so the pinned scrollback does not slide into the
    /// live tail on the way past.
    /// </para>
    /// <para>
    /// The web view is skipped. Its pane is not fed from this buffer at all — <see cref="ShowWeb"/>
    /// sets the page's markup straight onto the control — so re-feeding it would replace the page with
    /// whatever <c>/graphics</c> or <c>/triggers</c> happened to print into that window. Web pages carry
    /// no arrival times and have no gutter to gain.
    /// </para>
    /// </summary>
    private void RepaintPanes()
    {
        foreach (var (windowId, buffer) in _lines)
        {
            if (string.Equals(windowId, WebWindowId, StringComparison.Ordinal))
            {
                continue;
            }

            if (_freezePoints.TryGetValue(windowId, out var point))
            {
                var split = Math.Clamp(point, 0, buffer.Count);
                if (_frozenPanes.TryGetValue(windowId, out var frozen))
                {
                    FeedRange(frozen, buffer, 0, split);
                }

                if (_panes.TryGetValue(windowId, out var tail))
                {
                    FeedRange(tail, buffer, split, buffer.Count - split);
                }

                continue;
            }

            if (_panes.TryGetValue(windowId, out var control))
            {
                FeedRange(control, buffer, 0, buffer.Count);
            }
        }
    }

    /// <summary>
    /// Appends a line to a window's pane and badges it unread when the reader cannot see where it landed.
    /// <para>
    /// That is <see cref="Workspace.IsCaughtUp"/> and not <c>IsVisible</c>: a visible tab whose output the
    /// reader has scrolled back off is exactly as blind as a tab they are not looking at, and asking only
    /// about visibility meant new output arrived silently below the viewport — the one state in which a
    /// badge is the only thing that could tell them.
    /// </para>
    /// </summary>
    private void OnLine(WorldSession session, string windowId, StyledLine line)
    {
        var stamp = StampNow();
        AppendWindowLine(windowId, _formatter.ToMarkup(line), stamp);
        RecordForRestore(session, windowId, WindowTitle(windowId), line, stamp);

        if (!_workspace.IsCaughtUp(windowId))
        {
            _workspace.NoteActivity(windowId);
            RefreshTabTitles();
        }
    }

    /// <summary>
    /// Routes a trigger-spawned line to its spawn window (creating the tab on first use).
    /// <para>
    /// The owner recorded on a first-seen window is the <paramref name="session"/> whose trigger fired,
    /// not <c>_active</c>. It used to be the latter, so a background world's capture opened a window
    /// labelled and owned by whichever character happened to be focused — and ownership is not
    /// cosmetic: the rail lists a character's windows by it, and <see cref="WindowSession"/> resolves
    /// which world a link clicked in a spawn window sends to by it.
    /// </para>
    /// </summary>
    private void OnSpawnLine(WorldSession session, string target, StyledLine line)
    {
        var existed = _workspace.FindWindow(Workspace.SpawnWindowId(target)) is not null;
        var window = _workspace.RouteSpawn(target, session.SessionKey);

        // Its owner's own name, which for a session with no character is its world's. It used to fall back on
        // the *main window's* title, which is a different session's name as soon as more than one is open.
        window.OwnerLabel ??= SessionTitle(session);
        PaneContentFor(window.Id, window.Title); // ensure the live control exists before buffering

        // The restore log is fed here as well as in OnLine, and that is the crux of the whole feature:
        // a spawn window's content never reaches WorldSession.Scrollback, so a restore built on session
        // scrollback would bring the main windows back and leave every channel pane empty.
        var stamp = StampNow();
        AppendWindowLine(window.Id, _formatter.ToMarkup(line), stamp);
        RecordForRestore(session, window.Id, window.Title, line, stamp);

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
            OpenWeb(windowId, command[5..].Trim());
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

        // `/triggers` answers "why did no window open?" — see TriggerReport. Appended to the window rather
        // than routed through the session, like `/graphics`, so it still answers with nothing connected.
        if (command.Trim().Equals("/triggers", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in TriggerReportNow())
            {
                AppendWindowLine(windowId, $"[dim]*** {Escape(line)}[/]");
            }

            return;
        }

        // Resolved from the window in front of you, never from _active — see SendTarget. A pane with no
        // connection has nowhere to send, and that is reported here, at the moment of sending, rather than
        // by the line quietly going to whichever world was active before you navigated.
        if (SendTarget() is { } target)
        {
            _ = target.SendUserInputAsync(command);
            return;
        }

        RefuseCommand(NothingToSendTo(windowId));
    }

    /// <summary>
    /// <b>The session ⏎ sends to: the one the focused window belongs to.</b> Resolved by
    /// <see cref="WindowSession"/> — the session printing into the window, else the character the
    /// workspace records as owning it, else nothing — and never <c>_active</c>.
    /// <para>
    /// This is the same rule <see cref="OnLinkClicked"/> uses for a click, and for the same reason. The
    /// command line used to send to <c>_active</c>, which <see cref="AdoptSessionOf"/> deliberately leaves
    /// on the previous world when the window you navigated to has no session of its own. The two halves
    /// were each defensible and together they were a misdelivery: with a connected Ann in one pane and a
    /// session-less window in the other, ⌃→ moved the focus, the indicator and the tab marker to the
    /// second pane, and the next line went to <em>Ann</em> — a world whose pane was not the focused one.
    /// That is exactly the property the per-window link work exists to guarantee, and the keyboard is the
    /// last route that did not honour it.
    /// </para>
    /// <para>
    /// Navigation is untouched by this and must stay that way: asking to go somewhere always arrives. It
    /// is <em>sending</em> that needs a target, so a pane with no connection takes the focus and the caret
    /// like any other and refuses at ⏎ (<see cref="NothingToSendTo"/>), with the prompt saying so in the
    /// meantime (<see cref="UpdateInputChrome"/>) rather than naming a world it cannot reach.
    /// </para>
    /// </summary>
    private WorldSession? SendTarget() => WindowSession(ActiveWindowId());

    /// <summary>
    /// Why a line could not be sent, naming what would open a connection. Three states, because they need
    /// three different next steps: a window whose recorded owner has no session this run, a window that
    /// belongs to no connection at all (the web view), and a client with nothing open anywhere — which is
    /// where the first line someone types lands, and where it used to vanish without a word.
    /// </summary>
    private string NothingToSendTo(string windowId)
    {
        var window = _workspace.FindWindow(windowId);
        if (window?.SessionKey is { Length: > 0 } owner)
        {
            return $"{owner} has no open session — ⌃P ▸ Switch to it opens one; nothing was sent";
        }

        // Through Snippet: a window's title can be a world's own text (the web view is titled from the
        // page it loaded), and the status row is no place for an unbounded string off the wire.
        return _sessionWindows.Count == 0
            ? "nothing is connected — ⌃P ▸ Switch to a character; nothing was sent"
            : $"{Snippet(window?.Title ?? windowId)} belongs to no connection — nothing was sent";
    }

    /// <summary>
    /// The active session's trigger report, or the one line there is to say when there is no session to
    /// report on. Internal so a headless test can read what <c>/triggers</c> says without parsing a pane.
    /// </summary>
    internal IReadOnlyList<string> TriggerReportNow()
    {
        if (_active is not { } session)
        {
            return new[] { "No connection is active, so no capture rules are loaded — ⌃P ▸ Switch to …" };
        }

        var character = session.Character;
        return TriggerReport.Describe(
            SessionTitle(session),
            character?.TriggerSets ?? (IReadOnlyList<string>)Array.Empty<string>(),
            character is null ? Array.Empty<TriggerSet>() : _config.ResolveTriggerSets(character),
            session.Triggers);
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

    /// <summary>The window id of the visible tab (the input line belongs to it). Internal so a test can
    /// assert which window a click on the rail brought forward.</summary>
    internal string ActiveWindowId() => _workspace.Layout.FocusedPane.ActiveTab ?? MainWindowId;

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
        _palette.IsOpen || _settings.IsOpen || _quit.IsOpen || _messageLog.IsOpen || _historySearch.IsOpen
        || _prefixPanel.IsOpen;

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
        // Both bands come from the theme through WorkspacePalette, so the armed bar is lit by the very
        // same tone as the focused pane and the idle one sits on the same recessed plane as the rest of
        // the chrome. They used to be two hardcoded blue-greys thirteen points apart per channel, which
        // is what "make it super obvious which input window is selected" was reported about.
        bar.BandColor = ToColor(WorkspacePalette.ArmedBand(_theme));
        bar.IdleBandColor = ToColor(WorkspacePalette.IdleBand(_theme));
        bar.TextColor = ToColor(_theme.Resolve(TerminalColor.Default, isBackground: false));
        bar.IdleTextColor = ToColor(WorkspacePalette.IdleInk(_theme));
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
    /// <para>
    /// <b>The bare arrows only.</b> This used to look at the key and never at the modifiers, which is how
    /// it came to swallow Shift+↑ from the scrollback work — repaired then by putting the scrollback keys
    /// ahead of it, which fixes the one chord that had already been reported and leaves the next one to
    /// be found the same way. Ordering still matters and is unchanged; declining what is not ours is the
    /// half that was missing.
    /// </para>
    /// </summary>
    private bool TryRecallKey(KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Modifiers != 0)
        {
            return false;
        }

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

    // --- Alt+⏎ reassembly -----------------------------------------------------------------------

    /// <summary>
    /// How long after an Escape a following Enter is still the second half of one Alt+⏎ rather than two
    /// keystrokes. It is SharpConsoleUI's own <c>UnixStdinReader.EscTimeoutMs</c> — the framework's bound
    /// on how far apart an ESC and the byte after it may be and still belong to one keypress — so this is
    /// not a number that was tuned until it felt right.
    /// <para>
    /// In practice the gap is microseconds, not milliseconds, in <em>both</em> of the paths the reader has:
    /// a terminal writes <c>ESC CR</c> in one write, so the two land in one <c>read</c>, one parse and one
    /// dispatch batch; and when the read boundary happens to fall between them the reader waits out this
    /// same timeout <em>before</em> emitting the Escape and then parses the CR immediately after. So the
    /// window is generous rather than tight, and a human cannot close it: the fastest deliberate
    /// Esc-then-Enter is several times this, which is what keeps a real Escape followed by a real ⏎ from
    /// being mistaken for a newline.
    /// </para>
    /// </summary>
    private static readonly TimeSpan AltEnterWindow = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// When the last unclaimed Escape arrived, or null when the previous key was not one. Only an Escape
    /// that reached the end of the chain sets it — one that closed an overlay, abandoned a drag, disarmed
    /// the ⌃B prefix or left move mode is an Escape the user meant, and pairing it would turn "back out of
    /// this" followed by "send my line" into a newline.
    /// </summary>
    private DateTimeOffset? _escapeAt;

    /// <summary>
    /// Reassembles Alt+⏎ from the Escape and Enter the parser splits it into, and inserts a newline in the
    /// armed command line.
    /// <para>
    /// This is the whole of item 3, and it is worth being plain about why it is not simply a modifier
    /// check. The terminal reports Shift+⏎ and Ctrl+⏎ as a bare ⏎, so those two cannot be bound at all
    /// here; Alt+⏎ is the one modifier+Enter this host actually delivers, and it delivers it as
    /// <c>ESC</c> then <c>CR</c> — two <see cref="ConsoleKey"/> events, because
    /// <c>AnsiInputParser.ProcessEscape</c> emits ESC followed by a <em>control</em> byte as two keys
    /// (only ESC followed by a printable byte becomes a single Alt chord). Both halves reach this handler,
    /// in order, so the pair can be recognised.
    /// </para>
    /// <para>
    /// It is safe to consume here specifically because <b>Escape in the command line does nothing</b>: the
    /// bar's key table falls through it (its <c>KeyChar</c> is a control character) and every other
    /// meaning Escape has in this app — closing an overlay, abandoning a drag, leaving move mode,
    /// disarming the prefix — is handled earlier and returns before <see cref="_escapeAt"/> is ever set.
    /// So the only behaviour this can take away is a lone Escape immediately followed by a send, inside a
    /// window no hand can hit.
    /// </para>
    /// </summary>
    private bool TryAltEnter(KeyPressedEventArgs e)
    {
        var wasPending = _escapeAt;
        _escapeAt = null;

        if (e.KeyInfo.Key == ConsoleKey.Escape && e.KeyInfo.Modifiers == 0)
        {
            // Remembered, not consumed: a lone Escape must still behave exactly as it did (as nothing),
            // so it falls through the rest of the chain untouched.
            _escapeAt = _time.GetUtcNow();
            return false;
        }

        if (e.KeyInfo.Key != ConsoleKey.Enter
            || e.KeyInfo.Modifiers != 0
            || wasPending is not { } at
            || _time.GetUtcNow() - at > AltEnterWindow)
        {
            return false;
        }

        // Handed to the bar's own key table as the Alt+Enter it was, rather than reaching into the buffer
        // from here: what ⏎ and its modified forms mean is one list, in InputBarControl.
        e.Handled = true;
        return RouteToInput(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: true, control: false));
    }

    // --- Focus navigation -----------------------------------------------------------------------

    /// <summary>
    /// Handles Ctrl+←/→/↑/↓ — move between panes, and at the bottom edge into the command lines — or
    /// reports that this key is not one of them.
    /// <para>
    /// <b>Where it sits.</b> On the window's <c>PreviewKeyPressed</c>, for the reason every other key in
    /// this window is: focus is pinned to the armed command line, so a pane's own control never sees a
    /// keystroke. It runs <em>after</em> <see cref="DispatchMacro"/>, which is what keeps
    /// <see cref="MacroKeys.Verdict"/> honest — that reports a modified navigation key as one a macro
    /// fires on, and it still does, because a chord the user has deliberately bound is theirs. It runs
    /// <em>before</em> <see cref="TryScrollKey"/> and <see cref="TryRecallKey"/> and before the command
    /// line: moving between panes is a workspace gesture and outranks a caret move, and the bars must not
    /// swallow these — <see cref="TryRecallKey"/> does not look at modifiers at all, which is how Shift+↑
    /// used to be eaten by history recall.
    /// </para>
    /// <para>
    /// <b>Why Ctrl+←/→ is no longer word-movement on the command line.</b> It was, and that was the one
    /// real cost of this feature; word movement is now Alt+←/→, which this host delivers just as reliably
    /// (the parser reads the Alt bit out of <c>CSI 1;3 D</c>) and which is the more widely-used spelling
    /// of the two. The readline set (⌃A ⌃E ⌃K ⌃U) is untouched.
    /// </para>
    /// <para>
    /// <b>What "focus" means here, given the pin.</b> This app has two focus facts and they are separate
    /// by design: which pane the workspace keys act on (<c>Layout.FocusedPaneId</c>) and which command
    /// line ⏎ sends from (<see cref="_armed"/>). Keyboard focus is pinned to the second — a fix for a
    /// paste bug — and that pin is not weakened here: these keys move pane <em>selection</em> and bar
    /// <em>arming</em>, never framework focus, so typing continues to land in the command line from
    /// wherever you have navigated to.
    /// </para>
    /// <para>
    /// <b>But moving selection does move the session.</b> The pin is about which control the framework
    /// hands a keystroke to; it says nothing about <em>which character the bar talks to</em>, and that
    /// second fact does have to follow. The first cut of these keys reasoned "it never moves keyboard
    /// focus, so no third piece of state is needed" and left the command line pointed at the world you had
    /// navigated away from — attention on one pane, keystrokes to another, which is the class of bug the
    /// per-window link work exists to eliminate. So <see cref="FocusPane"/> goes through
    /// <see cref="Activate"/>: session, prompt, drafts and all, by the same route a tab click takes.
    /// </para>
    /// <para>
    /// <b>Vertically the panes and the bars are one ladder</b>, because on screen they are: the bars are
    /// drawn below the panes. Ctrl+↓ prefers a pane below and falls through to the next command line when
    /// there is none; Ctrl+↑ from the second bar steps up to the first, and otherwise moves between panes.
    /// The asymmetry is only apparent — each direction consults the station it would reach first. With a
    /// single bar Ctrl+↓ at the bottom row has nowhere to go and says so, and what it says is the useful
    /// truth: the command line already has the keyboard.
    /// </para>
    /// </summary>
    private bool TryFocusKey(KeyPressedEventArgs e)
    {
        if (!e.KeyInfo.Modifiers.HasFlag(ConsoleModifiers.Control)
            || e.KeyInfo.Modifiers.HasFlag(ConsoleModifiers.Alt)
            || e.KeyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift))
        {
            return false;
        }

        var direction = e.KeyInfo.Key switch
        {
            ConsoleKey.LeftArrow => PaneDirection.Left,
            ConsoleKey.RightArrow => PaneDirection.Right,
            ConsoleKey.UpArrow => PaneDirection.Up,
            ConsoleKey.DownArrow => PaneDirection.Down,
            _ => (PaneDirection?)null,
        };

        if (direction is not { } move)
        {
            return false;
        }

        // Claimed whichever way it turns out, including the no-neighbour edge: a navigation key that is
        // live in one geometry and types a character in another is the shape of bug that gets reported as
        // a corrupted command line.
        e.Handled = true;

        // Up out of the second command line before anything else — it is the station above it.
        if (move == PaneDirection.Up && ReferenceEquals(_armed, _second) && _second.Visible)
        {
            ArmBar(_input);
            return true;
        }

        if (PaneNavigation.Neighbour(FocusRects(), _workspace.Layout.FocusedPaneId, move) is { } target)
        {
            FocusPane(target);
            return true;
        }

        // Down off the last pane lands on the command lines, which are drawn below them.
        if (move == PaneDirection.Down && _second.Visible && ReferenceEquals(_armed, _input))
        {
            ArmBar(_second);
            return true;
        }

        RefuseFocusMove(move);
        return true;
    }

    /// <summary>
    /// Handles Alt+Shift+←/→/↑/↓ — makes the focused pane narrower, wider, taller or shorter by
    /// <see cref="PaneResize.StepCells"/> cells.
    /// <para>
    /// <b>It was Ctrl+Shift+arrow, and half of that chord never arrived.</b> The parser decodes
    /// <c>CSI 1;6 &lt;final&gt;</c> perfectly — which is what the original evidence checked, and it is a
    /// different claim from the terminal sending it. <c>kitty_mod</c> is <c>ctrl+shift</c> and kitty binds
    /// all four by default: <c>ctrl+shift+left</c>/<c>right</c> are <c>previous_tab</c>/<c>next_tab</c>,
    /// which return <c>None</c> from kitty's dispatcher and so are <em>consumed by the terminal</em> and
    /// never written to the pty at all; <c>ctrl+shift+up</c>/<c>down</c> are <c>scroll_line_up</c>/
    /// <c>_down</c>, which return <c>True</c> when the alternate screen is up and are therefore passed
    /// through. That asymmetry is the whole of the reported bug — the horizontal pair was dead in the
    /// user's terminal and no app-side code could have reached it — and the vertical pair only worked by
    /// an accident of one emulator's implementation (VTE binds the same two to its own scrolling and does
    /// not pass them on). So the family moved, whole, to a chord nothing else claims:
    /// <c>CSI 1;4 &lt;final&gt;</c> is decoded as the arrow with Alt and Shift set, distinctly from the
    /// plain arrow, from Shift alone (scrollback), from Alt alone (word movement in the command line) and
    /// from Ctrl alone (pane selection). <c>TerminalKeyArrivalTests</c> asks the framework's own parser
    /// that question rather than assuming it.
    /// </para>
    /// <para>
    /// Matched on the exact modifier pair, not on flags: Ctrl+Alt+Shift+→ is somebody else's chord and a
    /// <c>HasFlag</c> test would quietly claim it.
    /// </para>
    /// </summary>
    private bool TryResizeKey(KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Modifiers != (ConsoleModifiers.Alt | ConsoleModifiers.Shift))
        {
            return false;
        }

        var direction = e.KeyInfo.Key switch
        {
            ConsoleKey.LeftArrow => PaneDirection.Left,
            ConsoleKey.RightArrow => PaneDirection.Right,
            ConsoleKey.UpArrow => PaneDirection.Up,
            ConsoleKey.DownArrow => PaneDirection.Down,
            _ => (PaneDirection?)null,
        };

        if (direction is not { } move)
        {
            return false;
        }

        // Claimed whichever way it turns out, for the reason TryFocusKey is: a chord that is live in one
        // geometry and types a character in another reads as a corrupted command line.
        e.Handled = true;
        ResizePane(move);
        return true;
    }

    /// <summary>
    /// Resizes the focused pane, or says why it could not — what both the chord and the ⌃P surface's four
    /// <c>Make this pane …</c> entries run.
    /// <para>
    /// The rectangles handed to <see cref="PaneResize"/> are <see cref="FocusRects"/>, the same arranged
    /// geometry pane navigation answers from, so the border moves the number of cells the user can count.
    /// Rebuilding the pane area is all the announcing this needs: NAWS rides the frame
    /// (<c>PostBufferPaint → ReportPaneSizes</c>) and is rate-limited there, so a held Alt+Shift+→
    /// costs the server at most one report per <see cref="WindowSizeReportInterval"/> plus the trailing
    /// flush that carries the size the resize settled on — the same throttle a drag goes through, reached
    /// by the same route rather than around it.
    /// </para>
    /// </summary>
    private void ResizePane(PaneDirection direction)
    {
        var result = PaneResize.Apply(_workspace.Layout, direction, FocusRects());
        if (result.Changed)
        {
            RebuildPaneArea();
            return;
        }

        Notice(
            PaneResize.Describe(result.Outcome, direction),
            MessageSeverity.Warning,
            $"⌥⇧{PaneResize.Arrow(direction)}");
    }

    /// <summary>
    /// Moves pane selection one step, or says there is nothing that way — what the ⌃P surface's four
    /// <c>Focus pane …</c> entries run. It is the keyboard's path minus the command-line fall-through:
    /// a palette entry named "Focus pane down" is about panes, and arming a command line from a list of
    /// layout commands would be a different action wearing that label.
    /// </summary>
    private void MoveFocus(PaneDirection direction)
    {
        if (PaneNavigation.Neighbour(FocusRects(), _workspace.Layout.FocusedPaneId, direction) is { } target)
        {
            FocusPane(target);
            return;
        }

        RefuseFocusMove(direction);
    }

    /// <summary>
    /// Goes to the <paramref name="number"/>th pane and brings it to the front — ⌥1–⌥9, and the ⌃P
    /// <c>Go to pane N</c> entries.
    /// <para>
    /// <b>The number is the rail's number.</b> Panes are counted in <c>Layout.Panes</c> order
    /// (<em>creation</em> order), which is the order the connection rail's hosting column numbers them
    /// in, so ⌥3 goes to the pane the sidebar labels <c>pane 3</c>. There is no second numbering to
    /// reconcile any more: the drag and move overlays used to call the first pane <c>main</c> while the
    /// rail called it <c>pane 1</c> (see <see cref="PaneLabel"/>), which is a mismatch a chord cannot
    /// survive — a key that lands somewhere other than the label says is worse than no key.
    /// </para>
    /// <para>
    /// <b>The numbering is global, and it is stable.</b> Global because it always was: a workspace has
    /// one split tree whoever is connected in it, so ⌥3 has always reached a pane holding another
    /// character's window — which is what makes these nine chords a character switcher as well as a pane
    /// switcher. Stable is the part that had to be built. Panes used to be counted in tree order, so
    /// creating one renumbered every pane after the insertion point and ⌥2 stopped meaning what it meant
    /// while the user was doing something else entirely. That the rail now says which character is in
    /// each pane (<see cref="BuildRail"/>) is the other half: a number nobody can see is a number nobody
    /// presses.
    /// </para>
    /// <para>
    /// <b>Why Alt, and why the framework had to be outranked.</b> Ctrl+digit was what was asked for and it
    /// is not a chord this terminal has: the digit row has no control bytes of its own, so a terminal
    /// sends the bare digit for 1/9/0 and, for the rest, a byte already spelt Escape, Backspace or NUL
    /// (<c>MacroKeys</c>'s <c>DigitBytes</c>, read off a real pty). Alt+digit is <c>ESC</c> + the digit and
    /// arrives cleanly. But <em>SharpConsoleUI already claims Alt+1–9</em>:
    /// <c>InputCoordinator.HandleAltInput</c> selects among top-level windows by index, and unlike the
    /// move and resize handlers beside it, it is not gated on <c>IsMovable</c>/<c>IsResizable</c> — so
    /// <c>Movable(false)</c> did not switch it off. It is reached only from the fall-through taken when
    /// the active window did not handle the key, and a global shortcut (which this is, registered from
    /// <see cref="MacroKeys.AppShortcuts"/>) runs before the window is offered the key at all. All nine
    /// digits are claimed for that reason, in range or not: an out-of-range ⌥7 reports here and stops,
    /// rather than falling through to a window selector that would silently do something else.
    /// </para>
    /// <para>
    /// <b>Zoom follows.</b> "Bring it to the forefront" over a zoomed workspace means the pane you named
    /// is the one filling the screen, so an existing zoom is carried to the target
    /// (<see cref="WorkspaceLayout.CarryZoomToFocused"/>) instead of leaving the selection — and the
    /// session, and the caret — on a pane that is not rendered. The zoom is not <em>started</em> and not
    /// cancelled; ⌃B z still means what it meant.
    /// </para>
    /// </summary>
    private void JumpToPane(int number)
    {
        var panes = _workspace.Layout.Panes;
        if (number < 1 || number > panes.Count)
        {
            // Never silent. A digit with no pane behind it is the commonest way to press this chord
            // wrong, and the count is the whole answer — ⌃P's Go to pane entries list exactly the panes
            // that exist, which is where a reader goes next.
            Notice(
                panes.Count == 1
                    ? "the workspace has one pane — ⌃B | and ⌃B - split it"
                    : $"there is no pane {number} — this workspace has {panes.Count}",
                MessageSeverity.Warning,
                $"⌥{number}");
            return;
        }

        var target = panes[number - 1].Id;
        FocusPane(target);

        // After the focus move, so the zoom lands on the pane that is now selected. Rebuilding is what
        // realises the change; FocusPane's own path only syncs the view to a pane it did not move.
        if (_workspace.Layout.CarryZoomToFocused())
        {
            RebuildPaneArea();
        }
    }

    /// <summary>
    /// Moves pane selection and brings the rest of the app in line with it, by <em>activating</em> the
    /// pane's own active window — the same path a tab click and a rail click take (see
    /// <see cref="Activate"/>), so ⌃O, the Ctrl+arrows, the ⌃P entries and the mouse cannot end up meaning
    /// different things by different routes.
    /// <para>
    /// Activating is what makes the command line talk to the character whose pane you moved to. It was
    /// once only <see cref="SyncToFocusedPane"/> — drafts followed the move but the session did not, so a
    /// line typed after ⌃→ went to the world you had just left.
    /// </para>
    /// </summary>
    private void FocusPane(string paneId)
    {
        if (!_workspace.Layout.Focus(paneId))
        {
            return;
        }

        ActivateFocusedWindow();
    }

    /// <summary>
    /// Activates whatever the newly focused pane is showing. A pane with no tab at all has no window to
    /// activate — the layout prunes empty panes, so this is a guard rather than a case — and still needs
    /// the rest of the app brought in line, so the sync runs either way.
    /// </summary>
    private void ActivateFocusedWindow()
    {
        if (_workspace.Layout.FocusedPane.ActiveTab is { } windowId && Activate(windowId))
        {
            return;
        }

        SyncToFocusedPane();
    }

    /// <summary>
    /// The rectangles <see cref="PaneNavigation"/> answers from: the ones the last frame actually arranged,
    /// so "the pane to my left" is the pane the user can see to their left. They already account for zoom —
    /// a zoomed workspace realises exactly one pane, so there is correctly nothing to move to.
    /// <para>
    /// Before the first frame nothing is realised, so it falls back to solving the tree over a nominal
    /// area. The fallback answers the same <em>ordering</em> questions (the tree is the same and the split
    /// fractions are the same), and it exists so a key that arrives before a paint is not silently a
    /// refusal.
    /// </para>
    /// </summary>
    private IReadOnlyDictionary<string, PaneRect> FocusRects()
    {
        var realised = RealisedPanes();
        if (realised.Count >= _workspace.Layout.Panes.Count)
        {
            return realised.ToDictionary(p => p.PaneId, p => p.Rect, StringComparer.Ordinal);
        }

        return LayoutSolver.Solve(
            _workspace.Layout.Root, new PaneRect(0, 0, 240, 64), _workspace.Layout.ZoomedPaneId);
    }

    /// <summary>
    /// Says on the status row that there is nothing that way. The reporting is the requirement, not a
    /// nicety: on a single-pane workspace every one of these keys is a legitimate no-op, and a keystroke
    /// that changes nothing and says nothing is indistinguishable from one that is not bound — which is
    /// exactly how the pane prefix read from the outside before <see cref="RefusePrefix"/> existed.
    /// </summary>
    private void RefuseFocusMove(PaneDirection direction)
    {
        var arrow = direction switch
        {
            PaneDirection.Left => "←",
            PaneDirection.Right => "→",
            PaneDirection.Up => "↑",
            _ => "↓",
        };

        var reason = _workspace.Layout.ZoomedPaneId is not null
            ? "this pane is zoomed — ⌃B z shows the others"
            : _workspace.Layout.Panes.Count <= 1
                ? "the workspace has one pane — ⌃B | and ⌃B - split it"
                : direction == PaneDirection.Down
                    ? "nothing below — the command line already has the keyboard"
                    : "no pane that way";

        Notice(reason, MessageSeverity.Warning, $"⌃{arrow}");
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
        //
        // The *number* is the same hazard and was not guarded: it counts lines below the viewport, which
        // grows unbidden from the wire while a reader sits in their scrollback, so the segment widened at
        // 9 → 10 and again at 99 → 100 with nobody touching a key. At 80 columns that second step took the
        // row from 80 cells to 81, it wrapped, and every pane lost a row — which per-pane NAWS then
        // re-announced to every connected server, reflowing the game's output. It is now written into a
        // reserved field capped the way the sidebar's unread badge is (RailRenderer.UnreadField), so the
        // segment is the same width whatever it says.
        var below = Math.Max(1, panel.TotalContentHeight - panel.ViewportHeight - panel.VerticalScrollOffset);
        var distance = UnreadBadge.Format(below).PadLeft(UnreadBadge.FieldWidth);
        return $"[#e5c07b]{Glyphs.Scrollback} scrollback[/] [dim]{distance} · ⌃End live[/]";
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
            ? StatusBarMarkup(id.Character, id.State)
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
        var label = PromptLabel();
        _input.Prompt = PromptMarkup(label, _input.Armed);
        _second.Prompt = PromptMarkup(SecondPromptLabel(label), _second.Armed);
        RefreshStatusBar();
    }

    /// <summary>
    /// What the command line calls itself: <b>the character ⏎ will reach</b>, and nothing else. It reads
    /// <see cref="SendTarget"/> rather than <c>_active</c>, because those two differ in exactly one state
    /// and it is the state that matters — a focused pane whose window has no session, where the prompt
    /// used to go on naming the world you had navigated away from while your eyes were on another pane.
    /// <para>
    /// Three answers. A window with a session names it. A window with none, on a client that has one
    /// somewhere, says <c>no connection</c>: the honest thing, since ⏎ from here sends nowhere
    /// (<see cref="NothingToSendTo"/>), and naming the other world would be a lie the moment the send
    /// started refusing. A client with nothing open at all keeps the resting prompt it has always had —
    /// there is no other world for a keystroke to reach, so there is nothing to warn about, and this is
    /// the arm the snapshot demo (owners, no live sessions) renders through.
    /// </para>
    /// </summary>
    private string PromptLabel()
    {
        if (SendTarget() is { } target)
        {
            return StatusFormatter.CharacterPrompt(target.Character?.Name, target.World.Name);
        }

        if (_active is null)
        {
            var resting = ActiveWorld();
            return StatusFormatter.CharacterPrompt(resting?.Character, resting?.World.Name);
        }

        return "no connection › ";
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
    /// band. It reads the tone out of <see cref="WorkspacePalette"/> rather than restating it: these two
    /// were a pair of hardcoded hexes carrying a "keep in sync with <see cref="SetUpBar"/>" comment,
    /// which is a colour the theme should have owned and two places to forget.
    /// </summary>
    private string InputBandHex => Hex(WorkspacePalette.ArmedBand(_theme));

    /// <summary>The band behind the bar ⏎ will not send from.</summary>
    private string IdleBandHex => Hex(WorkspacePalette.IdleBand(_theme));

    /// <summary>
    /// Wraps a prompt label so its cells carry the band background and its styling says whether this is
    /// the bar ⏎ sends from. The bar already fills its row with the same colour, so painting the label to
    /// match makes the whole row a continuous band with no gap at the prompt. Brackets in names are
    /// escaped to block injection.
    /// <para>
    /// The armed prompt is <b>bold</b> and the idle one dim — the second cue the band colour is not asked
    /// to carry on its own: weight still reads on a terminal theme that flattens two dark backgrounds
    /// together, and to someone who cannot tell the two hues apart. Both spellings are the same
    /// <em>width</em>, which is not a detail: the prompt's width is what the caret column and the wrap
    /// column are measured from, so a marker glyph that appeared only on the armed bar would reflow the
    /// line being typed every time the other bar took over.
    /// </para>
    /// </summary>
    private string PromptMarkup(string prompt, bool armed = true)
    {
        var text = prompt.Replace("[", "[[").Replace("]", "]]");
        return armed
            ? $"[bold on {InputBandHex}]{text}[/]"
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

        SetStatus(StatusBarMarkup(id.Character, id.State));
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
        // "password" and "startup" are further --view names for the same character-pane screen, the way
        // F2 carries four: the screen is identical, only the -edit script differs, and each names a
        // state a still frame is the only way to look at — a masked buffer mid-edit, and the two-entry
        // dropdown over `at start`, which is drawn *downward* over the rows beneath it.
        new(ConsoleKey.F9, "Character logging", new[] { "logging", "password", "startup" }, CharacterLoggingScreen),
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

            // Every claimed chord except ⌃B itself cancels an armed prefix before it runs. A global
            // shortcut runs ahead of any window, so one pressed while ⌃B was pending used to open its
            // surface and leave the prefix armed with nothing able to consume it — the next key after that
            // surface closed was eaten as a pane command, and `x` closes a window. ⌃B is excluded because
            // it is the toggle: ArmPrefix already answers a second press by disarming.
            if (claim.Modifiers != ConsoleModifiers.Control || claim.Key != ConsoleKey.B)
            {
                var claimed = action;
                action = () => { DisarmPrefix(); return claimed(); };
            }

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

        if (claim.Modifiers == ConsoleModifiers.Alt)
        {
            // Alt+R reconnects. ⌃R is the readline history chord and was already spent, and this is the
            // nearest spelling of the word left: ESC + a *printable* byte is decoded as one Alt chord
            // (AnsiInputParser.ProcessEscape), so unlike most of the alphabet's Ctrl chords it genuinely
            // arrives. A lone Escape is flushed on its own after UnixStdinReader's 50 ms timeout, so
            // pressing Esc and later typing an r is two keys and not this one.
            if (claim.Key == ConsoleKey.R)
            {
                return () => { Reconnect(); return true; };
            }

            // ⌥1–⌥9 go to the numbered pane. Same delivery story as Alt+R and one digit over: the
            // terminal writes ESC + the digit and the parser reads it as that digit with Alt set.
            if (MacroKeys.PaneJumpNumber(claim.Key) is { } number)
            {
                return () => { JumpToPane(number); return true; };
            }

            return null;
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
            // ⌃D is the idiomatic disconnect/EOF chord, and this client spends it on exactly that: it
            // drops the focused character's connection at once. It deliberately does *not* end the client
            // — that is ⌃Q, which asks first — so the shell reflex it borrows lands on the smaller of the
            // two meanings. With nothing connected it says so and does nothing at all.
            ConsoleKey.D => () => { Disconnect(); return true; },
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

        // An open settings screen is deliberately not among the facts. It used to contribute "F5 is open
        // — 3 unsaved edits", which was true while closing a screen could throw its edits away; now every
        // committed value is already written to disk when it is committed, so the count is always zero and a
        // line that could never appear is a line to delete rather than to leave hanging.
        return new QuitFacts(ConnectedCharacters(), drafts, holding.Select(w => w.Title).ToList());
    }

    /// <summary>
    /// What is connected right now, by <see cref="WorldSession.SessionKey"/> — <c>world.character</c>, or
    /// the world's own name for an anonymous connection. <strong>The one derivation</strong> the header's
    /// fraction, the rail's connected dots and the quit prompt's consequence line all read, so the three
    /// cannot disagree about what is connected.
    /// <para>
    /// They did. The header counted a stale set of keys maintained only for the active session and divided
    /// it by the number of configured <em>worlds</em> — two connected characters across three worlds read
    /// <c>2/3</c>. The quit prompt counted the same connections but reduced them to distinct world names,
    /// so two characters on one world read "1 world connected" while the user was looking at two. Both
    /// halves of a fraction and both ends of a warning now count the same thing.
    /// </para>
    /// <para>
    /// The unit is the <em>character</em>, because in this client a connection <em>is</em> a character:
    /// F5 says so in as many words, you connect as a character rather than as a world, and a world with
    /// two characters logged in is two things a quit would drop. Live sessions are the truth; the demo
    /// scene opens no sockets at all, so when there is no session to ask, the keys it declares
    /// (<see cref="_demoConnectedKeys"/>) answer instead.
    /// </para>
    /// </summary>
    internal IReadOnlyList<string> ConnectedCharacters()
    {
        var sessions = _sessions.Sessions;
        if (sessions.Count == 0)
        {
            return _demoConnectedKeys.ToList();
        }

        return sessions
            .Where(s => s.IsConnected)
            .Select(s => s.SessionKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// How many connections the configuration offers — the denominator of the header's fraction, in the
    /// same unit as <see cref="ConnectedCharacters"/>. A world with characters offers one per character; a
    /// world with none still offers one, because it can be connected to anonymously (what a host typed on
    /// the command line is), and a fraction whose denominator ignored it could be exceeded by its own
    /// numerator.
    /// </summary>
    private int ConfiguredConnections() => _config.Worlds.Sum(w => Math.Max(1, w.Characters.Count));

    /// <summary>
    /// Re-resolves every live session's trigger sets and hands them to it, so a rule added on F2, an alias
    /// on F3, a binding on F4 or a set assigned to a character on F5 reaches the connection that is
    /// already open. Called from <see cref="SaveConfiguration"/>, which is the single funnel every
    /// settings screen commits through (<see cref="ScreenEdits"/>) — one hook rather than one per screen,
    /// because "every path remembered to call it" is the assumption this client has already been bitten by.
    /// <para>
    /// This is the fix for the reported bug: <c>WorldSession</c> composed its engines from the sets it was
    /// handed at construction, so a session opened before its character had the capture set assigned ran an
    /// empty trigger engine for its whole life, no line ever matched, and no spawn window ever appeared —
    /// with nothing anywhere in the client saying so. The alternative, having the engines read through to
    /// the configuration on every line, is rejected in <see cref="TriggerEngine.ReplaceConfigured"/>: the
    /// engine runs on the telnet read loop and these lists are mutated on the UI thread.
    /// </para>
    /// <para>
    /// A reload that changes <em>what is live</em> says so on the status row and in the ⌃P log. An edit that
    /// only retypes a pattern changes no count and is silent, because the rule was already live — that is
    /// the case <see cref="Trigger.Pattern"/> handles by dropping its compiled regex.
    /// </para>
    /// </summary>
    private void ReloadAutomation()
    {
        foreach (var session in _sessions.Sessions)
        {
            if (session.Character is not { } character)
            {
                continue; // an anonymous connection has no character, so nothing resolves for it
            }

            var before = session.Triggers.Triggers.Count
                + session.Aliases.Aliases.Count
                + session.Macros.Macros.Count;

            var sets = _config.ResolveTriggerSets(character);
            session.ReloadAutomation(sets);

            var after = session.Triggers.Triggers.Count
                + session.Aliases.Aliases.Count
                + session.Macros.Macros.Count;
            if (before != after)
            {
                Notice(
                    $"{SessionTitle(session)}: {TriggerReport.Summary(SessionTitle(session), character.TriggerSets, sets)}",
                    MessageSeverity.Info);
            }
        }
    }

    /// <summary>
    /// Persists the configuration the settings screens edit. It runs after <em>every</em> change a screen
    /// commits, not on the way out of one: see <see cref="ScreenEdits"/> for why that is where saving
    /// belongs now. The workspace layout is captured alongside it so a save never rolls back the resumed
    /// session; a failed write is swallowed for the same reason startup's is (the config is a
    /// convenience, not the session).
    /// <para>
    /// An app with no <c>save</c> writes nothing at all — see the constructor. The live bars and the live
    /// sessions' automation are still re-synced, because those are the change <em>taking effect</em> and
    /// not a record of it.
    /// </para>
    /// </summary>
    internal void SaveConfiguration()
    {
        // F8 edits the live InputSettings, so a committed height applies to the bars immediately rather
        // than at the next launch.
        SyncInputBars();

        // And F2/F3/F4/F5 edit the trigger sets, so a rule added or a set assigned applies to the next
        // line rather than the next reconnect. See ReloadAutomation.
        ReloadAutomation();

        // F5 also adds and removes worlds and characters, which are what the header's fraction and the
        // rail are drawn from. Neither is repainted by anything else here, so a world added on F5 used to
        // leave both reading the configuration as it was before.
        RefreshHeader();
        RefreshRail();

        PersistConfiguration();
    }

    /// <summary>
    /// Writes the configuration out, with the workspace layout captured alongside it so a save never
    /// rolls back the resumed session. The <em>write</em> half of <see cref="SaveConfiguration"/> and
    /// nothing else.
    /// <para>
    /// It is separate because the other half belongs to the settings screens.
    /// <see cref="ReloadAutomation"/> re-resolves and re-hands every live session its trigger sets,
    /// which re-periodises running timers and so resets every other timer's phase — right after an F2
    /// edit, and wrong after a view preference that has nothing to do with automation. A caller that
    /// changed only a preference (<see cref="SetTimestamps"/>) persists through here.
    /// </para>
    /// <para>
    /// An app with no <c>save</c> writes nothing at all — see the constructor, and
    /// <c>CommandSurfaceSettingsTests.AnAppWithNoSaveActionPersistsNothing</c>. A failed write is
    /// swallowed onto the status row for the same reason startup's is: the config is a convenience,
    /// not the session.
    /// </para>
    /// </summary>
    private void PersistConfiguration()
    {
        if (_save is null)
        {
            return;
        }

        try
        {
            _config.LastSession = CaptureSession();
            _save(_config);
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
    /// The format the header's <c>LOG</c> cell reports — the active character's, or
    /// <see cref="LogFormat.None"/> whenever this app owns no log root, because then no character's
    /// output is being written whatever its settings say.
    /// <para>
    /// The same reasoning <see cref="WorldSession.CurrentEncoding"/> is built on: a configured value is a
    /// <em>preference</em>, and a status cell that reports one as though it were in force is a claim the
    /// client cannot back. <c>LOG html</c> over a snapshot or a test run — which is where every logless app
    /// is — said exactly that, next to a directory the app was never given.
    /// </para>
    /// </summary>
    private LogFormat HeaderLogFormat() => _logRoot is null ? LogFormat.None : ActiveLogging().Format;

    /// <summary>
    /// Opens the F5 Worlds &amp; Characters screen: four panes (worlds → characters → the selected
    /// character's trigger sets → the selected world's security checkboxes), seeded on whatever is
    /// connected so the screen opens where the user already is.
    /// </summary>
    /// <summary>
    /// Files one server's MSSP report under the world it came from. The one writer: the live
    /// subscription in <see cref="BindSession"/> and the snapshot's demo report both go through it, so
    /// the endpoint a frame is rendered from and the endpoint a connection files under cannot be
    /// different strings — the gap that has hidden three separate bugs in the demo scene already.
    /// </summary>
    internal void CaptureMssp(WorldDefinition world, MsspData report)
    {
        ArgumentNullException.ThrowIfNull(world);
        _mssp.RecordReport(world.Host, world.Port, report, _time.GetUtcNow());
    }

    /// <summary>
    /// Opens the read-only MSSP report for a world, over the screen that asked for it. Bounds-checked
    /// against the live list rather than trusted, because the index was captured when the WORLDS pane's
    /// buttons were built and a keystroke between then and now could have deleted the row.
    /// </summary>
    private void OpenMsspScreen(int world)
    {
        if (world < 0 || world >= _config.Worlds.Count)
        {
            return;
        }

        _settings.OpenDetail(MsspScreen(_config.Worlds[world]));
    }

    /// <summary>
    /// The MSSP report screen for one world. A full <see cref="ScreenBinding"/> like every other screen
    /// — the same session, the same key table, the same overlay — because that is what gives it
    /// scrolling, a cursor and headless key simulation for nothing. Its model offers no fields, no
    /// toggles and no removals, so the header derives none of those hints.
    /// </summary>
    private ScreenBinding MsspScreen(WorldDefinition world)
    {
        var observation = _mssp.Find(world.Host, world.Port);
        var session = new SettingsSession(_ => MsspScreenRenderer.Model(
            world, observation, _time.GetUtcNow(), _system.DesktopDimensions.Width));

        return new ScreenBinding(session, () => MsspScreenView.Build(
            world,
            observation,
            _time.GetUtcNow(),
            _system.DesktopDimensions.Width,
            session.Focus(),
            _system.DesktopDimensions.Height));
    }

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
            selection.SelectionIn(WorldsScreenRenderer.TriggerSetsPane),
            OpenMsspScreen),
            SaveConfiguration);
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
            _system.DesktopDimensions.Height,
            info: true));
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
            TriggersScreenRenderer.Model(_config.TriggerSets, selection.SelectionIn(0), SpawnTargets()),
            SaveConfiguration);

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
            AliasesScreenRenderer.Model(_config.TriggerSets, selection.SelectionIn(0)),
            SaveConfiguration);

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
            KeypadScreenRenderer.Model(Macros(), _config.TriggerSets, selection.SelectionIn(0)),
            SaveConfiguration);

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
            TimersScreenRenderer.Model(_config.TriggerSets, selection.SelectionIn(0)),
            SaveConfiguration);

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
        var session = new SettingsSession(_ => OptionsScreenRenderer.Model(screen()), SaveConfiguration);
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

        if (string.Equals(view, "password", StringComparison.OrdinalIgnoreCase))
        {
            // name → password, then type into it: the frame shows a masked buffer with the caret inside
            // it, which is the only state in this app where what is drawn is deliberately not what is
            // held. Demo config has no password to reveal, so the typed value is the whole secret in the
            // frame — and it still comes out as dots.
            yield return Stroke('\t', ConsoleKey.Tab);
            foreach (var c in "hunter2")
            {
                yield return Stroke(c, ConsoleKey.NoName);
            }

            yield break;
        }

        if (string.Equals(view, "startup", StringComparison.OrdinalIgnoreCase))
        {
            // name → … → at start. Its list has two entries and it is not the last row of the block, so
            // this is the frame that shows a dropdown drawn *downward* over the rows under it — the
            // opposite of the `logging` view's, which has nowhere below to go and opens upward.
            for (var i = 0; i < WorldsScreenRenderer.StartupField; i++)
            {
                yield return Stroke('\t', ConsoleKey.Tab);
            }

            yield break;
        }

        if (string.Equals(view, "logging", StringComparison.OrdinalIgnoreCase))
        {
            // name → password → connect → on connect → log: the character row's fields, in order. The
            // two new ones sit between the name and the on-connect line, so this walk grew with them
            // rather than the log format quietly becoming a different field.
            for (var i = 0; i < WorldsScreenRenderer.LogFormatField; i++)
            {
                yield return Stroke('\t', ConsoleKey.Tab);
            }

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

    private static ConsoleKeyInfo Stroke(char c, ConsoleKey key, bool ctrl = false, bool alt = false) =>
        new(c, key, shift: false, alt, ctrl);

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

        // The same set the header's fraction counts, so a dot and the count cannot disagree.
        var connected = new HashSet<string>(ConnectedCharacters(), StringComparer.Ordinal);

        // Where each window is, for the window rows' second column — and only when there is more than one
        // answer. On a single-pane workspace a window can only be in the one pane, so the column says
        // nothing; it also used to call that pane "main", which collided head-on with the *window* named
        // "main" in the column beside it (`▪ main   main`: two different meanings wearing one word). Every
        // pane is now spelt "pane N", which a window title cannot be mistaken for.
        var panes = _workspace.Layout.Panes;
        var paneLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (panes.Count > 1)
        {
            for (var i = 0; i < panes.Count; i++)
            {
                // Through PaneOrdinal so the sidebar, the move/drag overlays, the ⌃P entries and the ⌥N
                // chord are all reading one number. They were two expressions and they disagreed about
                // the first pane.
                paneLabels[panes[i].Id] = RailPaneLabel(i + 1);
            }
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
                var windows = active ? BuildRailWindows(key, paneLabels) : Array.Empty<RailWindow>();
                characters.Add(new RailCharacter(
                    character.Name,
                    key,
                    Connected: connected.Contains(key),
                    Active: active,
                    Unread: windows.Sum(w => w.Unread),
                    windows,
                    Pane: CharacterPaneLabel(key, paneLabels)));
            }

            worlds.Add(new RailWorld(world.Name, world.Host, world.Port, accent, characters));
            index++;
        }

        return RailModel.Build(worlds);
    }

    /// <summary>
    /// The windows to list under <paramref name="owner"/>, in registration order: that character's own
    /// windows, plus the ones that belong to nobody.
    /// <para>
    /// The owner test is the point. Every window a session or a trigger opens carries its owner
    /// (<see cref="OpenSessionWindow"/>, and <c>RouteSpawn(target, _active?.SessionKey)</c>), so a
    /// window with no owner is an auxiliary like the web view — global, reachable from wherever you
    /// are, and listed here because the rail is the only place you can click back to it. Without the
    /// test this returned <em>every</em> window in the workspace, so the rail drew one character's tabs
    /// nested under another: invisible while a single session was all the client could open, and wrong
    /// from the moment switching characters started giving each one its own window.
    /// </para>
    /// </summary>
    private IReadOnlyList<RailWindow> BuildRailWindows(
        string owner, IReadOnlyDictionary<string, string> paneLabels)
    {
        var windows = new List<RailWindow>();
        foreach (var window in _workspace.Windows)
        {
            var mine = string.Equals(window.SessionKey, owner, StringComparison.Ordinal);
            if (window.SessionKey is not null && !mine)
            {
                continue;
            }

            var pane = _workspace.Layout.FindWindow(window.Id);
            var label = pane is not null ? paneLabels.GetValueOrDefault(pane.Id) : null;
            windows.Add(new RailWindow(
                RailWindowLabel(window, mine),
                window.Id,
                label,
                window.Unread,
                window.HasUnsentInput,
                Closed: pane is null));
        }

        return windows;
    }

    /// <summary>
    /// Which pane a character's session is in — the <c>pane N</c> its rail row carries, whether or not
    /// it is the active character.
    /// <para>
    /// <b>This is what makes ⌥N usable as a character switch.</b> The chord has always been global
    /// (<see cref="JumpToPane"/> indexes the workspace's one pane tree, not the active character's
    /// windows), but the rail lists window rows for the active character only — so a reader looking at
    /// Ann could see <c>pane 1</c> and nothing else, while ⌥2 and ⌥3 sat on the screen holding Bob and
    /// Cal. The pane number was global; only the way to read it was not.
    /// </para>
    /// <para>
    /// The <em>character</em> row rather than more window rows, because <see cref="BuildRailWindows"/>'s
    /// owner filter is load-bearing: a window row under a character means that window is that
    /// character's, and listing everyone's windows everywhere would take that reading away for the sake
    /// of a fact one column can carry. One row per character already exists, it is exactly the row a
    /// user clicks to reach that character, and the answer belongs on it.
    /// </para>
    /// <para>
    /// The session window when there is one, else any window the character owns that a pane still holds
    /// — a character with a spawn window open and its main window closed is still somewhere, and the row
    /// should say where rather than go blank. Null when the workspace has one pane, because
    /// <paramref name="paneLabels"/> is empty then and "which of the one pane" is not information.
    /// </para>
    /// </summary>
    /// <summary>
    /// What the sidebar's hosting column calls pane <paramref name="ordinal"/>: the chord that goes there,
    /// <c>⌥3</c>, rather than the words <c>pane 3</c>.
    /// <para>
    /// The sidebar's width comes out of the pane area and is reported to every connected session over
    /// NAWS, so four cells on every row is four cells off every pane. This is the one surface where the
    /// noun is redundant — the column's position already says "where this is" — and dropping it pays for
    /// itself twice: it is shorter, and <c>⌥3</c> names the key that goes there, which <c>pane 3</c> left
    /// the reader to infer.
    /// </para>
    /// <para>
    /// It is not a second spelling of the number. <see cref="PaneLabel"/> still says <c>pane N</c>
    /// everywhere the noun carries meaning — <c>split pane 2 left</c>, <c>Go to pane 3</c>, <c>there is no
    /// pane 7</c> — and both read the same ordinal. What changed is the abbreviation, not the count.
    /// </para>
    /// <para>
    /// The sigil is also what keeps the column legible beside the unread badge. A bare <c>3</c> after a
    /// count of <c>2</c> is <c>2  3</c>, two numbers with nothing to tell them apart; the word used to do
    /// that work, and something has to.
    /// </para>
    /// </summary>
    private static string RailPaneLabel(int ordinal) => $"⌥{ordinal}";

    private string? CharacterPaneLabel(string sessionKey, IReadOnlyDictionary<string, string> paneLabels)
    {
        if (paneLabels.Count == 0)
        {
            return null;
        }

        string? fallback = null;
        foreach (var window in _workspace.Windows)
        {
            if (!string.Equals(window.SessionKey, sessionKey, StringComparison.Ordinal) ||
                _workspace.Layout.FindWindow(window.Id) is not { } pane ||
                paneLabels.GetValueOrDefault(pane.Id) is not { } label)
            {
                continue;
            }

            if (window.Kind == WindowKind.Main)
            {
                return label;
            }

            fallback ??= label;
        }

        return fallback;
    }

    /// <summary>
    /// What a window row is called in the rail. A character's <em>own</em> session window reads
    /// <c>main</c>; everything else keeps its title (a spawn target's name, the web page's title).
    /// <para>
    /// A window's <see cref="WorkspaceWindow.Title"/> names its <em>connection</em> — the world, or the
    /// character for a second session — because the tab strip has no other context to identify it by. The
    /// rail does: a window row sits under its character, which sits under its world, so repeating either
    /// there says nothing. It was repeating the world (<c>Convergence MUSH ▸ Mannaz ▸ Convergence
    /// MUSH</c>), which is what got reported — and in a narrow sidebar it wrapped, which is what made it
    /// look broken rather than merely redundant.
    /// </para>
    /// <para>
    /// "main" rather than putting it beside the character, of the two shapes offered: one rail row is one
    /// destination you can click, and folding a window into the character row would make that row
    /// sometimes a character and sometimes also a window, while its siblings (<c>Chat</c>) stayed rows of
    /// their own. Naming it is also the shorter label, and the rail's width is its widest row.
    /// </para>
    /// </summary>
    /// <param name="mine">Whether this window belongs to the character whose subtree it is being listed
    /// under. An unowned window (the web view) is nobody's main window, whatever its kind.</param>
    private static string RailWindowLabel(WorkspaceWindow window, bool mine) =>
        mine && window.Kind == WindowKind.Main ? MainWindowRailLabel : window.Title;

    /// <summary>What a character's own session window is called in the rail. See <see cref="RailWindowLabel"/>.</summary>
    private const string MainWindowRailLabel = "main";

    /// <summary>Repaints the rail from current state, and resizes its column to fit what it now says.</summary>
    private void RefreshRail()
    {
        var lines = RenderRailLines();
        _rail.SetContent(lines);
        ApplyRailWidth(RailWidth(lines));
    }

    /// <summary>
    /// The rail's markup as it currently stands, one string per row. Internal so a test can read the
    /// click targets out of the rail the app actually draws rather than re-deriving them: the rows are
    /// rebuilt on every refresh, and a payload that came from anywhere but the live model would go
    /// stale the moment a world connected or a window closed.
    /// </summary>
    internal IReadOnlyList<string> RailLines => RenderRailLines();

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
            TimestampsOn: ShowTimestamps,
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

    /// <summary>Whether the ⌃P command surface is up — the header's ☰ opens it, and only the header may.</summary>
    internal bool MenuIsOpen => _palette.IsOpen;

    /// <summary>
    /// Clicks the connection rail at a cell measured from the rail's own top-left, the way the framework
    /// delivers a click to a control (<c>MouseEventArgs.Position</c> is control-relative). Everything
    /// past this point is real: the link hit-test against the geometry the last paint recorded, the
    /// control's <c>LinkClicked</c>, and the focus the framework takes on the way through.
    /// <para>
    /// It exists because <c>ConsoleWindowSystem</c> only subscribes to the driver's mouse stream inside
    /// <c>Run()</c>, so a headless test cannot reach a control by clicking the <em>desktop</em> — the one
    /// link in the chain these tests cannot cover, exactly as with the pane-drag suite. Requires a
    /// rendered frame first: the rail's rows have no hit-test geometry until they have been painted.
    /// </para>
    /// </summary>
    internal bool SimulateRailClick(int x, int y)
    {
        var local = new System.Drawing.Point(x, y);
        var onWindow = new System.Drawing.Point(_rail.ActualX + x, _rail.ActualY + y);
        return _rail.ProcessMouseEvent(new MouseEventArgs(
            new List<MouseFlags> { MouseFlags.Button1Clicked }, local, onWindow, onWindow, _window));
    }

    /// <summary>
    /// Clicks a window's output pane at a cell measured from that control's own top-left, the same way
    /// <see cref="SimulateRailClick"/> clicks the rail — real hit-test geometry from the last paint, the
    /// control's own <c>LinkClicked</c>, and so the real <see cref="OnLinkClicked"/> with the real window
    /// id. It is the only way to drive a server-supplied link the whole distance (MXP bytes → parser →
    /// <see cref="MarkupFormatter"/> → the framework's markup parse and unescape → the handler), which is
    /// exactly the chain the link-forgery defect lived in.
    /// </summary>
    internal bool SimulatePaneClick(string windowId, int x, int y)
    {
        if (!_panes.TryGetValue(windowId, out var pane))
        {
            return false;
        }

        var local = new System.Drawing.Point(x, y);
        var onWindow = new System.Drawing.Point(pane.ActualX + x, pane.ActualY + y);
        return pane.ProcessMouseEvent(new MouseEventArgs(
            new List<MouseFlags> { MouseFlags.Button1Clicked }, local, onWindow, onWindow, _window));
    }

    /// <summary>
    /// The markup a window's output pane currently holds, one string per row. Internal so a test can read
    /// a link payload off the pane the app really drew instead of writing the expected one down — the
    /// payload is the thing under test, and a hand-copied one would pass whatever the formatter emitted.
    /// </summary>
    internal IReadOnlyList<string> PaneLines(string windowId) =>
        _panes.TryGetValue(windowId, out var pane)
            ? pane.Text.Split('\n')
            : Array.Empty<string>();

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

        if (id.StartsWith(CommandIds.WindowPrefix, StringComparison.Ordinal))
        {
            var windowId = id[CommandIds.WindowPrefix.Length..];
            if (!Activate(windowId))
            {
                RefuseCommand($"{windowId} is not open any more");
            }

            return true;
        }

        // A numbered pane entry runs the chord's own action, refusal and all — the surface is another
        // door onto ⌥N, not a second way of focusing a pane. Parsed rather than switched on because the
        // catalog emits one id per live pane and there is no fixed set of them.
        if (id.StartsWith(CommandIds.PanePrefix, StringComparison.Ordinal))
        {
            if (int.TryParse(
                    id[CommandIds.PanePrefix.Length..],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var paneNumber))
            {
                JumpToPane(paneNumber);
                return true;
            }

            RefuseCommand($"{id} does not name a pane number");
            return false;
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
            case "layout:focus-left":
                MoveFocus(PaneDirection.Left); // reports when there is no pane that way
                return true;
            case "layout:focus-right":
                MoveFocus(PaneDirection.Right);
                return true;
            case "layout:focus-up":
                MoveFocus(PaneDirection.Up);
                return true;
            case "layout:focus-down":
                MoveFocus(PaneDirection.Down);
                return true;
            case "layout:wider":
                ResizePane(PaneDirection.Right); // reports when there is no border to move that way
                return true;
            case "layout:narrower":
                ResizePane(PaneDirection.Left);
                return true;
            case "layout:taller":
                ResizePane(PaneDirection.Up); // ↑ is bigger and ↓ is smaller — the pane's size, not the divider's way
                return true;
            case "layout:shorter":
                ResizePane(PaneDirection.Down);
                return true;
            case "layout:cycle":
                if (_workspace.Layout.Panes.Count <= 1)
                {
                    RefuseCommand("nowhere to cycle to — the workspace has one pane");
                    return true;
                }

                CyclePane();
                return true;
            case "term:newline":
                // The same edit Alt+⏎ makes, through the same key table, so the surface cannot drift from
                // the chord it advertises.
                RouteToInput(new ConsoleKeyInfo(
                    '\r', ConsoleKey.Enter, shift: false, alt: true, control: false));
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
            case "term:restore-purge":
                PurgeRestoreLog();
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
                SetTimestamps(true);
                break;
            case "term:timestamps-off":
                SetTimestamps(false);
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

    /// <summary>
    /// The command id prefix carrying a character's <c>world.character</c> session key. Read from
    /// <see cref="CommandIds"/> rather than spelt again here: the catalog that offers these ids, the
    /// rail that now also names them, and this switch that implements them are three places that
    /// otherwise drift in silence — which is the exact way <c>char:</c> came to be offered and
    /// implemented nowhere.
    /// </summary>
    private const string CharacterCommandPrefix = CommandIds.CharacterPrefix;

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
    /// <remarks>
    /// <b>It acts at once</b>, from <c>Alt+R</c> as from the ⌃P entry. There is no confirmation in front
    /// of it: the client asks before ending itself (⌃Q) and before nothing else, and a prompt in front of
    /// the key you press <em>because</em> a connection has gone wrong is friction at the worst moment.
    /// </remarks>
    private void Reconnect()
    {
        // Which connection this acts on is decided by the window in front of you — SendTarget, the same
        // resolver ⏎ and a link click go through, and never _active. Nothing asks before either of these
        // commands acts, so an arm that guessed would drop the wrong world's connection on one keystroke
        // and say nothing about it; a window that belongs to no connection refuses out loud instead.
        if (SendTarget() is not { } session)
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
        if (SendTarget() is not { } session) // the focused window's connection — see Reconnect
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
    /// <para>
    /// An app with no log root refuses out loud, first and before anything else is looked at, because that
    /// is an app-wide fact and true whatever is connected. Refusing is the only honest answer available:
    /// doing nothing quietly would leave the entry looking like it worked, and printing
    /// <c>*** Logging to …</c> over a client that opened no file is the defect this gate exists to stop.
    /// </para>
    /// </summary>
    /// <summary>
    /// ⌃P ▸ <c>Purge the restore log</c>: deletes every window's saved content, now.
    /// <para>
    /// It is "forget what is on disk", not "switch the feature off" — a pane that goes on printing
    /// starts a fresh file on its next line, and the standing preference is F9's per-character
    /// <c>restore</c> row. Nor does it blank the panes: what has already been drawn is on your screen
    /// and clearing it would be answering a question nobody asked, while leaving it would be leaving a
    /// file. The count comes back so the entry reports what it actually did rather than claiming
    /// success at nothing, which is the difference between a purge and a placebo.
    /// </para>
    /// </summary>
    private void PurgeRestoreLog()
    {
        if (_restore is null)
        {
            RefuseCommand("nothing to purge — this client owns no restore log");
            return;
        }

        var removed = _restore.Purge();
        Notice(
            removed == 0
                ? "restore log purged — there was nothing saved"
                : $"restore log purged — {removed} window{(removed == 1 ? string.Empty : "s")} forgotten",
            MessageSeverity.Info,
            "⌃P");
    }

    private void StartLogging()
    {
        if (_logRoot is null)
        {
            RefuseCommand("nothing to log to — this client owns no log directory");
            return;
        }

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

    /// <summary>
    /// The link handler for the app's own chrome — today just the header's <c>☰</c> button. It is not
    /// <see cref="OnLinkClicked"/>: that handler serves the output panes, whose content is written by
    /// the <em>server</em>, and while the menu lived in its namespace any world could open this
    /// client's command surface by sending <c>&lt;a href="sharpmuterm-menu:toggle"&gt;</c> in MXP.
    /// Nothing painted by a world reaches this method, because no control a world writes into is wired
    /// to it. See <see cref="DispatchRailTarget"/> for the same boundary drawn around the rail.
    /// </summary>
    private void OnChromeLinkClicked(string url)
    {
        if (url.StartsWith(MenuScheme, StringComparison.Ordinal))
        {
            ToggleMenu();
            return;
        }

        // Anything else in the chrome is an ordinary link, attributed to the window on show: the header
        // is not a world's output, so the active window is the only owner it can honestly claim.
        OnLinkClicked(ActiveWindowId(), url);
    }

    /// <summary>
    /// What a click on the connection rail does: switch to the character, window or world the clicked
    /// row names, or say why it cannot. Returns whether the target was understood; internal so a test
    /// can drive it the way a click does, and so the suite can prove every target
    /// <see cref="RailModel"/> can emit is handled here.
    /// <para>
    /// <strong>This is deliberately not <see cref="OnLinkClicked"/>, and must not be merged into
    /// it.</strong> That handler serves the output panes, which render MXP/Pueblo links a world sends
    /// over the wire — a server can legitimately put any <c>[link=…]</c> it likes in front of you
    /// there. If rail targets shared that namespace, a hostile or careless world could emit a link
    /// carrying <c>char:</c> or <c>win:</c> and drive this client's UI from the wire, and the blast
    /// radius would grow with every command scheme added afterwards. Because <c>_rail</c> is its own
    /// <see cref="MarkupControl"/> with its own <c>LinkClicked</c>, the trust boundary is the
    /// <em>control</em> rather than a URL-scheme convention: servers cannot write into the rail, so
    /// they cannot reach this. <c>RailClickTests</c> pins both halves.
    /// </para>
    /// </summary>
    internal bool DispatchRailTarget(string url)
    {
        // A world with nothing to switch to. There is no command for this and there should not be one;
        // the rail asks and the rail answers, on the status row, recoverably (⌃P).
        if (url.StartsWith(RailModel.NoCharactersPrefix, StringComparison.Ordinal))
        {
            var world = url[RailModel.NoCharactersPrefix.Length..];
            Notice($"{world} has no characters yet — F5 adds one", MessageSeverity.Warning, "F5");
            return true;
        }

        // Everything else the rail can name is a command the ⌃P surface already dispatches. Reusing
        // DispatchCommand is the point: the rail is another door onto those actions, not a second
        // implementation of switching that can drift away from them.
        if (url.StartsWith(CommandIds.CharacterPrefix, StringComparison.Ordinal) ||
            url.StartsWith(CommandIds.WindowPrefix, StringComparison.Ordinal))
        {
            return DispatchCommand(url);
        }

        RefuseCommand($"the rail offered {url} and nothing here handles it");
        return false;
    }

    /// <summary>
    /// The link handler for the <em>output panes</em> and the web view — everything whose content can
    /// come from a server. Internal only so <c>RailClickTests</c> can hand it a rail payload and prove
    /// it does not act on one; nothing outside this class calls it.
    /// <para>
    /// <paramref name="windowId"/> is the window whose control was clicked, and it is why this takes a
    /// parameter at all. Every pane, spawn window and frozen region shared one closure that acted on
    /// <c>_active</c>, so a link clicked in a <em>background</em> spawn window sent to whichever
    /// character happened to be focused — a command delivered to the wrong world. The owning session is
    /// resolved through <see cref="WindowSession"/> instead, and a window that belongs to no world sends
    /// nowhere rather than guessing.
    /// </para>
    /// <para>
    /// Which payloads mean what is <see cref="LinkPayload"/>'s business, and the schemes are disjoint by
    /// construction there — a world cannot make an <c>&lt;A HREF&gt;</c> arrive as a
    /// <c>mux:send:</c>. An untagged payload is refused out loud; it used to be opened as a URL, and
    /// that fallback is the other half of what made a forged scheme work.
    /// </para>
    /// </summary>
    internal void OnLinkClicked(string windowId, string url)
    {
        switch (LinkPayload.Parse(url))
        {
            case (LinkAction.Send, var command):
                if (WindowSession(windowId) is { } session)
                {
                    _ = session.SendRawAsync(command);
                }
                else
                {
                    RefuseCommand("that window belongs to no connection, so there is nowhere to send that link");
                }

                break;

            case (LinkAction.Prompt, var text):
                // The command line is shared, so the text has to land with the window the link lives in
                // showing — otherwise MXP's PROMPT fills in a command for a world you are not looking at.
                if (!string.Equals(windowId, ActiveWindowId(), StringComparison.Ordinal))
                {
                    Activate(windowId);
                }

                ActiveBar().SetAndNotify(text);
                break;

            case (LinkAction.Web, var target):
                OpenWeb(windowId, target);
                break;

            default:
                RefuseCommand($"that link carries no scheme this client writes ({Snippet(url)}) — nothing here handles it");
                break;
        }
    }

    /// <summary>
    /// The session a window belongs to: the session printing into it, or — for a spawn window, which is
    /// not a session's own output window — the character the workspace records as owning it. Null for a
    /// window that belongs to no connection (the web view), which is a refusal and never a reason to fall
    /// back on <c>_active</c>.
    /// <para>
    /// One rule, two callers, deliberately. <see cref="OnLinkClicked"/> asks it which world a click in a
    /// pane acts on, and <see cref="AdoptSessionOf"/> asks it which character the command line talks to
    /// once a window becomes the active one. Both questions are "whose window is this?", and answering
    /// them differently is how a client ends up sending one pane's link to another pane's world.
    /// </para>
    /// </summary>
    private WorldSession? WindowSession(string windowId) =>
        SessionFor(windowId)
        ?? (_workspace.FindWindow(windowId)?.SessionKey is { Length: > 0 } key ? _sessions.Find(key) : null);

    /// <summary>A bounded piece of untrusted text, for a message that has to name what it refused.</summary>
    private static string Snippet(string text) =>
        text.Length <= 60 ? text : text[..60] + "…";

    /// <summary>
    /// A tab became the visible one — because it was clicked, or because ⌃N/⌃Tab moved along the strip.
    /// Routed through <see cref="Activate"/> like every other way of bringing a window forward: this is
    /// the most-used gesture of all, and it used to reload the drafts while leaving <c>_active</c> on the
    /// previous character, so clicking another world's tab showed you its output and sent your next line
    /// to the world you had left.
    /// </summary>
    private void OnTabChanged(string paneId, TabPage? newTab)
    {
        _workspace.Layout.Focus(paneId);
        if (newTab?.Tag is string id)
        {
            Activate(id);
        }
    }

    /// <summary>
    /// Opens a URL in the web view, reporting progress into the transcript of the world that
    /// <paramref name="windowId"/> belongs to — the window the link was clicked in, or the one
    /// <c>/web</c> was typed in — rather than whichever session is active by the time it resolves.
    /// <para>
    /// The scheme gate is <c>WebPageFetcher</c>'s and stays there: anything that is not
    /// <c>http</c>/<c>https</c> comes back as a rendered error page, so <c>file://</c> is refused by the
    /// one component that would otherwise read it. Nothing here routes around that.
    /// </para>
    /// </summary>
    private void OpenWeb(string windowId, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var owner = WindowSession(windowId) ?? _active;
        owner?.PrintSystem($"*** Opening {url} in the web view...");
        _ = LoadWebAsync(owner, url);
    }

    private async Task LoadWebAsync(WorldSession? owner, string url)
    {
        var width = Math.Max(20, _window.Width - 4);
        try
        {
            var page = await _fetcher.FetchAsync(url, width).ConfigureAwait(false);
            OnUi(() => ShowWeb(page));
        }
        catch (Exception ex)
        {
            OnUi(() => owner?.PrintSystem($"*** Failed to load {url}: {ex.Message}"));
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
    private void ShowDemoWebPage(string title = "The Cartographer's Study")
    {
        const string url = "https://sharpmuterm.invalid/room";
        var html =
            $"<html><head><title>{title}</title></head><body>" +
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
                        markup.LinkClicked += (_, e) => OnLinkClicked(WebWindowId, e.Url);
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

    /// <summary>
    /// The content control for a window, created (with link routing) on first use — and filled from
    /// that window's line buffer if one is already there.
    /// <para>
    /// The fill is what makes a control and its buffer the same thing at every moment rather than only
    /// after the next re-feed. It matters because a buffer can now outlive every control for its window
    /// and predate the first one: the restore log fills <see cref="_lines"/> for a window the saved
    /// workspace no longer holds, and the control only comes into being later, when a capture reopens
    /// that spawn window. Without this the pane would open holding one line — the one that reopened it
    /// — with its restored history sitting invisibly in the buffer behind it.
    /// </para>
    /// </summary>
    private MarkupControl PaneContentFor(string id, string title)
    {
        if (_panes.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var control = new MarkupControl(new List<string>());
        control.LinkClicked += (_, e) => OnLinkClicked(id, e.Url);
        _panes[id] = control;

        if (_lines.TryGetValue(id, out var buffer) && buffer.Count > 0)
        {
            FeedRange(control, buffer, 0, buffer.Count);
        }

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

        _paneSurfaces.Clear();


        // When a pane is zoomed, render just that pane full-area; otherwise render the whole tree.
        var zoomed = _workspace.Layout.ZoomedPaneId is { } zid ? _workspace.Layout.FindPane(zid) : null;
        var paneArea = zoomed is not null
            ? OnSurface(BuildPaneTabs(zoomed), zoomed.Id)
            : BuildLayoutNode(_workspace.Layout.Root);

        // Size the rail to what its rows actually need (clamped), so it never hogs width nor clips.
        var railLines = RenderRailLines();
        _rail.SetContent(railLines);
        var railWidth = RailWidth(railLines);
        _railWidth = railWidth;

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

        // Kept so a later refresh can resize the column without rebuilding the pane area — see
        // ApplyRailWidth, which is what stops a rail row wrapping.
        _railColumn = row.Columns.Count > 0 ? row.Columns[0] : null;
        return row;
    }

    /// <summary>The rail's own grid column, so its width can follow its content between rebuilds.</summary>
    private ColumnContainer? _railColumn;

    /// <summary>The width that column currently has, so an unchanged one costs no relayout.</summary>
    private int _railWidth;

    /// <summary>
    /// Keeps the sidebar as wide as its widest row.
    /// <para>
    /// The width is derived from the rows, and <b>the rows change without the pane tree changing</b>: a
    /// title arriving with a session (the startup path retitles the main window from <c>Main</c> to the
    /// character's name), a page loaded into the web view, a window closing, an unread badge appearing. It was
    /// computed only in <see cref="BuildWorkspaceRow"/> — so only on a <see cref="RebuildPaneArea"/> — and
    /// <see cref="RefreshRail"/> then poured longer rows into a column sized for the shorter ones, which
    /// the framework wrapped. That is the two-line rail row in the report: not a long name, a stale column.
    /// </para>
    /// <para>
    /// The rail's width comes out of the pane area, so this changes every pane's rectangle and therefore
    /// the size every connected session is told over NAWS. That report is not made here: it rides the
    /// frame (<c>PostBufferPaint → ReportPaneSizes</c>) because pane rectangles only exist once a layout
    /// has been arranged, and nothing has been arranged at the moment the width is set.
    /// </para>
    /// </summary>
    private void ApplyRailWidth(int width)
    {
        if (_railColumn is null || _railWidth == width)
        {
            return;
        }

        _railWidth = width;
        _railColumn.Width = width;
        _window.ForceRebuildLayout();
    }

    /// <summary>The rail column's current width. Internal so a test can hold it against the widest row's
    /// measured width — which is the invariant a wrapped rail row breaks.</summary>
    internal int RailColumnWidth => _railWidth;

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
    /// <param name="content">The pane's tab strip (or its move/drag stand-in).</param>
    /// <param name="focused">
    /// Whether this is <c>Layout.FocusedPane</c> — the pane the scrollback keys, the ⌃B commands, ⌃F and
    /// the Ctrl+arrows all act on. Nothing rendered it before, so the one pane every keystroke was aimed
    /// at looked exactly like the ones it was not; it is lit with <see cref="WorkspacePalette.Focus"/>,
    /// the same tone as the armed command line.
    /// <para>
    /// It is a <em>repaint of the same rectangle</em>, which is the constraint: a border or a marker
    /// column would only exist on the focused pane, so the pane's rectangle would change as focus moved
    /// — and per-pane NAWS is derived from that rectangle (<see cref="PaneOutputRects"/>), so every
    /// focus change would announce a new terminal size to the server and reflow the game's own output.
    /// (The scrollback work turned down a scrollbar for the same reason.)
    /// </para>
    /// </param>
    private IWindowControl OnSurface(IWindowControl content, string? paneId = null)
    {
        var surface = Controls.Grid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill);
        surface.Rows(GridLength.Star(1)).Columns(GridLength.Star(1));
        surface.Place(content, 0, 0, 1, 1);
        var built = surface.Build();
        built.BackgroundColor = ToColor(PaneSurfaceTone(paneId));
        if (paneId is not null)
        {
            _paneSurfaces[paneId] = built;
        }

        return built;
    }

    /// <summary>The plane a pane is painted on: the lit one when it holds the focus, otherwise the surface.</summary>
    private Rgb PaneSurfaceTone(string? paneId) =>
        paneId is not null && IsFocusedPane(paneId)
            ? WorkspacePalette.Focus(_theme)
            : WorkspacePalette.Surface(_theme);

    /// <summary>
    /// Repaints the focus indicator without rebuilding the pane area. A focus move changes two things and
    /// neither is a layout change: which plane each pane is painted on, and which tab carries the marker.
    /// Rebuilding would work and is what the ⌃B commands do, but they change the <em>tree</em>; a
    /// rebuild here would dispose and re-parent every viewport for a repaint, on a key the user is
    /// expected to press repeatedly to get where they are going.
    /// </summary>
    private void RefreshPaneFocus()
    {
        foreach (var (paneId, surface) in _paneSurfaces)
        {
            // GridControl's setter invalidates for repaint, so this is the whole update.
            surface.BackgroundColor = ToColor(PaneSurfaceTone(paneId));
        }

        lock (_paneTabsLock)
        {
            foreach (var (paneId, tabs) in _paneTabs)
            {
                PaintTabChips(tabs, IsFocusedPane(paneId));
            }
        }

        RefreshTabTitles(); // carries the ▌ marker onto the focused pane's active tab
    }

    /// <summary>
    /// Renders the current rail rows to markup (collapsed or expanded), each row already fitted to the
    /// widest the sidebar may become. Fitting has to happen before the width is measured, or a row past the
    /// clamp is measured as needing more columns than the layout will ever give it — and then wraps.
    /// </summary>
    private List<string> RenderRailLines()
    {
        var rows = BuildRail();
        return _railCollapsed
            ? RailRenderer.RenderCollapsed(rows)
            : RailRenderer.Render(rows, RailMaxWidth - RailMargin);
    }

    /// <summary>The narrowest the expanded sidebar goes, so a sparse rail still reads as a column.</summary>
    private const int RailMinWidth = 16;

    /// <summary>The widest it goes, so one long world or window name cannot run away with the layout.</summary>
    private const int RailMaxWidth = 44;

    /// <summary>Breathing room between the widest row and the divider beside it.</summary>
    private const int RailMargin = 2;

    /// <summary>
    /// The rail column width: the widest row's visible width plus a small margin, clamped. Collapsed, it
    /// hugs its short status strip.
    /// </summary>
    private int RailWidth(IReadOnlyList<string> lines)
    {
        var widest = lines.Count == 0 ? 0 : lines.Max(MarkupWidth);
        return _railCollapsed
            ? Math.Clamp(widest + 1, 4, 10)
            : Math.Clamp(widest + RailMargin, RailMinWidth, RailMaxWidth);
    }

    /// <summary>Visible width of a markup string: strips <c>[…]</c> tags, unescapes <c>[[</c>/<c>]]</c>,
    /// and counts text elements (so combining/wide runes count once). Internal because it is the
    /// measure <see cref="RailWidth"/> sizes the sidebar by, so a test that a rail row's markup did not
    /// change width has to ask the same question the layout asks.</summary>
    internal static int MarkupWidth(string markup)
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
                return OnSurface(BuildDragPane(pane), pane.Id);
            }

            return OnSurface(
                _moveMode && _moveOrdinals.TryGetValue(pane.Id, out var ordinal)
                    ? BuildMovePane(pane, ordinal)
                    : BuildPaneTabs(pane),
                pane.Id);
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

    /// <summary>Whether a pane is the one every workspace keystroke is aimed at.</summary>
    private bool IsFocusedPane(string paneId) =>
        string.Equals(paneId, _workspace.Layout.FocusedPaneId, StringComparison.Ordinal);

    /// <summary>
    /// Colours a pane's tab chips by whether the pane holds the focus. This is the cue that carries the
    /// signal: the focused pane's active tab is painted in the very band the armed command line is painted
    /// in, so one colour means "you are here" in both of the places it can be said, and the strip is
    /// already drawn — so, like the plane behind it, this consumes no cells and cannot move the pane's
    /// rectangle or the NAWS size derived from it.
    /// <para>
    /// The framework's four properties split on <em>its own</em> keyboard focus, which in this app is
    /// never the tab strip: focus is pinned to the armed command line, so the <c>…Focused…</c> variants
    /// would never be reached. Both are therefore set to the same value and driven by <em>our</em> pane
    /// focus, which is the fact the user is asking about.
    /// </para>
    /// <para>
    /// Elevation alone was not enough. A pane's plane is what the game's own colours are read against, so
    /// it can only be lifted so far before it starts competing with them — and a lift small enough to be
    /// safe there reads, in a rendered frame, as a gentle elevation rather than as "super obvious", which
    /// is what was asked for. The chip has no such constraint: nothing is read on it but its own label.
    /// </para>
    /// </summary>
    private void PaintTabChips(TabControl tabs, bool focused)
    {
        var activeBg = ToColor(focused ? WorkspacePalette.ArmedBand(_theme) : WorkspacePalette.Surface(_theme));
        var activeFg = ToColor(focused
            ? _theme.Resolve(TerminalColor.Default, isBackground: false)
            : WorkspacePalette.IdleInk(_theme));
        var restBg = ToColor(focused ? WorkspacePalette.Focus(_theme) : WorkspacePalette.Surface(_theme));
        var restFg = ToColor(WorkspacePalette.IdleInk(_theme));

        tabs.ActiveFocusedBackgroundColor = activeBg;
        tabs.ActiveUnfocusedBackgroundColor = activeBg;
        tabs.ActiveFocusedForegroundColor = activeFg;
        tabs.ActiveUnfocusedForegroundColor = activeFg;
        tabs.InactiveFocusedBackgroundColor = restBg;
        tabs.InactiveUnfocusedBackgroundColor = restBg;
        tabs.InactiveFocusedForegroundColor = restFg;
        tabs.InactiveUnfocusedForegroundColor = restFg;
    }

    /// <summary>Builds a leaf pane's tab strip from its window ids, tracking it under its pane id.</summary>
    private IWindowControl BuildPaneTabs(PaneNode pane)
    {
        var builder = Controls.TabControl();
        var ids = new List<string>();
        var focused = IsFocusedPane(pane.Id);
        foreach (var windowId in pane.Tabs)
        {
            if (_workspace.FindWindow(windowId) is not { } window)
            {
                continue;
            }

            // The marker rides the focused pane's *active* tab: the strip is plain text to the framework,
            // so a glyph is the only per-pane cue the strip can carry, and it is the shape half of the
            // focus signal — it reads on a monochrome terminal, where a luminance step does not.
            builder.AddTab(
                TabTitles.For(window, ActiveCharacterKey(), focused && pane.ActiveTab == windowId),
                BuildTabContent(pane, windowId, window));
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

        PaintTabChips(tabs, focused);

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
    /// Chooses a tab's content: a frozen <em>active</em> window gets the pinned/live split, the web view
    /// gets the picture, and everything else shows the plain live control.
    /// <para>
    /// A spawn window used to get a fourth arm — a dim <c>⇱ capture ^\[Chat\]</c> row between the tab
    /// strip and the output, naming the trigger pattern that routes lines in. It was asked to go
    /// ("do not show the capture line for capture panels") and it took a whole column of plumbing with
    /// it: the pattern had ridden from <c>TriggerEngine</c> through <c>SpawnLineEventArgs</c> onto
    /// <c>WorkspaceWindow</c> and into the saved workspace for this one row, and nothing else ever read
    /// it. Every spawn window now renders exactly like every other output window, which is one fewer row
    /// of pane taken from the output and one fewer shape a pane can be in.
    /// </para>
    /// </summary>
    private IWindowControl BuildTabContent(PaneNode pane, string windowId, WorkspaceWindow window)
    {
        if (pane.Frozen && pane.ActiveTab == windowId)
        {
            return BuildFrozenContent(windowId, window.Title);
        }

        if (windowId == WebWindowId)
        {
            return BuildWebContent(window.Title);
        }

        return OutputViewFor(windowId, window.Title);
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
        var buffer = _lines.TryGetValue(windowId, out var b) ? b : new List<PaneLine>();
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
        control.LinkClicked += (_, e) => OnLinkClicked(windowId, e.Url);
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
    /// Arms the tmux-style ⌃B prefix, or disarms it when ⌃B arrives again: the header shows the terse
    /// <see cref="PrefixPanel.Strip"/> at once and the next key is consumed by <see cref="OnWindowKey"/>
    /// as a pane command. Arming also starts <see cref="_prefixTimer"/>; if no key has arrived by the
    /// time it fires, <see cref="PrefixOverlay"/> opens and explains the keymap — the which-key pattern,
    /// so the expert who is already typing never sees the panel and the newcomer is told without asking.
    /// <para>
    /// <b>⌃B ⌃B disarms.</b> It was the one mode in this client you could not leave with the key that
    /// entered it — ⌃P, ⌃R, ⌃Q and every F-key screen close on their own chord — and a held or fumbled
    /// chord therefore left a prefix armed that ate the next keystroke.
    /// </para>
    /// <para>
    /// <b>Ignored during a move as well as under an overlay.</b> <see cref="HandleWindowKey"/> tests
    /// <see cref="_moveMode"/> first, so a prefix armed during a move can never be consumed while the move
    /// lasts: the arming survived it, and the first key <em>after</em> the move was eaten as a pane
    /// command — which, if it happened to be <c>x</c>, closed a window. The guard used to name overlays
    /// only.
    /// </para>
    /// </summary>
    private void ArmPrefix()
    {
        if (_prefixArmed)
        {
            DisarmPrefix();
            return;
        }

        if (AnyOverlayOpen || _moveMode)
        {
            return;
        }

        _prefixArmed = true;
        _header.SetContent(new List<string> { HeaderMarkup() });
        _prefixTimer ??= _time.CreateTimer(
            _ => OnUiThread(ShowPrefixPanel), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _prefixTimer.Change(PrefixPanelDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Spends the prefix: stops the which-key timer, takes the panel down if it is up, and puts the header
    /// back. <b>Every</b> path off the armed state runs this, because a prefix left armed with nothing able
    /// to consume it eats the next keystroke — which is the shape of all three defects this change fixes.
    /// </summary>
    private void DisarmPrefix()
    {
        if (!_prefixArmed)
        {
            return;
        }

        _prefixArmed = false;
        _prefixTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _prefixPanel.Close();
        _header.SetContent(new List<string> { HeaderMarkup() });
    }

    /// <summary>
    /// Opens the which-key panel, if the prefix is still waiting for a key by the time the timer fires. It
    /// re-checks rather than trusting the timer: the callback is marshalled onto the UI thread, so the
    /// keystroke that spends the prefix can land between the two.
    /// </summary>
    private void ShowPrefixPanel()
    {
        if (_prefixArmed && !AnyOverlayOpen)
        {
            _prefixPanel.Open();
        }
    }

    /// <summary>
    /// Runs one key against the armed prefix and disarms — the single consumer, shared by the key that
    /// arrives before the panel appears (<see cref="HandleWindowKey"/>) and the one that arrives into it
    /// (<see cref="PrefixOverlay"/>), so the two timings are one behaviour rather than two.
    /// </summary>
    private void ConsumePrefixKey(ConsoleKeyInfo key)
    {
        DisarmPrefix();
        RunPrefixCommand(PrefixKey(key));
    }

    /// <summary>
    /// The workspace as the ⌃B keymap sees it. Gathered here because only the app can see the layout, the
    /// active window and the command lines; <see cref="PrefixPanel"/> turns it into the rows.
    /// </summary>
    private PrefixFacts PrefixFactsNow() => new(
        _workspace.Layout.Panes.Count,
        _workspace.Layout.FocusedPane.Tabs.Count,
        ActiveWindowId() == MainWindowId,
        _workspace.Layout.ZoomedPaneId is not null,
        _railCollapsed,
        _second.Visible);

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
    /// Loads the demo page into the web view, through the real <see cref="ShowWeb"/> path. The seam a test
    /// uses to put a window that <em>belongs to no connection</em> into the workspace: the web view is the
    /// only one there is, and it can otherwise only be created by a fetch over a socket.
    /// </summary>
    internal void SimulateWebPage() => ShowDemoWebPage();

    /// <inheritdoc cref="SimulateWebPage"/>
    /// <remarks>With a title of its own, for the case a page's title is longer than the rail can ever be —
    /// a world chooses that string, so the rail has to cope with any length of it.</remarks>
    internal void SimulateWebPageTitled(string title) => ShowDemoWebPage(title);

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

    /// <summary>
    /// The header's markup as it currently stands — including, while the prefix is armed, the strip listing
    /// the ⌃B keymap. Internal because that strip is one of the surfaces this app advertises keys on, and
    /// two surfaces naming the same keymap have to agree; the honesty tests read it rather than re-deriving
    /// the list.
    /// </summary>
    internal string HeaderText => _header.Text;

    /// <summary>Whether the framework's keyboard focus is on the bar ⏎ sends from — the app's one rule.</summary>
    internal bool ArmedBarHasFocus => ReferenceEquals(_window.FocusManager.FocusedControl, ActiveBar());

    /// <summary>Whether the control holding the keyboard focus is one the window still draws.</summary>
    internal bool FocusIsOnAVisibleControl =>
        _window.FocusManager.FocusedControl is IWindowControl { Visible: true };

    /// <summary>Whether each bar is reporting a caret for the terminal to sit on — armed and focused.</summary>
    internal (bool Primary, bool Second) CaretReported => (
        _input.GetLogicalCursorPosition() is not null,
        _second.GetLogicalCursorPosition() is not null);

    /// <summary>
    /// <b>Where the terminal's caret actually goes</b> — the cell the driver was last told to put it in,
    /// after a real frame and a real cursor pass.
    /// <para>
    /// This exists because <see cref="CaretReported"/> does not answer the question anyone asks. It calls
    /// the control's own <c>GetLogicalCursorPosition</c>, which is the function a caret bug lives in, so a
    /// test built on it agrees with the code and disagrees with the screen — and one did, passing for a
    /// week while the caret sat visibly on the wrong row. What the terminal receives is the driver's
    /// <c>SetCursorPosition</c>, and that is what this reads.
    /// </para>
    /// <para>
    /// It goes through <c>ProcessOnce</c> rather than <see cref="RenderFrame"/> because
    /// <c>ConsoleWindowSystem.ForceRender</c> paints and stops: the cursor pass is a separate step of the
    /// real loop (<c>UpdateDisplay</c> then <c>UpdateCursor</c>) and is <c>internal</c> to the framework,
    /// so the only way to run it from here is the loop iteration that contains it. Input is drained on the
    /// way past, which is a no-op on a headless driver with no console attached.
    /// </para>
    /// </summary>
    internal (bool Visible, int X, int Y) CaretOnScreen()
    {
        var real = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            _system.ProcessOnce();
        }
        finally
        {
            Console.SetOut(real);
        }

        var driver = (HeadlessConsoleDriver)_system.ConsoleDriver;
        return (driver.CursorVisible, driver.CursorPosition.X, driver.CursorPosition.Y);
    }

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
    /// Which pane every workspace keystroke is aimed at. Internal because the focus tests assert on this
    /// <em>and</em> on the frame: "the model moved" and "the screen says so" are two claims, and a focus
    /// indicator is precisely the thing that can satisfy the first while failing the second.
    /// </summary>
    internal string FocusedPaneId => _workspace.Layout.FocusedPaneId;

    /// <summary>Every pane's id in layout order, for the tests that walk a geometry end to end.</summary>
    internal IReadOnlyList<string> PaneIds => _workspace.Layout.Panes.Select(p => p.Id).ToArray();

    /// <summary>
    /// The zoomed pane's id, or null when nothing is zoomed. Internal because the ordinal movers carry a
    /// zoom with them, and "the pane jumped to is the one rendered" is a claim about this field as much as
    /// about the frame.
    /// </summary>
    internal string? ZoomedPaneId => _workspace.Layout.ZoomedPaneId;

    /// <summary>The pane hosting a window, or null when no pane does. Internal so a test can find the pane
    /// a split put a window in rather than assuming which side it landed on.</summary>
    internal string? PaneIdOf(string windowId) => _workspace.Layout.FindWindow(windowId)?.Id;

    /// <summary>A window's title, so a test can ask whether one a session <em>adopted</em> is named
    /// correctly rather than inferring it from the tab strip.</summary>
    internal string? WindowTitleOf(string windowId) => _workspace.FindWindow(windowId)?.Title;

    /// <summary>A window's recorded owner. Ownership is read by the rail and by
    /// <see cref="WindowSession"/>, and it goes stale silently, so it is worth asserting directly.</summary>
    internal string? WindowOwnerOf(string windowId) => _workspace.FindWindow(windowId)?.SessionKey;

    /// <summary>A pane's visible tab, so a test can say which window a click on a tab strip brought up.</summary>
    internal string? PaneActiveTab(string paneId) => _workspace.Layout.FindPane(paneId)?.ActiveTab;

    /// <summary>
    /// The plane each pane is painted on, by pane id — read off the controls the last frame was built
    /// from, so a test asserts the colour the compositor was actually handed rather than re-deriving it
    /// from the palette it came out of.
    /// </summary>
    internal IReadOnlyDictionary<string, Color> PaneSurfaceColors =>
        _paneSurfaces.ToDictionary(p => p.Key, p => p.Value.BackgroundColor, StringComparer.Ordinal);

    /// <summary>The focused and unfocused pane planes, as <see cref="WorkspacePalette"/> resolves them.</summary>
    internal (Color Focused, Color Unfocused) PaneBandColors =>
        (ToColor(WorkspacePalette.Focus(_theme)), ToColor(WorkspacePalette.Surface(_theme)));

    /// <summary>
    /// The markup each bar's prompt is drawn from. Internal because the armed/idle distinction is carried
    /// by the prompt's <em>weight</em> as well as by the band behind it, and weight is a markup tag rather
    /// than a colour — so this is where a test reads the second cue.
    /// </summary>
    internal string PrimaryPromptMarkup => _input.Prompt;

    /// <inheritdoc cref="PrimaryPromptMarkup"/>
    internal string SecondPromptMarkup => _second.Prompt;

    /// <summary>Every realised pane's tab-strip labels, so a test can find the focus marker in the strip.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<string>> PaneTabTitles
    {
        get
        {
            var titles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            lock (_paneTabsLock)
            {
                foreach (var (paneId, tabs) in _paneTabs)
                {
                    titles[paneId] = tabs.TabPages.Select(t => t.Title).ToArray();
                }
            }

            return titles;
        }
    }

    /// <summary>
    /// One pane's tab-strip contents in strip order: each tab's window id, its label, and whether it
    /// carries the framework's <c>×</c>. Internal because a test that clicks a particular tab has to
    /// locate it the way <c>TabControl.ProcessMouseEvent</c> does — from the labels and the close buttons
    /// ahead of it — and every one of those three facts is needed to land on the label rather than on a
    /// neighbour's <c>×</c>.
    /// </summary>
    internal IReadOnlyList<(string WindowId, string Title, bool Closable)> PaneTabStrip(string paneId)
    {
        lock (_paneTabsLock)
        {
            if (!_paneTabs.TryGetValue(paneId, out var tabs))
            {
                return Array.Empty<(string, string, bool)>();
            }

            return tabs.TabPages
                .Select(t => (WindowId: t.Tag as string ?? string.Empty, t.Title, Closable: t.IsClosable))
                .ToArray();
        }
    }

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

    /// <summary>Whether the ⌃B prefix is armed and waiting for the key it will consume.</summary>
    internal bool PrefixArmed => _prefixArmed;

    /// <summary>Whether the ⌃B which-key panel is up.</summary>
    internal bool PrefixPanelOpen => _prefixPanel.IsOpen;

    /// <summary>What the panel is saying — the rendered markup, for a headless test to read back.</summary>
    internal IReadOnlyList<string> PrefixPanelLines => _prefixPanel.Lines;

    /// <summary>The keymap rows the panel is drawing, each with whether this workspace can run it.</summary>
    internal IReadOnlyList<PrefixEntry> PrefixPanelEntries => _prefixPanel.Entries;

    /// <summary>The workspace as the keymap sees it, without opening anything.</summary>
    internal PrefixFacts PrefixFactsSnapshot => PrefixFactsNow();

    /// <summary>Feeds one key to the open panel, for the reason <see cref="SimulateQuitKey"/> exists.</summary>
    internal void SimulatePrefixPanelKey(ConsoleKeyInfo key) => _prefixPanel.SimulateKey(key);

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

    /// <summary>Whether the closing deletion review is up.</summary>
    internal bool DeletionReviewOpen => _settings.Review.IsOpen;

    /// <summary>What that review is asking — the rendered markup, for a headless test to read.</summary>
    internal IReadOnlyList<string> DeletionReviewLines => _settings.Review.Lines;

    /// <summary>Feeds one key to it, for the reason <see cref="SimulateQuitKey"/> exists.</summary>
    internal void SimulateReviewKey(ConsoleKeyInfo key) => _settings.Review.SimulateKey(key);

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
    /// Whether the one window accepts the framework's built-in window management. Both must stay false:
    /// an unhandled Ctrl+chord on a movable window reaches <c>InputCoordinator.HandleMoveInput</c>, whose
    /// <c>ConsoleKey.X</c> arm closes the window — and closing the only window blanks the client.
    /// </summary>
    internal (bool Movable, bool Resizable) WindowManagementFlags => (_window.IsMovable, _window.IsResizable);

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

            // Alt+⏎ → newline, reassembled from the Escape and Enter the parser splits it into. First in
            // the chain because it is the only entry that has to see *every* key: it remembers a lone
            // Escape and must forget it again the moment anything else arrives, whoever ends up claiming
            // that key. Nothing competes for either half — MacroKeys.Verdict reports Enter and Escape as
            // chords no binding can be written on, so no macro can be shadowed here.
            if (TryAltEnter(e))
            {
                return null;
            }

            if (DispatchMacro(e.KeyInfo) is { } sent)
            {
                e.Handled = true;
                return sent;
            }

            // Alt+Shift+arrows: resize the focused pane. Immediately before its sibling because the two
            // are one gesture family, and before both the scrollback keys (Shift+↑/↓ is theirs) and the
            // command line (Alt+←/→ is word movement there) — neither of which looks at the *other*
            // modifier, so either would otherwise have to be trusted not to take the Alt+Shift form.
            if (TryResizeKey(e))
            {
                return null;
            }

            // Ctrl+arrows: move between panes, and at the bottom edge into the command lines. Ahead of
            // both the scrollback keys and recall because it is a workspace gesture rather than a move
            // inside one, and ahead of the command line because the bars would otherwise eat it —
            // Ctrl+←/→ used to be word movement there (it is Alt+←/→ now) and TryRecallKey ignores
            // modifiers entirely. After the macros, for the same reason the rest of this chain is.
            if (TryFocusKey(e))
            {
                return null;
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

        e.Handled = true;
        ConsumePrefixKey(e.KeyInfo);
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
        // Escape is the advertised way out, so it is resolved from the *key* rather than left to arrive as
        // a control character in KeyChar — which is how it used to reach the "any other key" arm, and why
        // no surface could honestly name it.
        ConsoleKey.Escape => PrefixPanel.CancelKey,
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
                    RefusePrefix(PrefixPanel.NoZoomRefusal);
                    break;
                }

                _workspace.Layout.ToggleZoom();
                RebuildPaneArea();
                break;

            case 'o':
                if (_workspace.Layout.Panes.Count <= 1)
                {
                    RefusePrefix(PrefixPanel.NoCycleRefusal);
                    break;
                }

                CyclePane();
                break;

            case 'x':
                // CloseActiveWindow refuses the main window; it is the session, not a closable tab.
                if (ActiveWindowId() == MainWindowId)
                {
                    RefusePrefix(PrefixPanel.NoCloseRefusal);
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
                    // The strip has to be rebuilt, not retitled. A reorder changes the *order* of the
                    // pane's tabs, and `TabControl` has no way to move a page — `TabPages` is a copy and
                    // the only mutators are Add/Insert — so `RefreshTabTitles`, which repaints each page
                    // by its own `Tag`, left the strip exactly as it was. That is not a cosmetic lag: the
                    // model then holds an order the screen does not, and the *refusal* is read against the
                    // model. Reordering the middle of three tabs looked like nothing happened, and the
                    // next press — genuinely at the end, in a strip still showing it in the middle — said
                    // "the tab is already at that end of the strip", which is how it was reported.
                    RebuildPaneArea();
                    break;
                }

                RefusePrefix(tabs > 1 ? PrefixPanel.TabAtEndRefusal : PrefixPanel.NoReorderRefusal);
                break;

            case 'm':
                EnterMoveMode();
                break;

            case 'i':
                ToggleSecondBar();
                break;

            // The advertised way out. Behaviourally it is the same as the fall-through below — the prefix
            // is spent and no command runs — and it is a case of its own because a surface may only name a
            // key that is really there. Quietly: cancelling is not a refusal, and a status-line notice
            // would be the client answering a question nobody asked.
            case PrefixPanel.CancelKey:
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

        RefusePrefix(PrefixPanel.NoSplitRefusal);
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
        // The focused window's session, not _active: a macro key is a keystroke, and a keystroke may not
        // reach a world other than the one whose pane is focused (see SendTarget). A pane with no
        // connection fires no macro and the key falls through the rest of the chain unclaimed.
        if (SendTarget() is not { } session || MacroKeys.Descriptor(key) is not { } descriptor)
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
    /// Enters move mode (⌃B m): the active window lifts, every pane dims and shows its own number, and
    /// the status bar becomes the move prompt. 1–9 pick the destination, arrows toggle an edge (split
    /// there), ⏎ commits, Esc cancels.
    /// <para>
    /// The digits are the pane ordinals, so the badge on a pane, the <c>pane N</c> the prompt names as
    /// the target, the sidebar's hosting column and ⌥N are all one numbering.
    /// </para>
    /// </summary>
    private void EnterMoveMode()
    {
        _moveWindowId = ActiveWindowId();
        _moveMode = true;
        _moveTargetPaneId = null;
        _moveOrdinals.Clear();
        var ordinal = 1;
        foreach (var pane in _workspace.Layout.Panes)
        {
            if (ordinal > CommandIds.PaneJumpDigits)
            {
                break;
            }

            _moveOrdinals[pane.Id] = ordinal++;
        }

        RebuildPaneArea();
        SetStatus(MovePromptMarkup(), displace: true);
    }

    /// <summary>Handles a key while in move mode: pick pane (1–9), edge (arrows), commit (⏎), cancel (Esc).</summary>
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

        if (ch is >= '1' and <= '9')
        {
            // Only retarget on a real match — a digit past the last pane must not clear the current target.
            var match = _moveOrdinals.FirstOrDefault(kv => kv.Value == ch - '0');
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
        _moveOrdinals.Clear();
        RebuildPaneArea();
        UpdateStatus();
    }

    /// <summary>The move-mode status prompt.</summary>
    private string MovePromptMarkup()
    {
        var name = _moveWindowId is { } id && _workspace.FindWindow(id) is { } w ? Escape(w.Title) : "window";
        return $"[#e5c07b]MOVE[/] [bold]{name}[/] [dim]→[/] [#00f5b7]{DropLabel(_moveTargetPaneId, _moveEdge)}[/]"
            + "   [dim]1–9 pane · ←↑↓→ edge · ⏎ commit · Esc cancel[/]";
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

    /// <summary>
    /// The name every surface in this client gives a pane: <c>pane N</c>, counting
    /// <see cref="WorkspaceLayout.Panes"/> — <b>creation order</b> — from one.
    /// <para>
    /// The number a pane wears is its position in that list, so it does not move while the pane is open
    /// and it closes up behind a pane that goes away. Under the tree order this used to count in, a pane
    /// created to the left of pane 2 made it pane 3 without the user having touched it, and ⌥2 quietly
    /// went somewhere else.
    /// </para>
    /// <para>
    /// It used to call the first pane <c>main</c> — the spelling the rail's hosting column abandoned
    /// because <c>▪ main   main</c> put two meanings in one line, the <em>window</em> named main beside
    /// the pane also called main. The move and drag overlays kept it, so the same pane was <c>pane 1</c>
    /// in the sidebar and <c>main</c> under the cursor. That was survivable while nothing depended on the
    /// number; ⌥1 is a chord that lands on the pane a label names, and two spellings of one pane is
    /// exactly the mismatch that makes such a chord read as broken.
    /// </para>
    /// </summary>
    private string PaneLabel(string paneId)
    {
        var index = 0;
        foreach (var pane in _workspace.Layout.Panes)
        {
            if (pane.Id == paneId)
            {
                return $"pane {index + 1}";
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

    /// <summary>A pane rendered as a move-mode target: its own number over the dimmed window list.</summary>
    private IWindowControl BuildMovePane(PaneNode pane, int ordinal)
    {
        var selected = pane.Id == _moveTargetPaneId;
        var color = selected ? "#00f5b7" : "#e5c07b";
        var lines = new List<string> { string.Empty, string.Empty };
        lines.Add($"     [bold {color}]▛▀▀▜[/]");
        lines.Add($"     [bold {color}]▌ {ordinal} ▐[/]");
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

        // Through the same path the Ctrl+arrows take, so ⌃O and a directional move cannot come to mean
        // different things: the pane's active window is activated, which makes its character the one the
        // command line talks to and its drafts the ones in the bars. The keyboard stays where it was —
        // typing belongs to the armed command line, wherever the selection is.
        ActivateFocusedWindow();

        // An existing zoom follows, exactly as it does for ⌥N: both are *ordinal* movers, and a zoomed
        // workspace realises one pane, so cycling without this left the selection, the session the bar
        // talks to and the caret on a pane that was not on screen. (The directional movers cannot have
        // this: while one pane is realised there is no neighbour to ask for, and ⌃← refuses out loud.)
        if (_workspace.Layout.CarryZoomToFocused())
        {
            RebuildPaneArea();
        }
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
    /// <b>The one activation path.</b> Makes a window active in its hosting pane (model + view), focuses
    /// that pane, points the command line at that window's session, and hands the input area that
    /// window's drafts. Returns false when the window is not placed in any pane, so a caller acting on a
    /// user's request can say so instead of appearing to do nothing.
    /// <para>
    /// Everything that changes which window is in front comes through here — a tab click
    /// (<see cref="OnTabChanged"/>), a rail or ⌃P entry (<see cref="DispatchCommand"/>), a character
    /// switch, an MXP <c>PROMPT</c>, the web view, and every mover of pane <em>selection</em>
    /// (<see cref="FocusPane"/>, <see cref="CyclePane"/>). They were separate paths and they disagreed:
    /// ⌃O and the Ctrl+arrows reloaded the drafts but left <c>_active</c> behind, so typing after
    /// navigating to another world's pane went to the world you had left; a tab click did the same. The
    /// invariant is one sentence — <em>activating a window activates its session</em> — and it holds only
    /// if there is one place that does it.
    /// </para>
    /// <para>
    /// <b>This is not about keyboard focus, and it does not weaken the pin.</b> Framework focus stays on
    /// the armed command line (<c>FocusChanged → PinFocusToArmedBar</c>), which is what makes paste, the
    /// caret and "which bar ⏎ sends from" one fact. What moves here is <em>which session the bar talks
    /// to</em> and <em>which draft it holds</em> — two facts about the bar's contents, not about which
    /// control the framework thinks has the keyboard. The previous author conflated the two in the
    /// opposite direction ("it never moves focus, so nothing more is needed"); they are separate, and both
    /// are needed.
    /// </para>
    /// </summary>
    private bool Activate(string id)
    {
        if (!_workspace.ActivateWindow(id))
        {
            return false;
        }

        // Selecting the tab below raises the framework's own TabChanged, which lands back in
        // OnTabChanged and so back here. The model call above is idempotent, so re-entering is harmless
        // — but doing the rest twice would recall the drafts twice and say anything it has to say twice.
        if (_activating)
        {
            return true;
        }

        _activating = true;
        try
        {
            AdoptSessionOf(id);
            SelectTabFor(id);
            SyncToFocusedPane();
        }
        finally
        {
            _activating = false;
        }

        return true;
    }

    /// <summary>Guards <see cref="Activate"/> against re-entering itself through <c>TabChanged</c>.</summary>
    private bool _activating;

    /// <summary>
    /// Points the command line at the session of the window that has just become active, so ⏎ talks to
    /// the character whose output is on screen.
    /// <para>
    /// The resolution is <see cref="WindowSession"/>'s and it has exactly two answers before it gives up:
    /// the session printing into the window, else the character the workspace records as owning it. There
    /// is no third arm falling back on <c>_active</c> — that fallback is the bug, in both the shapes it
    /// has taken today (a link in a background pane sent to the focused character; a pane selection moved
    /// without the bar following).
    /// </para>
    /// <para>
    /// A window that resolves to nothing — the web view, or a window whose owner has no session in this
    /// run — cannot be given the command line, so <c>_active</c> stays where it was and this says so.
    /// <b>Nothing about that refuses the navigation</b>: the pane takes the focus, the indicator, the tab
    /// marker and the caret like any other, because asking to go somewhere should always arrive. What it
    /// refuses is a <em>redirect</em> of the client's active session, which is a different thing again from
    /// where ⏎ goes — that follows the focused window through <see cref="SendTarget"/> and, here, becomes
    /// nowhere. The notice therefore says the line has nowhere to go rather than naming another world:
    /// while this reported "⏎ still sends to Ann" it was describing a real misdelivery accurately instead
    /// of preventing it.
    /// </para>
    /// <para>
    /// Two cases are quiet because there is genuinely no redirect to report: a window already owned by the
    /// active character, and a client with no active session at all — where the resting status row already
    /// reads "not connected" and ⏎ goes nowhere for anyone. The second one matters beyond tidiness: the
    /// snapshot demo has owners but no live sessions, so a notice there would put a message on every frame
    /// that opens the web view, saying something untrue about a client that has nothing to misdeliver.
    /// </para>
    /// </summary>
    private void AdoptSessionOf(string windowId)
    {
        if (WindowSession(windowId) is { } session)
        {
            if (!ReferenceEquals(session, _active))
            {
                _active = session;
                UpdateStatus();
            }

            return;
        }

        var owner = _workspace.FindWindow(windowId)?.SessionKey;
        if (_active is null || string.Equals(owner, _active.SessionKey, StringComparison.Ordinal))
        {
            return;
        }

        // Through Snippet, because a window's title can be a *world's* text — the web view is titled from
        // the page it loaded — and the status row is not a place to paste an unbounded string from the wire.
        Notice(
            owner is { Length: > 0 }
                ? $"{owner} has no open session — ⌃P ▸ Switch to it opens one; ⏎ sends nowhere from this pane"
                : $"{Snippet(_workspace.FindWindow(windowId)?.Title ?? windowId)} belongs to no connection — " +
                  "⏎ sends nowhere from this pane");
    }

    /// <summary>
    /// Brings the pane's own tab strip in line with the model, for callers that activated a window
    /// without clicking its tab. Guarded by <see cref="_activating"/> against the <c>TabChanged</c> this
    /// raises coming straight back round.
    /// </summary>
    private void SelectTabFor(string id)
    {
        if (_workspace.Layout.FindWindow(id) is not { } pane ||
            _paneTabs.GetValueOrDefault(pane.Id) is not { } tabs)
        {
            return;
        }

        for (var i = 0; i < tabs.TabCount; i++)
        {
            if (tabs.TabPages[i].Tag as string == id)
            {
                tabs.ActiveTabIndex = i;
                break;
            }
        }
    }

    /// <summary>
    /// Everything that follows a change of focused pane or active window, in one place so the movers
    /// cannot drift apart: the input area follows the window (both drafts, the second bar's visibility,
    /// the history cursors), the focus indicator repaints, the status row's scrollback segment is the
    /// newly focused pane's, and every session is re-told the size of its own pane.
    /// <para>
    /// The keyboard is deliberately not touched. Typing belongs to the armed command line wherever the
    /// selection has moved to.
    /// </para>
    /// </summary>
    private void SyncToFocusedPane()
    {
        ChangeWindow();
        RefreshPaneFocus(); // and, through RefreshTabTitles, the tab labels, the rail and the input chrome
        SyncScrollbackState();
        ReportPaneSizes();
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
                    page.Title = TabTitles.For(
                        window, focusedCharacter, IsFocusedPane(paneId) && activeTab == id);
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

        // Nothing to record: the rail's dots, the header's fraction and the quit prompt all ask
        // ConnectedCharacters() for the live answer. This used to maintain a set here, for the *active*
        // session only, which is why a background world connecting or dropping changed none of the three.
        var character = session.Character?.Name ?? session.World.Name;
        _statusIdentity = (character, session.World.Host, session.World.Port, session.State.ToString().ToLowerInvariant());
        RefreshStatusBar();
        _header.SetContent(new List<string> { HeaderMarkup() });
        RefreshRail();
    }

    /// <summary>
    /// Repaints the header band. Needed wherever something the band <em>counts</em> changes without a
    /// session event behind it — adding or removing a world on F5, for one, which used to leave the
    /// connected fraction reading the old denominator until the next connect or disconnect happened to
    /// repaint it.
    /// </summary>
    private void RefreshHeader() => _header.SetContent(new List<string> { HeaderMarkup() });

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

        // The ⌃B prefix indicator shows only while armed, and it is the *terse* half of the which-key
        // pair: the keys immediately, the panel a few hundred milliseconds later if nothing was pressed.
        // What it says — including the spelling of the exit — lives in PrefixPanel, so the two surfaces
        // cannot drift. It is picked to fit the room the identity ribbon leaves, because the header is one
        // row and an overlong one *wraps*, which costs a row of workspace; the widest spelling ran the
        // eighty-column layout to within four cells of the edge before anything was added to it.
        if (_prefixArmed)
        {
            var room = HeaderWidth() - MarkupWidth(leftBar) - 2;
            return $"{leftBar}  {PrefixPanel.Strip(room)}";
        }

        // Both halves count characters — see ConnectedCharacters for why that is the unit and what it used
        // to read. The unit is *named* rather than left to be guessed: "2/5 connected" beside a world
        // ribbon reads just as easily as two of five worlds, which is exactly the ambiguity that let this
        // fraction compare two different things for as long as it did.
        //
        // The word is spelt out. It had to be abbreviated to "chars" while this row also carried the
        // graphics readout, which took the 80-column layout to precisely 80 cells; dropping that (see
        // below) gave the width back. InputAreaLayoutTests.TheFirstFrameFitsTheTerminalItIsOn(80) is the
        // test that says so, and anything added here has to keep it passing.
        var connected = ConnectedCharacters().Count;
        var conn = _config.Worlds.Count > 0
            ? $"{connected}/{ConfiguredConnections()} characters   "
            : string.Empty;
        var logFormat = HeaderLogFormat();
        var log = logFormat == LogFormat.None
            ? $"[dim]{Glyphs.Log} LOG off[/]"
            : $"[#00f5b7]{Glyphs.Log}[/] [dim]LOG {logFormat.ToString().ToLowerInvariant()}[/]";
        // No graphics readout here. Which protocol the probe settled on is decided once at startup and
        // never changes, so a permanent cell of chrome spends the row's scarcest resource on a fact that
        // cannot become news — and it was already said twice elsewhere: the session prints
        // "*** SharpMUTerm — theme '…', graphics: …" when it opens, and the not-connected status line
        // repeats it. A startup fact belongs where startup facts are read, not in the row that has to
        // fit an identity ribbon and a live count at eighty columns.
        var right = $"[dim]{conn}[/]{log}   ";

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

    /// <summary>The least space kept between the status row's identity and its right-hand cluster.</summary>
    private const int MinStatusGap = 3;

    /// <summary>
    /// The status row's focus-navigation hint, or empty when there is nothing to navigate. It names the
    /// keys that move between the things the row is otherwise silent about — which pane the workspace keys
    /// act on, and which command line ⏎ sends from.
    /// <para>
    /// Contextual on purpose, and this is the codebase's own idiom rather than a preference: the ⌃P
    /// surface lists <c>Back to live output</c> only when there is somewhere to come back from, and the
    /// settings screens advertise <c>⏎ edit</c> only when a row can be opened. A hint for a second command
    /// line that is not on screen, or for panes on a workspace that has one, is the same defect as a
    /// screen naming a key it does not offer — read from the other end.
    /// </para>
    /// <para>
    /// Both halves are named when both apply, because ⌃↓ genuinely spans them: it walks down the panes and
    /// then on into the second line. Neither claims Shift+⏎ or Ctrl+⏎ anywhere — see <see cref="TryAltEnter"/>
    /// for why those cannot fire on this terminal; the newline chord is advertised on the ⌃P surface and in
    /// <c>--help</c>, where there is room to name it and its second spelling both.
    /// </para>
    /// <para>
    /// <b>Longest first, and the caller takes the first that fits.</b> Resizing a pane belongs on this row
    /// for the same reason moving between them does, but it is the longer claim and this row cannot afford
    /// to grow: an overflow wraps the sticky band and costs a row of workspace. Returning candidates
    /// rather than one string means the narrow terminal loses the <em>resize</em> hint and keeps the
    /// navigation one, instead of losing both because the pair no longer fitted. The chord is still named
    /// on the ⌃P surface and in <c>--help</c> either way.
    /// </para>
    /// </summary>
    private string[] FocusHints()
    {
        var panes = _workspace.Layout.Panes.Count > 1 && _workspace.Layout.ZoomedPaneId is null;
        var bars = _second.Visible;
        return (panes, bars) switch
        {
            (true, true) => new[]
            {
                "[dim]⌃←→↑↓ pane · ⌥⇧←→↑↓ size · ⇥ line[/]",
                "[dim]⌃←→↑↓ pane · ⇥ line[/]",
            },
            (true, false) => new[]
            {
                "[dim]⌃←→↑↓ pane · ⌥⇧←→↑↓ size[/]",
                "[dim]⌃←→↑↓ pane[/]",
            },
            (false, true) => new[] { "[dim]⇥ · ⌃↑↓ line[/]" },
            _ => Array.Empty<string>(),
        };
    }

    /// <summary>
    /// The encoding the active session is decoding with. The demo scene has no session and declares one
    /// instead (<see cref="_demoEncoding"/>); a live client with nothing connected never reaches this
    /// row at all, because <see cref="UpdateStatus"/> sends it to <see cref="NotConnectedMarkup"/>.
    /// </summary>
    private SessionEncoding? EffectiveEncoding() => _active?.CurrentEncoding ?? _demoEncoding;

    private string StatusBarMarkup(string character, string state)
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

        // No latency meter. There was one — a heartbeat glyph, a sparkline and a round-trip figure — and
        // every part of it was invented: the sparkline came from a literal `{ 38, 44, 41, 47, 40, 43 }`
        // and the figure was the literal 41 whenever keepalive was switched on. It read as telemetry, so
        // a live session against a real world showed exactly the same "41ms" the demo did.
        //
        // It is removed rather than implemented because this client cannot currently measure the thing it
        // was pretending to. The keepalive is an IAC NOP, which by design draws no reply, and telnet's
        // only round-trip primitive is TIMING-MARK (RFC 860, option 6) — negotiated nowhere in this stack
        // or in TelnetNegotiationCore. What *is* measurable without any of that is time since the last
        // byte arrived, which is a liveness signal rather than a latency one; if this row earns a meter
        // again, that is the honest one to build.
        // No host:port. It is a per-world setting that cannot change while you are connected to it, so a
        // permanent cell said the same thing for the whole session — and the rail had already reached
        // that conclusion for the same reason (RailModel omits the address deliberately, which
        // RailModelTests pins). F5 is where a world's address is read and edited.
        // The encoding actually in force on the active session — not the world's configured one, which
        // is a *preference* and was drawn here as though it were fact. It differs from the configuration
        // in both directions: a world left on `auto` shows whatever CHARSET settled on, and a server
        // that never negotiates shows what we assumed instead. The qualifier is the point of the cell:
        // "utf-8" means the server agreed, "utf-8 assumed" means nobody said, "iso-8859-1 forced" means
        // you did. With nothing connected there is no fact to report, and the cell is not drawn — the
        // same rule that took the address and the invented latency meter off this row.
        if (EffectiveEncoding() is { } encoding)
        {
            right.Add($"[dim]{Escape(encoding.Label)}[/]");
        }

        // The character count lives at the bottom now (the input gutter is gone); while recalling
        // history it becomes the "back to draft" hint instead. Both read the armed bar, so a count that
        // moves is always the line ⏎ would send.
        right.Add(HistoryFor(BarKind(ActiveBar())).IsRecalling
            ? $"[{AccentHex(AccentPalette[0])}]history[/] [dim]· ↓ back to draft[/]"
            : $"[dim]{ActiveBar().Text.Length} chars[/]");

        right.Add("[dim]⌃P palette[/]");

        // The focus hint goes in last and only if it fits. This row is a *sticky* band whose length
        // changes at runtime, and a cluster that overflows the width wraps it onto a second row — which
        // takes that row off the workspace (see SyncInputHeights' veto, which PaintStatus re-runs for
        // exactly this reason). A hint that quietly costs a row of output on a 100-column terminal is a
        // worse trade than a hint that is only there when there is room for it, and this is the same
        // measurement the header already makes to right-align its own cluster.
        var rightBar = string.Join("   ", right);
        var room = HeaderWidth() - MarkupWidth(left) - MarkupWidth(rightBar) - MinStatusGap;
        foreach (var focusHint in FocusHints())
        {
            if (MarkupWidth(focusHint) + 3 <= room)
            {
                right.Insert(right.Count - 1, focusHint);
                rightBar = string.Join("   ", right);
                break;
            }
        }

        // Right-align the cluster to the far edge; identity stays pinned left.
        var gap = Math.Max(MinStatusGap, HeaderWidth() - MarkupWidth(left) - MarkupWidth(rightBar));
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
        _prefixTimer?.Dispose();    // and no window left to float a which-key panel over
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
