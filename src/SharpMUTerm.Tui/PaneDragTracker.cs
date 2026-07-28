using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Tui;

/// <summary>The kind of mouse frame a flag list represents, as far as a pane drag cares.</summary>
internal enum PaneDragInput
{
    /// <summary>Nothing this gesture reacts to (wheel, plain motion, other buttons).</summary>
    Ignored,

    /// <summary>The primary button went down.</summary>
    Press,

    /// <summary>The pointer moved with the primary button held.</summary>
    Drag,

    /// <summary>The primary button came up.</summary>
    Release,

    /// <summary>The secondary button went down — the conventional "abandon this drag".</summary>
    Abort,
}

/// <summary>What the adapter should do with the tracker's new state after a mouse frame.</summary>
internal enum PaneDragAction
{
    /// <summary>Nothing changed that the UI needs to show.</summary>
    None,

    /// <summary>A drag just started: paint the drop preview.</summary>
    Begin,

    /// <summary>The hovered pane or edge changed: repaint the drop preview.</summary>
    Update,

    /// <summary>The drag ended on a target: apply the drop, then repaint.</summary>
    Commit,

    /// <summary>The drag ended with nothing to apply: tear the preview down.</summary>
    Cancel,
}

/// <summary>The outcome of feeding one mouse frame to <see cref="PaneDragTracker"/>.</summary>
/// <param name="Action">What the adapter should do.</param>
/// <param name="WindowId">The window being dragged, when a drag is or was live.</param>
/// <param name="TargetPaneId">The pane under the pointer, or null when it is over no pane.</param>
/// <param name="Edge">The edge to split toward, or null for a central (tab) drop.</param>
internal readonly record struct PaneDragResult(
    PaneDragAction Action,
    string? WindowId,
    string? TargetPaneId,
    Edge? Edge);

/// <summary>
/// Assembles a pane drag-and-drop gesture out of the individual mouse frames the console driver
/// reports — the framework tracks no drag state of its own beyond routing capture, so press, motion
/// and release have to be stitched together here. A press on a pane's tab strip arms the gesture; the
/// first motion away from that cell starts the drag; each subsequent motion resolves the pane under
/// the pointer and, through <see cref="DropZones"/>, which edge of it is targeted; release commits.
///
/// Pure and framework-free apart from the <see cref="MouseFlags"/> enum it decodes, so the whole
/// gesture is unit-testable without a terminal. The app supplies the geometry snapshot and applies
/// the result via <see cref="PaneDrop"/>.
/// </summary>
internal sealed class PaneDragTracker
{
    private readonly double _edgeFraction;

    private PaneDragSurface? _surface;
    private string? _windowId;
    private string? _sourcePaneId;
    private int _originX;
    private int _originY;
    private bool _armed;
    private bool _dragging;

    /// <summary>Creates a tracker; <paramref name="edgeFraction"/> is the drop-zone edge margin.</summary>
    public PaneDragTracker(double edgeFraction = DropZones.DefaultEdgeFraction) => _edgeFraction = edgeFraction;

    /// <summary>True once the pointer has moved off the pressed cell with the button still down.</summary>
    public bool IsDragging => _dragging;

    /// <summary>The window being dragged, or null when no drag is live.</summary>
    public string? WindowId => _dragging ? _windowId : null;

    /// <summary>The pane the drag started from, or null when no drag is live.</summary>
    public string? SourcePaneId => _dragging ? _sourcePaneId : null;

    /// <summary>The pane under the pointer, or null when it is over no pane (or no drag is live).</summary>
    public string? TargetPaneId { get; private set; }

    /// <summary>The edge the current drop would split toward, or null for a tab drop.</summary>
    public Edge? TargetEdge { get; private set; }

    /// <summary>The geometry frozen when the drag started, or null when no drag is live.</summary>
    public PaneDragSurface? Surface => _dragging ? _surface : null;

    /// <summary>
    /// Classifies a driver flag list. Motion with the button held arrives two ways depending on the
    /// terminal's encoding — as <see cref="MouseFlags.Button1Dragged"/>, or (in SGR) as
    /// <see cref="MouseFlags.Button1Pressed"/> together with <see cref="MouseFlags.ReportMousePosition"/>
    /// — so both are treated as a drag, and a press is only a press when neither motion bit is set.
    /// </summary>
    public static PaneDragInput Classify(IReadOnlyList<MouseFlags> flags)
    {
        if (flags is null || flags.Count == 0)
        {
            return PaneDragInput.Ignored;
        }

        if (Has(flags, MouseFlags.Button1Released))
        {
            return PaneDragInput.Release;
        }

        var motion = Has(flags, MouseFlags.Button1Dragged) || Has(flags, MouseFlags.ReportMousePosition);
        var down = Has(flags, MouseFlags.Button1Pressed);

        if (Has(flags, MouseFlags.Button1Dragged) || (down && motion))
        {
            return PaneDragInput.Drag;
        }

        if (down)
        {
            return PaneDragInput.Press;
        }

        if (Has(flags, MouseFlags.Button3Pressed) || Has(flags, MouseFlags.Button3Clicked))
        {
            return PaneDragInput.Abort;
        }

        return PaneDragInput.Ignored;
    }

