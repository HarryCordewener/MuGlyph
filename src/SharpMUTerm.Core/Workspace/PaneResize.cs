namespace SharpMUTerm.Core.Workspaces;

/// <summary>What a resize did, or the reason it did nothing.</summary>
public enum PaneResizeOutcome
{
    /// <summary>The split moved. <see cref="PaneResizeResult.Cells"/> says by how much.</summary>
    Resized,

    /// <summary>Nothing above this pane divides space along that axis — there is no border to move.</summary>
    NoSplitInThatAxis,

    /// <summary>The border is against a floor: this pane, or the one paying for it, is at its smallest.</summary>
    AtMinimum,

    /// <summary>A zoomed pane fills the whole area, so there is nothing to divide.</summary>
    Zoomed,

    /// <summary>No rectangles for the panes involved — nothing has been laid out yet.</summary>
    NotMeasured,
}

/// <summary>The outcome of one resize, and the cells the border actually moved by (signed, 0 when it did not).</summary>
public readonly record struct PaneResizeResult(PaneResizeOutcome Outcome, int Cells)
{
    /// <summary>Whether the layout changed and the view needs rebuilding.</summary>
    public bool Changed => Outcome == PaneResizeOutcome.Resized;
}

/// <summary>
/// Makes the focused pane wider/narrower or taller/shorter, in character cells — the mutation
/// <see cref="SplitNode.Sizes"/> was built for and never had. Every split in this client has been
/// permanently 50/50 since splits existed: <c>Sizes</c> was written by <c>Even</c> at construction and
/// preserved by pruning, and by nothing else. The renderer has always read them
/// (<c>GridLength.Star</c> per child), so this is the missing half.
/// <para>
/// <b>Cells, not fractions.</b> The request is about character size and cells are what a user can see,
/// so the arithmetic is done in cells and only converted back to fractions at the end. The current cell
/// extents come from the caller's rectangles — the ones the last frame actually <em>arranged</em>, the
/// same source <see cref="PaneNavigation"/> answers from — rather than from re-solving the fractions,
/// because the framework's grid and <see cref="LayoutSolver"/> round independently and the border the
/// user is moving is the one on screen. Both round cumulatively over the same weights, so a border
/// asked to move a cell moves a cell.
/// </para>
/// <para>
/// <b>Which split an arrow acts on</b> — tmux's rule. The focused pane's own parent may not divide space
/// along the arrow's axis (a pane in a stacked pair inside a side-by-side pair has a <em>column</em>
/// parent, and ⌥⇧→ is about columns). So the ancestors are walked from the pane upward and the first
/// split running in that axis is the one that moves. When no ancestor does, there is genuinely no border
/// to move, and the caller is expected to <em>report</em> that rather than swallow it: a key that changes
/// nothing and says nothing is indistinguishable from one that is not bound.
/// </para>
/// <para>
/// <b>What an arrow means: the focused pane's own size, never the divider's position.</b> ↑ taller,
/// ↓ shorter, → wider, ← narrower — up and right make it bigger, down and left smaller — and that
/// sentence is true from either side of the split. It used to be true only of the horizontal pair: the
/// vertical arrows read as "move the border that way", so with the <em>bottom</em> pane focused ↑ made
/// it shorter, and the answer to "what does ⌥⇧↑ do" needed to know where in the tree you were standing.
/// Which sibling pays is an implementation detail nobody should have to reason about.
/// </para>
/// <para>
/// <b>Who pays.</b> The sibling on the side the border is on — the next child, or the previous one when
/// the focused subtree is last. So the pane always grows when the arrow says bigger and shrinks when it
/// says smaller, whichever end of the split it sits at; only the side the border moves on differs. With
/// three or more children the payer is the nearest one on the side the growth comes from, and the
/// <em>same</em> border moves back on the opposite arrow — so → then ← is an exact undo rather than a
/// gesture that walks the pane across its split.
/// </para>
/// <para>
/// <b>Only the affected panes move.</b> One split's <c>Sizes</c> are rewritten and their sum is
/// unchanged, so every pane outside that split keeps its rectangle exactly — which matters because
/// per-pane NAWS is derived from those rectangles and a resize that nudged the whole workspace would
/// re-announce a terminal size to every connected server.
/// </para>
/// </summary>
public static class PaneResize
{
    /// <summary>
    /// Cells one keypress moves the border. <b>One</b>, so that "one press, one cell" is a sentence with
    /// no exceptions in it and a border can be put exactly where it is wanted. It was two, on the argument
    /// that a resize is a gesture people repeat and two halves the presses across a 200-column terminal;
    /// what a user actually watches is the border, and one that skips a column cannot be lined up with one.
    /// <para>
    /// <b>The same step on both axes, deliberately.</b> The case for two was made about columns, and a
    /// terminal has an order of magnitude fewer rows than columns — so a step chosen for width is a
    /// proportionally much coarser jump in height, which is an argument for a <em>smaller</em> row step,
    /// not a larger one. At one cell there is nothing left to split, and one constant keeps every surface
    /// that describes this ("by one character cell") saying one thing.
    /// </para>
    /// </summary>
    public const int StepCells = 1;

