namespace SharpMUTerm.Core.Workspaces;

/// <summary>Which way a directional focus move goes.</summary>
public enum PaneDirection
{
    /// <summary>Toward smaller X.</summary>
    Left,

    /// <summary>Toward larger X.</summary>
    Right,

    /// <summary>Toward smaller Y.</summary>
    Up,

    /// <summary>Toward larger Y.</summary>
    Down,
}

/// <summary>
/// Directional pane focus — which pane is "left of" this one, and so on. It answers from
/// <em>geometry</em> (the solved <see cref="PaneRect"/>s) rather than from the split tree, because the
/// question the user is asking is about what they can see: in a tree of nested splits, "the pane to my
/// left" is a spatial fact, and the same tree walked structurally gives answers that are correct about
/// the tree and wrong about the screen.
/// <para>
/// Pure and UI-agnostic. The caller supplies the rectangles, so the same rule serves the live layout
/// (the arranged pane rectangles, which is what the app passes — they already reflect zoom, since a
/// zoomed workspace realises exactly one pane) and a unit test (a hand-written dictionary, or
/// <see cref="LayoutSolver.Solve"/> over any bounds).
/// </para>
/// </summary>
public static class PaneNavigation
{
    /// <summary>
    /// The pane <paramref name="direction"/> of <paramref name="fromPaneId"/>, or null when there is
    /// none — which the caller is expected to <em>report</em> rather than swallow, because a navigation
    /// key that silently does nothing is indistinguishable from one that is not bound.
    /// <para>
    /// Candidates are the panes that begin beyond the near edge of the starting pane on the axis of
    /// travel. Among them the winner is the one that <em>overlaps</em> the starting pane on the cross
    /// axis and lies nearest along the axis of travel; overlap is preferred absolutely, so a wide pane
    /// directly alongside always beats a nearer one that is merely diagonal. Ties — two panes stacked in
    /// the column you are stepping into — go to the one whose cross-axis centre is closest to yours,
    /// then to the lower pane id, so the answer is stable frame to frame.
    /// </para>
    /// </summary>
    public static string? Neighbour(
        IReadOnlyDictionary<string, PaneRect> rects,
        string fromPaneId,
        PaneDirection direction)
    {
        ArgumentNullException.ThrowIfNull(rects);
        ArgumentNullException.ThrowIfNull(fromPaneId);

        if (!rects.TryGetValue(fromPaneId, out var from))
        {
            return null;
        }

        var horizontal = direction is PaneDirection.Left or PaneDirection.Right;
        string? best = null;
        (bool Diagonal, int Gap, int Cross, string Id) bestScore = default;

        foreach (var (id, rect) in rects)
        {
            if (string.Equals(id, fromPaneId, StringComparison.Ordinal) || rect.IsEmpty)
            {
                continue;
            }

            if (Gap(from, rect, direction) is not { } gap)
            {
                continue;
            }

            // Overlap on the cross axis is what makes a pane "alongside" rather than "diagonal from".
            var diagonal = !Overlaps(from, rect, horizontal);
            var cross = Math.Abs(Centre(rect, horizontal) - Centre(from, horizontal));
            var score = (diagonal, gap, cross, id);
            if (best is null || Better(score, bestScore))
            {
                best = id;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Whether one candidate beats another: alongside first, then nearest, then most aligned.</summary>
    private static bool Better(
        (bool Diagonal, int Gap, int Cross, string Id) candidate,
        (bool Diagonal, int Gap, int Cross, string Id) incumbent)
    {
        if (candidate.Diagonal != incumbent.Diagonal)
        {
            return !candidate.Diagonal;
        }

        if (candidate.Gap != incumbent.Gap)
        {
            return candidate.Gap < incumbent.Gap;
        }

        if (candidate.Cross != incumbent.Cross)
        {
            return candidate.Cross < incumbent.Cross;
        }

        return string.CompareOrdinal(candidate.Id, incumbent.Id) < 0;
    }

    /// <summary>
    /// How far <paramref name="candidate"/> lies beyond <paramref name="from"/> in the direction of
    /// travel, or null when it does not lie beyond it at all. Measured edge to edge, so the one-cell
    /// divider between two siblings scores 1 and a pane across a nested split scores more.
    /// </summary>
    private static int? Gap(PaneRect from, PaneRect candidate, PaneDirection direction)
    {
        var gap = direction switch
        {
            PaneDirection.Left => from.X - (candidate.X + candidate.Width),
            PaneDirection.Right => candidate.X - (from.X + from.Width),
            PaneDirection.Up => from.Y - (candidate.Y + candidate.Height),
            _ => candidate.Y - (from.Y + from.Height),
        };

        return gap >= 0 ? gap : null;
    }

    /// <summary>Whether two rectangles share any extent on the axis <em>across</em> the travel.</summary>
    private static bool Overlaps(PaneRect a, PaneRect b, bool horizontal) => horizontal
        ? a.Y < b.Y + b.Height && b.Y < a.Y + a.Height
        : a.X < b.X + b.Width && b.X < a.X + a.Width;

    /// <summary>A rectangle's centre on the axis across the travel, doubled to stay integral.</summary>
    private static int Centre(PaneRect rect, bool horizontal) =>
        horizontal ? (rect.Y * 2) + rect.Height : (rect.X * 2) + rect.Width;
}
