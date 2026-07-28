namespace MuClient.Core.Workspaces;

/// <summary>An integer cell rectangle in the terminal grid (top-left origin).</summary>
public readonly record struct PaneRect(int X, int Y, int Width, int Height)
{
    /// <summary>True when the rectangle has no area.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Turns a <see cref="LayoutNode"/> split tree into concrete per-pane rectangles for a given
/// bounding area, reserving one cell for the divider between siblings and honouring each split's
/// fractional <see cref="SplitNode.Sizes"/>. Pure and deterministic; the Terminal.Gui layer maps
/// the resulting <see cref="PaneRect"/>s onto views. When a pane is zoomed it fills the whole area.
/// </summary>
public static class LayoutSolver
{
    /// <summary>Divider thickness, in cells, between two sibling panes.</summary>
    public const int DividerThickness = 1;

    /// <summary>
    /// Solves pane rectangles within <paramref name="bounds"/>. If <paramref name="zoomedPaneId"/>
    /// names a live pane, only that pane is returned, filling the whole area. Panes that collapse to
    /// zero area under a tiny bounds are still included (as empty rects) so callers can decide.
    /// </summary>
    public static IReadOnlyDictionary<string, PaneRect> Solve(
        LayoutNode root,
        PaneRect bounds,
        string? zoomedPaneId = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var result = new Dictionary<string, PaneRect>(StringComparer.Ordinal);

        if (zoomedPaneId is not null && root.Panes().Any(p => p.Id == zoomedPaneId))
        {
            result[zoomedPaneId] = bounds;
            return result;
        }

        Place(root, bounds, result);
        return result;
    }

    private static void Place(LayoutNode node, PaneRect rect, Dictionary<string, PaneRect> result)
    {
        if (node is PaneNode pane)
        {
            result[pane.Id] = rect;
            return;
        }

        var split = (SplitNode)node;
        var count = split.Children.Count;
        var isRow = split.Direction == SplitDirection.Row;

        var span = isRow ? rect.Width : rect.Height;
        var forChildren = Math.Max(0, span - (DividerThickness * (count - 1)));
        var sizes = Distribute(forChildren, split.Sizes);

        var cursor = isRow ? rect.X : rect.Y;
        for (var i = 0; i < count; i++)
        {
            var childRect = isRow
                ? new PaneRect(cursor, rect.Y, sizes[i], rect.Height)
                : new PaneRect(rect.X, cursor, rect.Width, sizes[i]);
            Place(split.Children[i], childRect, result);
            cursor += sizes[i] + DividerThickness;
        }
    }

    /// <summary>
    /// Splits <paramref name="total"/> cells among <paramref name="fractions"/> as integers that sum
    /// exactly to <paramref name="total"/>, using cumulative rounding so no cell is lost to drift.
    /// </summary>
    public static int[] Distribute(int total, IReadOnlyList<double> fractions)
    {
        var result = new int[fractions.Count];
        if (total <= 0)
        {
            return result;
        }

        double accumulated = 0;
        var previousBoundary = 0;
        for (var i = 0; i < fractions.Count; i++)
        {
            accumulated += fractions[i];
            var boundary = i == fractions.Count - 1
                ? total
                : (int)Math.Round(accumulated * total, MidpointRounding.AwayFromZero);
            boundary = Math.Clamp(boundary, previousBoundary, total);
            result[i] = boundary - previousBoundary;
            previousBoundary = boundary;
        }

        return result;
    }
}
