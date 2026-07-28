namespace SharpMUTerm.Core.Commands;

/// <summary>A <see cref="CommandItem"/> paired with its match score for the current query.</summary>
public readonly record struct RankedCommand(CommandItem Item, int Score);

/// <summary>
/// Ranks command-surface entries against a query, per the design's rule: a prefix match on the
/// title ranks highest, a substring match beats a fuzzy subsequence, and subtitle matches rank
/// below title matches. Case-insensitive. An empty query returns every item in catalog order.
/// Pure and deterministic.
/// </summary>
public static class CommandMatcher
{
    // Tier bases keep the classes well separated; the per-tier bonus breaks ties within a class.
    private const int PrefixTier = 4000;
    private const int SubstringTier = 3000;
    private const int SubtitleTier = 2000;
    private const int FuzzyTier = 1000;

    /// <summary>Returns the matching items, best first (stable within equal scores).</summary>
    public static IReadOnlyList<RankedCommand> Rank(string query, IEnumerable<CommandItem> items)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(items);

        var q = query.Trim().ToLowerInvariant();
        var results = new List<(RankedCommand Ranked, int Index)>();
        var index = 0;
        foreach (var item in items)
        {
            var score = q.Length == 0 ? 0 : Score(q, item);
            if (score is not null)
            {
                results.Add((new RankedCommand(item, score.Value), index));
            }

            index++;
        }

        // Highest score first; ties keep catalog order (stable).
        results.Sort((a, b) =>
        {
            var byScore = b.Ranked.Score.CompareTo(a.Ranked.Score);
            return byScore != 0 ? byScore : a.Index.CompareTo(b.Index);
        });

        return results.Select(r => r.Ranked).ToArray();
    }

    private static int? Score(string q, CommandItem item)
    {
        var title = item.Title.ToLowerInvariant();
        if (title.StartsWith(q, StringComparison.Ordinal))
        {
            // Shorter titles are tighter prefix matches.
            return PrefixTier + Math.Max(0, 100 - title.Length);
        }

        var titleIndex = title.IndexOf(q, StringComparison.Ordinal);
        if (titleIndex >= 0)
        {
            return SubstringTier + Math.Max(0, 100 - titleIndex);
        }

        var subtitle = item.Subtitle?.ToLowerInvariant();
        if (subtitle is not null && subtitle.Contains(q, StringComparison.Ordinal))
        {
            return SubtitleTier;
        }

        var spread = FuzzySpread(q, title);
        return spread is null ? null : FuzzyTier + Math.Max(0, 200 - spread.Value);
    }

    /// <summary>
    /// If every char of <paramref name="q"/> appears in <paramref name="title"/> in order, returns
    /// the span from the first to the last matched character (a tighter run scores higher); else null.
    /// </summary>
    private static int? FuzzySpread(string q, string title)
    {
        if (q.Length == 0)
        {
            return 0;
        }

        int? best = null;
        for (var start = 0; start < title.Length; start++)
        {
            if (title[start] != q[0])
            {
                continue;
            }

            var titleIndex = start + 1;
            var queryIndex = 1;
            while (queryIndex < q.Length && titleIndex < title.Length)
            {
                if (title[titleIndex++] == q[queryIndex])
                {
                    queryIndex++;
                }
            }

            if (queryIndex == q.Length)
            {
                var spread = titleIndex - start - 1;
                best = best is null ? spread : Math.Min(best.Value, spread);
            }
        }

        return best;
    }
}
