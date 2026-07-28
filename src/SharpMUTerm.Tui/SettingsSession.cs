namespace SharpMUTerm.Tui;

/// <summary>What a keystroke asked a settings screen to do.</summary>
internal enum ScreenAction
{
    /// <summary>Not a settings-screen key — leave it for the framework.</summary>
    None,

    /// <summary>Ours, but nothing changed (↑ on the first row); swallow it and leave the screen alone.</summary>
    Consumed,

    /// <summary>State changed — rebuild the screen from config.</summary>
    Redraw,

    /// <summary>Commit the pending edits and close.</summary>
    Save,

    /// <summary>Discard the pending edits and close.</summary>
    Cancel,
}

/// <summary>
/// One open settings screen's keyboard state: where the cursor is, what has been changed, and what a
/// key means. It owns no controls and no window — <see cref="SettingsOverlay"/> asks it what a
/// keystroke meant and acts on the answer — so the whole interaction contract (which keys move, which
/// toggle, what Esc undoes) is unit-testable without a terminal.
/// <para>
/// The screen's <see cref="ScreenModel"/> is rebuilt on every key rather than cached: a keystroke can
/// change how many rows a pane has (picking another world changes the character list), and navigating
/// against last frame's row counts is exactly how a cursor ends up pointing at nothing.
/// </para>
/// </summary>
internal sealed class SettingsSession
{
    private readonly Func<ScreenSelection, ScreenModel> _model;

    /// <summary>
    /// Binds a screen to the factory that projects live config into navigable rows. The factory is
    /// handed the selection rather than closing over it, because what a pane *contains* usually
    /// depends on what the pane above it has selected — and a screen can't close over a session it is
    /// in the middle of constructing. How many panes the screen has is read off a first projection, so
    /// the renderer stays the single source of truth for the screen's shape.
    /// </summary>
    internal SettingsSession(Func<ScreenSelection, ScreenModel> model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        Selection = new ScreenSelection(model(new ScreenSelection(1)).PaneCount);
    }

    /// <summary>Where the keyboard is. Seeded by the screen before it first opens.</summary>
    internal ScreenSelection Selection { get; }

    /// <summary>The pending changes Esc undoes and ⏎ keeps.</summary>
    internal ScreenEdits Edits { get; } = new();

    /// <summary>
    /// The cursor as the renderers should draw it, clamped to the rows that exist right now. Returns
    /// <see cref="ScreenFocus.None"/> when the focused pane is empty, so nothing is highlighted.
    /// </summary>
    internal ScreenFocus Focus()
    {
        var model = _model(Selection);
        Selection.Clamp(model.Sizes);
        return Selection.HasSelection(model.Sizes)
            ? new ScreenFocus(Selection.Pane, Selection.Index)
            : ScreenFocus.None;
    }

    /// <summary>
    /// Interprets a keystroke: ↑↓ move within the focused pane, ⇥ / Shift+⇥ change pane, Space
    /// toggles the checkbox under the cursor, ⏎ saves, Esc cancels. Anything else is not ours.
    /// </summary>
    internal ScreenAction Handle(ConsoleKeyInfo key)
    {
        var model = _model(Selection);
        Selection.Clamp(model.Sizes);

        switch (key.Key)
        {
            case ConsoleKey.Escape:
                return ScreenAction.Cancel;

            case ConsoleKey.Enter:
                return ScreenAction.Save;

            case ConsoleKey.UpArrow:
                return Changed(Selection.Move(-1, model.Sizes));

            case ConsoleKey.DownArrow:
                return Changed(Selection.Move(1, model.Sizes));

            case ConsoleKey.Tab:
                return Changed(key.Modifiers.HasFlag(ConsoleModifiers.Shift)
                    ? Selection.PreviousPane(model.Sizes)
                    : Selection.NextPane(model.Sizes));

            case ConsoleKey.Spacebar:
                return Toggle(model);

            default:
                return ScreenAction.None;
        }
    }

    private ScreenAction Toggle(ScreenModel model)
    {
        if (model.ToggleAt(Selection.Pane, Selection.Index) is not { } toggle)
        {
            return ScreenAction.Consumed;
        }

        Edits.Apply(toggle);
        return ScreenAction.Redraw;
    }

    private static ScreenAction Changed(bool moved) => moved ? ScreenAction.Redraw : ScreenAction.Consumed;
}
