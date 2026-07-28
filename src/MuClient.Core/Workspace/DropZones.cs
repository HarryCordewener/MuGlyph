namespace MuClient.Core.Workspaces;

/// <summary>
/// Resolves where a dragged tab or rail window should land when dropped on a pane: in the middle to
/// add it as a tab, or within a margin of an edge to split the pane there. This is the geometry heart
/// of the design's mouse drag ("drop in the middle to add it as a tab, drop within 25% of an edge to
/// split there"). Pure and coordinate-only so it is fully unit-testable; the UI feeds pane bounds and
/// the drop point and applies the returned <see cref="Edge"/> (or adds a tab when null).
/// </summary>
public static class DropZones
{
    /// <summary>The design's edge margin: a drop within this fraction of a side splits toward it.</summary>
    public const double DefaultEdgeFraction = 0.25;

    /// <summary>
    /// Returns the edge to split toward for a drop at (<paramref name="pointX"/>,
    /// <paramref name="pointY"/>) inside the pane rectangle, or <c>null</c> to add the window as a tab.
    /// The point is clamped into the rectangle; a degenerate (zero-area) rectangle is always a tab drop.
    /// When a corner is closest to two edges, the nearer one wins (ties favour the horizontal edge).
    /// </summary>
    public static Edge? Resolve(
        int paneX,
        int paneY,
        int paneWidth,
        int paneHeight,
        int pointX,
        int pointY,
        double edgeFraction = DefaultEdgeFraction)
    {
        if (paneWidth <= 0 || paneHeight <= 0)
        {
            return null;
        }

        // Fractional position within the pane, clamped to [0, 1].
        var fx = Math.Clamp((pointX - paneX) / (double)paneWidth, 0.0, 1.0);
        var fy = Math.Clamp((pointY - paneY) / (double)paneHeight, 0.0, 1.0);

        // Distance to each edge as a fraction of the pane; the smallest wins if it's within the margin.
        var left = fx;
        var right = 1.0 - fx;
        var top = fy;
        var bottom = 1.0 - fy;

        var nearest = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
        if (nearest >= edgeFraction)
        {
            return null; // central drop → add as a tab
        }

        // Prefer the horizontal edges (left/right) on ties so a corner resolves deterministically.
        if (left <= right && left <= top && left <= bottom)
        {
            return Edge.Left;
        }

        if (right <= top && right <= bottom)
        {
            return Edge.Right;
        }

        return top <= bottom ? Edge.Top : Edge.Bottom;
    }
}
