using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MuClient.Core.Text;

namespace MuClient.Core.Automation;

/// <summary>The typed actions a matched <see cref="Trigger"/> performs.</summary>
public sealed class TriggerActions
{
    /// <summary>Suppress the line from output entirely.</summary>
    public bool Gag { get; init; }

    /// <summary>Recolour the matched region's foreground.</summary>
    public TerminalColor? HighlightForeground { get; init; }

    /// <summary>Recolour the matched region's background.</summary>
    public TerminalColor? HighlightBackground { get; init; }

    /// <summary>Add these attributes to the matched region (e.g. bold).</summary>
    public TextAttributes AddAttributes { get; init; } = TextAttributes.None;

    /// <summary>
    /// Replace the whole line's text with this template (supports <c>$1</c>..<c>$9</c> and
    /// <c>${name}</c> capture references). Rewritten text renders with the default style.
    /// </summary>
    public string? Rewrite { get; init; }

    /// <summary>Send this command back to the server (capture references supported).</summary>
    public string? SendResponse { get; init; }

    /// <summary>Route the line to a named spawn window instead of the main output.</summary>
    public string? SpawnTarget { get; init; }

    /// <summary>Invoke this named script callback (resolved by the scripting layer).</summary>
    public string? ScriptCallback { get; init; }
}

/// <summary>
/// A regex-driven trigger. Matching is done against the plain text of an output line;
/// actions may gag, highlight, rewrite, respond, spawn-route, or invoke a script.
/// </summary>
public sealed class Trigger
{
    private Regex? _compiled;

    public string Name { get; init; } = string.Empty;

    /// <summary>The .NET regular expression matched against a line's plain text.</summary>
    public required string Pattern { get; init; }

    public bool Enabled { get; set; } = true;

    public bool CaseSensitive { get; init; }

    /// <summary>When true, later triggers are not evaluated once this one matches.</summary>
    public bool StopProcessing { get; init; }

    public TriggerActions Actions { get; init; } = new();

    /// <summary>The compiled regex (built once, lazily), with a match timeout guarding against ReDoS.</summary>
    [JsonIgnore]
    public Regex Regex => _compiled ??= new Regex(
        Pattern,
        RegexOptions.Compiled | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
        AutomationDefaults.RegexMatchTimeout);
}
