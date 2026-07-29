using System.Globalization;
using SharpMUTerm.Core.Configuration;

namespace SharpMUTerm.Tui;

/// <summary>
/// The arithmetic four settings screens share because their list panes are *flattened*: F2, F3, F4 and
/// F6 each draw one column of every <see cref="TriggerSet"/>'s rules, aliases, bindings or timers, but
/// the thing itself lives in one particular set's list. A button therefore cannot simply act on "the
/// pane" — it has to find the owning list, the item's index inside it, and how many rows of the pane
/// precede that list, because the cursor is asked for a row of the pane and not for an index into
/// whichever list happens to hold the new item.
/// <para>
/// F5 needs none of this: a world is a row of one list, which is why <c>offset</c> defaults to zero on
/// <see cref="ScreenButton.Add"/>.
/// </para>
/// </summary>
internal static class ScreenLists
{
    /// <summary>
    /// Where a flattened row lives: the set-owned list holding it, its index within that list, and how
    /// many flattened rows precede the list. Null when the index addresses no row at all.
    /// </summary>
    internal static (List<T> Items, int Index, int Offset)? Locate<T>(
        IReadOnlyList<TriggerSet> sets, Func<TriggerSet, List<T>> items, int flattened)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(items);

        if (flattened < 0)
        {
            return null;
        }

        var offset = 0;
        foreach (var set in sets)
        {
            var list = items(set);
            if (flattened < offset + list.Count)
            {
                return (list, flattened - offset, offset);
            }

            offset += list.Count;
        }

        return null;
    }

    /// <summary>
    /// The list a new row goes into: the one holding the selection, so a rule is added beside the rule
    /// being looked at rather than wherever the configuration happens to end. With nothing selected it
    /// is the first set's list — a screen with no rows at all still has to be able to grow one. Null
    /// only when there is no set to put anything in, which is when the add button isn't drawn.
    /// </summary>
    internal static (List<T> Items, int Offset)? Target<T>(
        IReadOnlyList<TriggerSet> sets, Func<TriggerSet, List<T>> items, int selected)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(items);

        if (Locate(sets, items, selected) is { } found)
        {
            return (found.Items, found.Offset);
        }

        return sets.Count > 0 ? (items(sets[0]), 0) : null;
    }

    /// <summary>
    /// A name none of <paramref name="taken"/> already holds: <c>Tell copy</c>, then
    /// <c>Tell copy 2</c>. Matching is case-insensitive, because two names differing only in case read
    /// as the same name in a list.
    /// <para>
    /// Names are not required to be unique anywhere (see <see cref="ScreenField.Name"/>) — this exists
    /// so a fresh copy is *findable*, not because a collision would break anything. A duplicate landing
    /// as a second identical row is the one case where the user cannot tell which one they just made.
    /// </para>
    /// </summary>
    internal static string Unique(IEnumerable<string> taken, string name)
    {
        ArgumentNullException.ThrowIfNull(taken);

        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var candidate = name + " copy";
        for (var n = 2; used.Contains(candidate); n++)
        {
            candidate = $"{name} copy {n.ToString(CultureInfo.InvariantCulture)}";
        }

        return candidate;
    }
}
