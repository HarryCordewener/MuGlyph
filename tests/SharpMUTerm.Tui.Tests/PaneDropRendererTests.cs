using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

public class PaneDropRendererTests
{
    private static List<string> Hovered(Edge? edge, int width = 40, int height = 20) =>
        PaneDropRenderer.Render("pane 2", "split pane 2 left", width, height, hovered: true, edge);

    [Test]
    public async Task ItRendersOneRowPerCellRow()
    {
        await Assert.That(Hovered(Edge.Left).Count).IsEqualTo(20);
        await Assert.That(PaneDropRenderer.Render("main", "x", 40, 7, hovered: false, edge: null).Count).IsEqualTo(7);
    }

    [Test]
    public async Task EveryRowIsExactlyThePaneWidth()
    {
        foreach (var line in Hovered(Edge.Right))
        {
            await Assert.That(MarkupText.VisibleLength(line)).IsEqualTo(40);
        }
    }

    [Test]
    public async Task ADegenerateRectStillRendersSomething()
    {
        var lines = PaneDropRenderer.Render("main", "x", 0, 0, hovered: true, edge: Edge.Left);

        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(MarkupText.VisibleLength(lines[0])).IsEqualTo(1);
    }

    [Test]
    public async Task AHoveredPaneShowsTheDropLabel_AnIdleOneShowsItsName()
    {
        var hovered = string.Concat(Hovered(Edge.Left));
        var idle = string.Concat(PaneDropRenderer.Render("main", "split main left", 40, 20, hovered: false, edge: null));

        await Assert.That(hovered).Contains("split pane 2 left");
        await Assert.That(idle).Contains("main");
        await Assert.That(idle).DoesNotContain("split main left");
    }

    [Test]
    public async Task OnlyAHoveredPaneIsHighlighted()
    {
        var idle = string.Concat(PaneDropRenderer.Render("main", "x", 40, 20, hovered: false, edge: Edge.Left));

        await Assert.That(idle).DoesNotContain(PaneDropRenderer.ZoneColor);
        await Assert.That(string.Concat(Hovered(Edge.Left))).Contains(PaneDropRenderer.ZoneColor);
    }

    [Test]
    public async Task ALongLabelIsTruncatedRatherThanOverflowing()
    {
        var lines = PaneDropRenderer.Render("p", new string('x', 200), 12, 5, hovered: true, edge: null);

        foreach (var line in lines)
        {
            await Assert.That(MarkupText.VisibleLength(line)).IsEqualTo(12);
        }

        await Assert.That(string.Concat(lines)).Contains("…");
    }

    [Test]
    public async Task LiteralBracketsInALabelAreEscaped()
    {
        var lines = PaneDropRenderer.Render("p", "tab in [Chat]", 40, 5, hovered: true, edge: null);

        // Unescaped, "[Chat]" would be swallowed as a markup tag.
        await Assert.That(string.Concat(lines)).Contains("[[Chat]]");
    }

    // --- the zone geometry ---------------------------------------------------

    [Test]
    public async Task TheBandMatchesTheDropZoneFraction()
    {
        // DropZones splits when a drop lands within 25% of an edge; the band must show that same 25%.
        await Assert.That(PaneDropRenderer.Band(40, DropZones.DefaultEdgeFraction)).IsEqualTo(10);
        await Assert.That(PaneDropRenderer.Band(46, DropZones.DefaultEdgeFraction)).IsEqualTo(12);
    }

    [Test]
    public async Task TheBandIsNeverThinnerThanOneCellNorWiderThanThePane()
    {
        await Assert.That(PaneDropRenderer.Band(1, DropZones.DefaultEdgeFraction)).IsEqualTo(1);
        await Assert.That(PaneDropRenderer.Band(2, DropZones.DefaultEdgeFraction)).IsEqualTo(1);
        await Assert.That(PaneDropRenderer.Band(3, 1.0)).IsEqualTo(3);
    }

    [Test]
    [Arguments(Edge.Left, 0, 0, 10)]
    [Arguments(Edge.Right, 0, 30, 40)]
    [Arguments(Edge.Top, 1, 0, 5)]
    [Arguments(Edge.Bottom, 1, 15, 20)]
    public async Task TheBandCoversExactlyItsOwnEdgeStrip(Edge edge, int axis, int from, int to)
    {
        const int width = 40;
        const int height = 20;

        // axis 0 = the band is a column range, axis 1 = a row range. Walking the whole rectangle
        // pins down both which cells are claimed and, just as importantly, which are not.
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var along = axis == 0 ? column : row;
                var expected = along >= from && along < to;
                await Assert.That(PaneDropRenderer.InZone(column, row, width, height, edge))
                    .IsEqualTo(expected);
            }
        }
    }

    [Test]
    [Arguments(Edge.Left)]
    [Arguments(Edge.Right)]
    [Arguments(Edge.Top)]
    [Arguments(Edge.Bottom)]
    public async Task EveryCellThatResolvesToAnEdgeIsInsideThatEdgesBand(Edge edge)
    {
        const int width = 40;
        const int height = 20;

        // The guarantee the preview owes the user: wherever the pointer is resolving to this edge,
        // this edge's band is lit under it. The converse does not hold — the band is symmetric while
        // DropZones measures from cell corners, so its right/bottom regions are a cell narrower, and
        // near a corner a cell in the left band can resolve to Top (whichever edge is nearest wins).
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                if (DropZones.Resolve(0, 0, width, height, column, row) != edge)
                {
                    continue;
                }

                await Assert.That(PaneDropRenderer.InZone(column, row, width, height, edge)).IsTrue();
            }
        }
    }

    [Test]
    public async Task ATabDropOutlinesThePaneInsteadOfBandingAnEdge()
    {
        await Assert.That(PaneDropRenderer.InZone(0, 5, 40, 20, edge: null)).IsTrue();   // left border
        await Assert.That(PaneDropRenderer.InZone(39, 5, 40, 20, edge: null)).IsTrue();  // right border
        await Assert.That(PaneDropRenderer.InZone(20, 0, 40, 20, edge: null)).IsTrue();  // top border
        await Assert.That(PaneDropRenderer.InZone(20, 19, 40, 20, edge: null)).IsTrue(); // bottom border
        await Assert.That(PaneDropRenderer.InZone(20, 10, 40, 20, edge: null)).IsFalse();
    }
}
