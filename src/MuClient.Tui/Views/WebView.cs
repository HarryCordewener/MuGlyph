using System.Text;
using MuClient.Core.Text;
using MuClient.Core.Theming;
using MuClient.Web;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace MuClient.Tui.Views;

/// <summary>
/// An in-TUI text-mode web view. Displays a <see cref="WebPage"/>'s pre-wrapped styled lines,
/// scrolls, and lets links be followed in-pane (raising <see cref="Navigate"/>) or the pane be
/// closed (<see cref="Closed"/>). Images render as labelled link spans; a graphics-capable
/// terminal can later realise them through the graphics layer. Final placement/chrome will follow
/// the layout design.
/// </summary>
internal sealed class WebView : View
{
    private readonly Dictionary<(int Row, int Col), SpanInteraction> _cellInteractions = new();
    private IReadOnlyList<StyledLine> _lines = Array.Empty<StyledLine>();
    private int _scroll;

    public WebView()
    {
        CanFocus = true;
        MouseEvent += OnMouseEvent;
        KeyDown += OnKeyDown;
    }

    public ColorMapper Mapper { get; set; } = new(ThemeLibrary.Dark());

    public string Title { get; private set; } = string.Empty;

    /// <summary>Raised when a hyperlink is followed (the target URL).</summary>
    public event Action<string>? Navigate;

    /// <summary>Raised when the view is closed (Esc/q).</summary>
    public event Action? Closed;

    public void Show(WebPage page)
    {
        Title = string.IsNullOrEmpty(page.Title) ? page.Url : $"{page.Title} — {page.Url}";
        _lines = page.Lines;
        _scroll = 0;
        SetNeedsDraw();
    }

    private void OnKeyDown(object? sender, Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.Esc:
                Closed?.Invoke();
                key.Handled = true;
                break;
            case KeyCode.CursorUp:
                Scroll(-1);
                key.Handled = true;
                break;
            case KeyCode.CursorDown:
                Scroll(1);
                key.Handled = true;
                break;
            case KeyCode.PageUp:
                Scroll(-Math.Max(1, Viewport.Height - 1));
                key.Handled = true;
                break;
            case KeyCode.PageDown:
                Scroll(Math.Max(1, Viewport.Height - 1));
                key.Handled = true;
                break;
        }
    }

    private void Scroll(int delta)
    {
        // Clamp so the last full screen of content stays visible rather than scrolling into blank space.
        var max = Math.Max(0, _lines.Count - Math.Max(1, Viewport.Height));
        _scroll = Math.Clamp(_scroll + delta, 0, max);
        SetNeedsDraw();
    }

    private void OnMouseEvent(object? sender, Mouse mouse)
    {
        if (!mouse.IsSingleClicked || mouse.Position is not { } position)
        {
            return;
        }

        if (_cellInteractions.TryGetValue((position.Y, position.X), out var interaction) &&
            interaction.Kind == InteractionKind.Hyperlink)
        {
            Navigate?.Invoke(interaction.Target);
            mouse.Handled = true;
        }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        _cellInteractions.Clear();
        var viewport = Viewport;
        var width = Math.Max(1, viewport.Width);
        var height = Math.Max(1, viewport.Height);

        for (var screenRow = 0; screenRow < height; screenRow++)
        {
            var index = _scroll + screenRow;
            if (index >= _lines.Count)
            {
                break;
            }

            DrawLine(_lines[index], screenRow, width);
        }

        return true;
    }

    private void DrawLine(StyledLine line, int screenRow, int width)
    {
        Move(0, screenRow);
        var col = 0;
        foreach (var span in line.Spans)
        {
            var attr = Mapper.ToAttribute(span.Style);
            foreach (var rune in span.Text.EnumerateRunes())
            {
                if (col >= width)
                {
                    return;
                }

                SetAttribute(attr);
                AddRune(col, screenRow, rune);
                if (span.Interaction is not null)
                {
                    _cellInteractions[(screenRow, col)] = span.Interaction;
                }

                col++;
            }
        }
    }
}
