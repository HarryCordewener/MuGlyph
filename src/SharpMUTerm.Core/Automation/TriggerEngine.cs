using System.Text.RegularExpressions;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Automation;

/// <summary>A script callback requested by a matched trigger, with its capture groups.</summary>
public sealed record TriggerScriptInvocation(string Callback, Match Match);

/// <summary>
/// One line's route to a spawn window: the window's resolved name, and the pattern of the rule that
/// sent it there.
/// <para>
/// The pattern rides along because the destination is no longer enough to identify the rule. A route
/// of <c>Channel $1</c> resolves to <c>Channel Public</c>, <c>Channel Newbie</c> and so on, so a
/// consumer that wanted to know "which rule feeds this pane" and looked the rule up by comparing its
/// <see cref="TriggerActions.SpawnTarget"/> to the window's name would find nothing for every dynamic
/// pane. Carrying it costs one reference and removes the lookup.
/// </para>
/// </summary>
public sealed record SpawnRoute(string Target, string Pattern);

/// <summary>The outcome of running the trigger engine over one output line.</summary>
public sealed class TriggerResult
{
    public TriggerResult(
        StyledLine line,
        bool suppress,
        IReadOnlyList<string> responses,
        IReadOnlyList<SpawnRoute> spawnTargets,
        IReadOnlyList<TriggerScriptInvocation> scriptInvocations,
        IReadOnlyList<Trigger> matched)
    {
        Line = line;
        Suppress = suppress;
        Responses = responses;
        SpawnTargets = spawnTargets;
        ScriptInvocations = scriptInvocations;
        Matched = matched;
    }

    /// <summary>The (possibly highlighted/rewritten) line to display.</summary>
    public StyledLine Line { get; }

    /// <summary>True if the line should be gagged (not displayed in the main window).</summary>
    public bool Suppress { get; }

    /// <summary>Commands to send back to the server, in order.</summary>
    public IReadOnlyList<string> Responses { get; }

    /// <summary>The spawn windows this line should be routed to, with the rule that routed it.</summary>
    public IReadOnlyList<SpawnRoute> SpawnTargets { get; }

    /// <summary>Script callbacks to invoke, with their match data.</summary>
    public IReadOnlyList<TriggerScriptInvocation> ScriptInvocations { get; }

    /// <summary>The triggers that matched, in evaluation order.</summary>
    public IReadOnlyList<Trigger> Matched { get; }
}

/// <summary>
/// Evaluates an ordered set of <see cref="Trigger"/>s against each output line and
/// accumulates their effects. UI-agnostic and fully deterministic.
/// <para>
/// The rules are held in two lists, and the split is the whole reason a trigger set can be edited
/// while a session is connected. <em>Configured</em> rules are the ones the active
/// <see cref="Configuration.TriggerSet"/>s contribute; <see cref="ReplaceConfigured"/> swaps them for
/// the ones the configuration holds now. <em>Runtime</em> rules are the ones <see cref="Add"/> put here
/// — the scripting layer's <c>trigger.add</c> — and a reload leaves them alone, because a Lua rule is
/// not something the F2 screen knows about and must not vanish because somebody ticked a checkbox.
/// </para>
/// <para>
/// It is a push, not a read-through to the configuration: <see cref="Process"/> runs on the telnet read
/// loop while the settings screens mutate their lists on the UI thread, and enumerating a
/// <see cref="List{T}"/> another thread is adding to throws. Reading a rule's own <em>fields</em>
/// per match (<see cref="Trigger.Pattern"/>, <see cref="Trigger.Enabled"/>,
/// <see cref="Trigger.Actions"/>) is safe and is how those edits already go live; reading its
/// <em>membership</em> is not the same kind of thing.
/// </para>
/// </summary>
public sealed class TriggerEngine
{
    private readonly List<Trigger> _configured = new();
    private readonly List<Trigger> _runtime = new();
    private readonly Dictionary<Trigger, int> _matches = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();
    private long _linesProcessed;

    public TriggerEngine(IEnumerable<Trigger>? triggers = null)
    {
        if (triggers is not null)
        {
            _configured.AddRange(triggers);
        }
    }

    /// <summary>
    /// Points the engine at the rules the configuration holds <em>now</em>, in place of the ones it was
    /// last given, leaving anything <see cref="Add"/> contributed untouched. Called when a live session's
    /// automation is re-resolved (<see cref="Session.WorldSession.ReloadAutomation"/>), which is what
    /// makes a trigger added or a set assigned mid-connection take effect on the next line rather than at
    /// the next reconnect.
    /// <para>
    /// Match counts survive for rules that survive: the count is <em>this rule's</em> history and losing
    /// it every time an unrelated checkbox is ticked would make "it has never matched" unreadable.
    /// </para>
    /// </summary>
    public void ReplaceConfigured(IEnumerable<Trigger> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        var replacement = triggers.ToArray();
        lock (_gate)
        {
            _configured.Clear();
            _configured.AddRange(replacement);
            PruneCounts();
        }
    }

