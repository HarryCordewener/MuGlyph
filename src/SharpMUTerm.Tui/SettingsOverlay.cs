using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using static SharpMUTerm.Tui.ScreenPalette;

namespace SharpMUTerm.Tui;

/// <summary>
/// One openable settings screen: the keyboard state it edits with, and the factory that renders that
/// state into a control tree. The factory closes over the session, so re-invoking it after a key is
/// how the screen reflects a moved cursor or a flipped checkbox.
/// </summary>
internal readonly record struct ScreenBinding(SettingsSession Session, Func<IWindowControl> Control);

/// <summary>
/// A full-screen overlay hosting the F2–F9 settings screens. The design specifies these as
/// full-screen surfaces, not floating dialogs: this maximises a frameless modal (no title bar,
/// buttons, or resize grip) with a deep panel background. Every screen supplies a composed tree of
/// real panels.
/// <para>
/// It is also where the screens get their keyboard. Keys arrive on <c>PreviewKeyPressed</c>, which
/// runs before any control sees them, and are handed to the screen's <see cref="SettingsSession"/>;
/// this class only acts on the answer — rebuild the content, commit and close, or discard and close.
/// The interaction rules live in the session so they stay testable; the renderers stay pure.
/// </para>
/// </summary>
internal sealed class SettingsOverlay
{
    private readonly ConsoleWindowSystem _system;
    private readonly Action _save;

    private Window? _window;
    private ConsoleKey _openKey;
    private ScreenBinding? _binding;

    /// <summary>
    /// <paramref name="save"/> persists the configuration the screens edit in place — the ⏎ Save
    /// action on every screen's action bar.
    /// </summary>
    public SettingsOverlay(ConsoleWindowSystem system, Action save)
    {
        _system = system;
        _save = save;
    }

    /// <summary>The F-key of the currently open screen, or null when closed.</summary>
    public ConsoleKey? OpenKey => _window is null ? null : _openKey;

    public bool IsOpen => _window is not null;

    /// <summary>
    /// How many edits the open screen is holding that were never saved — zero when none is open. Read by
    /// the quit confirmation, which is the one place something outside a screen can end them.
    /// </summary>
    public int PendingEdits => _binding?.Session.Edits.Count ?? 0;

    /// <summary>
    /// Opens a screen, or closes it when its own F-key is pressed again. Closing this way discards
    /// pending edits, exactly like Esc — the F-key is a toggle, not a commit.
    /// </summary>
    public void Toggle(ConsoleKey key, Func<ScreenBinding> binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (_window is not null)
        {
            var reopening = _openKey != key;
            Cancel();
            if (!reopening)
            {
                return;
            }
        }

        Open(key, binding());
    }

    /// <summary>Renders a screen into a headless frame (used by snapshots).</summary>
    public void OpenForSnapshot(ConsoleKey key, ScreenBinding binding) => Open(key, binding);

    /// <summary>
    /// Feeds one key to the open screen through the very handler <c>PreviewKeyPressed</c> raises, so a
    /// snapshot can show a screen in a state only the keyboard can reach (a field mid-edit) without
    /// any of it being faked. Keys cannot be driven in through the console driver here: the framework
    /// only subscribes its key pump inside <c>Run()</c>, which a headless snapshot never enters.
    /// </summary>
    public void SimulateKey(ConsoleKeyInfo key) => OnKey(this, new KeyPressedEventArgs(key, false));

    private void Open(ConsoleKey key, ScreenBinding binding)
    {
        _openKey = key;
        _binding = binding;

        _window = new WindowBuilder(_system)
            .AsModal()
            .Maximized()
            .Frameless()
            .WithColors(new Color(PanelFg), new Color(PanelBg))
            .AddControl(binding.Control())
            .OnClosed((_, _) => Reset())
            .Build();

        _window.PreviewKeyPressed += OnKey;
        _system.AddWindow(_window);
    }

    private void OnKey(object? sender, KeyPressedEventArgs e)
    {
        if (_window is null || _binding is not { } binding)
        {
            return;
        }

        switch (binding.Session.Handle(e.KeyInfo))
        {
            case ScreenAction.Cancel:
                Cancel();
                e.Handled = true;
                break;

            case ScreenAction.Save:
                binding.Session.Edits.Commit();
                _save();
                Close();
                e.Handled = true;
                break;

            case ScreenAction.Redraw:
                Refresh();
                e.Handled = true;
                break;

            case ScreenAction.Consumed:
                e.Handled = true;
                break;

            case ScreenAction.None:
            default:
                break;
        }
    }

    /// <summary>
    /// Rebuilds the screen from the (now changed) config and cursor. The factory produces a whole
    /// tree, so swapping the window's single control is both the simplest and the most honest refresh:
    /// nothing can drift out of step with the renderers, because everything is re-rendered.
    /// </summary>
    private void Refresh()
    {
        if (_window is not { } window || _binding is not { } binding)
        {
            return;
        }

        window.ClearControls();
        window.AddControl(binding.Control());
        window.Invalidate(redrawAll: true);
    }

    /// <summary>Discards the screen's pending edits and closes it — Esc, and the F-key toggle.</summary>
    private void Cancel()
    {
        _binding?.Session.Edits.Revert();
        Close();
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
        _binding = null;
    }
}
