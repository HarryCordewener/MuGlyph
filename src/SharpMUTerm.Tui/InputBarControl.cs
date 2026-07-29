using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Extensions;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace SharpMUTerm.Tui;

/// <summary>
/// One command line: a prompt, a wrapping buffer that grows with what is typed, and the keys that edit
/// it. Two of these make up the input area — the everyday bar and the optional second one — and the
/// pair is what <see cref="SharpMUTermApp"/> routes typing into.
/// <para>
/// It is our control rather than the framework's <c>PromptControl</c> because that one cannot be made
/// to do this: it stores a single line (its setter turns <c>\n</c> into a space), it measures one row
/// tall, and it scrolls sideways when the text outgrows the field. It also unfocuses itself on ⏎
/// (<c>UnfocusOnEnter</c>, on by default), which is exactly the behaviour the maintainer reported as
/// "pressing enter should not lose focus". None of that is configurable from outside, and SharpConsoleUI
/// is a dependency here, not a fork — so the control is written against its public painting API
/// (<see cref="BaseControl"/>, <see cref="CharacterBuffer"/>, <see cref="MarkupParser"/>) instead.
/// </para>
/// <para>
/// The parts worth testing are deliberately not in here: the text edits are <see cref="InputBuffer"/>
/// and the geometry is <see cref="InputLayout"/>, both terminal-free. What is left is the key table,
/// the painting, and the focus rules.
/// </para>
/// </summary>
internal sealed class InputBarControl : BaseControl, IInteractiveControl, IFocusableControl,
    IMouseAwareControl, ILogicalCursorProvider, ICursorShapeProvider, IPasteTarget
{
    /// <summary>The buffer this bar edits. Public so the app can read the draft without a round trip.</summary>
    internal InputBuffer Buffer { get; } = new();

    private string _prompt = string.Empty;
    private bool _armed = true;
    private int _minRows = 3;
    private int _maxRows = 8;
    private int _scroll;

    /// <summary>Raised when ⏎ sends the line. The payload is the text as it was, newlines included.</summary>
    internal event Action<string>? Entered;

    /// <summary>Raised whenever an edit changes the text — what records the per-window draft.</summary>
    internal event Action<string>? Changed;

    /// <summary>Raised when ⇥ asks for the other bar, or a click asks for this one.</summary>
    internal event Action<InputBarControl>? ActivationRequested;

    /// <summary>Raised when ⇥ should hand the caret to the bar after this one.</summary>
    internal event Action? CycleRequested;

    /// <summary>Whether ⇥ has somewhere to go — false while this is the only bar on screen.</summary>
    internal Func<bool> HasSibling { get; set; } = () => false;

    /// <summary>The prompt drawn on the bar's first row, as markup. Continuation rows indent past it.</summary>
    internal string Prompt
    {
        get => _prompt;
        set => SetProperty(ref _prompt, value ?? string.Empty);
    }

    /// <summary>Whether ⏎ sends from this bar. The unarmed bar is dimmed and shows no caret.</summary>
    internal bool Armed
    {
        get => _armed;
        set
        {
            if (_armed == value)
            {
                return;
            }

            _armed = value;
            Invalidate(Invalidation.Repaint);
        }
    }

    /// <summary>The bar's height before anything wraps — F8's <c>input height</c>.</summary>
    internal int MinRows
    {
        get => _minRows;
        set => SetProperty(ref _minRows, value);
    }

    /// <summary>The most rows it grows to before scrolling inside itself — F8's <c>grows to</c>.</summary>
    internal int MaxRows
    {
        get => _maxRows;
        set => SetProperty(ref _maxRows, value);
    }

    /// <summary>The band colour behind an armed bar.</summary>
    internal Color BandColor { get; set; } = new(0x33, 0x39, 0x4c, 255);

    /// <summary>The band colour behind the bar ⏎ will not send from.</summary>
    internal Color IdleBandColor { get; set; } = new(0x26, 0x2b, 0x3a, 255);

    /// <summary>The colour typed text is drawn in.</summary>
    internal Color TextColor { get; set; } = new(0xe6, 0xe6, 0xe6, 255);

    /// <summary>The colour typed text is drawn in on the bar ⏎ will not send from.</summary>
    internal Color IdleTextColor { get; set; } = new(0x8a, 0x8f, 0x9e, 255);

    /// <summary>The whole buffer. Setting it is a recall, not a keystroke: it raises no change event.</summary>
    internal string Text
    {
        get => Buffer.Text;
        set
        {
            if (Buffer.Set(value))
            {
                Invalidate(Invalidation.Relayout);
            }
        }
    }

    /// <summary>
    /// Puts text in and reports the change, which is what a link's <c>prompt:</c> payload wants: the
    /// text arrived from outside, but it is the user's draft now and the window should keep it.
    /// </summary>
    internal void SetAndNotify(string? text)
    {
        if (Buffer.Set(text))
        {
            Invalidate(Invalidation.Relayout);
            Changed?.Invoke(Buffer.Text);
        }
    }

    /// <summary>
    /// Moves the caret one visual row, reporting whether it had anywhere to go. The app asks first and
    /// only falls back to history recall when the answer is no, so ↑ inside a grown command line moves
    /// the caret and ↑ on its first row still recalls.
    /// </summary>
    internal bool TryMoveRow(int delta)
    {
        if (!Buffer.MoveRow(delta, FieldWidth()))
        {
            return false;
        }

        Invalidate(Invalidation.Repaint);
        return true;
    }

    /// <summary>How tall the bar currently is, in rows — what the layout asks for and what tests assert.</summary>
    internal int Rows() =>
        InputLayout.Height(InputLayout.Wrap(Buffer.Text, FieldWidth()).Count, MinRows, MaxRows);

    /// <inheritdoc/>
    public override int? ContentWidth => MarkupParser.StripLength(_prompt) + 10 + Margin.Left + Margin.Right;

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc/>
    public bool CanReceiveFocus => IsEnabled;

    /// <inheritdoc/>
    public bool HasFocus => ComputeHasFocus();

    /// <inheritdoc/>
    public CursorShape? PreferredCursorShape => CursorShape.VerticalBar;

    /// <summary>⇥ is ours whenever there is another bar to hand it to; otherwise it stays traversal.</summary>
    public bool WantsTabKey => HasSibling();

    /// <summary>
    /// The key table.
    /// <para>
    /// ⏎ sends, always — that is the one binding a MU* client may not get wrong. The newline chords are
    /// Shift+⏎, Ctrl+⏎ and Ctrl+L, and the reason there are three is worth stating: SharpConsoleUI's
    /// Unix input parser (<c>AnsiInputParser</c>) decodes no CSI-u and enables no <c>modifyOtherKeys</c>,
    /// so it turns both CR (0x0D) and LF (0x0A) into a bare <see cref="ConsoleKey.Enter"/> with no
    /// modifiers — Shift+⏎ and Ctrl+⏎ are indistinguishable from ⏎ on this maintainer's terminal, and
    /// Alt+⏎ arrives as Escape <em>then</em> Enter, two keys. They are honoured anyway because the
    /// Windows <c>Console.ReadKey</c> path does report them and costs nothing to accept. Ctrl+L is the
    /// chord that always arrives: <see cref="MacroKeys"/> already records that a Ctrl+letter reaches the
    /// app as its control byte except for I, M, J and H, which the terminal spells Tab, Enter, Enter and
    /// Backspace.
    /// </para>
    /// <para>
    /// <b>Ctrl+A is move-to-line-start, not select-all</b>, and it is not up for grabs. The same parser
    /// limitation makes Ctrl+Shift+A indistinguishable from Ctrl+A, so there is no second chord to move
    /// line-start onto: handing ⌃A to a selection would simply remove it from the readline set
    /// (⌃A ⌃E ⌃K ⌃U ⌃W) that is the only way around a wrapped, multi-row command line. Selection is
    /// wanted and unbuilt; see <c>docs/HANDOFF.md</c> §5 for what it needs and which chord it should get.
    /// </para>
    /// </summary>
    public bool ProcessKey(ConsoleKeyInfo key)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        var shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);

        if (key.Key == ConsoleKey.Enter)
        {
            return ctrl || shift ? Edit(Buffer.InsertNewline()) : Send();
        }

        if (ctrl)
        {
            switch (key.Key)
            {
                case ConsoleKey.L: return Edit(Buffer.InsertNewline());
                case ConsoleKey.A: return Move(Buffer.MoveTo(0));
                case ConsoleKey.E: return Move(Buffer.MoveTo(Buffer.Text.Length));
                case ConsoleKey.K: return Edit(Buffer.KillToEnd());
                case ConsoleKey.U: return Edit(Buffer.KillToStart());
                case ConsoleKey.W: return Edit(Buffer.KillWordLeft());
                case ConsoleKey.LeftArrow: return Move(Buffer.MoveWordLeft());
                case ConsoleKey.RightArrow: return Move(Buffer.MoveWordRight());
            }
        }

        switch (key.Key)
        {
            case ConsoleKey.Backspace: return Edit(Buffer.Backspace());
            case ConsoleKey.Delete: return Edit(Buffer.Delete());
            case ConsoleKey.Home: return Move(Buffer.MoveHome(FieldWidth()));
            case ConsoleKey.End: return Move(Buffer.MoveEnd(FieldWidth()));
            case ConsoleKey.LeftArrow: return Move(Buffer.MoveLeft());
            case ConsoleKey.RightArrow: return Move(Buffer.MoveRight());
            case ConsoleKey.UpArrow: return TryMoveRow(-1);
            case ConsoleKey.DownArrow: return TryMoveRow(1);
            case ConsoleKey.Tab when HasSibling():
                CycleRequested?.Invoke();
                return true;
        }

        // Alt chords are the app's or a macro's, never text: the parser spells them ESC + the character,
        // so accepting them here would type the letter of every Alt binding into the command line.
        if (!key.Modifiers.HasFlag(ConsoleModifiers.Alt) && !ctrl && !char.IsControl(key.KeyChar))
        {
            return Edit(Buffer.Insert(key.KeyChar));
        }

        return false;
    }

    /// <summary>Sends the line and clears it, leaving the caret exactly where it was — on this bar.</summary>
    private bool Send()
    {
        var text = Buffer.Text;
        if (Buffer.Clear())
        {
            _scroll = 0;
            Invalidate(Invalidation.Relayout);
        }

        Entered?.Invoke(text);
        return true;
    }

    /// <summary>Commits an edit: relayout (the bar may have grown) and tell the app the draft moved.</summary>
    private bool Edit(bool changed)
    {
        if (changed)
        {
            Invalidate(Invalidation.Relayout);
            Changed?.Invoke(Buffer.Text);
        }

        return true;
    }

    /// <summary>Commits a caret move: repaint only, and no draft change — the text did not move.</summary>
    private bool Move(bool moved)
    {
        if (moved)
        {
            Invalidate(Invalidation.Repaint);
        }

        return true;
    }

    /// <summary>Pastes at the caret. Newlines are kept: this bar has rows for them.</summary>
    public void Paste(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Edit(Buffer.Insert(text.Replace("\r\n", "\n").Replace('\r', '\n')));
    }

    /// <summary>How many columns of text fit on one row: the field, less the prompt it indents past.</summary>
    private int FieldWidth()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width ?? 80;
        return Math.Max(1, width - Margin.Left - Margin.Right - MarkupParser.StripLength(_prompt));
    }

    /// <inheritdoc/>
    public Point? GetLogicalCursorPosition()
    {
        if (!HasFocus || !Armed)
        {
            return null;
        }

        var rows = InputLayout.Wrap(Buffer.Text, FieldWidth());
        var (row, column) = InputLayout.Caret(rows, Buffer.Caret);
        var height = InputLayout.Height(rows.Count, MinRows, MaxRows);
        _scroll = InputLayout.Scroll(row, rows.Count, height, _scroll);
        return new Point(
            Margin.Left + MarkupParser.StripLength(_prompt) + column,
            Margin.Top + row - _scroll);
    }

    /// <inheritdoc/>
    public void SetLogicalCursorPosition(Point position)
    {
        var rows = InputLayout.Wrap(Buffer.Text, FieldWidth());
        var row = Math.Clamp(position.Y - Margin.Top + _scroll, 0, rows.Count - 1);
        var column = position.X - Margin.Left - MarkupParser.StripLength(_prompt);
        if (Buffer.MoveTo(InputLayout.Offset(rows, row, column)))
        {
            Invalidate(Invalidation.Repaint);
        }
    }

    /// <inheritdoc/>
    public override Size GetLogicalContentSize() => new(Width ?? (ContentWidth ?? 0), Rows());

    /// <inheritdoc/>
    public override LayoutSize MeasureDOM(LayoutConstraints constraints)
    {
        var width = (Width ?? constraints.MaxWidth) + Margin.Left + Margin.Right;
        var height = Rows() + Margin.Top + Margin.Bottom;
        return new LayoutSize(
            Math.Clamp(width, constraints.MinWidth, constraints.MaxWidth),
            Math.Clamp(height, constraints.MinHeight, constraints.MaxHeight));
    }

    /// <inheritdoc/>
    public override void PaintDOM(
        CharacterBuffer buffer, LayoutRect bounds, LayoutRect clipRect, Color defaultFg, Color defaultBg)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        SetActualBounds(bounds);

        var band = Armed ? BandColor : IdleBandColor;
        var ink = Armed ? TextColor : IdleTextColor;
        var startX = bounds.X + Margin.Left;
        var startY = bounds.Y + Margin.Top;
        var promptWidth = MarkupParser.StripLength(_prompt);
        var fieldWidth = Math.Max(1, bounds.Width - Margin.Left - Margin.Right - promptWidth);

        var rows = InputLayout.Wrap(Buffer.Text, fieldWidth);
        var height = Math.Min(
            InputLayout.Height(rows.Count, MinRows, MaxRows), bounds.Height - Margin.Top - Margin.Bottom);
        var (caretRow, _) = InputLayout.Caret(rows, Buffer.Caret);
        _scroll = InputLayout.Scroll(caretRow, rows.Count, height, _scroll);

        for (var line = 0; line < height; line++)
        {
            var y = startY + line;
            if (y < clipRect.Y || y >= clipRect.Bottom || y >= bounds.Bottom)
            {
                continue;
            }

            // The whole row is band first, so a short line still paints the bar edge to edge.
            for (var x = bounds.X; x < bounds.Right; x++)
            {
                if (x >= clipRect.X && x < clipRect.Right)
                {
                    buffer.SetNarrowCell(x, y, ' ', ink, band);
                }
            }

            if (line == 0 && promptWidth > 0)
            {
                buffer.WriteCellsClipped(startX, y, MarkupParser.Parse(_prompt, ink, band), clipRect);
            }

            var index = _scroll + line;
            if (index >= rows.Count)
            {
                continue;
            }

            var row = rows[index];
            var textX = startX + promptWidth;
            for (var i = 0; i < row.Length && i < fieldWidth; i++)
            {
                var x = textX + i;
                if (x >= clipRect.X && x < clipRect.Right && x < bounds.Right - Margin.Right)
                {
                    buffer.SetNarrowCell(x, y, Buffer.Text[row.Start + i], ink, band);
                }
            }
        }

        PaintOverflow(buffer, bounds, clipRect, rows.Count, height, ink, band);
    }

    /// <summary>
    /// Marks a bar that has more rows than it is tall. Without it the cap reads as lost text: the line
    /// simply stops, with nothing to say the rest is above or below.
    /// </summary>
    private void PaintOverflow(
        CharacterBuffer buffer, LayoutRect bounds, LayoutRect clipRect, int rows, int height,
        Color ink, Color band)
    {
        if (rows <= height || height <= 0)
        {
            return;
        }

        var x = bounds.Right - Margin.Right - 1;
        if (x < clipRect.X || x >= clipRect.Right)
        {
            return;
        }

        var top = bounds.Y + Margin.Top;
        if (_scroll > 0 && top >= clipRect.Y && top < clipRect.Bottom)
        {
            buffer.SetNarrowCell(x, top, '▴', ink, band);
        }

        var last = top + height - 1;
        if (_scroll + height < rows && last >= clipRect.Y && last < clipRect.Bottom)
        {
            buffer.SetNarrowCell(x, last, '▾', ink, band);
        }
    }

    #region IMouseAwareControl

    /// <inheritdoc/>
    public bool WantsMouseEvents => true;

    /// <inheritdoc/>
    public bool CanFocusWithMouse => CanReceiveFocus;

    /// <inheritdoc/>
    public event EventHandler<SharpConsoleUI.Events.MouseEventArgs>? MouseClick;

    /// <inheritdoc/>
    public event EventHandler<SharpConsoleUI.Events.MouseEventArgs>? MouseDoubleClick;

    /// <inheritdoc/>
    public event EventHandler<SharpConsoleUI.Events.MouseEventArgs>? MouseRightClick;

    /// <inheritdoc/>
    public event EventHandler<SharpConsoleUI.Events.MouseEventArgs>? MouseEnter;

    /// <inheritdoc/>
    public event EventHandler<SharpConsoleUI.Events.MouseEventArgs>? MouseLeave;

    /// <inheritdoc/>
    public event EventHandler<SharpConsoleUI.Events.MouseEventArgs>? MouseMove;

    /// <summary>
    /// Clicking a bar arms it and drops the caret where the pointer landed — the second way (after ⇥)
    /// to say which line ⏎ should send. The hover events are relayed as the framework reports them so a
    /// subscriber sees the same stream any other mouse-aware control gives.
    /// </summary>
    public bool ProcessMouseEvent(SharpConsoleUI.Events.MouseEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!IsEnabled)
        {
            return false;
        }

        if (args.HasFlag(SharpConsoleUI.Drivers.MouseFlags.MouseEnter))
        {
            MouseEnter?.Invoke(this, args);
        }

        if (args.HasFlag(SharpConsoleUI.Drivers.MouseFlags.MouseLeave))
        {
            MouseLeave?.Invoke(this, args);
        }

        if (args.HasFlag(SharpConsoleUI.Drivers.MouseFlags.ReportMousePosition))
        {
            MouseMove?.Invoke(this, args);
        }

        if (args.HasFlag(SharpConsoleUI.Drivers.MouseFlags.Button3Clicked))
        {
            MouseRightClick?.Invoke(this, args);
        }

        if (args.HasFlag(SharpConsoleUI.Drivers.MouseFlags.Button1DoubleClicked))
        {
            MouseDoubleClick?.Invoke(this, args);
        }

        if (!args.HasFlag(SharpConsoleUI.Drivers.MouseFlags.Button1Clicked)
            && !args.HasFlag(SharpConsoleUI.Drivers.MouseFlags.Button1Pressed))
        {
            return false;
        }

        if (!HasFocus && CanFocusWithMouse)
        {
            this.GetParentWindow()?.FocusManager.SetFocus(this, FocusReason.Mouse);
        }

        ActivationRequested?.Invoke(this);
        SetLogicalCursorPosition(args.Position);
        MouseClick?.Invoke(this, args);
        args.Handled = true;
        return true;
    }

    #endregion

    /// <inheritdoc/>
    protected override void OnDisposing()
    {
    }
}
