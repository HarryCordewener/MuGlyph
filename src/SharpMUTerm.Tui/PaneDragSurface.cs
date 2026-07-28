using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Tui;

/// <summary>
/// A frozen picture of the pane area taken the moment a mouse drag starts: where each realised pane
/// sits in desktop cells, and which window each pane currently shows. Frozen deliberately — the pane
/// area is torn down and rebuilt to paint the drop preview, so resolving against live controls
/// mid-drag would chase a moving target. Pure and coordinate-only, so it is fully unit-testable.
/// </summary>
internal sealed class PaneDragSurface
{
    private readonly Dictionary<string, PaneRect> _rects;
    private readonly Dictionary<string, string> _activeWindows;

    /// <summary>Creates a surface from pane rectangles and each pane's active window id.</summary>
    public PaneDragSurface(
        IReadOnlyDictionary<string, PaneRect> rects,
        IReadOnlyDictionary<string, string> activeWindows)
    {
        ArgumentNullException.ThrowIfNull(rects);
        ArgumentNullException.ThrowIfNull(activeWindows);
        _rects = new Dictionary<string, PaneRect>(rects, StringComparer.Ordinal);
        _activeWindows = new Dictionary<string, string>(activeWindows, StringComparer.Ordinal);
    }

    /// <summary>The pane rectangles, keyed by pane id.</summary>
    public IReadOnlyDictionary<string, PaneRect> Rects => _rects;

    /// <summary>True when no pane was realised (nothing can be dragged).</summary>
    public bool IsEmpty => _rects.Count == 0;

    /// <summary>
    /// The pane containing the desktop cell, or null when the point falls outside every pane (the
    /// rail, a divider, the header, the input band). Panes never overlap, so the first hit wins;
    /// zero-area panes are skipped so a collapsed pane can't swallow a point.
    /// </summary>
    public string? PaneAt(int x, int y)
    {
        foreach (var (id, rect) in _rects)
        {
            if (rect.IsEmpty)
            {
                continue;
            }

            if (x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height)
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>The rectangle of a pane, or null when it isn't in the snapshot.</summary>
    public PaneRect? RectOf(string paneId) => _rects.TryGetValue(paneId, out var rect) ? rect : null;

    /// <summary>The window shown by a pane when the drag started, or null when the pane was empty.</summary>
    public string? ActiveWindow(string paneId) => _activeWindows.GetValueOrDefault(paneId);

    /// <summary>
    /// True when the cell sits on a pane's tab strip — its top row. Only the tab strip starts a pane
    /// drag, so clicking, selecting text and following links in a pane's body are all left alone.
    /// </summary>
    public bool IsTabStrip(string paneId, int y) => RectOf(paneId) is { } rect && !rect.IsEmpty && y == rect.Y;
}
