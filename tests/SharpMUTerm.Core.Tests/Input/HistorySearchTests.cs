using SharpMUTerm.Core.Input;

namespace SharpMUTerm.Core.Tests.Input;

/// <summary>
/// The ⌃R surface's filtering: newest first, case-insensitive substring, and no scoring. See
/// <see cref="HistorySearch"/> for why this is deliberately not <c>CommandMatcher</c>.
/// </summary>
public class HistorySearchTests
{
    private static readonly string[] Entries =
    {
        "look",                     // oldest
        "say hello",
        "north",
        "say the northern watch sent word",
        "score",                    // newest
    };

    /// <summary>No query is the surface's opening state: everything, in reverse chronological order.</summary>
    [Test]
    public async Task AnEmptyQueryListsEverythingNewestFirst()
    {
        var matches = HistorySearch.Match(Entries, string.Empty);

        await Assert.That(matches.Select(m => m.Text))
            .IsEquivalentTo(new[]
            {
                "score", "say the northern watch sent word", "north", "say hello", "look",
            });
    }

    /// <summary>The index travels with the row, because that is what the host recalls by.</summary>
    [Test]
    public async Task EachRowCarriesItsIndexInTheStore()
    {
        var matches = HistorySearch.Match(Entries, string.Empty);

        await Assert.That(matches[0].Index).IsEqualTo(4); // "score" — the newest entry
        await Assert.That(matches[^1].Index).IsEqualTo(0); // "look" — the oldest
        await Assert.That(Entries[matches[2].Index]).IsEqualTo(matches[2].Text);
    }

    /// <summary>A query keeps the substring matches, still newest first — no re-ranking.</summary>
    [Test]
    public async Task AQueryFiltersAndKeepsChronologicalOrder()
    {
        var matches = HistorySearch.Match(Entries, "north");

        await Assert.That(matches.Select(m => m.Text))
            .IsEquivalentTo(new[] { "say the northern watch sent word", "north" });
    }

    /// <summary>
    /// A short query matches nothing it does not literally appear in. This is the fuzzy-matching
    /// difference: <c>sw</c> subsequence-matches "say the northern watch sent word" and
    /// <c>CommandMatcher</c> would list it, which on a history of poses would fill the surface with rows
    /// the query is not in.
    /// </summary>
    [Test]
    public async Task ThereIsNoFuzzyMatching()
    {
        await Assert.That(HistorySearch.Match(Entries, "sw")).IsEmpty();
    }

    [Test]
    public async Task MatchingIsCaseInsensitive()
    {
        await Assert.That(HistorySearch.Match(Entries, "NORTH").Count).IsEqualTo(2);
        await Assert.That(HistorySearch.Match(new[] { "Look" }, "look").Count).IsEqualTo(1);
    }

    /// <summary>Where the query matched, so the surface can mark it.</summary>
    [Test]
    public async Task TheMatchedSpanIsReported()
    {
        var match = HistorySearch.Match(new[] { "say the northern watch" }, "north").Single();

        await Assert.That(match.MatchStart).IsEqualTo(8);
        await Assert.That(match.MatchLength).IsEqualTo(5);
    }

    /// <summary>No query means no span, rather than a span of nothing at position zero.</summary>
    [Test]
    public async Task AnUnfilteredRowReportsNoSpan()
    {
        var match = HistorySearch.Match(new[] { "look" }, string.Empty).Single();

        await Assert.That(match.MatchStart).IsEqualTo(-1);
        await Assert.That(match.MatchLength).IsEqualTo(0);
    }

    [Test]
    public async Task AQueryThatMatchesNothingListsNothing()
    {
        await Assert.That(HistorySearch.Match(Entries, "zzz")).IsEmpty();
    }

    /// <summary>
    /// The query is not trimmed: a trailing space is a real narrowing on a MU* command line, and a query of
    /// only spaces legitimately finds multi-word lines.
    /// </summary>
    [Test]
    public async Task TheQueryIsNotTrimmed()
    {
        await Assert.That(HistorySearch.Match(Entries, "say ").Count).IsEqualTo(2);
        await Assert.That(HistorySearch.Match(new[] { "look", "say hi" }, " ").Single().Text)
            .IsEqualTo("say hi");
    }

    /// <summary>
    /// A line entered again later appears twice. <see cref="InputHistory"/> already drops consecutive
    /// repeats, which is the case that produces runs of identical rows; a chronological list that quietly
    /// omitted the rest would not be chronological.
    /// </summary>
    [Test]
    public async Task ARepeatedLineIsListedEachTimeItWasEntered()
    {
        var history = new InputHistory();
        history.Add("look");
        history.Add("north");
        history.Add("look");

        var matches = HistorySearch.Match(history.Entries, "look");

        await Assert.That(matches.Count).IsEqualTo(2);
        await Assert.That(matches[0].Index).IsEqualTo(2);
        await Assert.That(matches[1].Index).IsEqualTo(0);
    }

    [Test]
    public async Task AnEmptyStoreListsNothing()
    {
        await Assert.That(HistorySearch.Match(Array.Empty<string>(), string.Empty)).IsEmpty();
    }
}
