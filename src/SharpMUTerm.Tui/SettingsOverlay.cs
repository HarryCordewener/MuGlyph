using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Helpers;
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
    /// <returns>Whether the screen consumed the key, as the framework would see it.</returns>
    public bool SimulateKey(ConsoleKeyInfo key)
    {
        var args = new KeyPressedEventArgs(key, false);
        OnKey(this, args);
        return args.Handled;
    }

    /// <summary>
    /// Feeds one paste to the open screen through the very handler the driver's <c>Paste</c> event
    /// raises — <see cref="SimulateKey"/>'s counterpart, and for the same reason: the framework only
    /// pumps input inside <c>Run()</c>, so a headless test has no other way in.
    /// </summary>
    public void SimulatePaste(string text) => Paste(text);

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
        _system.ConsoleDriver.Paste += OnPaste;
        _system.AddWindow(_window);
    }

    /// <summary>
    /// A terminal paste, taken at the driver rather than from the window.
    /// <para>
    /// The framework delivers a paste to the active window's <em>focused control</em>, and only when
    /// that control is an <c>IPasteTarget</c> — and a settings screen has no such control to offer.
    /// The screens are markup rendered into controls that are rebuilt wholesale on every key
    /// (<see cref="Refresh"/>), and the field being edited is a buffer inside
    /// <see cref="SettingsSession"/> driven from <c>PreviewKeyPressed</c>; there is nothing for the
    /// framework to hand a paste to, so it dropped every one of them. Listening at the driver is the
    /// same seam the app already uses for pane drags, and for the same reason.
    /// </para>
    /// <para>
    /// It cannot double up: the window's own delivery is a guaranteed no-op while a screen is open,
    /// precisely because no control on it accepts paste. Give a screen a focusable
    /// <c>IPasteTarget</c> one day and that stops being true — the two paths would both fire.
    /// </para>
    /// </summary>
    private void OnPaste(object? sender, string text) => _system.EnqueueOnUIThread(() => Paste(text));

    /// <summary>Hands a paste to the open screen through the same seam its typing uses.</summary>
    private void Paste(string text)
    {
        if (_window is null || _binding is not { } binding)
        {
            return;
        }

        Apply(binding, binding.Session.Paste(text));
    }

    private void OnKey(object? sender, KeyPressedEventArgs e)
    {
        if (_window is null || _binding is not { } binding)
        {
            return;
        }

        // ⌃V is the app-level paste, and it was as dead on these screens as the terminal's own. The
        // framework reads the clipboard for the focused control when that control accepts paste, and a
        // settings screen has none to offer, so the read is done here instead. ⌃⇧V is not this chord:
        // most emulators turn it into a bracketed paste, which arrives at OnPaste. Only while a field
        // is open, so no clipboard tool is spawned for a chord with nowhere to go — and never over a
        // key capture, where a chord is the value being recorded.
        if (e.KeyInfo.Key == ConsoleKey.V
            && e.KeyInfo.Modifiers.HasFlag(ConsoleModifiers.Control)
            && binding.Session.IsEditing
            && !binding.Session.IsCapturingKey)
        {
            e.Handled = Apply(
                binding, binding.Session.Paste(ClipboardHelper.GetTextWithTimeout() ?? string.Empty));
            return;
        }

        e.Handled = Apply(binding, binding.Session.Handle(e.KeyInfo));
    }

    /// <summary>
    /// Acts on what a screen said an input meant, and reports whether the input was consumed. Shared by
    /// the key and the paste paths so a screen cannot answer the two differently.
    /// </summary>
    private bool Apply(ScreenBinding binding, ScreenAction action)
    {
        switch (action)
        {
            case ScreenAction.Cancel:
                Cancel();
                return true;

            case ScreenAction.Save:
                binding.Session.Edits.Commit();
                _save();
                Close();
                return true;

            case ScreenAction.Redraw:
                Refresh();
                return true;

            case ScreenAction.Consumed:
                return true;

            case ScreenAction.None:
            default:
                return false;
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
        // The driver outlives every screen, so the paste hook has to come off with the window it was
        // put on for — otherwise a closed screen keeps listening and a later paste reaches a session
        // that is no longer on screen.
        _system.ConsoleDriver.Paste -= OnPaste;
        _window = null;
        _binding = null;
    }
}
