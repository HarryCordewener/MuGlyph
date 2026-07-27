using System.Text;
using MuClient.Core.Session;
using MuClient.Core.Text;
using MuClient.Core.Theming;
using Terminal.Gui.ViewBase;

namespace MuClient.Tui.Views;

/// <summary>
/// Renders a <see cref="WorldSession"/>'s scrollback (plus its current prompt) with truecolor
/// styling, word/character wrapping, and vertical scrolling. Custom-drawn because Terminal.Gui's
/// cell grid has no concept of our styled-span model.
/// </summary>
internal sealed class OutputView : View
{
    private WorldSession? _session;
    private int _scrollOffset; // rows scrolled up from the bottom (0 = following the tail)

    public OutputView()
    {
        CanFocus = false;
    }

    /// <summary>The theme-aware colour mapper used to render styled spans.</summary>
    public ColorMapper Mapper { get; set; } = new(ThemeLibrary.Dark());

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

    /// <summary>True when the view is pinned to the newest output.</summary>
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

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var viewport = Viewport;
        var width = Math.Max(1, viewport.Width);
        var height = Math.Max(1, viewport.Height);

        var rows = BuildVisualRows(width, height + _scrollOffset + 1);
        if (rows.Count == 0)
        {
            return true;
        }

        // Clamp scroll so we never page past the top.
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

    private void DrawRow(IReadOnlyList<(Rune Rune, TextStyle Style)> row, int screenRow, int width)
    {
        Move(0, screenRow);
        var col = 0;
        foreach (var (rune, style) in row)
        {
            if (col >= width)
            {
                break;
            }

            SetAttribute(Mapper.ToAttribute(style));
            AddRune(col, screenRow, rune);
            col++;
        }
    }

    /// <summary>
    /// Builds up to <paramref name="maxRows"/> visual (wrapped) rows from the tail of the
    /// scrollback and the active prompt, newest last.
    /// </summary>
    private List<List<(Rune Rune, TextStyle Style)>> BuildVisualRows(int width, int maxRows)
    {
        var result = new List<List<(Rune, TextStyle)>>();
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

        // Walk logical lines from the end, wrapping each and prepending, until we have enough.
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

    private static List<List<(Rune Rune, TextStyle Style)>> WrapLine(StyledLine line, int width)
    {
        var rows = new List<List<(Rune, TextStyle)>>();
        var current = new List<(Rune, TextStyle)>();

        foreach (var span in line.Spans)
        {
            foreach (var rune in span.Text.EnumerateRunes())
            {
                if (rune.Value == '\t')
                {
                    // Expand tabs to the next multiple of 4 columns.
                    var stop = 4 - current.Count % 4;
                    for (var s = 0; s < stop && current.Count < width; s++)
                    {
                        current.Add((new Rune(' '), span.Style));
                    }
                }
                else
                {
                    current.Add((rune, span.Style));
                }

                if (current.Count >= width)
                {
                    rows.Add(current);
                    current = new List<(Rune, TextStyle)>();
                }
            }
        }

        // Always emit at least one (possibly empty) row so blank lines occupy space.
        if (current.Count > 0 || rows.Count == 0)
        {
            rows.Add(current);
        }

        return rows;
    }
}
