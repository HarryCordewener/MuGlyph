using MuClient.Core.Configuration;
using MuClient.Core.Text;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;

namespace MuClient.Tui;

/// <summary>
/// The world settings menu (F2): a modal panel over the workspace showing the active world's
/// connection, rendering, and character settings, rendered by the tested
/// <see cref="WorldSettingsRenderer"/>. Esc (or F2 again) closes it. A thin view over pure logic —
/// editing lands later; this surfaces the settings the design shows.
/// </summary>
internal sealed class WorldSettingsView
{
    private readonly ConsoleWindowSystem _system;
    private readonly Func<(WorldDefinition World, TerminalColor Accent)?> _world;

    private Window? _window;

    public WorldSettingsView(ConsoleWindowSystem system, Func<(WorldDefinition World, TerminalColor Accent)?> world)
    {
        _system = system;
        _world = world;
    }

    public bool IsOpen => _window is not null;

    /// <summary>Opens the menu if closed, closes it if open (bound to F2).</summary>
    public void Toggle()
    {
        if (_window is not null)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    /// <summary>Opens the settings menu for the active world (no-op when disconnected).</summary>
    public void Open()
    {
        if (_world() is not { } context)
        {
            return;
        }

        var content = new MarkupControl(WorldSettingsRenderer.Render(context.World, context.Accent));

        _window = new WindowBuilder(_system)
            .WithTitle("World settings")
            .AsModal()
            .Centered()
            .WithSize(60, 22)
            .AddControl(content)
            .OnClosed((_, _) => _window = null)
            .Build();

        _window.PreviewKeyPressed += OnKey;
        _system.AddWindow(_window);
    }

    private void OnKey(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key is ConsoleKey.Escape or ConsoleKey.F2)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Close()
    {
        if (_window is { } window)
        {
            _system.CloseModalWindow(window);
        }
    }
}
