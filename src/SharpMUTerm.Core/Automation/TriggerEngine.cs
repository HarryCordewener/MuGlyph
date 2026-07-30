using System.Text.RegularExpressions;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Automation;

/// <summary>A script callback requested by a matched trigger, with its capture groups.</summary>
public sealed record TriggerScriptInvocation(string Callback, Match Match);

/// <summary>The outcome of running the trigger engine over one output line.</summary>
public sealed class TriggerResult
{
    public TriggerResult(
        StyledLine line,
        bool suppress,
        IReadOnlyList<string> responses,
        IReadOnlyList<string> spawnTargets,
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

    /// <summary>Named spawn windows this line should be routed to.</summary>
    public IReadOnlyList<string> SpawnTargets { get; }

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
        List<string>? spawns = null;
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

            if (!string.IsNullOrEmpty(actions.SpawnTarget))
            {
                (spawns ??= new List<string>()).Add(actions.SpawnTarget);
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
            (IReadOnlyList<string>?)spawns ?? Array.Empty<string>(),
            (IReadOnlyList<TriggerScriptInvocation>?)scripts ?? Array.Empty<TriggerScriptInvocation>(),
            (IReadOnlyList<Trigger>?)matched ?? Array.Empty<Trigger>());
    }

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
