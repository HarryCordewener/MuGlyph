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
    internal static string Unique(IEnumerable<string> taken, string name) => Available(taken, name + " copy");

    /// <summary>
    /// <paramref name="name"/> itself when nothing in <paramref name="taken"/> holds it, and otherwise
    /// the same name with the first free number after it — <c>New Set</c>, then <c>New Set 2</c>.
    /// Case-insensitive, because two names differing only in case read as one name in a list (and, for a
    /// trigger set, <em>are</em> one name to the resolver).
    /// </summary>
    internal static string Available(IEnumerable<string> taken, string name)
    {
        ArgumentNullException.ThrowIfNull(taken);

        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (var n = 2; used.Contains(candidate); n++)
        {
            candidate = $"{name} {n.ToString(CultureInfo.InvariantCulture)}";
        }

        return candidate;
    }

    /// <summary>What the owning-set field is labelled on all four flattened screens.</summary>
    internal const string OwnerLabel = "set";

    /// <summary>
    /// Which set owns an item, as an editable value — the field that makes a rule, an alias, a timer or
    /// a binding <em>movable</em> between sets, which nothing on these screens could do.
    /// <para>
    /// It is a closed list over the configured set names, deliberately: unlike a spawn window (which
    /// comes into existence by being routed to), a set is a real object with rules, timers and character
    /// assignments hanging off it, so a name typed here could only ever be a set that does not exist.
    /// Sets are made and unmade on F5, where they are listed as things in their own right — including
    /// the empty ones, which a flattened pane cannot show.
    /// </para>
    /// <para>
    /// Writing it moves the item: out of its current set's list, onto the end of the target's. It is a
    /// move and not a deletion — nothing is destroyed, so it is a committed edit like any other and is
    /// kept on close. Because the pane is flattened across every set the row moves too, so the field
    /// carries a <see cref="ScreenField.Follow"/> that takes the cursor with it.
    /// </para>
    /// </summary>
    internal static ScreenField Owner<T>(IReadOnlyList<TriggerSet> sets, Func<TriggerSet, List<T>> items, T item)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(item);

        var names = sets.Select(s => s.Name).ToArray();

        return new ScreenField(
            OwnerLabel,
            () => OwnerOf(sets, items, item)?.Name ?? string.Empty,
            value => Named(sets, value) is null
                ? $"{OwnerLabel} must be one of: {string.Join(", ", names)}"
                : null,
            value => Move(sets, items, item, value),
            names,
            ClosedChoices: true,
            Follow: () => Flattened(sets, items, item));
    }

    /// <summary>The set whose list holds an item, or null when none of them does.</summary>
    internal static TriggerSet? OwnerOf<T>(
        IReadOnlyList<TriggerSet> sets, Func<TriggerSet, List<T>> items, T item)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(items);

        foreach (var set in sets)
        {
            if (IndexOf(items(set), item) >= 0)
            {
                return set;
            }
        }

        return null;
    }

    /// <summary>The set called <paramref name="name"/>, matched as the resolver matches, or null.</summary>
    private static TriggerSet? Named(IReadOnlyList<TriggerSet> sets, string name)
    {
        var trimmed = name.Trim();
        foreach (var set in sets)
        {
            if (string.Equals(set.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return set;
            }
        }

        return null;
    }

    /// <summary>
    /// Moves an item into the named set's list. Moving it into the set it is already in is a no-op
    /// rather than a remove-and-append: the item would otherwise silently jump to the bottom of its own
    /// set every time the field was committed on the value it opened with.
    /// </summary>
    private static void Move<T>(
        IReadOnlyList<TriggerSet> sets, Func<TriggerSet, List<T>> items, T item, string name)
        where T : class
    {
        if (Named(sets, name) is not { } target || OwnerOf(sets, items, item) is not { } owner)
        {
            return;
        }

        if (ReferenceEquals(owner, target))
        {
            return;
        }

        var from = items(owner);
        from.RemoveAt(IndexOf(from, item));
        items(target).Add(item);
    }

    /// <summary>
    /// The row an item occupies in the flattened pane: every earlier set's items, then its own position.
    /// -1 when no set holds it.
    /// </summary>
    internal static int Flattened<T>(IReadOnlyList<TriggerSet> sets, Func<TriggerSet, List<T>> items, T item)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(items);

        var offset = 0;
        foreach (var set in sets)
        {
            var list = items(set);
            var index = IndexOf(list, item);
            if (index >= 0)
            {
                return offset + index;
            }

            offset += list.Count;
        }

        return -1;
    }

    /// <summary>
    /// Where an item sits in a list, by identity. <see cref="List{T}.IndexOf(T)"/> would go through
    /// the type's equality, and two rules that happen to hold the same values are still two rules.
    /// </summary>
    private static int IndexOf<T>(List<T> list, T item) where T : class
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
