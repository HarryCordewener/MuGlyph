using MuClient.Core.Workspaces;

namespace MuClient.Core.Tests.Workspaces;

public class PaneCommandsTests
{
    [Test]
    [Arguments('|', PaneCommand.SplitRight)]
    [Arguments('-', PaneCommand.SplitDown)]
    [Arguments('z', PaneCommand.Zoom)]
    [Arguments('o', PaneCommand.CycleFocus)]
    [Arguments('x', PaneCommand.ClosePane)]
    [Arguments('<', PaneCommand.ReorderTabLeft)]
    [Arguments('>', PaneCommand.ReorderTabRight)]
    [Arguments('q', PaneCommand.None)]
    public async Task Resolve_MapsPrefixKeys(char key, PaneCommand expected)
    {
        await Assert.That(PaneCommands.Resolve(key)).IsEqualTo(expected);
    }

    [Test]
    public async Task Apply_SplitRight_SplitsFocusedPane()
    {
        var layout = new WorkspaceLayout(new[] { "a", "b" });

        var changed = PaneCommands.Apply(layout, PaneCommand.SplitRight);

        await Assert.That(changed).IsTrue();
        await Assert.That(layout.Root).IsTypeOf<SplitNode>();
        await Assert.That(((SplitNode)layout.Root).Direction).IsEqualTo(SplitDirection.Row);
    }

    [Test]
    public async Task Apply_SplitDown_UsesColumnDirection()
    {
        var layout = new WorkspaceLayout(new[] { "a", "b" });

        PaneCommands.Apply(layout, PaneCommand.SplitDown);

        await Assert.That(((SplitNode)layout.Root).Direction).IsEqualTo(SplitDirection.Column);
    }

    [Test]
    public async Task Apply_Zoom_TogglesZoomOnFocusedPane()
    {
        var layout = new WorkspaceLayout(new[] { "a" });

        PaneCommands.Apply(layout, PaneCommand.Zoom);

        await Assert.That(layout.ZoomedPaneId).IsEqualTo(layout.FocusedPaneId);
    }

    [Test]
    public async Task Apply_CycleFocus_MovesFocus()
    {
        var layout = new WorkspaceLayout(new[] { "a", "b" });
        layout.SplitFocused(SplitDirection.Row);
        var before = layout.FocusedPaneId;

        PaneCommands.Apply(layout, PaneCommand.CycleFocus);

        await Assert.That(layout.FocusedPaneId).IsNotEqualTo(before);
    }

    [Test]
    public async Task Apply_ReorderTabRight_MovesActiveTab()
    {
        var layout = new WorkspaceLayout(new[] { "a", "b", "c" }); // active "a" at index 0

        var changed = PaneCommands.Apply(layout, PaneCommand.ReorderTabRight);

        await Assert.That(changed).IsTrue();
        await Assert.That(layout.FocusedPane.Tabs).IsEquivalentTo(new[] { "b", "a", "c" });
        await Assert.That(layout.FocusedPane.ActiveTab).IsEqualTo("a");
    }

    [Test]
    public async Task Apply_None_IsNoOp()
    {
        var layout = new WorkspaceLayout(new[] { "a" });

        await Assert.That(PaneCommands.Apply(layout, PaneCommand.None)).IsFalse();
    }

    [Test]
    public async Task Apply_SplitRight_WithSingleTab_ReturnsFalse()
    {
        var layout = new WorkspaceLayout(new[] { "only" });

        await Assert.That(PaneCommands.Apply(layout, PaneCommand.SplitRight)).IsFalse();
    }
}
