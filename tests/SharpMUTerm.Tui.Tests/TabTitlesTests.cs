using SharpConsoleUI.Parsing;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

public class TabTitlesTests
{
    /// <summary>
    /// A background window carrying <paramref name="unread"/> unread lines, accrued through the workspace's
    /// own counter rather than written onto the window — <c>WorkspaceWindow.Unread</c> is settable only
    /// inside Core, and going round it would be testing a number this code does not read.
    /// </summary>
    private static WorkspaceWindow Background(string title, int unread)
    {
        var ws = new Workspace();
        var window = ws.RouteSpawn(title); // opens in the background, so the first route already counts 1
        for (var i = 1; i < unread; i++)
        {
            ws.NoteActivity(window.Id);
        }

        return window;
    }

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
        await Assert.That(TabTitles.For(chat)).IsEqualTo($"[{UnreadBadge.Tint}]Chat (2)[/]");
    }

    /// <summary>
    /// The count is capped exactly as the sidebar's badge is, from the same formatter. Uncapped it grew a
    /// digit at a time from the wire — and the rail, which <em>is</em> capped, then read <c>99+</c> beside
    /// a tab reading <c>(150)</c>: two answers to one number.
    /// </summary>
    [Test]
    [Arguments(99, "99")]
    [Arguments(100, "99+")]
    [Arguments(4127, "99+")]
    public async Task Unread_IsCappedTheWayTheSidebarCapsIt(int unread, string badge)
    {
        var window = Background("Mannaz", unread);
        await Assert.That(window.Unread).IsEqualTo(unread);
        await Assert.That(TabTitles.For(window)).IsEqualTo($"[{UnreadBadge.Tint}]Mannaz ({badge})[/]");
    }

    /// <summary>
    /// The tint covers the name and the count and nothing else. The <c>▌</c> ahead of it says which pane
    /// holds the keyboard — an independent fact, true or false whatever the count is — so recolouring it
    /// would make one signal look like the other, which is the confusion this indicator has to avoid.
    /// </summary>
    [Test]
    public async Task TheFocusMarkerStaysOutsideTheActivityTint()
    {
        var window = Background("Mannaz", 36);
        var label = TabTitles.For(window, focusedPane: true);

        await Assert.That(label).IsEqualTo($"{Glyphs.FocusedPane} [{UnreadBadge.Tint}]Mannaz (36)[/]");
        await Assert.That(label.StartsWith(Glyphs.FocusedPane, StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// A window title is world- and user-supplied text, and the label is markup — the framework parses it
    /// and measures every width, including the click hit test, with <c>StripLength</c>. So a title
    /// containing brackets has to survive as brackets rather than being eaten as a tag.
    /// </summary>
    [Test]
    public async Task ATitleWithBracketsIsEscapedRatherThanParsedAsMarkup()
    {
        var window = new WorkspaceWindow("w1", "[Chat] room", WindowKind.Spawn) { OwnerLabel = "[Corvid]" };
        var label = TabTitles.For(window);

        await Assert.That(label).IsEqualTo("[[Corvid]] - [[Chat]] room");
        await Assert.That(MarkupParser.StripLength(label)).IsEqualTo("[Corvid] - [Chat] room".Length);
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
        await Assert.That(TabTitles.For(chat)).IsEqualTo($"[{UnreadBadge.Tint}]Chat (1)[/] {Glyphs.Draft}");
    }

    [Test]
    public async Task TheLabelCarriesNoCloseAffordance()
    {
        // The ✕ used to be appended here for the active tab, and that was the bug: a tab title is
        // drawn as plain text, so the framework's hit test counts those cells as part of the title
        // and a click on them only selects the tab. The close button is now the framework's own
        // TabPage.IsClosable — pinned in PaneTabCloseTests, which drives a real click through it.
        var main = new Workspace(mainWindowId: "main", mainTitle: "Server").FindWindow("main")!;
        await Assert.That(TabTitles.For(main)).DoesNotContain(Glyphs.Close);
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
        await Assert.That(TabTitles.For(chat))
            .IsEqualTo($"[{UnreadBadge.Tint}]Corvid - Chat (1)[/] {Glyphs.Draft}");
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
