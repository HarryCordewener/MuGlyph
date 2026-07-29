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
/// One open settings screen's keyboard state: where the cursor is, what has been changed, whether a
/// field is being typed into, and what a key means. It owns no controls and no window —
/// <see cref="SettingsOverlay"/> asks it what a keystroke meant and acts on the answer — so the whole
/// interaction contract (which keys move, which toggle, which open an edit, what Esc undoes) is
/// unit-testable without a terminal.
/// <para>
/// The screen's <see cref="ScreenModel"/> is rebuilt on every key rather than cached: a keystroke can
/// change how many rows a pane has (picking another world changes the character list), and navigating
/// against last frame's row counts is exactly how a cursor ends up pointing at nothing. An open edit
/// therefore remembers a *position* (pane, row, field ordinal) and its buffer, and re-resolves the
/// field it is typing into out of each fresh model.
/// </para>
/// <para>
/// The keys, in one place: ↑↓ move a row and Home/End jump to a pane's first and last — End is how a
/// pane's trailing buttons are reached without walking the selection to the end of its list; Delete on
/// a list row runs that pane's remove button, so the common case never needs End at all; ⏎
/// activates the focused row when it has something to activate (a field → open an edit, a button →
/// run it) and otherwise saves and closes; ⌃S saves from anywhere; Esc cancels the screen,
/// except while an edit is open, where it abandons the edit and leaves the screen up. Inside an edit,
/// typing inserts, Backspace/Delete remove, ←→/Home/End move the caret, ↑↓ move through the drawn
/// candidate list (narrowing it is what typing does), ⇥ commits and steps to the row's next field, ⏎
/// commits, and Esc reverts.
/// </para>
/// <para>
/// One field kind takes the keyboard whole: a <see cref="ScreenField.Key"/> capture (F4's binding), where
/// the next keystroke is the value and only Esc means anything else. See <see cref="HandleCapture"/>.
/// </para>
/// </summary>
internal sealed class SettingsSession
{
    private readonly Func<ScreenSelection, ScreenModel> _model;
    private FieldEdit? _edit;

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

    /// <summary>Whether a field edit is open — the screens' one modal state.</summary>
    internal bool IsEditing => _edit is not null;

    /// <summary>
    /// The cursor as the renderers should draw it, clamped to the rows that exist right now, carrying
    /// the open field edit when the cursor is on the row that owns it. Returns
    /// <see cref="ScreenFocus.None"/> when the focused pane is empty, so nothing is highlighted.
    /// </summary>
    internal ScreenFocus Focus()
    {
        var model = _model(Selection);
        Selection.Clamp(model.Sizes);
        Selection.Anchor(model.ListSizes);
        if (!Selection.HasSelection(model.Sizes))
        {
            return ScreenFocus.None;
        }

        var field = _edit is { } at ? model.FieldAt(at.Pane, at.Index, at.Field) : null;
        var edit = _edit is { } open && open.Pane == Selection.Pane && open.Index == Selection.Index
            ? new ScreenFieldEdit(
                open.Field,
                open.Text,
                open.Caret,
                open.Error,
                model.RowAt(open.Pane, open.Index).FieldCount,
                field?.Choices,
                field?.ClosedChoices ?? false,
                field?.Capture ?? false)
            : (ScreenFieldEdit?)null;

        return new ScreenFocus(Selection.Pane, Selection.Index, edit);
    }

    /// <summary>
    /// Interprets a keystroke. With no edit open: ↑↓ move within the focused pane, ⇥ / Shift+⇥ change
    /// pane, Space toggles the checkbox under the cursor, ⏎ opens the focused row's first field or —
    /// when the row has none — saves, ⌃S saves, Esc cancels. With an edit open the whole keyboard
    /// belongs to the buffer instead; see the type summary. Anything else is not ours.
    /// </summary>
    internal ScreenAction Handle(ConsoleKeyInfo key)
    {
        var action = Interpret(key);

        // Re-anchored *after* the key, because a key is exactly what moves the cursor between a pane's
        // list and its buttons — and the screen is rebuilt from the anchor on the very next frame.
        Selection.Anchor(_model(Selection).ListSizes);
        return action;
    }

    private ScreenAction Interpret(ConsoleKeyInfo key)
    {
        var model = _model(Selection);
        Selection.Clamp(model.Sizes);
        Selection.Anchor(model.ListSizes);

        if (_edit is not null)
        {
            return HandleEdit(key, model);
        }

        switch (key.Key)
        {
            case ConsoleKey.S when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                return ScreenAction.Save;

            case ConsoleKey.Escape:
                return ScreenAction.Cancel;

            case ConsoleKey.Enter:
                return Activate(model);

            case ConsoleKey.UpArrow:
                return Changed(Selection.Move(-1, model.Sizes));

            case ConsoleKey.DownArrow:
                return Changed(Selection.Move(1, model.Sizes));

            case ConsoleKey.Tab:
                return Changed(key.Modifiers.HasFlag(ConsoleModifiers.Shift)
                    ? Selection.PreviousPane(model.Sizes)
                    : Selection.NextPane(model.Sizes));

            case ConsoleKey.Home:
                return Changed(Selection.MoveTo(0, model.Sizes));

            case ConsoleKey.End:
                return Changed(Selection.MoveTo(int.MaxValue, model.Sizes));

            case ConsoleKey.Delete:
                return Remove(model);

            case ConsoleKey.Spacebar:
                return Toggle(model);

            default:
                return ScreenAction.None;
        }
    }

