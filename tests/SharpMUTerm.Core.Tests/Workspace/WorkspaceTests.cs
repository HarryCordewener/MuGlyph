using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Tests.Workspaces;

public class WorkspaceTests
{
    [Test]
    public async Task New_HasVisibleMainWindow()
    {
        var w = new Workspace("main", "Main", sessionKey: "Server.Wizard");

        await Assert.That(w.Windows).HasSingleItem();
        var main = w.FindWindow("main")!;
        await Assert.That(main.Kind).IsEqualTo(WindowKind.Main);
        await Assert.That(main.SessionKey).IsEqualTo("Server.Wizard");
        await Assert.That(w.IsVisible("main")).IsTrue();
        await Assert.That(main.Unread).IsEqualTo(0);
    }

    [Test]
    public async Task RouteSpawn_CreatesBackgroundWindow_AndAccruesUnread()
    {
        var w = new Workspace();

        var chat = w.RouteSpawn("Chat");

        await Assert.That(chat.Kind).IsEqualTo(WindowKind.Spawn);
        await Assert.That(chat.Id).IsEqualTo(Workspace.SpawnWindowId("Chat"));
        await Assert.That(w.IsVisible(chat.Id)).IsFalse(); // main stays the active tab
        await Assert.That(chat.Unread).IsEqualTo(1);

        // Second route reuses the same window and keeps counting.
        var again = w.RouteSpawn("Chat");
        await Assert.That(again).IsEqualTo(chat);
        await Assert.That(w.Windows).Count().IsEqualTo(2);
        await Assert.That(chat.Unread).IsEqualTo(2);
    }

    [Test]
    public async Task ActivateWindow_MakesVisible_AndClearsUnread()
    {
        var w = new Workspace();
        var chat = w.RouteSpawn("Chat");
        await Assert.That(chat.Unread).IsEqualTo(1);

        var ok = w.ActivateWindow(chat.Id);

        await Assert.That(ok).IsTrue();
        await Assert.That(w.IsVisible(chat.Id)).IsTrue();
        await Assert.That(chat.Unread).IsEqualTo(0);
        // Activating a background tab hides the previously-active one.
        await Assert.That(w.IsVisible("main")).IsFalse();
    }

    [Test]
    public async Task NoteActivity_OnVisibleWindow_DoesNotIncrement()
    {
        var w = new Workspace();

        w.NoteActivity("main"); // main is visible

        await Assert.That(w.FindWindow("main")!.Unread).IsEqualTo(0);
    }

    [Test]
    public async Task OpenWindow_IsIdempotent_AndActivatesByDefault()
    {
        var w = new Workspace();

        var web = w.OpenWindow("web:1", "example.com", WindowKind.Auxiliary);
        await Assert.That(w.IsVisible("web:1")).IsTrue();

        var second = w.OpenWindow("web:1", "ignored");
        await Assert.That(second).IsEqualTo(web);
        await Assert.That(web.Title).IsEqualTo("example.com");
        await Assert.That(w.Windows).Count().IsEqualTo(2);
    }

    [Test]
    public async Task SetUnsentInput_TogglesMarker()
    {
        var w = new Workspace();

        w.SetUnsentInput("main", true);
        await Assert.That(w.FindWindow("main")!.HasUnsentInput).IsTrue();

        w.SetUnsentInput("main", false);
        await Assert.That(w.FindWindow("main")!.HasUnsentInput).IsFalse();
    }

    [Test]
    public async Task CloseWindow_RemovesFromRegistryAndLayout()
    {
        var w = new Workspace();
        w.RouteSpawn("Chat");
        var chatId = Workspace.SpawnWindowId("Chat");

        var closed = w.CloseWindow(chatId);

        await Assert.That(closed).IsTrue();
        await Assert.That(w.FindWindow(chatId)).IsNull();
        await Assert.That(w.Layout.FindWindow(chatId)).IsNull();
        await Assert.That(w.Windows).HasSingleItem();
    }

    [Test]
    public async Task CloseWindow_Unknown_ReturnsFalse()
    {
        var w = new Workspace();
        await Assert.That(w.CloseWindow("ghost")).IsFalse();
    }

    [Test]
    public async Task RouteSpawn_WhileVisible_DoesNotAccrueUnread()
    {
        var w = new Workspace();
        var chat = w.RouteSpawn("Chat"); // background tab, unread = 1
        w.ActivateWindow(chat.Id);        // now the visible active tab, unread cleared

        w.RouteSpawn("Chat");             // routed while visible

        await Assert.That(w.IsVisible(chat.Id)).IsTrue();
        await Assert.That(chat.Unread).IsEqualTo(0);
    }
}
