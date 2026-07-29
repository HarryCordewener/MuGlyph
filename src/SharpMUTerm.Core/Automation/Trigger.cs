using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Automation;

/// <summary>The typed actions a matched <see cref="Trigger"/> performs.</summary>
public sealed class TriggerActions
{
    /// <summary>Suppress the line from output entirely. Settable so the F2 screen can flip it live.</summary>
    public bool Gag { get; set; }

    /// <summary>
    /// Recolour the matched region's foreground. Settable so the F2 screen's colour picker can change
    /// it live; nothing is derived from it, so there is no cache to drop the way
    /// <see cref="Trigger.Pattern"/> has.
    /// </summary>
    public TerminalColor? HighlightForeground { get; set; }

    /// <summary>Recolour the matched region's background. Settable for the same reason as its foreground.</summary>
    public TerminalColor? HighlightBackground { get; set; }

    /// <summary>Add these attributes to the matched region (e.g. bold).</summary>
    public TextAttributes AddAttributes { get; init; } = TextAttributes.None;

    /// <summary>
    /// Replace the whole line's text with this template (supports <c>$1</c>..<c>$9</c> and
    /// <c>${name}</c> capture references). Rewritten text renders with the default style.
    /// </summary>
    public string? Rewrite { get; init; }

    /// <summary>Send this command back to the server (capture references supported).</summary>
    public string? SendResponse { get; init; }

    /// <summary>
    /// Route the line to a named spawn window instead of the main output; null routes to the main
    /// window. Settable so the F2 screen's route-to list can re-point a rule live — the engine reads
    /// it per match and <see cref="TriggerEngine"/> keeps no routing table of its own, so a change
    /// applies to the next line.
    /// </summary>
    public string? SpawnTarget { get; set; }

    /// <summary>Invoke this named script callback (resolved by the scripting layer).</summary>
    public string? ScriptCallback { get; init; }

    /// <summary>
    /// A copy of these actions. Every field is a value or an immutable string, so a member-wise copy
    /// really is a deep one — but it has to be written out rather than shared, because
    /// <see cref="Trigger.Actions"/> is <c>init</c>-only and two triggers holding the same instance
    /// would silently share a gag flag and a route.
    /// </summary>
    public TriggerActions Clone() => new()
    {
        Gag = Gag,
        HighlightForeground = HighlightForeground,
        HighlightBackground = HighlightBackground,
        AddAttributes = AddAttributes,
        Rewrite = Rewrite,
        SendResponse = SendResponse,
        SpawnTarget = SpawnTarget,
        ScriptCallback = ScriptCallback,
    };
}

/// <summary>
/// A regex-driven trigger. Matching is done against the plain text of an output line;
/// actions may gag, highlight, rewrite, respond, spawn-route, or invoke a script.
/// </summary>
public sealed class Trigger
{
    private Regex? _compiled;
    private string _pattern = string.Empty;

    /// <summary>
    /// What the rule is called, for the lists that show it. Settable so the F2 screen can rename a rule
    /// live; unlike <see cref="Pattern"/> and <see cref="CaseSensitive"/> nothing is derived from it —
    /// the engines match on the pattern and never look a rule up by name — so there is no cache to drop.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The .NET regular expression matched against a line's plain text. Settable so the F2 settings
    /// screen can edit it live; writing it drops the cached <see cref="Regex"/> so the next match
    /// recompiles against the new pattern rather than silently going on matching the old one.
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

    public bool CaseSensitive { get; init; }

    /// <summary>
    /// When true, later triggers are not evaluated once this one matches. Settable so the F2 screen
    /// can flip it live; the engine reads it per match, so a change applies to the next line.
    /// </summary>
    public bool StopProcessing { get; set; }

    public TriggerActions Actions { get; init; } = new();

    /// <summary>The compiled regex (built once, lazily), with a match timeout guarding against ReDoS.</summary>
    [JsonIgnore]
    public Regex Regex => _compiled ??= new Regex(
        Pattern,
        RegexOptions.Compiled | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
        AutomationDefaults.RegexMatchTimeout);

    /// <summary>
    /// A deep copy of this rule — the F2 screen's <c>duplicate</c> button is the caller. The
    /// <see cref="Actions"/> are copied rather than shared: an aliased copy would look right and then
    /// follow every later edit of its original's gag, route and highlight around. The compiled
    /// <see cref="Regex"/> is deliberately not carried over; the copy builds its own on first use, so a
    /// later pattern edit on either rule cannot be seen by the other.
    /// </summary>
    public Trigger Clone() => new()
    {
        Name = Name,
        Pattern = Pattern,
        Enabled = Enabled,
        CaseSensitive = CaseSensitive,
        StopProcessing = StopProcessing,
        Actions = Actions.Clone(),
    };
}
