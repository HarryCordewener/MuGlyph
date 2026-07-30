using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;

namespace SharpMUTerm.Tui;

/// <summary>
/// The ⌃B which-key panel: the keymap, explained, shown a short moment after the prefix is armed and
/// only if nothing has been pressed yet. It is the host and nothing else — <see cref="PrefixPanel"/>
/// owns what it says and which commands the workspace can currently run — which is the split
/// <see cref="QuitOverlay"/>, <see cref="HistorySurface"/> and <see cref="SettingsOverlay"/> already use
/// and the only reason any of this is testable without a terminal.
/// <para>
/// Keys arrive on <c>PreviewKeyPressed</c>, before any control sees them, exactly as they do for the quit
/// prompt. Every key is marked handled and every key closes the panel: the prefix is a <em>pending
/// keystroke</em>, so there is no such thing as a key this surface should leave for the workspace
/// underneath — it hands the key straight back to the app's prefix consumer, which is the same code path
/// a key pressed before the panel appeared takes. That is what keeps the two timings one behaviour
/// rather than two.
/// </para>
/// <para>
/// It costs no rows. The panel floats over the workspace and the header strip it follows replaces text
/// already in the header band, so neither display takes a row of output away from the panes.
/// </para>
/// </summary>
internal sealed class PrefixOverlay
{
    /// <summary>
    /// The narrowest the panel goes, whatever the keymap turns out to be. It is set by the footer: a panel
    /// too narrow for the line naming the way out would wrap it, and that is the one line here that has to
    /// be readable. Checked by test against <see cref="PrefixPanel.ExitHint"/> rather than trusted.
    /// </summary>
    internal const int MinimumWidth = 59;

    /// <summary>The rows the panel spends on something other than a command: the blank, and the footer.</summary>
    private const int ChromeRows = 2;

    private readonly ConsoleWindowSystem _system;
    private readonly Func<PrefixFacts> _facts;
    private readonly Action<ConsoleKeyInfo> _run;

    private Window? _window;
    private PrefixFacts _current = PrefixFacts.Fresh;

    /// <summary>
    /// <paramref name="facts"/> is read at the moment the panel opens — what it explains is the workspace
    /// the user is looking at. <paramref name="run"/> is handed whichever key ends the panel, and is the
    /// app's own prefix consumer: the panel decides nothing about what a key means.
    /// </summary>
    public PrefixOverlay(ConsoleWindowSystem system, Func<PrefixFacts> facts, Action<ConsoleKeyInfo> run)
    {
        _system = system;
        _facts = facts;
        _run = run;
    }

    public bool IsOpen => _window is not null;

    /// <summary>
    /// The facts the panel is drawn from: the ones it opened with while it is up, and the live ones while
    /// it is not. The second arm is what makes <see cref="Entries"/> answerable at all with the panel
    /// closed — and it has to be the live ones, because a stale snapshot would let a test read a keymap
    /// for a workspace nobody is looking at and call it agreement.
    /// </summary>
    private PrefixFacts Current => _window is null ? _facts() : _current;

    /// <summary>What the panel is showing — or would show if it opened now — for a headless test to read.</summary>
    internal IReadOnlyList<string> Lines => PrefixPanel.Render(Current);

    /// <summary>The keymap it is drawing, or would draw, availability and all.</summary>
    internal IReadOnlyList<PrefixEntry> Entries => PrefixPanel.Entries(Current);

    /// <summary>Opens the panel over the workspace. Called by the arming timer, and by the snapshot view.</summary>
    public void Open()
    {
        if (_window is not null)
        {
            return;
        }

        _current = _facts();
        var lines = PrefixPanel.Render(_current);

        // Hug the content, the way QuitOverlay does. Centred *after* WithSize, because the builder reads
        // the bounds set so far and falls back to 80x25 otherwise.
        var desktop = _system.DesktopDimensions;
        var width = Math.Clamp(
            PrefixPanel.MaxWidth(lines) + 4, // a 1-cell border each side plus a cell of margin
            MinimumWidth,
            Math.Max(MinimumWidth, desktop.Width - 6));
        var height = Math.Clamp(
            lines.Count + 2, ChromeRows + 3, Math.Max(ChromeRows + 3, desktop.Height - 2));

        _window = new WindowBuilder(_system)
            .WithTitle("⌃B — pane commands")
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .WithBackgroundColor(new Color(ScreenPalette.MenuBg))
            .HideTitleButtons()
            .Resizable(false)
            .WithSize(width, height)
            .Centered()
            .AddControl(new MarkupControl(new List<string>(lines)))
            .OnClosed((_, _) => Reset())
            .Build();

        _window.PreviewKeyPressed += OnKey;
        _system.AddWindow(_window);
    }

    /// <summary>Takes the panel down without consuming a key — what disarming the prefix does to it.</summary>
    public void Close()
    {
        if (_window is { } window)
        {
            _system.CloseModalWindow(window);
        }
    }

    /// <summary>
    /// Feeds one key to the very handler <c>PreviewKeyPressed</c> raises, for the same reason
    /// <see cref="QuitOverlay.SimulateKey"/> exists: the framework only pumps keys inside <c>Run()</c>,
    /// which a headless test or snapshot never enters.
    /// </summary>
    public void SimulateKey(ConsoleKeyInfo key) => OnKey(this, new KeyPressedEventArgs(key, false));

    private void OnKey(object? sender, KeyPressedEventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        e.Handled = true;

        // Closed first: the command is about to change the workspace this panel is floating over, and
        // splitting or zooming underneath a modal that is still painted would hide what just happened.
        Close();
        _run(e.KeyInfo);
    }

    private void Reset() => _window = null;
}
