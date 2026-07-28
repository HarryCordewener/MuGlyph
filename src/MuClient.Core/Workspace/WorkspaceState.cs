namespace MuClient.Core.Workspaces;

/// <summary>Serialisable snapshot of one window's identity/metadata (not its scrollback).</summary>
public sealed class WorkspaceWindowState
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public WindowKind Kind { get; set; } = WindowKind.Main;
    public string? SessionKey { get; set; }
    public string? OwnerLabel { get; set; }
    public string? CapturePattern { get; set; }
}

/// <summary>
/// Serialisable snapshot of a layout tree node. A single shape carries both variants, tagged by
/// <see cref="Type"/> (<c>pane</c> or <c>split</c>), so the tree round-trips through plain JSON with
/// no polymorphic-serialisation setup.
/// </summary>
public sealed class LayoutNodeState
{
    public string Type { get; set; } = "pane";

    // pane
    public string? Id { get; set; }
    public List<string> Tabs { get; set; } = new();
    public int ActiveIndex { get; set; }
    public bool Frozen { get; set; }

    // split
    public SplitDirection Direction { get; set; }
    public List<double> Sizes { get; set; } = new();
    public List<LayoutNodeState> Children { get; set; } = new();
}

/// <summary>
/// A serialisable snapshot of a <see cref="Workspace"/> — its windows, pane tree, and focus/zoom —
/// so the last session can be persisted and resumed. Scrollback and transient badges (unread,
/// unsent-input) are intentionally excluded: resume restores structure, not history. Pure and
/// UI-agnostic; <see cref="Capture"/> serialises a live workspace and <see cref="Restore"/> rebuilds one.
/// </summary>
public sealed class WorkspaceState
{
    public List<WorkspaceWindowState> Windows { get; set; } = new();
    public LayoutNodeState Root { get; set; } = new();
    public string FocusedPaneId { get; set; } = string.Empty;
    public string? ZoomedPaneId { get; set; }

    /// <summary>Captures the current workspace into a serialisable state.</summary>
    public static WorkspaceState Capture(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return new WorkspaceState
        {
            Windows = workspace.Windows.Select(w => new WorkspaceWindowState
            {
                Id = w.Id,
                Title = w.Title,
                Kind = w.Kind,
                SessionKey = w.SessionKey,
                OwnerLabel = w.OwnerLabel,
                CapturePattern = w.CapturePattern,
            }).ToList(),
            Root = CaptureNode(workspace.Layout.Root),
            FocusedPaneId = workspace.Layout.FocusedPaneId,
            ZoomedPaneId = workspace.Layout.ZoomedPaneId,
        };
    }

    /// <summary>Rebuilds a live workspace from this state.</summary>
    public Workspace Restore()
    {
        var windows = Windows.Select(w =>
            new WorkspaceWindow(w.Id, w.Title, w.Kind, w.SessionKey)
            {
                OwnerLabel = w.OwnerLabel,
                CapturePattern = w.CapturePattern,
            });

        var layout = new WorkspaceLayout(BuildNode(Root), FocusedPaneId, ZoomedPaneId);
        return new Workspace(windows, layout);
    }

    private static LayoutNodeState CaptureNode(LayoutNode node) => node switch
    {
        PaneNode pane => new LayoutNodeState
        {
            Type = "pane",
            Id = pane.Id,
            Tabs = new List<string>(pane.Tabs),
            ActiveIndex = pane.ActiveIndex,
            Frozen = pane.Frozen,
        },
        SplitNode split => new LayoutNodeState
        {
            Type = "split",
            Direction = split.Direction,
            Sizes = new List<double>(split.Sizes),
            Children = split.Children.Select(CaptureNode).ToList(),
        },
        _ => throw new InvalidOperationException($"Unknown layout node: {node.GetType().Name}"),
    };

    private static LayoutNode BuildNode(LayoutNodeState state)
    {
        if (string.Equals(state.Type, "split", StringComparison.OrdinalIgnoreCase))
        {
            var children = state.Children.Select(BuildNode).ToList();
            return new SplitNode(state.Direction, children, state.Sizes);
        }

        if (string.Equals(state.Type, "pane", StringComparison.OrdinalIgnoreCase))
        {
            return new PaneNode(state.Id ?? "p1", state.Tabs, state.ActiveIndex, state.Frozen);
        }

        // A typo or a newer serialized node type shouldn't silently degrade to a pane and lose structure.
        throw new InvalidOperationException($"Unknown layout node type: {state.Type}");
    }
}
