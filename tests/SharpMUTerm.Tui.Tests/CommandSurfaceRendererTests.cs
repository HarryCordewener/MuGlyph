using SharpMUTerm.Core.Commands;
using SharpMUTerm.Tui;
using TUnit.Assertions.Enums;

namespace SharpMUTerm.Tui.Tests;

public class CommandSurfaceRendererTests
{
    private static readonly CommandItem[] Items =
    {
        new(CommandGroup.Layout, "Zoom pane", "layout:zoom"),
        new(CommandGroup.GoTo, "Switch to Rookery", "char:k2", "Aetherfall · offline"),
        new(CommandGroup.GoTo, "Go to #public", "win:2", "Corvid · 3 unread"),
        new(CommandGroup.Terminal, "Start logging", "term:log-on"),
    };

    [Test]
    public async Task Order_GroupsInFixedOrder_RankedWithin()
    {
        var ranked = CommandMatcher.Rank("", Items);
        var ordered = CommandSurfaceRenderer.Order(ranked);

        // GoTo entries come first (both), then Terminal, then Layout.
        await Assert.That(ordered.Select(i => i.Id))
            .IsEquivalentTo(new[] { "char:k2", "win:2", "term:log-on", "layout:zoom" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Render_EmitsGroupHeadersAndHighlightsSelected()
    {
        var ranked = CommandMatcher.Rank("", Items);
        var lines = CommandSurfaceRenderer.Render(ranked, selected: 0, total: Items.Length, context: "Corvid");

        await Assert.That(lines[0]).Contains("acting on Corvid");
        await Assert.That(lines.Any(l => l.Contains("├ GO TO"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("├ LAYOUT"))).IsTrue();
        // The selected (first flattened) row is highlighted with the accent background.
        await Assert.That(lines.Any(l => l.Contains("on #00f5b7") && l.Contains("Switch to Rookery"))).IsTrue();
    }

    [Test]
    public async Task Render_NoMatches_SaysSo()
    {
        var ranked = CommandMatcher.Rank("zzzz", Items);
        var lines = CommandSurfaceRenderer.Render(ranked, -1, Items.Length, null);
        await Assert.That(lines.Any(l => l.Contains("no matches"))).IsTrue();
    }
}