    /// <summary>
    /// Delete on one of a pane's list rows runs that pane's own remove button — the same command,
    /// the same undo, the same conditions. It exists because the drawn <c>[[- del]]</c> row sits past
    /// the end of the list, so reaching it with ↑↓ walks the cursor over every row on the way; End
    /// steps over them without dragging the selection along, but End is a key nothing on screen names.
    /// Delete acts on the row the cursor is already on, which is the row the user is looking at, and
    /// the header hint says so because <see cref="ScreenModel.HasRemovableRow"/> derives it.
    /// <para>
    /// It deliberately does nothing on a button row: the cursor there already has ⏎, and the row it
    /// would delete is somewhere else on screen.
    /// </para>
    /// </summary>
    private ScreenAction Remove(ScreenModel model)
    {
        if (!model.IsListRow(Selection.Pane, Selection.Index)
            || model.RemoveIn(Selection.Pane) is not { } button)
        {
            return ScreenAction.None;
        }

        if (Edits.Apply(button) is { } select)
        {
            Selection.Seed(Selection.Pane, select);
        }

        return ScreenAction.Redraw;
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

    /// <summary>
    /// ⏎ on a row that has something to activate: a button runs, a record of fields opens its first.
    /// A row that is neither is not activatable, and ⏎ keeps its old meaning there — the footer's
    /// <c>[[⏎]] Save</c>.
    /// </summary>
    private ScreenAction Activate(ScreenModel model)
    {
        var row = model.RowAt(Selection.Pane, Selection.Index);
        if (row.Button is { } button)
        {
            // The button rewrites the very list the cursor navigates, so where the cursor lands is the
            // button's own answer (a new row wants the cursor on it, ready to be named) rather than a
            // rule this method could guess. The next Focus()/Handle() re-projects and clamps it.
            if (Edits.Apply(button) is { } select)
            {
                Selection.Seed(Selection.Pane, select);
            }

            return ScreenAction.Redraw;
        }

        if (!row.IsActivatable)
        {
            return ScreenAction.Save;
        }

        Open(model, 0);
        return ScreenAction.Redraw;
    }

    /// <summary>Opens a field of the focused row, seeding the buffer with its current value.</summary>
    private void Open(ScreenModel model, int field)
    {
        if (model.FieldAt(Selection.Pane, Selection.Index, field) is not { } target)
        {
            _edit = null;
            return;
        }

        var text = target.Get();
        _edit = new FieldEdit(Selection.Pane, Selection.Index, field, text);
    }

    private ScreenAction HandleEdit(ConsoleKeyInfo key, ScreenModel model)
    {
        var edit = _edit!;

        // The model is rebuilt per key, so the row being edited can disappear underneath the buffer
        // (a list the last keystroke shortened). Abandon the edit rather than typing into nothing.
        if (model.FieldAt(edit.Pane, edit.Index, edit.Field) is not { } field)
        {
            _edit = null;
            return ScreenAction.Redraw;
        }

        if (field.Capture)
        {
            return HandleCapture(key, field);
        }

        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _edit = null;
                return ScreenAction.Redraw;

            case ConsoleKey.Enter:
                Commit(field);
                return ScreenAction.Redraw;

            case ConsoleKey.S when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                // ⌃S saves from anywhere, but never by discarding what is on screen: the open field is
                // committed first, and a value that will not validate stops the save rather than being
                // silently dropped.
                return Commit(field) ? ScreenAction.Save : ScreenAction.Redraw;

            case ConsoleKey.Tab:
                return Step(model, field, key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1);

            case ConsoleKey.UpArrow:
                return Cycle(field, -1);

            case ConsoleKey.DownArrow:
                return Cycle(field, 1);

            case ConsoleKey.Backspace:
                return Changed(edit.Backspace());

            case ConsoleKey.Delete:
                return Changed(edit.Delete());

            case ConsoleKey.LeftArrow:
                return Changed(edit.MoveCaret(-1));

            case ConsoleKey.RightArrow:
                return Changed(edit.MoveCaret(1));

            case ConsoleKey.Home:
                return Changed(edit.MoveCaret(int.MinValue));

            case ConsoleKey.End:
                return Changed(edit.MoveCaret(int.MaxValue));

            default:
                if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    edit.Insert(key.KeyChar);
                    return ScreenAction.Redraw;
                }

                return ScreenAction.None;
        }
    }

    /// <summary>
    /// A keystroke while a key capture is armed. There is no buffer to type into here: the keystroke
    /// <em>is</em> the value, so it is written into the edit and committed at once, and the field's own
    /// validator does the refusing — a chord this host cannot deliver, or one another binding already
    /// holds, leaves the capture armed carrying the reason instead of writing a row that would never fire.
    /// <para>
    /// <b>Esc is never a candidate.</b> It is the way out of every modal state these screens have, and a
    /// capture that swallowed it would be the one trap this whole mode could set: nothing else on screen
    /// ends the prompt, because ⏎ and ⇥ are themselves keys someone might reasonably want to bind. A key
    /// with no descriptor at all (a lone modifier, an undecoded sequence) is swallowed and the capture
    /// stays armed — it is a keystroke that never happened as far as any binding is concerned.
    /// </para>
    /// </summary>
    private ScreenAction HandleCapture(ConsoleKeyInfo key, ScreenField field)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            _edit = null;
            return ScreenAction.Redraw;
        }

        if (MacroKeys.Capture(key) is not { } descriptor)
        {
            return ScreenAction.Consumed;
        }

        _edit!.Replace(descriptor);
        Commit(field);
        return ScreenAction.Redraw;
    }

    /// <summary>
    /// Validates the buffer and, if it holds, writes it through the undo log and closes the edit. A
    /// rejected buffer stays open and carries the reason, so the field is marked rather than the
    /// keystroke that produced it being thrown away mid-word.
    /// </summary>
    private bool Commit(ScreenField field)
    {
        var edit = _edit!;
        if (Edits.Apply(field, edit.Text) is { } error)
        {
            edit.Error = error;
            return false;
        }

        _edit = null;
        return true;
    }

    /// <summary>⇥ inside an edit: commit this field, then open the row's next one, wrapping.</summary>
    private ScreenAction Step(ScreenModel model, ScreenField field, int direction)
    {
        var edit = _edit!;
        var from = edit.Field;
        var count = model.RowAt(edit.Pane, edit.Index).FieldCount;
        if (!Commit(field))
        {
            return ScreenAction.Redraw;
        }

        if (count <= 1)
        {
            return ScreenAction.Redraw;
        }

        Open(model, ((from + direction) % count + count) % count);
        return ScreenAction.Redraw;
    }

    /// <summary>
    /// ↑↓ inside an edit: move through the candidate list the chrome is drawing beneath the field, and
    /// put the entry landed on into the buffer. The highlight and the buffer are deliberately the same
    /// thing rather than two cursors — a separate highlight would give ⏎ two meanings (take the
    /// highlighted entry, or commit what is typed) and Esc two levels to back out of, inside a screen
    /// whose only modal state so far is this edit. Keeping them one means the value visibly moves as the
    /// list is walked, which is what a picker is for.
    /// <para>
    /// A field with no choices, and one whose buffer has narrowed the list to nothing, both have nowhere
    /// to move to and swallow the key with the buffer untouched — on an open field that buffer is a name
    /// being typed for the first time, and an arrow key must not eat it.
    /// </para>
    /// </summary>
    private ScreenAction Cycle(ScreenField field, int direction)
    {
        var edit = _edit!;
        if (field.Cycle(edit.Text, direction) is not { } next)
        {
            return ScreenAction.Consumed;
        }

        edit.Replace(next);
        return ScreenAction.Redraw;
    }

    private static ScreenAction Changed(bool moved) => moved ? ScreenAction.Redraw : ScreenAction.Consumed;

    /// <summary>
    /// The buffer behind an open edit: which row and field it belongs to, the text being typed, where
    /// the caret is in it, and the reason the last commit was refused. Held by position rather than by
    /// field so it survives the model being rebuilt on every key.
    /// </summary>
    private sealed class FieldEdit
    {
        internal FieldEdit(int pane, int index, int field, string text)
        {
            Pane = pane;
            Index = index;
            Field = field;
            Text = text;
            Caret = text.Length;
        }

        internal int Pane { get; }

        internal int Index { get; }

        internal int Field { get; }

        internal string Text { get; private set; }

        internal int Caret { get; private set; }

        internal string? Error { get; set; }

        internal void Insert(char c)
        {
            Text = Text.Insert(Caret, c.ToString());
            Caret++;
            Error = null;
        }

        internal void Replace(string text)
        {
            Text = text;
            Caret = text.Length;
            Error = null;
        }

        internal bool Backspace()
        {
            if (Caret == 0)
            {
                return false;
            }

            Text = Text.Remove(Caret - 1, 1);
            Caret--;
            Error = null;
            return true;
        }

        internal bool Delete()
        {
            if (Caret >= Text.Length)
            {
                return false;
            }

            Text = Text.Remove(Caret, 1);
            Error = null;
            return true;
        }

        /// <summary>
        /// Moves the caret, clamped to the buffer. <see cref="int.MinValue"/> and
        /// <see cref="int.MaxValue"/> are Home and End; the arithmetic is done wide so neither
        /// overflows.
        /// </summary>
        internal bool MoveCaret(int delta)
        {
            var next = (int)Math.Clamp((long)Caret + delta, 0, Text.Length);
            if (next == Caret)
            {
                return false;
            }

            Caret = next;
            return true;
        }
    }
}