    /// <summary>
    /// Feeds one mouse frame, in desktop cells. <paramref name="snapshot"/> is called only when a
    /// press lands, so the layout is read back at most once per gesture.
    /// </summary>
    public PaneDragResult Handle(IReadOnlyList<MouseFlags> flags, int x, int y, Func<PaneDragSurface> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return Classify(flags) switch
        {
            PaneDragInput.Press => OnPress(x, y, snapshot),
            PaneDragInput.Drag => OnDrag(x, y),
            PaneDragInput.Release => OnRelease(x, y),
            PaneDragInput.Abort => _dragging ? Finish(PaneDragAction.Cancel) : Idle(),
            _ => Idle(),
        };
    }

    /// <summary>Abandons any in-flight gesture without producing a result (e.g. on a layout change).</summary>
    public void Reset()
    {
        _surface = null;
        _windowId = null;
        _sourcePaneId = null;
        _armed = false;
        _dragging = false;
        TargetPaneId = null;
        TargetEdge = null;
    }

    private PaneDragResult OnPress(int x, int y, Func<PaneDragSurface> snapshot)
    {
        // A press always ends whatever came before: a drag can only be abandoned mid-flight if the
        // release was swallowed, and re-pressing must not resume it.
        var wasDragging = _dragging;
        Reset();

        var surface = snapshot();
        if (surface.IsEmpty || surface.PaneAt(x, y) is not { } paneId)
        {
            return wasDragging ? Cancelled() : Idle();
        }

        // Only the tab strip is a drag handle; a press in the body belongs to the pane's content.
        if (!surface.IsTabStrip(paneId, y) || surface.ActiveWindow(paneId) is not { } windowId)
        {
            return wasDragging ? Cancelled() : Idle();
        }

        _surface = surface;
        _sourcePaneId = paneId;
        _windowId = windowId;
        _originX = x;
        _originY = y;
        _armed = true;
        return wasDragging ? Cancelled() : Idle();
    }

    private PaneDragResult OnDrag(int x, int y)
    {
        if (_armed && !_dragging)
        {
            // Stay a plain click until the pointer actually leaves the pressed cell, so clicking a tab
            // still selects it (the framework's own handling) without flickering a drop preview.
            if (x == _originX && y == _originY)
            {
                return Idle();
            }

            _dragging = true;
            _armed = false;
            Retarget(x, y);
            return new PaneDragResult(PaneDragAction.Begin, _windowId, TargetPaneId, TargetEdge);
        }

        if (!_dragging)
        {
            return Idle();
        }

        var previousPane = TargetPaneId;
        var previousEdge = TargetEdge;
        Retarget(x, y);

        return TargetPaneId == previousPane && TargetEdge == previousEdge
            ? Idle()
            : new PaneDragResult(PaneDragAction.Update, _windowId, TargetPaneId, TargetEdge);
    }

    private PaneDragResult OnRelease(int x, int y)
    {
        if (!_dragging)
        {
            // A press that never moved: a plain click, already handled by the control under it.
            Reset();
            return Idle();
        }

        Retarget(x, y);
        return Finish(TargetPaneId is null ? PaneDragAction.Cancel : PaneDragAction.Commit);
    }

    private PaneDragResult Finish(PaneDragAction action)
    {
        var result = new PaneDragResult(action, _windowId, TargetPaneId, TargetEdge);
        Reset();
        return result;
    }

    private void Retarget(int x, int y)
    {
        TargetPaneId = null;
        TargetEdge = null;

        if (_surface?.PaneAt(x, y) is not { } paneId || _surface.RectOf(paneId) is not { } rect)
        {
            return;
        }

        TargetPaneId = paneId;
        TargetEdge = DropZones.Resolve(rect.X, rect.Y, rect.Width, rect.Height, x, y, _edgeFraction);
    }

    private static PaneDragResult Idle() => new(PaneDragAction.None, null, null, null);

    private static PaneDragResult Cancelled() => new(PaneDragAction.Cancel, null, null, null);

    private static bool Has(IReadOnlyList<MouseFlags> flags, MouseFlags flag)
    {
        // Mirrors SharpConsoleUI's own MouseEventArgs.HasFlag: a driver may report each bit as its own
        // list entry (X10) or combine several into one value (SGR), so test the bits, not equality.
        for (var i = 0; i < flags.Count; i++)
        {
            if ((flags[i] & flag) == flag)
            {
                return true;
            }
        }

        return false;
    }
}
