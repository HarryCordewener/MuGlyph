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

    private readonly ConsoleWindowSystem _system;
    private readonly Window _window;
    private readonly MarkupControl _status;
    private readonly TabControl _tabs;
    private readonly PromptControl _input;
    private readonly GmcpStats _stats = new();
    private readonly MuClient.Web.WebPageFetcher _fetcher = new();

    private WorldSession? _active;
    private WorldDefinition? _pendingWorld;

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

        _status = Controls.Markup("Not connected.").StickyTop().Build();

        var main = new MarkupControl(new List<string>());
        main.LinkClicked += (_, e) => OnLinkClicked(e.Url);
        _panes[MainWindowId] = main;

        _tabs = Controls.TabControl().AddTab("Main", main).Fill().Build();
        _tabs.TabPages[0].Tag = MainWindowId;
        _tabs.TabChanged += (_, e) => OnTabChanged(e.NewTab);

        _input = Controls.Prompt(">").WithHistory(true).StickyBottom().Build();
        _input.Entered += (_, text) => OnCommandEntered(text);
        _input.InputChanged += (_, text) => OnInputChanged(text);

        var bg = ToColor(_theme.Resolve(TerminalColor.Default, isBackground: true));
        var fg = ToColor(_theme.Resolve(TerminalColor.Default, isBackground: false));

        _window = new WindowBuilder(_system)
            .WithTitle("MuGlyph — MU* client")
            .Maximized()
            .WithColors(fg, bg)
            .AddControl(_status)
            .AddControl(_tabs)
            .AddControl(_input)
            .Build();

        _window.OnResize += (_, _) => ReportWindowSize();
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.Q, () => _system.RequestExit(0));
        // Next window (Ctrl+N, plus Ctrl+Tab where the terminal reports it) and close window (Ctrl+W).
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.N, NextWindow);
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.Tab, NextWindow);
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.W, CloseActiveWindow);
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

        // Render through the framework's own loop on a worker thread — calling the render primitives
        // directly outside Run() races the compositor. Capture stdout, wait for the first frame, then
        // ask the loop to exit.
        var real = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        var loop = new Thread(() =>
        {
            try
            {
                _system.Run();
            }
            catch
            {
                // The loop is torn down by RequestExit; ignore the resulting cancellation noise.
            }
        })
        {
            IsBackground = true,
            Name = "muglyph-snapshot",
        };

        try
        {
            loop.Start();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (writer.GetStringBuilder().Length == 0 && clock.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(25);
            }

            Thread.Sleep(250); // let the first full frame settle
            _system.RequestExit(0);
            loop.Join(3000);
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

        _status.SetContent(new List<string>
        {
            $"[bold #00f5b7]Aardwolf[/]  [dim][[Connected]][/]  aardmud.org:4000  " +
            $"Graphics: {_capabilities.Protocol}.  Ctrl+Q quit.",
        });
        _input.Input = "say hello there";
        RefreshTabTitles();
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

    private void UpdateStatus()
    {
        var session = _active;
        if (session is null)
        {
            SetStatus($"Not connected.  Graphics: {_capabilities.Protocol}.  Ctrl+Q to quit.");
            return;
        }

        var prompt = session.CurrentPrompt is { IsEmpty: false } p ? $"  {_formatter.ToMarkup(p)}" : string.Empty;
        SetStatus($"{Escape(session.World.Name)}  [{session.State}]  {Escape($"{session.World.Host}:{session.World.Port}")}  " +
                  $"Graphics: {_capabilities.Protocol}.  Ctrl+Q quit.{prompt}");
    }

    private void SetStatus(string markup) => _status.SetContent(new List<string> { markup });

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
