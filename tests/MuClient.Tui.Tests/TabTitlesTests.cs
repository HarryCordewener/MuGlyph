using MuClient.Core.Workspaces;
using MuClient.Tui;

namespace MuClient.Tui.Tests;

public class TabTitlesTests
{
    [Test]
    public async Task PlainWindow_IsJustItsTitle()
    {
        var main = new Workspace(mainWindowId: "main", mainTitle: "Server").FindWindow("main")!;
        await Assert.That(TabTitles.For(main)).IsEqualTo("Server");
    }

    [Test]
    public async Task Unread_AppendsACountBadge()
    {
        var ws = new Workspace();
        ws.RouteSpawn("Chat");
        var chat = ws.RouteSpawn("Chat"); // two background routes → unread 2
        await Assert.That(TabTitles.For(chat)).IsEqualTo("Chat (2)");
    }

    [Test]
    public async Task UnsentInput_AppendsAPen()
    {
        var ws = new Workspace();
        var chat = ws.RouteSpawn("Trade");
        ws.ActivateWindow(chat.Id);        // clears unread
        ws.SetUnsentInput(chat.Id, true);
        await Assert.That(TabTitles.For(chat)).IsEqualTo($"Trade {Glyphs.Draft}");
    }

    [Test]
    public async Task UnreadAndUnsent_ShowBoth()
    {
        var ws = new Workspace();
        var chat = ws.RouteSpawn("Chat"); // unread 1, background
        ws.SetUnsentInput(chat.Id, true);
        await Assert.That(TabTitles.For(chat)).IsEqualTo($"Chat (1) {Glyphs.Draft}");
    }

    [Test]
    public async Task ActiveTab_AppendsACloseAffordance()
    {
        var main = new Workspace(mainWindowId: "main", mainTitle: "Server").FindWindow("main")!;
        await Assert.That(TabTitles.For(main, focusedCharacterKey: null, isActive: true)).IsEqualTo($"Server {Glyphs.Close}");
    }

    [Test]
    public async Task ChildWindow_WithOwner_PrefixesTheConnectionOwner()
    {
        var window = new WorkspaceWindow("w1", "Chat", WindowKind.Spawn) { OwnerLabel = "Corvid" };
        await Assert.That(TabTitles.For(window)).IsEqualTo("Corvid - Chat");
    }

    [Test]
    public async Task ChildWindow_OwnerPrefixPrecedesBadges()
    {
        var ws = new Workspace();
        var chat = ws.RouteSpawn("Chat"); // unread 1, background
        chat.OwnerLabel = "Corvid";
        ws.SetUnsentInput(chat.Id, true);
        await Assert.That(TabTitles.For(chat)).IsEqualTo($"Corvid - Chat (1) {Glyphs.Draft}");
    }

    [Test]
    public async Task MainWindow_IsNeverPrefixed()
    {
        var main = new Workspace(mainWindowId: "main", mainTitle: "main").FindWindow("main")!;
        main.OwnerLabel = "Corvid"; // even if set, a main window shows no prefix
        await Assert.That(TabTitles.For(main)).IsEqualTo("main");
    }

    [Test]
    public async Task DifferentCharacter_AppendsACrossMarker()
    {
        var window = new WorkspaceWindow("w1", "pages", WindowKind.Spawn, sessionKey: "Aetherfall.Rookery");

        // Focused character is Corvid, but this window belongs to Rookery → ⌁.
        var label = TabTitles.For(window, focusedCharacterKey: "Aetherfall.Corvid");
        await Assert.That(label).IsEqualTo("pages ⌁");
    }

    [Test]
    public async Task SameCharacter_HasNoCrossMarker()
    {
        var window = new WorkspaceWindow("w1", "main", WindowKind.Main, sessionKey: "Aetherfall.Corvid");
        var label = TabTitles.For(window, focusedCharacterKey: "Aetherfall.Corvid");
        await Assert.That(label).IsEqualTo("main");
    }
}
