using MuClient.Core.Configuration;
using MuClient.Core.Session;
using MuClient.Core.Text;
using MuClient.Core.Theming;
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
/// The top-level MuGlyph application on SharpConsoleUI: a status line, a truecolor markup output
/// pane (with clickable MXP/Pueblo/web links), and a command prompt. Binds a single active
/// <see cref="WorldSession"/> and marshals its background events onto the UI thread. The multi-pane
/// workspace (splits/tabs, driven by <c>MuClient.Core.Workspaces</c>) layers on top of this shell.
/// </summary>
internal sealed class MuGlyphApp : IAsyncDisposable
{
    private readonly AppConfiguration _config;
    private readonly SessionManager _sessions = new();
    private readonly TerminalCapabilities _capabilities;
    private readonly Theme _theme;
    private readonly MarkupFormatter _formatter;

    private readonly ConsoleWindowSystem _system;
    private readonly Window _window;
    private readonly MarkupControl _status;
    private readonly MarkupControl _output;
    private readonly PromptControl _input;
    private readonly GmcpStats _stats = new();
    private readonly HashSet<string> _spawnTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly MuClient.Web.WebPageFetcher _fetcher = new();

    private WorldSession? _active;
    private WorldDefinition? _pendingWorld;
    private Window? _webWindow;
    private MarkupControl? _webContent;

    public MuGlyphApp(AppConfiguration config, TerminalCapabilities capabilities)
    {
        _config = config;
        _capabilities = capabilities;
        _theme = ResolveTheme(config);
        _formatter = new MarkupFormatter(_theme);

        _system = new ConsoleWindowSystem(new NetConsoleDriver(RenderMode.Buffer), new ConsoleWindowSystemOptions());

        _status = Controls.Markup("Not connected.").StickyTop().Build();
        _output = Controls.Markup(string.Empty).Build();
        _output.LinkClicked += (_, e) => OnLinkClicked(e.Url);

        _input = Controls.Prompt(">").WithHistory(true).StickyBottom().Build();
        _input.Entered += (_, text) => OnCommandEntered(text);

        var bg = ToColor(_theme.Resolve(TerminalColor.Default, isBackground: true));
        var fg = ToColor(_theme.Resolve(TerminalColor.Default, isBackground: false));

        _window = new WindowBuilder(_system)
            .WithTitle("MuGlyph — MU* client")
            .Maximized()
            .WithColors(fg, bg)
            .AddControl(_status)
            .AddControl(_output)
            .AddControl(_input)
            .Build();

        _window.OnResize += (_, _) => ReportWindowSize();
        _system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.Q, () => _system.RequestExit(0));
        _system.AddWindow(_window);
    }

    /// <summary>Runs the UI loop, connecting <paramref name="world"/> once the window is shown.</summary>
    public int Run(WorldDefinition? world)
    {
        _pendingWorld = world;
        _window.OnShown += (_, _) => _ = StartAsync(_pendingWorld);
        return _system.Run();
    }

    /// <summary>Connects the given world and binds it to the UI.</summary>
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
        foreach (var line in session.Scrollback.Snapshot())
        {
            _output.AppendLine(_formatter.ToMarkup(line));
        }

        session.LinePrinted += (_, line) => OnUi(() => _output.AppendLine(_formatter.ToMarkup(line)));
        session.PromptChanged += (_, _) => OnUi(UpdateStatus);
        session.StateChanged += (_, _) => OnUi(UpdateStatus);
        session.GmcpReceived += (_, e) => OnUi(() =>
        {
            if (_stats.Update(e.Package, e.Json))
            {
                UpdateStatus();
            }
        });
        session.SpawnLine += (_, e) => OnUi(() => OnSpawnLine(e.Target));
        UpdateStatus();
    }

    private void OnSpawnLine(string target)
    {
        // Spawn output is captured; dedicated spawn windows are wired via the workspace model next.
        if (_spawnTargets.Add(target))
        {
            _active?.PrintSystem($"*** Spawn '{target}' is now receiving routed output.");
        }
    }

    private void OnCommandEntered(string command)
    {
        _input.Input = string.Empty;

        // `/web <url>` opens the in-TUI web view; everything else goes to the world.
        if (command.StartsWith("/web ", StringComparison.OrdinalIgnoreCase))
        {
            OpenWeb(command[5..].Trim());
            return;
        }

        _ = _active?.SendUserInputAsync(command);
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
        var markup = page.Lines.Select(_formatter.ToMarkup).ToList();
        if (_webWindow is null || _webContent is null)
        {
            _webContent = new MarkupControl(markup);
            _webContent.LinkClicked += (_, e) => OnLinkClicked(e.Url);
            _webWindow = new WindowBuilder(_system)
                .WithTitle(page.Title ?? page.Url)
                .Centered()
                .WithSize(Math.Max(40, _window.Width - 8), Math.Max(10, _window.Height - 6))
                .Closable(true)
                .AddControl(_webContent)
                .OnClosed((_, _) => { _webWindow = null; _webContent = null; })
                .Build();
            _system.AddWindow(_webWindow);
        }
        else
        {
            _webContent.SetContent(markup);
            _webWindow.Title = page.Title ?? page.Url;
            _system.SetActiveWindow(_webWindow);
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
