namespace SharpMUTerm.Core.Workspaces;

/// <summary>
/// Applies the outcome of a drop resolved by <see cref="DropZones"/>: a central drop (no edge) adds
/// the window to the target pane as a tab, an edge drop splits the target pane and puts the window in
/// the new pane. This is the single commit path shared by the mouse drag and the keyboard move mode,
/// so both produce identical results. Pure and UI-agnostic; the caller supplies the already-resolved
/// target pane and edge.
/// </summary>
public static class PaneDrop
{
    /// <summary>
    /// Commits a drop of <paramref name="windowId"/> onto <paramref name="targetPaneId"/>. A null
    /// <paramref name="edge"/> adds it as a tab; otherwise the target pane is split toward that edge.
    /// Returns false — changing nothing — when the target pane is gone or the drop would be a no-op:
    /// a tab drop onto the window's own pane, or an edge drop out of a pane the window already has to
    /// itself (which would merely rebuild the same single pane under a new id).
    /// </summary>
    public static bool Apply(WorkspaceLayout layout, string windowId, string targetPaneId, Edge? edge)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var target = layout.FindPane(targetPaneId);
        if (target is null)
        {
            return false;
        }

        var source = layout.FindWindow(windowId);
        if (source is null)
        {
            return false;
        }

        if (ReferenceEquals(source, target) && (edge is null || source.Tabs.Count <= 1))
        {
            return false;
        }

        return edge is { } side
            ? layout.SplitWithWindow(windowId, targetPaneId, side)
            : layout.MoveWindowToPane(windowId, targetPaneId);
    }

    /// <summary>
    /// Resolves and commits a drop at a point inside <paramref name="targetRect"/> in one step, using
    /// <see cref="DropZones.Resolve"/> to pick the edge (or a tab drop). Returns false when nothing changed.
    /// </summary>
    public static bool Apply(
        WorkspaceLayout layout,
        string windowId,
        string targetPaneId,
        PaneRect targetRect,
        int pointX,
        int pointY,
        double edgeFraction = DropZones.DefaultEdgeFraction)
    {
        var edge = DropZones.Resolve(
            targetRect.X,
            targetRect.Y,
            targetRect.Width,
            targetRect.Height,
            pointX,
            pointY,
            edgeFraction);

        return Apply(layout, windowId, targetPaneId, edge);
    }
}
