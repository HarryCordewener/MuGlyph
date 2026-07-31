using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The tab strip's <c>×</c> close button, driven through SharpConsoleUI's own hit test.
///
/// <para>This is the regression the maintainer hit in a real terminal: the close affordance used to
/// be a <c>✕</c> appended to the tab's <em>title</em>, and a title is drawn as plain text — the
/// framework's <c>TabControl.ProcessMouseEvent</c> counts those cells as part of the tab, so a click
/// on the glyph selected the tab and did nothing else. The button is now the framework's
/// <c>TabPage.IsClosable</c>, which it draws and hit-tests itself into <c>TabCloseRequested</c>.</para>
///
/// <para>The tests sweep the tab-strip row rather than recomputing where the <c>×</c> lands, so they
/// prove the affordance is <em>reachable by a click</em> instead of re-deriving the very column sum
/// that was wrong. They sweep right to left because only the pane's active tab is closable and a
/// click on any other tab activates it: left to right, the sweep would move the close button before
/// reaching it. Cells past the last tab belong to no tab, so the first thing a right-to-left sweep
/// can do is press the active tab's <c>×</c>.</para>
/// </summary>
/// <remarks>
/// Serialised for the same reason as <see cref="PaneDragEndToEndTests"/>: rendering a frame
/// redirects the process-global <c>Console.Out</c>/<c>Console.In</c>.
/// </remarks>
[NotInParallel]
public class PaneTabCloseTests
{
    private const int Width = 120;
    private const int Height = 32;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    private sealed record Harness(SharpMUTermApp App, string PaneId, int Columns);

    /// <summary>
    /// A rendered single-pane workspace holding the demo's main window plus its Chat spawn, with
    /// Chat active — so the pane has one closable tab and one that must survive.
    /// </summary>
    private static Harness Rendered()
    {
        Console.SetIn(TextReader.Null);
        var app = new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(Width, Height));

        // The "spawn" view activates the Chat window. Rendering is what arranges the layout, so the
        // tab strip only has real geometry (and therefore hit testing) after a frame.
        app.RenderSnapshot("spawn");

        var surface = app.PaneSnapshot();
        var paneId = surface.Rects.Keys.Single();
        return new Harness(app, paneId, surface.RectOf(paneId)!.Value.Width);
    }

    /// <summary>Clicks every cell of a pane row from the right edge inward.</summary>
    private static void SweepRow(Harness harness, int row)
    {
        for (var x = harness.Columns - 1; x >= 0; x--)
        {
            harness.App.SimulateTabStripClick(harness.PaneId, x, row);
        }
    }

    private static List<string> Tabs(SharpMUTermApp app) =>
        Flatten(app.CaptureSession().Root).SelectMany(p => p.Tabs).ToList();

    private static List<LayoutNodeState> Flatten(LayoutNodeState node) =>
        node.Children is { Count: > 0 } children
            ? children.SelectMany(Flatten).ToList()
            : new List<LayoutNodeState> { node };

    [Test]
    public async Task TheActiveTabsCloseButtonClosesIt()
    {
        var harness = Rendered();
        var chat = DemoScene.ChatWindowId;
        await Assert.That(Tabs(harness.App)).Contains(chat);

        SweepRow(harness, 0);

        // Before the fix nothing on this row could close anything: the ✕ was title text, so every
        // cell of the strip merely selected a tab.
        await Assert.That(Tabs(harness.App)).DoesNotContain(chat);
        await Assert.That(Tabs(harness.App)).Contains("main");
    }

    [Test]
    public async Task ClickingATabsLabelSelectsItWithoutClosing()
    {
        var harness = Rendered();
        var chat = DemoScene.ChatWindowId;

        // Column 1 is inside the first tab's label (the framework pads each tab with a space).
        harness.App.SimulateTabStripClick(harness.PaneId, 1, 0);

        await Assert.That(Tabs(harness.App)).Contains("main");
        await Assert.That(Tabs(harness.App)).Contains(chat);
    }

    [Test]
    public async Task ClicksBelowTheTabStripCloseNothing()
    {
        var harness = Rendered();
        var before = Tabs(harness.App);

        SweepRow(harness, 4);

        await Assert.That(Tabs(harness.App)).IsEquivalentTo(before);
    }

    [Test]
    public async Task TheMainWindowIsNeverClosable()
    {
        // Select the main window first, then try every cell: main is the session and carries no ×,
        // and the Chat tab is no longer active so it carries none either.
        var harness = Rendered();
        harness.App.SimulateTabStripClick(harness.PaneId, 1, 0);

        SweepRow(harness, 0);

        await Assert.That(Tabs(harness.App)).Contains("main");
        await Assert.That(Tabs(harness.App)).Contains(DemoScene.ChatWindowId);
    }
}
