using MuClient.Core.Commands;

namespace MuClient.Core.Tests.Commands;

public class CommandMatcherTests
{
    private static readonly CommandItem[] Items =
    {
        new(CommandGroup.Layout, "Split right", "layout:split-right"),
        new(CommandGroup.Layout, "Split down", "layout:split-down"),
        new(CommandGroup.Terminal, "Start logging", "term:log-on"),
        new(CommandGroup.GoTo, "Go to #public", "win:2", "Corvid · 3 unread"),
    };

    [Test]
    public async Task EmptyQuery_ReturnsAllInCatalogOrder()
    {
        var ranked = CommandMatcher.Rank("", Items);
        await Assert.That(ranked.Select(r => r.Item.Id))
            .IsEquivalentTo(new[] { "layout:split-right", "layout:split-down", "term:log-on", "win:2" });
    }

    [Test]
    public async Task PrefixMatch_RanksAboveSubstring()
    {
        // "sp" prefixes "Split …" but is only a substring of nothing else here.
        var ranked = CommandMatcher.Rank("sp", Items);
        await Assert.That(ranked).Count().IsEqualTo(2);
        await Assert.That(ranked[0].Item.Title).StartsWith("Split");
    }

    [Test]
    public async Task Substring_BeatsFuzzySubsequence()
    {
        // "log" is a substring of "Start logging"; "lgg" is only a fuzzy subsequence of it.
        var sub = CommandMatcher.Rank("log", Items);
        await Assert.That(sub[0].Item.Id).IsEqualTo("term:log-on");

        var fuzzy = CommandMatcher.Rank("lgg", Items);
        await Assert.That(fuzzy.Any(r => r.Item.Id == "term:log-on")).IsTrue();
        await Assert.That(sub[0].Score).IsGreaterThan(fuzzy.First(r => r.Item.Id == "term:log-on").Score);
    }

    [Test]
    public async Task Matches_SubtitleWhenTitleDoesNot()
    {
        var ranked = CommandMatcher.Rank("corvid", Items);
        await Assert.That(ranked).HasSingleItem();
        await Assert.That(ranked[0].Item.Id).IsEqualTo("win:2");
    }

    [Test]
    public async Task NonMatching_QueryIsExcluded()
    {
        await Assert.That(CommandMatcher.Rank("zzzzz", Items)).IsEmpty();
    }

    [Test]
    public async Task Ranking_IsCaseInsensitive()
    {
        var ranked = CommandMatcher.Rank("SPLIT", Items);
        await Assert.That(ranked).Count().IsEqualTo(2);
    }
}
