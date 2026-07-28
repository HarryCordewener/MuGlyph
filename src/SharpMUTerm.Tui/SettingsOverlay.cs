using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using static SharpMUTerm.Tui.ScreenPalette;

namespace SharpMUTerm.Tui;

/// <summary>
/// A full-screen overlay hosting the F2–F9 settings screens. The design specifies these as
/// full-screen surfaces, not floating dialogs: this maximises a frameless modal (no title bar,
/// buttons, or resize grip) with a deep panel background. Every screen supplies a composed tree of
/// real panels. Esc (or the same F-key) closes it. The renderers stay pure and tested; this is a thin
/// host.
/// </summary>
internal sealed class SettingsOverlay
{
    private readonly ConsoleWindowSystem _system;

    private Window? _window;
    private ConsoleKey _openKey;

    public SettingsOverlay(ConsoleWindowSystem system) => _system = system;

    /// <summary>The F-key of the currently open screen, or null when closed.</summary>
    public ConsoleKey? OpenKey => _window is null ? null : _openKey;

    public bool IsOpen => _window is not null;

    /// <summary>Toggles a screen's composed control tree.</summary>
    public void Toggle(ConsoleKey key, Func<IWindowControl> control) => ToggleControl(key, control);

    /// <summary>Renders a screen into a headless frame (used by snapshots).</summary>
    public void OpenForSnapshot(ConsoleKey key, Func<IWindowControl> control) => Open(key, control);

    private void ToggleControl(ConsoleKey key, Func<IWindowControl> factory)
    {
        if (_window is not null && _openKey == key)
        {
            Close();
            return;
        }

        if (_window is not null)
        {
            Close();
        }

        Open(key, factory);
    }

    private void Open(ConsoleKey key, Func<IWindowControl> factory)
    {
        _openKey = key;

        _window = new WindowBuilder(_system)
            .AsModal()
            .Maximized()
            .Frameless()
            .WithColors(new Color(PanelFg), new Color(PanelBg))
            .AddControl(factory())
            .OnClosed((_, _) => Reset())
            .Build();

        _window.PreviewKeyPressed += OnKey;
        _system.AddWindow(_window);
    }

    private void OnKey(object? sender, KeyPressedEventArgs e)
    {
        if (e.KeyInfo.Key == ConsoleKey.Escape || e.KeyInfo.Key == _openKey)
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

    private void Reset() => _window = null;
}
