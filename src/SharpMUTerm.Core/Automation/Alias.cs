using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SharpMUTerm.Core.Automation;

/// <summary>
/// A command alias: when user input matches <see cref="Pattern"/>, it is expanded into one
/// or more commands via <see cref="Substitution"/> (with <c>$1</c>..<c>$9</c> / <c>${name}</c>
/// capture references). Newlines in the substitution produce multiple commands.
/// </summary>
public sealed class Alias
{
    private Regex? _compiled;
    private bool _caseSensitive;
    private string _pattern = string.Empty;

    /// <summary>
    /// What the alias is called, for the lists that show it. Settable so the F3 screen can rename one
    /// live; unlike <see cref="Pattern"/> and <see cref="CaseSensitive"/> nothing is derived from it —
    /// expansion matches on the pattern and never looks an alias up by name — so there is no cache to
    /// drop.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The .NET regular expression matched against typed input. Settable so the F3 settings screen can
    /// edit it live; writing it drops the cached <see cref="Regex"/> so the next match recompiles
    /// against the new pattern rather than silently going on matching the old one.
    /// </summary>
    public required string Pattern
    {
        get => _pattern;
        set
        {
            if (string.Equals(_pattern, value, StringComparison.Ordinal))
            {
                return;
            }

            _pattern = value;
            _compiled = null;
        }
    }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Match case exactly. Settable so the F3 settings screen can flip it live; writing it drops the
    /// cached <see cref="Regex"/> so the next match recompiles with the new casing rather than
    /// silently keeping the old options.
    /// </summary>
    public bool CaseSensitive
    {
        get => _caseSensitive;
        set
        {
            if (_caseSensitive == value)
            {
                return;
            }

            _caseSensitive = value;
            _compiled = null;
        }
    }

    /// <summary>
    /// The expansion template. May contain multiple newline-separated commands. Settable so the F3
    /// screen can edit it live; the engine reads it per expansion, so a change applies to the next
    /// line typed. Nothing is cached from it.
    /// </summary>
    public string Substitution { get; set; } = string.Empty;

    /// <summary>Optional named script callback invoked instead of / in addition to expansion.</summary>
    public string? ScriptCallback { get; init; }

    [JsonIgnore]
    public Regex Regex => _compiled ??= new Regex(
        Pattern,
        RegexOptions.Compiled | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
        AutomationDefaults.RegexMatchTimeout);

    /// <summary>
    /// A copy of this alias — the F3 screen's <c>duplicate</c> button is the caller. Every part is a
    /// value or an immutable string, so nothing is shared; the compiled <see cref="Regex"/> is
    /// deliberately not carried over, so the copy builds its own on first use and a later pattern or
    /// casing edit on either alias cannot be seen by the other.
    /// </summary>
    public Alias Clone() => new()
    {
        Name = Name,
        Pattern = Pattern,
        Enabled = Enabled,
        CaseSensitive = CaseSensitive,
        Substitution = Substitution,
        ScriptCallback = ScriptCallback,
    };
}
