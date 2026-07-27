using MuClient.Core.Configuration;
using MuClient.Core.Session;
using MuClient.Core.Theming;
using MuClient.Graphics;
using MuClient.Tui.Views;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace MuClient.Tui;

/// <summary>
/// The top-level MuGlyph window: a status line, a truecolor <see cref="OutputView"/>, and a
/// <see cref="CommandInput"/>. Binds a single active <see cref="WorldSession"/> and marshals
/// its background events onto the UI thread.
/// </summary>
internal sealed class MuGlyphApp : IAsyncDisposable
{
    private readonly AppConfiguration _config;
    private readonly SessionManager _sessions = new();
    private readonly TerminalCapabilities _capabilities;
    private readonly Theme _theme;

    private readonly Window _window;
    private readonly Label _status;
    private readonly OutputView _output;
    private readonly CommandInput _input;

    private WorldSession? _active;

    public MuGlyphApp(AppConfiguration config, TerminalCapabilities capabilities)
    {
        _config = config;
        _capabilities = capabilities;
        _theme = ResolveTheme(config);

        _window = new Window { Title = "MuGlyph — MU* client" };

        _status = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            Text = "Not connected.",
        };

        _output = new OutputView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1), // leave the last row for input
            Mapper = new ColorMapper(_theme),
        };

        _input = new CommandInput
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
        };
        _input.CommandEntered += OnCommandEntered;

        _window.Add(_status, _output, _input);
        _window.KeyDown += OnGlobalKey;
    }

    public Window Window => _window;

    /// <summary>Connects the given world and binds it to the UI.</summary>
    public async Task StartAsync(WorldDefinition? world)
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
            var size = _output.Viewport;
            await session.SetWindowSizeAsync(Math.Max(1, size.Width), Math.Max(1, size.Height)).ConfigureAwait(false);
        }
        catch
        {
            // WorldSession already surfaced the failure as a system line.
        }
    }

    private void BindSession(WorldSession session)
    {
        _active = session;
        _output.Session = session;
        _input.SetFocus();

        session.LinePrinted += (_, _) => Application.Invoke(() =>
        {
            if (_output.AtBottom)
            {
                _output.ScrollToBottom();
            }

            _output.SetNeedsDraw();
        });

        session.PromptChanged += (_, _) => Application.Invoke(() => _output.SetNeedsDraw());
        session.StateChanged += (_, _) => Application.Invoke(UpdateStatus);
        UpdateStatus();
    }

    private void OnCommandEntered(string command)
    {
        var session = _active;
        if (session is null)
        {
            return;
        }

        _ = session.SendUserInputAsync(command);
    }

    private void OnGlobalKey(object? sender, Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.PageUp:
                _output.PageUp();
                key.Handled = true;
                break;

            case KeyCode.PageDown:
                _output.PageDown();
                key.Handled = true;
                break;

            default:
                // Terminal.Gui reports letter keys by their uppercase code (KeyCode.Q == 'Q').
                if (key.IsCtrl && key.KeyCode == KeyCode.Q)
                {
                    Application.RequestStop();
                    key.Handled = true;
                }

                break;
        }
    }

    private void UpdateStatus()
    {
        var session = _active;
        if (session is null)
        {
            SetStatus($"Not connected.  Graphics: {_capabilities.Protocol}.  Ctrl+Q to quit.");
            return;
        }

        SetStatus($"{session.World.Name}  [{session.State}]  {session.World.Host}:{session.World.Port}  " +
                  $"Graphics: {_capabilities.Protocol}.  PgUp/PgDn scroll · Ctrl+Q quit.");
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _status.SetNeedsDraw();
    }

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
        await _sessions.DisposeAsync().ConfigureAwait(false);
    }
}
