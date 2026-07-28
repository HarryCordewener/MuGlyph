namespace MuClient.Core.Workspaces;

/// <summary>A tmux-style pane command, triggered by a key after the <c>⌃B</c> prefix.</summary>
public enum PaneCommand
{
    /// <summary>No command bound to the key.</summary>
    None,

    /// <summary>Split the focused pane side-by-side (<c>|</c>), moving its non-active tabs right.</summary>
    SplitRight,

    /// <summary>Split the focused pane stacked (<c>-</c>), moving its non-active tabs down.</summary>
    SplitDown,

    /// <summary>Zoom / unzoom the focused pane (<c>z</c>).</summary>
    Zoom,

    /// <summary>Cycle focus to the next pane (<c>o</c>).</summary>
    CycleFocus,

    /// <summary>Close the focused pane (<c>x</c>).</summary>
    ClosePane,

    /// <summary>Move the active tab one slot left (<c>&lt;</c>).</summary>
    ReorderTabLeft,

    /// <summary>Move the active tab one slot right (<c>&gt;</c>).</summary>
    ReorderTabRight,
}

/// <summary>
/// Maps prefix keys to <see cref="PaneCommand"/>s and applies them to a <see cref="WorkspaceLayout"/>.
/// Pure and UI-agnostic: the Terminal.Gui key handler resolves a key here, then applies the result,
/// keeping the tmux keymap out of the view code and unit-testable.
/// </summary>
public static class PaneCommands
{
    /// <summary>Resolves a prefix key to its command, or <see cref="PaneCommand.None"/> if unbound.</summary>
    public static PaneCommand Resolve(char key) => key switch
    {
        '|' => PaneCommand.SplitRight,
        '-' => PaneCommand.SplitDown,
        'z' or 'Z' => PaneCommand.Zoom,
        'o' or 'O' => PaneCommand.CycleFocus,
        'x' or 'X' => PaneCommand.ClosePane,
        '<' => PaneCommand.ReorderTabLeft,
        '>' => PaneCommand.ReorderTabRight,
        _ => PaneCommand.None,
    };

    /// <summary>
    /// Applies a command to the layout. Returns true if the layout changed (or a change was
    /// attempted, e.g. cycle/close/zoom), false for <see cref="PaneCommand.None"/> and no-op splits.
    /// </summary>
    public static bool Apply(WorkspaceLayout layout, PaneCommand command)
    {
        ArgumentNullException.ThrowIfNull(layout);
        switch (command)
        {
            case PaneCommand.SplitRight:
                return layout.SplitFocused(SplitDirection.Row);
            case PaneCommand.SplitDown:
                return layout.SplitFocused(SplitDirection.Column);
            case PaneCommand.Zoom:
                layout.ToggleZoom();
                return true;
            case PaneCommand.CycleFocus:
                layout.CycleFocus();
                return true;
            case PaneCommand.ClosePane:
                layout.CloseFocused();
                return true;
            case PaneCommand.ReorderTabLeft:
                return layout.ReorderActiveTab(-1);
            case PaneCommand.ReorderTabRight:
                return layout.ReorderActiveTab(1);
            default:
                return false;
        }
    }
}