    /// <summary>
    /// The narrowest a pane may be squeezed to, in columns. A pane is a tab strip over a text area: below
    /// about a dozen columns the strip cannot show even an elided window title, and server output wraps
    /// every word or two. The model can express far less than this — the renderer's
    /// <c>Math.Max(0.01, …)</c> floor allows a pane one hundredth of the screen wide — which is exactly
    /// why the floor has to live here rather than being left to the layout.
    /// </summary>
    public const int MinimumPaneColumns = 12;

    /// <summary>
    /// The shortest a pane may be squeezed to, in rows. A pane spends its first row (two, under the
    /// framework's separator tab styles) on its tab strip, so four leaves at least two rows of output —
    /// a line and its continuation. Three would leave one row under a two-row strip, and at two the
    /// output rectangle is empty, which <c>ReportPaneSizes</c> skips: the server would keep whatever size
    /// it was last told and wrap to a pane that no longer exists.
    /// </summary>
    public const int MinimumPaneRows = 4;

    /// <summary>
    /// Moves the border of the nearest ancestor split running in the arrow's axis, so the focused pane
    /// grows (<see cref="PaneDirection.Right"/>, <see cref="PaneDirection.Up"/>) or shrinks
    /// (<see cref="PaneDirection.Left"/>, <see cref="PaneDirection.Down"/>) by up to
    /// <paramref name="step"/> cells.
    /// </summary>
    /// <param name="layout">The layout to mutate.</param>
    /// <param name="direction">Which way the size goes — right/up bigger, left/down smaller.</param>
    /// <param name="rects">
    /// Every pane's rectangle in cells, as arranged. The caller supplies them for the same reason
    /// <see cref="PaneNavigation.Neighbour"/> takes them: the question is about what is on screen.
    /// </param>
    /// <param name="step">Cells to move, defaulting to <see cref="StepCells"/>.</param>
    public static PaneResizeResult Apply(
        WorkspaceLayout layout,
        PaneDirection direction,
        IReadOnlyDictionary<string, PaneRect> rects,
        int step = StepCells)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rects);

        // A zoomed pane is the whole area by definition; the split tree is not on screen to be moved.
        if (layout.ZoomedPaneId is not null)
        {
            return new PaneResizeResult(PaneResizeOutcome.Zoomed, 0);
        }

        var horizontal = direction is PaneDirection.Left or PaneDirection.Right;
        var axis = horizontal ? SplitDirection.Row : SplitDirection.Column;

        // The sign is read off the focused pane's size, not off which way the divider would travel: ↑/→
        // grow it and ↓/← shrink it, and MoveBorder always adds `delta` to the focused subtree's own
        // extent. That is what makes the meaning independent of where in the split the pane sits.
        var delta = direction is PaneDirection.Right or PaneDirection.Up ? step : -step;

        // Nearest first: the path runs root → pane, so walking it backwards is walking up from the pane.
        var path = Ancestry(layout.Root, layout.FocusedPaneId);
        for (var depth = path.Count - 1; depth >= 0; depth--)
        {
            var (split, index) = path[depth];
            if (split.Direction == axis)
            {
                return MoveBorder(split, index, horizontal, delta, rects);
            }
        }

        return new PaneResizeResult(PaneResizeOutcome.NoSplitInThatAxis, 0);
    }

    /// <summary>
    /// The smallest <paramref name="node"/> can be along an axis, in cells. A pane is the flat floor; a
    /// split running <em>in</em> that axis needs the sum of its children's floors plus its dividers, and
    /// one running across it needs the largest of them. So squeezing a subtree can never crush a pane
    /// nested inside it below the floor — which a single flat minimum on the outermost child would allow.
    /// </summary>
    public static int Minimum(LayoutNode node, bool horizontal)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node is not SplitNode split)
        {
            return horizontal ? MinimumPaneColumns : MinimumPaneRows;
        }

        if ((split.Direction == SplitDirection.Row) != horizontal)
        {
            return split.Children.Max(child => Minimum(child, horizontal));
        }

        return split.Children.Sum(child => Minimum(child, horizontal))
            + (LayoutSolver.DividerThickness * (split.Children.Count - 1));
    }

    /// <summary>The arrow a direction is spelled with, for the surfaces that name the chord.</summary>
    public static string Arrow(PaneDirection direction) => direction switch
    {
        PaneDirection.Left => "←",
        PaneDirection.Right => "→",
        PaneDirection.Up => "↑",
        _ => "↓",
    };

    /// <summary>
    /// Why a resize did nothing, in the words the status row prints. Here rather than in the view for the
    /// same reason <c>CommandCatalog</c>'s titles are: it is the wording a test can hold the behaviour to,
    /// and every arm of <see cref="PaneResizeOutcome"/> has to have one — a refusal with no sentence is
    /// the silent no-op this whole path exists to avoid.
    /// </summary>
    public static string Describe(PaneResizeOutcome outcome, PaneDirection direction)
    {
        var horizontal = direction is PaneDirection.Left or PaneDirection.Right;
        var bigger = direction is PaneDirection.Right or PaneDirection.Up;

        return outcome switch
        {
            PaneResizeOutcome.Resized => string.Empty,
            PaneResizeOutcome.Zoomed => "this pane is zoomed — ⌃B z shows the others",
            PaneResizeOutcome.NotMeasured => "the panes have not been drawn yet",
            PaneResizeOutcome.NoSplitInThatAxis when horizontal =>
                "nothing divides this pane's columns — ⌃B | splits it side by side",
            PaneResizeOutcome.NoSplitInThatAxis =>
                "nothing divides this pane's rows — ⌃B - stacks a split under it",
            _ when bigger && horizontal => $"the pane beside it is down to {MinimumPaneColumns} columns",
            _ when bigger => $"the pane beyond it is down to {MinimumPaneRows} rows",
            _ when horizontal => $"this pane is down to {MinimumPaneColumns} columns",
            _ => $"this pane is down to {MinimumPaneRows} rows",
        };
    }

    /// <summary>
    /// Moves the border between child <paramref name="index"/> and its paying sibling, in cells, and
    /// writes the split's fractions back from the result.
    /// </summary>
    private static PaneResizeResult MoveBorder(
        SplitNode split,
        int index,
        bool horizontal,
        int delta,
        IReadOnlyDictionary<string, PaneRect> rects)
    {
        var extents = new int[split.Children.Count];
        for (var i = 0; i < extents.Length; i++)
        {
            if (Extent(split.Children[i], horizontal, rects) is not { } cells)
            {
                return new PaneResizeResult(PaneResizeOutcome.NotMeasured, 0);
            }

            extents[i] = cells;
        }

        var total = extents.Sum();
        if (total <= 0)
        {
            return new PaneResizeResult(PaneResizeOutcome.NotMeasured, 0);
        }

        // The sibling on the side the border is: the next child, or the previous one at the far end.
        // A SplitNode always has at least two children, so one of the two always exists.
        var donor = index + 1 < extents.Length ? index + 1 : index - 1;

        // How far the border can travel before either side is at its floor. A pane already under its
        // floor — a workspace on a terminal too small for the split it holds — inverts these, and is the
        // one case where even a partial step is refused rather than made worse.
        var most = extents[donor] - Minimum(split.Children[donor], horizontal);
        var least = Minimum(split.Children[index], horizontal) - extents[index];
        if (least > most)
        {
            return new PaneResizeResult(PaneResizeOutcome.AtMinimum, 0);
        }

        var moved = Math.Clamp(delta, least, most);
        if (moved == 0)
        {
            return new PaneResizeResult(PaneResizeOutcome.AtMinimum, 0);
        }

        extents[index] += moved;
        extents[donor] -= moved;
        split.SetSizes(extents.Select(cells => (double)cells / total).ToList());
        return new PaneResizeResult(PaneResizeOutcome.Resized, moved);
    }

    /// <summary>
    /// A subtree's extent along one axis, in cells: its panes' rectangles unioned. Null when any of them
    /// has no rectangle, because a partial answer here would move a border by a made-up distance.
    /// </summary>
    private static int? Extent(
        LayoutNode node,
        bool horizontal,
        IReadOnlyDictionary<string, PaneRect> rects)
    {
        int? near = null;
        int? far = null;

        foreach (var pane in node.Panes())
        {
            if (!rects.TryGetValue(pane.Id, out var rect))
            {
                return null;
            }

            var start = horizontal ? rect.X : rect.Y;
            var end = start + (horizontal ? rect.Width : rect.Height);
            near = near is { } n ? Math.Min(n, start) : start;
            far = far is { } f ? Math.Max(f, end) : end;
        }

        return near is { } lo && far is { } hi ? hi - lo : null;
    }

    /// <summary>
    /// The splits between the root and a pane, outermost first, each paired with the index of the child
    /// the pane is inside. Empty when the root <em>is</em> the pane (a one-pane workspace) or the id is
    /// stale; both mean the same thing to the caller — no border anywhere to move.
    /// </summary>
    private static List<(SplitNode Split, int Index)> Ancestry(LayoutNode root, string paneId)
    {
        var path = new List<(SplitNode, int)>();
        return Descend(root) ? path : new List<(SplitNode, int)>();

        bool Descend(LayoutNode node)
        {
            if (node is not SplitNode split)
            {
                return string.Equals(((PaneNode)node).Id, paneId, StringComparison.Ordinal);
            }

            for (var i = 0; i < split.Children.Count; i++)
            {
                path.Add((split, i));
                if (Descend(split.Children[i]))
                {
                    return true;
                }

                path.RemoveAt(path.Count - 1);
            }

            return false;
        }
    }
}
