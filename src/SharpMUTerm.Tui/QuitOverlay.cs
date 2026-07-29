using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using static SharpMUTerm.Tui.MarkupText;

namespace SharpMUTerm.Tui;

/// <summary>
/// The ⌃Q confirmation: a small modal that asks before the client ends, and says what ending it would
/// cost. It is the host and nothing else — <see cref="QuitPrompt"/> owns what a keystroke means and what
/// the question says, which is the split <see cref="SettingsOverlay"/> already uses and the only reason
/// any of this is testable without a terminal.
/// <para>
/// Keys arrive on <c>PreviewKeyPressed</c>, before any control sees them, exactly as they do for the
/// settings screens. Every key is marked handled: a confirmation that let stray typing through to the
/// command line beneath it would be collecting an answer to a question the user has stopped reading.
/// </para>
/// </summary>
internal sealed class QuitOverlay
{
    /// <summary>The narrowest the surface goes, whatever the question turns out to be.</summary>
    private const int MinimumWidth = 34;

    private readonly ConsoleWindowSystem _system;
    private readonly Func<QuitFacts> _facts;
    private readonly Action _quit;

    private Window? _window;
    private MarkupControl? _body;
    private QuitFacts _current = QuitFacts.Nothing;
    private QuitChoice _selected = QuitChoice.Stay;

    /// <summary>
    /// <paramref name="facts"/> is read at the moment the prompt opens — what the user is being asked
    /// about is the state their keystroke arrived in. <paramref name="quit"/> is what a confirmed answer
    /// runs; the overlay never ends the loop itself.
    /// </summary>
    public QuitOverlay(ConsoleWindowSystem system, Func<QuitFacts> facts, Action quit)
    {
        _system = system;
        _facts = facts;
        _quit = quit;
    }

    public bool IsOpen => _window is not null;

    /// <summary>What the prompt is currently showing, for a headless test to read back.</summary>
    internal IReadOnlyList<string> Lines => QuitPrompt.Render(_current, _selected);

    /// <summary>
    /// Opens the prompt, or dismisses it when ⌃Q arrives a second time. A global shortcut runs before
    /// any window, so the running client never reaches the modal's own handler with ⌃Q — this is where
    /// that key is answered, and it answers it the way <see cref="QuitPrompt.Interpret"/> says: staying.
    /// </summary>
    public void Toggle()
    {
        if (_window is not null)
        {
            Close();
            return;
        }

        Open();
    }

    /// <summary>Opens the prompt into a headless frame (used by the <c>quit</c> snapshot view).</summary>
    public void OpenForSnapshot() => Open();

    /// <summary>
    /// Feeds one key to the very handler <c>PreviewKeyPressed</c> raises, for the same reason
    /// <see cref="SettingsOverlay.SimulateKey"/> exists: the framework only pumps keys inside
    /// <c>Run()</c>, which a headless test never enters.
    /// </summary>
    public void SimulateKey(ConsoleKeyInfo key) => OnKey(this, new KeyPressedEventArgs(key, false));

    private void Open()
    {
        _current = _facts();
        _selected = QuitChoice.Stay;

        var lines = QuitPrompt.Render(_current, _selected);
        _body = new MarkupControl(new List<string>(lines));

        // Hug the content: the question is as tall as it has things to say. Centred *after* WithSize,
        // because the builder reads the bounds set so far and falls back to 80x25 otherwise.
        var desktop = _system.DesktopDimensions;
        var content = lines.Max(VisibleLength) + 4; // the 1-cell border each side, plus a cell of margin
        var width = Math.Clamp(content, MinimumWidth, Math.Max(MinimumWidth, desktop.Width - 6));
        var height = Math.Clamp(lines.Count + 2, 6, Math.Max(6, desktop.Height - 2));

        _window = new WindowBuilder(_system)
            .WithTitle("Quit")
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .WithBackgroundColor(new Color(ScreenPalette.MenuBg))
            .HideTitleButtons()
            .Resizable(false)
            .WithSize(width, height)
            .Centered()
            .AddControl(_body)
            .OnClosed((_, _) => Reset())
            .Build();

        _window.PreviewKeyPressed += OnKey;
        _system.AddWindow(_window);
    }

    private void OnKey(object? sender, KeyPressedEventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        var decision = QuitPrompt.Interpret(e.KeyInfo, _selected);
        _selected = decision.Selection;
        e.Handled = true;

        switch (decision.Action)
        {
            case QuitAction.Quit:
                // Closed first: the loop is about to end, and leaving a modal window on the desktop
                // while it does would be the last thing painted.
                Close();
                _quit();
                break;

            case QuitAction.Stay:
                Close();
                break;

            case QuitAction.Redraw:
                _body?.SetContent(new List<string>(QuitPrompt.Render(_current, _selected)));
                _window.Invalidate(redrawAll: true);
                break;

            case QuitAction.None:
            default:
                break;
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
        _body = null;
    }
}
