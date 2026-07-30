using System.Text;

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

    /// <summary>
    /// Leave the screen. Nothing is discarded on the way out: every committed edit is already in config
    /// and already written to disk (see <see cref="ScreenEdits"/>), so closing is navigation rather than
    /// a transaction boundary. The one thing that isn't settled is a <em>deletion</em>, which the host
    /// asks about as it closes.
    /// <para>
    /// It replaces an action called <c>Cancel</c> that replayed an undo log over the whole screen, which
    /// is the bug: the header offered <c>F5/Esc close</c>, and "close" silently meant "throw away
    /// everything you just typed".
    /// </para>
    /// </summary>
    Close,
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
/// The keys, in one place: ↑↓ move a row (and, where a screen stacks two panes in one column, carry on
/// into the next one) and ←→ move to the pane drawn beside this one; ⇥/⇧⇥ cycle the panes in the order
/// they are drawn, which is the only movement that wraps and so the only one guaranteed to reach a pane
/// standing alone in its column; Home/End jump to a pane's first and last row; Delete on a list row runs
/// that pane's remove button, which is the <em>only</em> way to remove one — the drawn removal row is not
/// a cursor stop (see <see cref="ScreenModel.Sizes"/>); ⏎ activates the focused row when it has something
/// to activate (a field → open an edit, a button → run it) and otherwise closes the screen; Esc closes
/// the screen. Inside an edit, typing inserts, Backspace/Delete remove, ←→/Home/End move the caret, ↑↓
/// move through the drawn candidate list (narrowing it is what typing does), ⇥ commits and steps to the
/// row's next field, ⏎ commits, and Esc reverts.
/// </para>
/// <para>
/// <b>Esc is layered, and the layering is a rule about scope rather than two special cases:</b> it backs
/// out of one level of nesting per press, and it discards only work that has not been confirmed. Inside a
/// field it abandons the buffer — which config never saw — and leaves the cursor on the row. On the row it
/// closes the screen, reverting nothing, because everything still on the screen was confirmed by the ⏎
/// that committed it. The one exception is a deletion, whose subject cannot be retyped, and the host asks
/// about those as it closes. Written out at length in <see cref="ScreenEdits"/>; both halves look
/// arbitrary on their own and obvious together, which is why the rule is written down rather than
/// inferred.
/// </para>
/// <para>
/// There is no save key and no ⌃S. Persistence is per committed change (see <see cref="ScreenEdits"/>), so
/// a chord claiming to save would be claiming to do something already done — and one that quietly closed
/// the screen instead would be worse than not having it.
/// </para>
/// <para>
/// The arrows therefore mean two different things, and which one is decided by exactly one bit of
/// state: an open edit takes the whole keyboard, so ←→ are caret movement and ↑↓ walk the dropdown for
/// as long as one is up, and are navigation the rest of the time. That is the same split ⇥ already
/// lived under — pane, or next field of the row — and it is checked first, before any navigation key is
/// looked at.
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
    /// <param name="save">
    /// Persists the configuration, run after every accepted change rather than on the way out — see
    /// <see cref="ScreenEdits"/> for why that is where it belongs. Null in the tests that only exercise
    /// navigation.
    /// </param>
    internal SettingsSession(Func<ScreenSelection, ScreenModel> model, Action? save = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        Edits = new ScreenEdits(save);
        Selection = new ScreenSelection(model(new ScreenSelection(1)).PaneCount);
    }

    /// <summary>Where the keyboard is. Seeded by the screen before it first opens.</summary>
    internal ScreenSelection Selection { get; }

    /// <summary>
    /// The screen's one path into config, and the log of the deletions its host reviews on close.
    /// </summary>
    internal ScreenEdits Edits { get; }

    /// <summary>Whether a field edit is open — the screens' one modal state.</summary>
    internal bool IsEditing => _edit is not null;

    /// <summary>
    /// Whether the open edit is a <see cref="ScreenField.Key"/> capture: the one state in these screens
    /// where a chord is a <em>value</em> rather than a command, so nothing outside may intercept one.
    /// </summary>
    internal bool IsCapturingKey =>
        _edit is { } edit && _model(Selection).FieldAt(edit.Pane, edit.Index, edit.Field) is { Capture: true };

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
                field?.Capture ?? false,
                field?.Masked ?? false)
            : (ScreenFieldEdit?)null;

        return new ScreenFocus(Selection.Pane, Selection.Index, edit, Enter(model));
    }

    /// <summary>
    /// What ⏎ would do right now, read off the focused row — the same question <see cref="Activate"/>
    /// answers, asked by the action bar so its chip can say it. Kept beside that method so the two
    /// cannot drift: if one grows a case, the other has to.
    /// </summary>
    private ScreenEnter Enter(ScreenModel model)
    {
        var row = model.RowAt(Selection.Pane, Selection.Index);
        if (row.Button is not null)
        {
            return ScreenEnter.Add;
        }

        return row.FieldCount > 0 ? ScreenEnter.Edit : ScreenEnter.Close;
    }

    /// <summary>
    /// Interprets a keystroke. With no edit open: ↑↓ move within the focused pane, ←→ and ⇥ / Shift+⇥
    /// change pane, Space toggles the checkbox under the cursor, ⏎ opens the focused row's first field or —
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
            case ConsoleKey.Escape:
                return ScreenAction.Close;

            case ConsoleKey.Enter:
                return Activate(model);

            case ConsoleKey.UpArrow:
                return Changed(Selection.MoveVertically(-1, model.Sizes, model.Layout));

            case ConsoleKey.DownArrow:
                return Changed(Selection.MoveVertically(1, model.Sizes, model.Layout));

            case ConsoleKey.LeftArrow:
                return Changed(Selection.MoveBeside(-1, model.Sizes, model.Layout));

            case ConsoleKey.RightArrow:
                return Changed(Selection.MoveBeside(1, model.Sizes, model.Layout));

            case ConsoleKey.Tab:
                return Changed(key.Modifiers.HasFlag(ConsoleModifiers.Shift)
                    ? Selection.PreviousPane(model.Sizes, model.Layout)
                    : Selection.NextPane(model.Sizes, model.Layout));

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
    /// Delete on one of a pane's list rows runs that pane's own remove button, and is now the only way
    /// to run it: the drawn removal row sits past the end of the list and is no longer a cursor stop,
    /// because reaching it with ↑↓ walked the selection to the last item and so could only ever delete
    /// that one (<see cref="ScreenModel.Sizes"/>). Delete acts on the row the cursor is already on,
    /// which is the row the user is looking at, and the header hint says so because
    /// <see cref="ScreenModel.HasRemovableRow"/> derives it.
    /// <para>
    /// It deliberately does nothing on a button row: the cursor there is on <c>[[+ …]]</c>, and the row
    /// it would delete is somewhere else on screen.
    /// </para>
    /// <para>
    /// The deletion happens at once — there is no per-press confirmation — and is recorded in
    /// <see cref="Edits"/> so the screen can ask about the batch of them as it closes. Three deletions
    /// are one question, not three.
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
    /// <para>
    /// A button that <em>built</em> something goes one step further and opens the new row's first
    /// field, which on every list screen is its name. Leaving the cursor on the row was already the
    /// rule and was one keystroke short of useful: the thing you have just made is unnamed, so the very
    /// next key was always going to be ⏎ again. It matters most on F5, where a character's editable
    /// values are drawn in the CHARACTER form at the foot of the screen rather than on the list row the
    /// cursor sits on — pressing <c>[[+ add character]]</c> now <em>puts</em> you in that form, which
    /// teaches the one route on this screen that nothing else pointed at. A removal deliberately opens
    /// nothing: the row under the cursor afterwards is a survivor, not something anybody asked to edit.
    /// </para>
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
                if (button.Kind == ScreenButtonKind.Add)
                {
                    // Re-projected first: the list the button just changed is the one this reads the
                    // new row's fields out of. Open() no-ops on a row that hasn't any.
                    Open(_model(Selection), 0);
                }
            }

            return ScreenAction.Redraw;
        }

        if (!row.IsActivatable)
        {
            // ⏎ on a row with nothing to open closes the screen, the way it always has. What changed is
            // what closing means: it used to be "commit and close" against a screen that would otherwise
            // have discarded everything, and it is now the same thing Esc does, because there is no
            // longer a state in which those two answers differ.
            return ScreenAction.Close;
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
                // The inner layer of Esc, and the only place on these screens that still discards
                // anything: the buffer is dropped, the field closes, and the committed value it was
                // opened on is untouched because config never saw a keystroke of it. One more Esc, now
                // on the row, closes the screen — and reverts nothing, because by then everything left
                // is confirmed. See the scope rule on this type.
                _edit = null;
                return ScreenAction.Redraw;

            case ConsoleKey.Enter:
                Commit(field);
                return ScreenAction.Redraw;

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
    /// Pastes a block of text into the open field edit — the same insert typing does, with a string
    /// instead of a character. It is a separate entry point rather than a key because a paste arrives
    /// as one atomic block from the terminal (or the clipboard) and never as keystrokes; see
    /// <see cref="SettingsOverlay"/> for why the framework cannot deliver it to a screen itself.
    /// <para>
    /// A paste with no edit open is not ours: these screens have no text anywhere else, and opening one
    /// on the user's behalf would put a clipboard into a field they had not chosen. A
    /// <see cref="ScreenField.Capture"/> field swallows it for the same reason it swallows typing —
    /// its value is a keystroke, and there is no buffer to insert into.
    /// </para>
    /// </summary>
    internal ScreenAction Paste(string text)
    {
        if (string.IsNullOrEmpty(text) || _edit is not { } edit)
        {
            return ScreenAction.None;
        }

        // Same guard HandleEdit opens with: the row being edited can have gone away underneath the
        // buffer, and pasting into nothing is worse than abandoning the edit.
        if (_model(Selection).FieldAt(edit.Pane, edit.Index, edit.Field) is not { } field)
        {
            _edit = null;
            return ScreenAction.Redraw;
        }

        if (field.Capture)
        {
            return ScreenAction.Consumed;
        }

        var flattened = Flatten(text);
        if (flattened.Length == 0)
        {
            return ScreenAction.Consumed;
        }

        edit.Insert(flattened);
        return ScreenAction.Redraw;
    }

    /// <summary>
    /// Reduces pasted text to something a one-line field can hold: each run of line breaks becomes a
    /// single space, every other control character is dropped, and the result is trimmed.
    /// <para>
    /// A settings field is one row of a fixed-width list — <see cref="ScreenField.Name"/> and the
    /// window-name field already refuse control characters for exactly that reason — so a multi-line
    /// clipboard has to be answered here rather than written through to a validator that would only
    /// reject the lot. <b>Collapse, not delete:</b> removing the break outright would run the words on
    /// either side together (<c>hello\nworld</c> → <c>helloworld</c>), quietly changing what was
    /// pasted; a space keeps it readable and is what SharpConsoleUI's own single-line
    /// <c>PromptControl</c> does. A <em>run</em> of breaks is one space rather than several, because a
    /// blank line between two pasted lines is a paragraph, not two words apart. Trimming follows: the
    /// leading or trailing space a break at either end would leave is an artefact of the flattening,
    /// not something the user copied.
    /// </para>
    /// <para>
    /// This is not a validation bypass. The flattened value is committed through
    /// <see cref="Commit"/> exactly like a typed one and is refused there on the same terms — a paste
    /// that flattens to nothing a field will accept still fails, and says why.
    /// </para>
    /// <para>
    /// The rule is single-line fields, not paste in general: the main command line has rows for
    /// newlines and <see cref="InputBarControl.Paste"/> deliberately keeps them.
    /// </para>
    /// </summary>
    private static string Flatten(string text)
    {
        var flattened = new StringBuilder(text.Length);
        var pendingBreak = false;
        foreach (var c in text)
        {
            // \r, \n and \r\n are all one break; a run of them collapses to a single space, emitted only
            // once something follows it (which is what keeps a trailing run from leaving one behind).
            if (c is '\r' or '\n')
            {
                pendingBreak = flattened.Length > 0;
                continue;
            }

            if (char.IsControl(c))
            {
                continue;
            }

            if (pendingBreak)
            {
                flattened.Append(' ');
                pendingBreak = false;
            }

            flattened.Append(c);
        }

        return flattened.ToString().Trim();
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
    /// <para>
    /// A field that moved its own row answers where it went (<see cref="ScreenField.Follow"/>) and the
    /// cursor goes with it. Only the owning-set field on the four flattened screens does this, and it
    /// has to: those panes are one column of every set's items, so committing a move genuinely slides
    /// the row out from under the cursor.
    /// </para>
    /// </summary>
    private bool Commit(ScreenField field)
    {
        var edit = _edit!;
        if (Edits.Apply(field, edit.Text) is { } error)
        {
            edit.Error = error;
            return false;
        }

        if (field.Follow is { } follow)
        {
            Selection.Seed(edit.Pane, follow());
        }

        _edit = null;
        return true;
    }

    /// <summary>
    /// ⇥ inside an edit: commit this field, then open the row's next one, wrapping. The model is
    /// re-projected after the commit rather than reused, because the commit is allowed to have changed
    /// the pane — a committed <c>set</c> field moves its row somewhere else in the flattened list, and
    /// stepping through the stale projection would open the next field of whichever row used to be
    /// there.
    /// </summary>
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

        Open(_model(Selection), ((from + direction) % count + count) % count);
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

        internal void Insert(char c) => Insert(c.ToString());

        /// <summary>Inserts a block at the caret — one paste, not a run of keystrokes.</summary>
        internal void Insert(string text)
        {
            Text = Text.Insert(Caret, text);
            Caret += text.Length;
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
