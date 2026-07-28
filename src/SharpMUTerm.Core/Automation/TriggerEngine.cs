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
/// </summary>
public sealed class TriggerEngine
{
    private readonly List<Trigger> _triggers = new();
    private readonly object _gate = new();

    public TriggerEngine(IEnumerable<Trigger>? triggers = null)
    {
        if (triggers is not null)
        {
            _triggers.AddRange(triggers);
        }
    }

    public IReadOnlyList<Trigger> Triggers
    {
        get
        {
            lock (_gate)
            {
                return _triggers.ToArray();
            }
        }
    }

    public void Add(Trigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        lock (_gate)
        {
            _triggers.Add(trigger);
        }
    }

    public bool Remove(Trigger trigger)
    {
        lock (_gate)
        {
            return _triggers.Remove(trigger);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _triggers.Clear();
        }
    }

    /// <summary>Runs every enabled trigger against <paramref name="line"/> and returns the combined result.</summary>
    public TriggerResult Process(StyledLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        Trigger[] snapshot;
        lock (_gate)
        {
            snapshot = _triggers.ToArray();
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
