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
        await Assert.That(TabTitles.For(chat)).IsEqualTo("Trade ✎");
    }

    [Test]
    public async Task UnreadAndUnsent_ShowBoth()
    {
        var ws = new Workspace();
        var chat = ws.RouteSpawn("Chat"); // unread 1, background
        ws.SetUnsentInput(chat.Id, true);
        await Assert.That(TabTitles.For(chat)).IsEqualTo("Chat (1) ✎");
    }
}