    /// <summary>Configured rules first, in the order the screens show them, then the runtime ones.</summary>
    public IReadOnlyList<Trigger> Triggers
    {
        get
        {
            lock (_gate)
            {
                var all = new List<Trigger>(_configured.Count + _runtime.Count);
                all.AddRange(_configured);
                all.AddRange(_runtime);
                return all;
            }
        }
    }

    /// <summary>
    /// How many output lines this engine has evaluated. Reported by the client's trigger report, where it
    /// is what tells "no rule matched" apart from "no line ever arrived".
    /// </summary>
    public long LinesProcessed
    {
        get
        {
            lock (_gate)
            {
                return _linesProcessed;
            }
        }
    }

    /// <summary>
    /// How many times <paramref name="trigger"/> has matched a line here. Zero for a rule this engine has
    /// never seen. A capture that has matched nothing is the single most useful fact about a spawn window
    /// that never opened, so it is counted rather than inferred.
    /// </summary>
    public int MatchesFor(Trigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        lock (_gate)
        {
            return _matches.TryGetValue(trigger, out var count) ? count : 0;
        }
    }

    /// <summary>Adds a rule at runtime — the scripting layer's route in. A reload does not remove it.</summary>
    public void Add(Trigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        lock (_gate)
        {
            _runtime.Add(trigger);
        }
    }

    public bool Remove(Trigger trigger)
    {
        lock (_gate)
        {
            var removed = _runtime.Remove(trigger) || _configured.Remove(trigger);
            if (removed)
            {
                _matches.Remove(trigger);
            }

            return removed;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _configured.Clear();
            _runtime.Clear();
            _matches.Clear();
        }
    }

    /// <summary>Drops counts for rules that are no longer here, so the dictionary cannot grow forever.</summary>
    private void PruneCounts()
    {
        if (_matches.Count == 0)
        {
            return;
        }

        var live = new HashSet<Trigger>(_configured, ReferenceEqualityComparer.Instance);
        live.UnionWith(_runtime);
        foreach (var trigger in _matches.Keys.Where(t => !live.Contains(t)).ToArray())
        {
            _matches.Remove(trigger);
        }
    }

    /// <summary>Runs every enabled trigger against <paramref name="line"/> and returns the combined result.</summary>
    public TriggerResult Process(StyledLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        Trigger[] snapshot;
        lock (_gate)
        {
            snapshot = new Trigger[_configured.Count + _runtime.Count];
            _configured.CopyTo(snapshot, 0);
            _runtime.CopyTo(snapshot, _configured.Count);
        }

        var current = line;
        var suppress = false;
        List<string>? responses = null;
        List<SpawnRoute>? spawns = null;
        List<TriggerScriptInvocation>? scripts = null;
        List<Trigger>? matched = null;

        foreach (var trigger in snapshot)
        {
            if (!trigger.Enabled)
            {
                continue;
            }

            Match match;
            try
            {
                match = trigger.Regex.Match(current.Text);
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological pattern timed out on this line; skip it rather than block output.
                continue;
            }

            if (!match.Success)
            {
                continue;
            }

            (matched ??= new List<Trigger>()).Add(trigger);
            var actions = trigger.Actions;

            if (actions.Gag)
            {
                suppress = true;
            }

            if (actions.HighlightForeground is not null ||
                actions.HighlightBackground is not null ||
                actions.AddAttributes != TextAttributes.None)
            {
                current = ApplyHighlight(current, match, actions);
            }

            if (actions.Rewrite is not null)
            {
                var text = match.Result(actions.Rewrite);
                current = StyledLine.FromText(text, TextStyle.Default);
            }

            if (!string.IsNullOrEmpty(actions.SendResponse))
            {
                (responses ??= new List<string>()).Add(match.Result(actions.SendResponse));
            }

            if (!string.IsNullOrEmpty(actions.SpawnTarget) &&
                ResolveSpawnTarget(actions.SpawnTarget, match) is { } target)
            {
                (spawns ??= new List<SpawnRoute>()).Add(new SpawnRoute(target, trigger.Pattern));
            }

            if (!string.IsNullOrEmpty(actions.ScriptCallback))
            {
                (scripts ??= new List<TriggerScriptInvocation>()).Add(new TriggerScriptInvocation(actions.ScriptCallback, match));
            }

            if (trigger.StopProcessing)
            {
                break;
            }
        }

        // One lock for the whole line's bookkeeping: what this engine has seen, and which rules answered.
        lock (_gate)
        {
            _linesProcessed++;
            if (matched is not null)
            {
                foreach (var trigger in matched)
                {
                    _matches[trigger] = (_matches.TryGetValue(trigger, out var count) ? count : 0) + 1;
                }
            }
        }

        return new TriggerResult(
            current,
            suppress,
            (IReadOnlyList<string>?)responses ?? Array.Empty<string>(),
            (IReadOnlyList<SpawnRoute>?)spawns ?? Array.Empty<SpawnRoute>(),
            (IReadOnlyList<TriggerScriptInvocation>?)scripts ?? Array.Empty<TriggerScriptInvocation>(),
            (IReadOnlyList<Trigger>?)matched ?? Array.Empty<Trigger>());
    }

