using System.Text;
using MuClient.Core.Session;
using MuClient.Core.Text;
using MuClient.Core.Theming;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace MuClient.Tui.Views;

/// <summary>
/// Renders a <see cref="WorldSession"/>'s scrollback (plus its current prompt) with truecolor
/// styling, wrapping, and vertical scrolling, and makes MXP/Pueblo interactive spans clickable.
/// Custom-drawn because Terminal.Gui's cell grid has no concept of our styled-span model.
/// </summary>
internal sealed class OutputView : View
{
    private WorldSession? _session;
    private int _scrollOffset; // rows scrolled up from the bottom (0 = following the tail)

    // Maps a rendered cell (screen row, col) to the interaction of the span drawn there, so a
    // click can be resolved back to a command/link. Rebuilt on every draw.
    private readonly Dictionary<(int Row, int Col), SpanInteraction> _cellInteractions = new();

    public OutputView()
    {
        CanFocus = false;
        MouseEvent += OnMouseEvent;
    }

    /// <summary>The theme-aware colour mapper used to render styled spans.</summary>
    public ColorMapper Mapper { get; set; } = new(ThemeLibrary.Dark());

    /// <summary>Raised when a clickable command span is activated (command, promptOnly).</summary>
    public event Action<string, bool>? CommandActivated;

    /// <summary>Raised when a hyperlink span is activated.</summary>
    public event Action<string>? LinkActivated;

    public WorldSession? Session
    {
        get => _session;
        set
        {
            _session = value;
            _scrollOffset = 0;
            SetNeedsDraw();
        }
    }

    public bool AtBottom => _scrollOffset == 0;

    public void ScrollToBottom()
    {
        _scrollOffset = 0;
        SetNeedsDraw();
    }

    public void ScrollLines(int delta)
    {
        _scrollOffset = Math.Max(0, _scrollOffset + delta);
        SetNeedsDraw();
    }

    public void PageUp() => ScrollLines(Math.Max(1, Viewport.Height - 1));

    public void PageDown() => ScrollLines(-Math.Max(1, Viewport.Height - 1));

    private void OnMouseEvent(object? sender, Mouse mouse)
    {
        if (!mouse.IsSingleClicked || mouse.Position is not { } position)
        {
            return;
        }

        if (_cellInteractions.TryGetValue((position.Y, position.X), out var interaction))
        {
            Activate(interaction);
            mouse.Handled = true;
        }
    }

    private void Activate(SpanInteraction interaction)
    {
        switch (interaction.Kind)
        {
            case InteractionKind.SendCommand:
                CommandActivated?.Invoke(interaction.Target, interaction.PromptOnly);
                break;
            case InteractionKind.Hyperlink:
                LinkActivated?.Invoke(interaction.Target);
                break;
        }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        _cellInteractions.Clear();

        var viewport = Viewport;
        var width = Math.Max(1, viewport.Width);
        var height = Math.Max(1, viewport.Height);

        var rows = BuildVisualRows(width, height + _scrollOffset + 1);
        if (rows.Count == 0)
        {
            return true;
        }

        var maxOffset = Math.Max(0, rows.Count - height);
        if (_scrollOffset > maxOffset)
        {
            _scrollOffset = maxOffset;
        }

        var bottom = rows.Count - 1 - _scrollOffset;
        var top = Math.Max(0, bottom - height + 1);

        var screenRow = height - 1;
        for (var i = bottom; i >= top && screenRow >= 0; i--, screenRow--)
        {
            DrawRow(rows[i], screenRow, width);
        }

        return true;
    }

    private void DrawRow(IReadOnlyList<Cell> row, int screenRow, int width)
    {
        Move(0, screenRow);
        var col = 0;
        foreach (var cell in row)
        {
            if (col >= width)
            {
                break;
            }

            SetAttribute(Mapper.ToAttribute(cell.Style));
            AddRune(col, screenRow, cell.Rune);
            if (cell.Interaction is not null)
            {
                _cellInteractions[(screenRow, col)] = cell.Interaction;
            }

            col++;
        }
    }

    private List<List<Cell>> BuildVisualRows(int width, int maxRows)
    {
        var result = new List<List<Cell>>();
        if (_session is null)
        {
            return result;
        }

        var lines = _session.Scrollback.Snapshot();
        var logical = new List<StyledLine>(lines);
        if (_session.CurrentPrompt is { IsEmpty: false } prompt)
        {
            logical.Add(prompt);
        }

        for (var i = logical.Count - 1; i >= 0 && result.Count < maxRows; i--)
        {
            var wrapped = WrapLine(logical[i], width);
            for (var r = wrapped.Count - 1; r >= 0; r--)
            {
                result.Insert(0, wrapped[r]);
            }
        }

        return result;
    }

    private static List<List<Cell>> WrapLine(StyledLine line, int width)
    {
        var rows = new List<List<Cell>>();
        var current = new List<Cell>();

        foreach (var span in line.Spans)
        {
            foreach (var rune in span.Text.EnumerateRunes())
            {
                if (rune.Value == '\t')
                {
                    var stop = 4 - current.Count % 4;
                    for (var s = 0; s < stop && current.Count < width; s++)
                    {
                        current.Add(new Cell(new Rune(' '), span.Style, span.Interaction));
                    }
                }
                else
                {
                    current.Add(new Cell(rune, span.Style, span.Interaction));
                }

                if (current.Count >= width)
                {
                    rows.Add(current);
                    current = new List<Cell>();
                }
            }
        }

        if (current.Count > 0 || rows.Count == 0)
        {
            rows.Add(current);
        }

        return rows;
    }

    private readonly record struct Cell(Rune Rune, TextStyle Style, SpanInteraction? Interaction);
}
