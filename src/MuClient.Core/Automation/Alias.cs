using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MuClient.Core.Automation;

/// <summary>
/// A command alias: when user input matches <see cref="Pattern"/>, it is expanded into one
/// or more commands via <see cref="Substitution"/> (with <c>$1</c>..<c>$9</c> / <c>${name}</c>
/// capture references). Newlines in the substitution produce multiple commands.
/// </summary>
public sealed class Alias
{
    private Regex? _compiled;

    public string Name { get; init; } = string.Empty;

    public required string Pattern { get; init; }

    public bool Enabled { get; set; } = true;

    public bool CaseSensitive { get; init; }

    /// <summary>The expansion template. May contain multiple newline-separated commands.</summary>
    public string Substitution { get; init; } = string.Empty;

    /// <summary>Optional named script callback invoked instead of / in addition to expansion.</summary>
    public string? ScriptCallback { get; init; }

    [JsonIgnore]
    public Regex Regex => _compiled ??= new Regex(
        Pattern,
        RegexOptions.Compiled | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
        AutomationDefaults.RegexMatchTimeout);
}
