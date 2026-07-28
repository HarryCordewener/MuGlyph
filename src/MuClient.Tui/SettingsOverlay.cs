using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace MuClient.Tui;

/// <summary>
/// A full-screen overlay hosting the F2–F9 settings screens. The design specifies these as
/// full-screen surfaces, not floating dialogs: this maximises a frameless modal (no title bar,
/// buttons, or resize grip) with a deep panel background. Most screens are a single markup panel;
/// F5 supplies a composed control tree (real panels). Esc (or the same F-key) closes it. The
/// renderers stay pure and tested; this is a thin host.
/// </summary>
internal sealed class SettingsOverlay
{
    private const string PanelBg = "#171b24";
    private const string PanelFg = "#c8d0e0";

    private readonly ConsoleWindowSystem _system;

    private Window? _window;
    private ConsoleKey _openKey;

    public SettingsOverlay(ConsoleWindowSystem system) => _system = system;

    /// <summary>The F-key of the currently open screen, or null when closed.</summary>
    public ConsoleKey? OpenKey => _window is null ? null : _openKey;

    public bool IsOpen => _window is not null;

    /// <summary>Toggles a markup screen (wrapped in a full-screen panel).</summary>
    public void Toggle(ConsoleKey key, Func<IReadOnlyList<string>> content) =>
        ToggleControl(key, () => MarkupPanel(content()));

    /// <summary>Toggles a composed-control screen (e.g. F5's real-panel layout).</summary>
    public void Toggle(ConsoleKey key, Func<IWindowControl> control) => ToggleControl(key, control);

    /// <summary>Renders a markup screen into a headless frame (used by snapshots).</summary>
    public void OpenForSnapshot(ConsoleKey key, Func<IReadOnlyList<string>> content) =>
        Open(key, () => MarkupPanel(content()));

    /// <summary>Renders a composed-control screen into a headless frame (used by snapshots).</summary>
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

    private static MarkupControl MarkupPanel(IReadOnlyList<string> lines) => new(lines.ToList())
    {
        BackgroundColor = new Color(PanelBg),
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

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
