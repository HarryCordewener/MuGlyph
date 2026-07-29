namespace SharpMUTerm.Core.Input;

/// <summary>
/// One history entry as the search surface shows it: the text, where it sits in
/// <see cref="InputHistory.Entries"/>, and where the query matched inside it.
/// </summary>
/// <param name="Text">The entry, verbatim.</param>
/// <param name="Index">
/// Its index in <see cref="InputHistory.Entries"/> (oldest first) — what the host hands back to
/// <see cref="InputHistory.RecallAt"/>, so the surface never has to match on text.
/// </param>
/// <param name="MatchStart">Where the query matched, or -1 when there was no query.</param>
/// <param name="MatchLength">How long the matched run is; zero when there was no query.</param>
public readonly record struct HistoryMatch(string Text, int Index, int MatchStart, int MatchLength);

/// <summary>
/// Filters command history for the history surface: newest first, case-insensitive substring, and
/// nothing else.
/// <para>
/// <b>Why not <c>CommandMatcher</c>.</b> The command surface ranks a fixed catalog of titles nobody has
/// typed before, so fuzzy subsequence matching earns its keep there — <c>szp</c> finding
/// <c>Show timestamps</c> is a feature. History is the opposite problem. Every row is something this user
/// typed, they are looking for a specific one they remember, and recency is the strongest signal there is
/// about which. Fuzzy matching on that list is actively harmful: a short query subsequence-matches almost
/// every long pose, so the fuzzy tier would fill the surface with lines the query does not appear in, and
/// a tier-based reordering would break the one ordering the user can reason about. So there is no scoring
/// here at all — a query either appears in a line or it does not, and what survives keeps its
/// chronological order. This is what <c>⌃R</c> means in bash, zsh and fish, which is the whole reason the
/// surface is on that chord.
/// </para>
/// <para>
/// <b>The query is not trimmed.</b> A trailing space is a real narrowing on a MU* command line
/// (<c>say </c> is not <c>say</c>), and a query of only spaces legitimately finds multi-word lines.
/// </para>
/// <para>
/// <b>Duplicates are not collapsed.</b> <see cref="InputHistory"/> already drops consecutive repeats,
/// which is the case that produces runs of identical rows; a line typed again later is a thing that
/// happened, and a chronological list that quietly omitted it would not be one.
/// </para>
/// Pure and fully unit-testable, like everything else in this namespace.
/// </summary>
public static class HistorySearch
{
    /// <summary>
    /// The entries matching <paramref name="query"/>, newest first. An empty query matches everything,
    /// which is the surface's opening state: the plain chronological list.
    /// </summary>
    public static IReadOnlyList<HistoryMatch> Match(IReadOnlyList<string> entries, string query)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);

        var matches = new List<HistoryMatch>(entries.Count);

        // Newest first: the store keeps entries oldest-first, and the surface reads top-down.
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var text = entries[index];
            if (query.Length == 0)
            {
                matches.Add(new HistoryMatch(text, index, -1, 0));
                continue;
            }

            var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                matches.Add(new HistoryMatch(text, index, at, query.Length));
            }
        }

        return matches;
    }
}
