using System.Text.Json;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Tests.Workspaces;

public class WorkspaceStateTests
{
    private static Workspace BuildScene()
    {
        // A main window plus a spawn, split into two panes: main | Chat.
        var ws = new Workspace(mainWindowId: "main", mainTitle: "main", sessionKey: "Aetherfall.Corvid");
        var chat = ws.RouteSpawn("Chat", "Aetherfall.Corvid");
        chat.OwnerLabel = "Corvid";
        ws.ActivateWindow("main");
        ws.Layout.SplitFocused(SplitDirection.Row); // moves Chat into a new pane
        return ws;
    }

    [Test]
    public async Task Capture_ThenRestore_PreservesWindowsAndTree()
    {
        var original = BuildScene();

        var restored = WorkspaceState.Capture(original).Restore();

        // Windows survive with their metadata.
        await Assert.That(restored.Windows.Count).IsEqualTo(2);
        var chat = restored.FindWindow(Workspace.SpawnWindowId("Chat"))!;
        await Assert.That(chat.Kind).IsEqualTo(WindowKind.Spawn);
        await Assert.That(chat.OwnerLabel).IsEqualTo("Corvid");
        await Assert.That(chat.SessionKey).IsEqualTo("Aetherfall.Corvid");

        // The split tree survives: two panes, main and Chat separated.
        await Assert.That(restored.Layout.Panes.Count).IsEqualTo(2);
        await Assert.That(restored.Layout.FindWindow("main")).IsNotNull();
        await Assert.That(restored.Layout.FindWindow(Workspace.SpawnWindowId("Chat"))).IsNotNull();
        await Assert.That(restored.Layout.FindWindow("main")!.Id)
            .IsNotEqualTo(restored.Layout.FindWindow(Workspace.SpawnWindowId("Chat"))!.Id);
    }

    [Test]
    public async Task Restore_PreservesFocusAndActiveTab()
    {
        var original = BuildScene();
        var focused = original.Layout.FocusedPaneId;

        var restored = WorkspaceState.Capture(original).Restore();

        await Assert.That(restored.Layout.FocusedPaneId).IsEqualTo(focused);
        await Assert.That(restored.Layout.FindWindow("main")!.ActiveTab).IsEqualTo("main");
    }

    [Test]
    public async Task Restore_AdvancesPaneCounter_SoNewSplitsDoNotCollide()
    {
        var restored = WorkspaceState.Capture(BuildScene()).Restore();
        var existingIds = restored.Layout.Panes.Select(p => p.Id).ToHashSet();

        // A fresh split must mint a pane id that doesn't clash with a restored one.
        restored.OpenWindow("aux", "Aux");
        restored.Layout.SplitFocused(SplitDirection.Column);
        var newIds = restored.Layout.Panes.Select(p => p.Id).ToList();

        await Assert.That(newIds.Count).IsEqualTo(newIds.Distinct().Count());
    }

    [Test]
    public async Task State_RoundTripsThroughJson()
    {
        var state = WorkspaceState.Capture(BuildScene());

        var json = JsonSerializer.Serialize(state, ConfigurationStore.SerializerOptions);
        var back = JsonSerializer.Deserialize<WorkspaceState>(json, ConfigurationStore.SerializerOptions)!;
        var restored = back.Restore();

        // Enums serialise as readable strings, and the tree survives the trip.
        await Assert.That(json).Contains("\"kind\": \"Spawn\"");
        await Assert.That(json).Contains("\"type\": \"split\"");
        await Assert.That(restored.Layout.Panes.Count).IsEqualTo(2);
        await Assert.That(restored.FindWindow(Workspace.SpawnWindowId("Chat"))!.OwnerLabel).IsEqualTo("Corvid");
    }
}
