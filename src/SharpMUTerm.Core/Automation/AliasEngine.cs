using System.Text.RegularExpressions;

namespace SharpMUTerm.Core.Automation;

/// <summary>The result of expanding a line of user input against the alias set.</summary>
public sealed class AliasResult
{
    private AliasResult(bool matched, Alias? alias, IReadOnlyList<string> commands, Match? match)
    {
        Matched = matched;
        Alias = alias;
        Commands = commands;
        Match = match;
    }

    /// <summary>True if an alias matched the input.</summary>
    public bool Matched { get; }

    /// <summary>The alias that matched, if any.</summary>
    public Alias? Alias { get; }

    /// <summary>The expanded commands to send (empty when the alias is script-only).</summary>
    public IReadOnlyList<string> Commands { get; }

    /// <summary>The regex match (for script callbacks), if any.</summary>
    public Match? Match { get; }

    public static readonly AliasResult NoMatch = new(false, null, Array.Empty<string>(), null);

    public static AliasResult Hit(Alias alias, IReadOnlyList<string> commands, Match match) =>
        new(true, alias, commands, match);
}

/// <summary>
/// Expands user input through an ordered set of <see cref="Alias"/>es (first match wins).
/// <para>
/// Split into configured and runtime rules for the reason <see cref="TriggerEngine"/> is: the F3 screen
/// can add an alias to a set while a session is connected, and <see cref="ReplaceConfigured"/> is how
/// that reaches the session without dropping whatever the scripting layer added.
/// </para>
/// </summary>
public sealed class AliasEngine
{
    private readonly List<Alias> _configured = new();
    private readonly List<Alias> _runtime = new();
    private readonly object _gate = new();

    public AliasEngine(IEnumerable<Alias>? aliases = null)
    {
        if (aliases is not null)
        {
            _configured.AddRange(aliases);
        }
    }

    /// <summary>
    /// Points the engine at the aliases the configuration holds now, leaving <see cref="Add"/>'s alone.
    /// See <see cref="TriggerEngine.ReplaceConfigured"/> for why this is a push and not a read-through.
    /// </summary>
    public void ReplaceConfigured(IEnumerable<Alias> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        var replacement = aliases.ToArray();
        lock (_gate)
        {
            _configured.Clear();
            _configured.AddRange(replacement);
        }
    }

    /// <summary>Configured aliases first, in the order F3 shows them, then the runtime ones.</summary>
    public IReadOnlyList<Alias> Aliases
    {
        get
        {
            lock (_gate)
            {
                var all = new List<Alias>(_configured.Count + _runtime.Count);
                all.AddRange(_configured);
                all.AddRange(_runtime);
                return all;
            }
        }
    }

    /// <summary>Adds an alias at runtime — the scripting layer's route in. A reload does not remove it.</summary>
    public void Add(Alias alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        lock (_gate)
        {
            _runtime.Add(alias);
        }
    }

    public bool Remove(Alias alias)
    {
        lock (_gate)
        {
            return _runtime.Remove(alias) || _configured.Remove(alias);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _configured.Clear();
            _runtime.Clear();
        }
    }

    /// <summary>
    /// Returns the expansion of the first enabled alias matching <paramref name="input"/>, or
    /// <see cref="AliasResult.NoMatch"/> if none match (caller should send the input verbatim).
    /// </summary>
    public AliasResult Expand(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        Alias[] snapshot;
        lock (_gate)
        {
            snapshot = new Alias[_configured.Count + _runtime.Count];
            _configured.CopyTo(snapshot, 0);
            _runtime.CopyTo(snapshot, _configured.Count);
        }

        foreach (var alias in snapshot)
        {
            if (!alias.Enabled)
            {
                continue;
            }

            Match match;
            try
            {
                match = alias.Regex.Match(input);
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological pattern timed out; skip this alias rather than hang input.
                continue;
            }

            if (!match.Success)
            {
                continue;
            }

            var commands = ExpandCommands(alias, match);
            return AliasResult.Hit(alias, commands, match);
        }

        return AliasResult.NoMatch;
    }

    private static IReadOnlyList<string> ExpandCommands(Alias alias, Match match)
    {
        if (string.IsNullOrEmpty(alias.Substitution))
        {
            return Array.Empty<string>();
        }

        var expanded = match.Result(alias.Substitution);
        return expanded
            .Split('\n')
            .Select(c => c.TrimEnd('\r'))
            .Where(c => c.Length > 0)
            .ToArray();
    }
}
