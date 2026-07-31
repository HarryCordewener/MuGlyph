using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Tests.Workspaces;

/// <summary>
/// <b>A window's number is when it was opened, not where its tab sits.</b> ⌥1–⌥9 name windows, so the
/// order they are counted in is a thing a user learns and presses; <see cref="Workspace.WindowsFor"/>
/// is that order and this suite is what it stands on.
/// <para>
/// <b>Why not the registry's order.</b> <see cref="Workspace.Windows"/> is a dictionary's values, and a
/// dictionary's enumeration order is unspecified after a removal: closing a channel and opening another
/// can drop the newcomer into the freed slot, renumbering everything after it. That is the same defect
/// creation order was given to panes to prevent — a number moving under someone who had not touched it —
/// and it is worse here, because a capture window opens <em>unbidden from the wire</em>.
/// </para>
/// <para>
/// <b>Number versus sequence.</b> <see cref="WorkspaceWindow.Sequence"/> is a sort key that is never
/// reused; the number is the window's <em>position</em> in the sorted list. That distinction is the whole
/// of the compaction rule: reading sequences directly would leave holes after a close, with a digit doing
/// nothing while the windows sat on the screen.
/// </para>
/// <para>
/// <b>Placed, because a chord has to land somewhere.</b> A window the registry still knows and no pane
/// holds is drawn in the rail as <c>closed</c>; numbering it would spend a digit on a place there is no
/// way to go, and would shift every window after it.
/// </para>
/// </summary>
public class WindowNumberingTests
{
    // --- creation order, and what it is not ---------------------------------------------------------

    /// <summary>Windows are numbered in the order they were opened, whatever pane they end up in.</summary>
    [Test]
    public async Task WindowsAreNumberedInTheOrderTheyWereOpened()
    {
        var ws = new Workspace(mainWindowId: "main", mainTitle: "Main", sessionKey: "W.C");
        ws.RouteSpawn("Chat", "W.C");
        ws.OpenWindow("web", "Web");

        await Assert.That(Order(ws)).IsEqualTo("main,spawn:Chat,web");
    }

    /// <summary>
    /// <b>The defect this ordering exists to prevent.</b> Moving a window's tab to the front of another
    /// pane changes where it is and must not change what it is called: it is the same window, the user
    /// asked for it to be somewhere else, and nobody asked for its chord to move.
    /// </summary>
    [Test]
    public async Task MovingAWindowToAnotherPaneDoesNotRenumberAnything()
    {
        var ws = new Workspace(mainWindowId: "main", mainTitle: "Main", sessionKey: "W.C");
        ws.RouteSpawn("Chat", "W.C");
        ws.RouteSpawn("Trade", "W.C");
        var before = Order(ws);

        ws.Layout.SplitWithWindow("spawn:Trade", ws.Layout.FocusedPaneId, Edge.Left);

        await Assert.That(Order(ws)).IsEqualTo(before);
    }

    /// <summary>
    /// And reordering the tabs inside a pane does not either — the strip's order is a view, and the
    /// numbering is not a function of it.
    /// </summary>
    [Test]
    public async Task ReorderingTabsDoesNotRenumberAnything()
    {
        var ws = new Workspace(mainWindowId: "main", mainTitle: "Main", sessionKey: "W.C");
        ws.RouteSpawn("Chat", "W.C");
        var before = Order(ws);

        ws.ActivateWindow("spawn:Chat");
        await Assert.That(ws.Layout.ReorderActiveTab(-1)).IsTrue();

        await Assert.That(Order(ws)).IsEqualTo(before);
    }

    // --- compaction ---------------------------------------------------------------------------------

    /// <summary>
    /// <b>Closing the second of three leaves 1 and 2, not 1 and 3.</b> Sequences have holes after a close
    /// and the numbering may not, or a digit is a silent no-op with two windows on the screen.
    /// </summary>
    [Test]
    public async Task ClosingAWindowCompactsTheNumbering()
    {
        var ws = new Workspace(mainWindowId: "main", mainTitle: "Main", sessionKey: "W.C");
        ws.RouteSpawn("Chat", "W.C");
        ws.RouteSpawn("Trade", "W.C");

        ws.CloseWindow("spawn:Chat");

        await Assert.That(Order(ws)).IsEqualTo("main,spawn:Trade");
        await Assert.That(ws.WindowsFor(Owner)[1].Sequence)
            .IsGreaterThan(2)
            .Because("the sequence keeps its hole; only the position closes up");
    }

    /// <summary>
    /// <b>A new window always lands at the end, even in the slot a closed one freed.</b> This is the
    /// registry-order bug in the one shape that makes it visible: a <see cref="Dictionary{K, V}"/>
    /// enumerates in insertion order right up until an entry is removed, and then the <em>next</em> insert
    /// reuses the freed slot and is enumerated from the middle. So closing the second of four windows and
    /// opening a fifth puts the newcomer where the closed one was — every digit after it moves, on a
    /// keystroke nobody made, and the sidebar and the chords both follow. A weaker fixture (open, close,
    /// open, with two windows left) passes under either ordering and proves nothing.
    /// </summary>
    [Test]
    public async Task AWindowOpenedIntoAClosedOnesSlotStillTakesTheLastNumber()
    {
        var ws = new Workspace(mainWindowId: "main", mainTitle: "Main", sessionKey: "W.C");
        ws.RouteSpawn("Chat", "W.C");
        ws.RouteSpawn("Trade", "W.C");
        ws.RouteSpawn("Newbie", "W.C");

        ws.CloseWindow("spawn:Chat");   // frees the second slot
        ws.RouteSpawn("Guild", "W.C");  // which the registry hands straight back out

        await Assert.That(Order(ws)).IsEqualTo("main,spawn:Trade,spawn:Newbie,spawn:Guild")
            .Because("the newcomer is ⌥4, and Trade and Newbie are still ⌥2 and ⌥3");
    }

