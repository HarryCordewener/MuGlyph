using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;

namespace SharpMUTerm.Tui;

/// <summary>
/// Says what a session's capture rules <em>are</em> and whether they have ever fired — the readable
/// answer to "why did no window open?".
/// <para>
/// It exists because that question had no answer from inside the client. A trigger that matches nothing
/// looks exactly like a trigger that was never loaded, a trigger set assigned to a character but holding
/// no rules looks exactly like one holding rules that do not match, and a set name a character references
/// after the set was renamed away resolves to nothing in silence
/// (<see cref="AppConfiguration.ResolveTriggerSets"/> skips what it cannot find). All three states are now
/// printable: <c>/triggers</c> renders <see cref="Describe"/>, and every connect prints
/// <see cref="Summary"/>.
/// </para>
/// <para>
/// The engine is the authority on what is <em>live</em>, not the configuration: that is the whole point,
/// since the bug being reported against was a live session running rules the configuration no longer
/// described. The configuration is read only to name the sets and to spot references that resolve to
/// nothing. Pure, so the wording is testable without a terminal.
/// </para>
/// </summary>
internal static class TriggerReport
{
    /// <summary>
    /// One line for the connect banner: which sets this character's automation came from and how much of
    /// it there is. A character with none is told so, with the two keys that change that — the state the
    /// maintainer spent an evening in without the client ever mentioning it.
    /// </summary>
    internal static string Summary(
        string who, IReadOnlyList<string> assigned, IReadOnlyList<TriggerSet> resolved)
    {
        ArgumentNullException.ThrowIfNull(assigned);
        ArgumentNullException.ThrowIfNull(resolved);

        var orphans = Orphans(assigned, resolved);
        if (resolved.Count == 0)
        {
            var none = orphans.Count > 0
                ? $"no trigger set called {Join(orphans)} exists — F2 defines sets, F5 assigns them"
                : $"no trigger set is assigned to {who} — F5 assigns one";
            return $"Automation: {none}.";
        }

        var counts = Counts(resolved);
        var tail = orphans.Count > 0
            ? $"; {Join(orphans)} assigned but missing (F2 defines sets)"
            : string.Empty;
        return $"Automation: {Join(resolved.Select(s => s.Name).ToList())} — {counts}{tail}.";
    }

    /// <summary>
    /// The full account, one plain sentence per line, for <c>/triggers</c>: how many lines the engine has
    /// evaluated, every live rule under the set it came from with its route and its match count, any
    /// assigned set that does not exist, and one line saying what matching actually does.
    /// </summary>
    internal static IReadOnlyList<string> Describe(
        string who,
        IReadOnlyList<string> assigned,
        IReadOnlyList<TriggerSet> resolved,
        TriggerEngine engine)
    {
        ArgumentNullException.ThrowIfNull(assigned);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(engine);

        var live = engine.Triggers;
        var lines = new List<string>
        {
            $"Triggers for {who}: {Count(live.Count, "rule")} live, "
            + $"{Count(engine.LinesProcessed, "line")} seen.",
        };

        var attributed = new HashSet<Trigger>(ReferenceEqualityComparer.Instance);
        foreach (var set in resolved)
        {
            var mine = live.Where(t => set.Triggers.Contains(t)).ToList();
            attributed.UnionWith(mine);
            lines.Add(mine.Count == 0
                ? $"  set {set.Name} — no capture rules (F2 adds one)"
                : $"  set {set.Name} — {Count(mine.Count, "rule")}");
            lines.AddRange(mine.Select(t => Rule(t, engine)));
        }

        // Rules the resolved sets do not account for: the scripting layer's own, and anything left over
        // from a set that has since been unassigned. Naming them beats leaving a count that does not add up.
        var extra = live.Where(t => !attributed.Contains(t)).ToList();
        if (extra.Count > 0)
        {
            lines.Add($"  added at runtime — {Count(extra.Count, "rule")}");
            lines.AddRange(extra.Select(t => Rule(t, engine)));
        }

        foreach (var orphan in Orphans(assigned, resolved))
        {
            lines.Add($"  set {orphan} — assigned to {who} but no such set exists (F2 defines sets)");
        }

        lines.Add(live.Count == 0
            ? "Nothing can match: F5 assigns a trigger set to this character, F2 adds rules to a set."
            : "Each rule's regex is matched against a line's plain text — the ANSI is already stripped — "
              + "top to bottom; a rule with a route sends its line to that spawn window, which is created "
              + "the first time it matches.");

        return lines;
    }

    /// <summary>
    /// Set names a character opted into that nothing answers to — a set renamed or deleted out from under
    /// the reference. <see cref="AppConfiguration.ResolveTriggerSets"/> skips them silently, which is the
    /// one way automation can be configured, look configured, and do nothing.
    /// </summary>
    internal static IReadOnlyList<string> Orphans(
        IReadOnlyList<string> assigned, IReadOnlyList<TriggerSet> resolved)
    {
        ArgumentNullException.ThrowIfNull(assigned);
        ArgumentNullException.ThrowIfNull(resolved);

        var known = new HashSet<string>(resolved.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        return assigned
            .Where(name => !known.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>One rule: whether it is on, its pattern, where it routes, and how often it has fired.</summary>
    private static string Rule(Trigger trigger, TriggerEngine engine)
    {
        var mark = trigger.Enabled ? "✓" : "✗";
        var name = string.IsNullOrWhiteSpace(trigger.Name) ? "(unnamed)" : trigger.Name;
        var route = string.IsNullOrEmpty(trigger.Actions.SpawnTarget)
            ? "main window"
            : $"window '{trigger.Actions.SpawnTarget}'";
        var matches = engine.MatchesFor(trigger);
        var state = trigger.Enabled ? string.Empty : " (disabled)";
        return $"    {mark} {name}{state}  /{trigger.Pattern}/  → {route}  · "
            + $"{Count(matches, "match", "matches")}";
    }

    /// <summary>The rule tally across a character's sets, in the order the F-screens own them.</summary>
    private static string Counts(IReadOnlyList<TriggerSet> sets) =>
        $"{Count(sets.Sum(s => s.Triggers.Count), "trigger")}, "
        + $"{Count(sets.Sum(s => s.Aliases.Count), "alias", "aliases")}, "
        + $"{Count(sets.Sum(s => s.Macros.Count), "macro")}, "
        + $"{Count(sets.Sum(s => s.Timers.Count), "timer")}";

    private static string Count(long n, string singular, string? plural = null) =>
        n == 1 ? $"1 {singular}" : $"{n} {plural ?? singular + "s"}";

    private static string Join(IReadOnlyList<string> names) => string.Join(", ", names);
}
