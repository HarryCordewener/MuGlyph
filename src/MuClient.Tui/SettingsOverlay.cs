using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;

namespace MuClient.Tui;

/// <summary>
/// A full-screen overlay hosting the F2–F9 settings screens. The design specifies these as
/// full-screen surfaces, not floating dialogs: this maximises a modal window holding a single
/// markup panel produced by one of the pure screen renderers. Esc (or the same F-key) closes it,
/// and opening a different screen swaps the content in place. The renderers stay pure and tested;
/// this is a thin host.
/// </summary>
internal sealed class SettingsOverlay
{
    private readonly ConsoleWindowSystem _system;

    private Window? _window;
    private MarkupControl? _panel;
    private ConsoleKey _openKey;

    public SettingsOverlay(ConsoleWindowSystem system) => _system = system;

    /// <summary>The F-key of the currently open screen, or null when closed.</summary>
    public ConsoleKey? OpenKey => _window is null ? null : _openKey;

    public bool IsOpen => _window is not null;

    /// <summary>
    /// Toggles a screen: closes if the same F-key screen is open, swaps content if a different one is
    /// open, opens fresh otherwise. <paramref name="content"/> is produced lazily so it reflects
    /// current state each time.
    /// </summary>
    public void Toggle(ConsoleKey key, Func<IReadOnlyList<string>> content)
    {
        if (_window is not null && _openKey == key)
        {
            Close();
            return;
        }

        if (_window is not null)
        {
            _openKey = key;
            _panel?.SetContent(content().ToList());
            return;
        }

        Open(key, content);
    }

    private void Open(ConsoleKey key, Func<IReadOnlyList<string>> content)
    {
        _openKey = key;
        _panel = new MarkupControl(content().ToList());

        _window = new WindowBuilder(_system)
            .WithTitle("Settings")
            .AsModal()
            .Maximized()
            .AddControl(_panel)
            .OnClosed((_, _) => Reset())
            .Build();

        _window.PreviewKeyPressed += OnKey;
        _system.AddWindow(_window);
    }

    /// <summary>Renders the currently open screen into a headless frame (used by snapshots).</summary>
    public void OpenForSnapshot(ConsoleKey key, Func<IReadOnlyList<string>> content) => Open(key, content);

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

    private void Reset()
    {
        _window = null;
        _panel = null;
    }
}