    // --- placed only --------------------------------------------------------------------------------

    /// <summary>
    /// A window no pane holds is still registered — the rail draws it as <c>closed</c> — and carries no
    /// number, because a digit that named it would name nowhere to go.
    /// </summary>
    [Test]
    public async Task AWindowNoPaneHoldsIsNotNumbered()
    {
        var ws = new Workspace(mainWindowId: "main", mainTitle: "Main", sessionKey: "W.C");
        ws.RouteSpawn("Chat", "W.C");
        ws.RouteSpawn("Trade", "W.C");

        ws.Layout.RemoveWindow("spawn:Chat"); // out of the tree, still in the registry

        await Assert.That(ws.Windows.Select(w => w.Id)).Contains("spawn:Chat");
        await Assert.That(Order(ws)).IsEqualTo("main,spawn:Trade");
    }

    // --- across a restart ---------------------------------------------------------------------------

    /// <summary>
    /// <b>The numbering survives a resume.</b> Captured and restored the way the shell does it, every
    /// window comes back in the position it was in — which is what persisting the sequence is for.
    /// </summary>
    [Test]
    public async Task TheNumberingComesBackAfterAResume()
    {
        var ws = new Workspace(mainWindowId: "main", mainTitle: "Main", sessionKey: "W.C");
        ws.RouteSpawn("Chat", "W.C");
        ws.RouteSpawn("Trade", "W.C");
        ws.CloseWindow("spawn:Chat");
        ws.RouteSpawn("Newbie", "W.C");
        var before = Order(ws);

        var resumed = WorkspaceState.Capture(ws).Restore();

        await Assert.That(Order(resumed)).IsEqualTo(before);
    }

    /// <summary>
    /// <b>A configuration written before windows carried a sequence comes back in the order it was saved
    /// in</b>, rather than sorting equal and landing wherever the sort happened to put them. That is the
    /// same migration <c>PaneNode.Unsequenced</c> gets, and it matters for the same reason: an existing
    /// client reads exactly as it read when it was closed.
    /// </summary>
    [Test]
    public async Task AWorkspaceSavedWithoutSequencesIsNumberedFromItsSavedOrder()
    {
        var state = new WorkspaceState
        {
            Windows =
            {
                new WorkspaceWindowState { Id = "main", Title = "Main", Kind = WindowKind.Main },
                new WorkspaceWindowState { Id = "spawn:Chat", Title = "Chat", Kind = WindowKind.Spawn },
                new WorkspaceWindowState { Id = "web", Title = "Web", Kind = WindowKind.Auxiliary },
            },
            Root = new LayoutNodeState
            {
                Type = "pane",
                Id = "p1",
                Tabs = { "main", "spawn:Chat", "web" },
                ActiveIndex = 0,
            },
            FocusedPaneId = "p1",
        };

        var ws = state.Restore();

        await Assert.That(Order(ws)).IsEqualTo("main,spawn:Chat,web");
        await Assert.That(ws.WindowsFor(Owner).All(w => w.Sequence > WorkspaceWindow.Unsequenced))
            .IsTrue()
            .Because("every window is numbered on load, or a later one would collide with an unnumbered one");
    }

    /// <summary>
    /// A half-migrated set — some windows carrying a sequence, some not — cannot produce two windows with
    /// one number: the unsequenced ones are numbered after the highest already taken.
    /// </summary>
    [Test]
    public async Task AHalfMigratedWorkspaceGivesNoTwoWindowsOneNumber()
    {
        var state = new WorkspaceState
        {
            Windows =
            {
                new WorkspaceWindowState { Id = "main", Title = "Main", Kind = WindowKind.Main, Sequence = 4 },
                new WorkspaceWindowState { Id = "spawn:Chat", Title = "Chat", Kind = WindowKind.Spawn },
            },
            Root = new LayoutNodeState
            {
                Type = "pane", Id = "p1", Tabs = { "main", "spawn:Chat" }, ActiveIndex = 0,
            },
            FocusedPaneId = "p1",
        };

        var ws = state.Restore();

        await Assert.That(ws.WindowsFor(Owner).Select(w => w.Sequence).Distinct().Count()).IsEqualTo(2);
        await Assert.That(ws.WindowsFor(Owner)[0].Id).IsEqualTo("main");
        await Assert.That(ws.WindowsFor(Owner)[1].Id).IsEqualTo("spawn:Chat");
    }

    // --- harness ------------------------------------------------------------------------------------

    /// <summary>
    /// The numbered windows' ids in order, as one string. Ordered comparison is the point of this whole
    /// suite, and TUnit's <c>IsEquivalentTo</c> compares collections as <em>sets</em> — it passes happily
    /// on <c>[a,b,c]</c> against <c>[a,c,b]</c>, which is exactly the difference being asserted. The same
    /// trap is recorded in <c>PaneNumberingTests</c>, and this suite fell into it once before it was.
    /// </summary>
    /// <summary>The character every window in these fixtures belongs to.</summary>
    private const string Owner = "W.C";

    private static string Order(Workspace workspace) =>
        string.Join(",", workspace.WindowsFor(Owner).Select(w => w.Id));
}
