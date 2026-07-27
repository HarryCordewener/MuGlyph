using System.Text.RegularExpressions;

namespace MuClient.Core.Automation;

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

/// <summary>Expands user input through an ordered set of <see cref="Alias"/>es (first match wins).</summary>
public sealed class AliasEngine
{
    private readonly List<Alias> _aliases = new();
    private readonly object _gate = new();

    public AliasEngine(IEnumerable<Alias>? aliases = null)
    {
        if (aliases is not null)
        {
            _aliases.AddRange(aliases);
        }
    }

    public IReadOnlyList<Alias> Aliases
    {
        get
        {
            lock (_gate)
            {
                return _aliases.ToArray();
            }
        }
    }

    public void Add(Alias alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        lock (_gate)
        {
            _aliases.Add(alias);
        }
    }

    public bool Remove(Alias alias)
    {
        lock (_gate)
        {
            return _aliases.Remove(alias);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _aliases.Clear();
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
            snapshot = _aliases.ToArray();
        }

        foreach (var alias in snapshot)
        {
            if (!alias.Enabled)
            {
                continue;
            }

            var match = alias.Regex.Match(input);
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