    /// <summary>
    /// The window a matched rule routes to: its <see cref="TriggerActions.SpawnTarget"/> with capture
    /// groups substituted, or null when what came out cannot be a window name.
    /// <para>
    /// Rewrite and respond have always expanded <c>$1</c>; the route did not, so one rule could only ever
    /// feed one statically-named pane. <c>^&lt;(.+?)&gt;</c> routing to <c>Channel $1</c> is the case this
    /// exists for: one rule, a pane per channel, each created on the channel's first line.
    /// </para>
    /// <para>
    /// <b>Why this is guarded and the other two are not.</b> A rewrite's output is text on a line and a
    /// response's output is a command the user's own rule chose to send. This one becomes a
    /// <em>durable named object</em> — a window id, a tab title, a row in the sidebar — built out of
    /// whatever the server put between the brackets. So a resolved name is trimmed and then refused if it
    /// is empty, carries a control character (a name holding a stray escape or newline would corrupt every
    /// surface that draws it), or runs past <see cref="MaxTargetLength"/>. Refusing means the line is not
    /// routed; it still prints to the main window unless the rule also gags it, which is the quiet failure
    /// rather than a pane named after a screenful of garbage.
    /// </para>
    /// <para>
    /// There is deliberately <b>no ceiling on how many panes a rule may create</b>. A pattern that captures
    /// more loosely than its author meant will open a pane per variant, and that is the author's to fix by
    /// tightening the pattern — a client-imposed cap would silently drop the channel you cared about.
    /// </para>
    /// </summary>
    private static string? ResolveSpawnTarget(string template, Match match)
    {
        string resolved;
        try
        {
            resolved = match.Result(template);
        }
        catch (FormatException)
        {
            // A template Regex.Result cannot parse at all. The rule is malformed rather than the line, so
            // routing it anywhere would be a guess.
            return null;
        }

        resolved = resolved.Trim();

        if (resolved.Length == 0 || resolved.Length > MaxTargetLength)
        {
            return null;
        }

        for (var i = 0; i < resolved.Length; i++)
        {
            if (char.IsControl(resolved[i]))
            {
                return null;
            }

            // A group reference that survived expansion: Regex.Result leaves "$3" as the two characters
            // "$3" when the pattern has no third group, rather than throwing, so without this a typo in
            // the template opens a pane literally named "$3" and keeps feeding it. Refusing is the honest
            // reading — the rule asked for something the pattern cannot give it.
            if (resolved[i] == '$' && i + 1 < resolved.Length && char.IsAsciiDigit(resolved[i + 1]))
            {
                return null;
            }
        }

        return resolved;
    }

    /// <summary>
    /// How long a resolved window name may be. A tab strip and the sidebar both draw it, and a capture is
    /// only bounded by what the server sent — this is the point past which a "name" is really a line of
    /// output that matched too much.
    /// </summary>
    public const int MaxTargetLength = 64;

    private static StyledLine ApplyHighlight(StyledLine line, Match match, TriggerActions actions)
    {
        var restyled = StyledText.Restyle(line, match.Index, match.Length, style =>
        {
            if (actions.HighlightForeground is not null)
            {
                style = style.WithForeground(actions.HighlightForeground.Value);
            }

            if (actions.HighlightBackground is not null)
            {
                style = style.WithBackground(actions.HighlightBackground.Value);
            }

            if (actions.AddAttributes != TextAttributes.None)
            {
                style = style.AddAttribute(actions.AddAttributes);
            }

            return style;
        });

        // Carry a left-rule colour so the UI can mark the whole line, per the design's output view.
        var rule = actions.HighlightForeground ?? actions.HighlightBackground;
        return rule is { } color ? restyled.WithRule(color) : restyled;
    }
}
