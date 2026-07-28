using MuClient.Core.Automation;

namespace MuClient.Core.Configuration;

/// <summary>
/// A named, world-independent bundle of automation. Characters select which sets apply by name,
/// so a "Comms" set can be shared across every character while a "Trade" set is live for one
/// character and dark for another on the same world. A session's engines are composed from the
/// union of its character's assigned sets.
/// </summary>
public sealed class TriggerSet
{
    public string Name { get; set; } = "New Set";

    public string? Description { get; set; }

    public List<Trigger> Triggers { get; set; } = new();

    public List<Alias> Aliases { get; set; } = new();

    public List<Macro> Macros { get; set; } = new();

    /// <summary>Interval timers that fire while this set is active for a session.</summary>
    public List<TimerDefinition> Timers { get; set; } = new();

    /// <summary>Lua script files loaded when this set is active for a session.</summary>
    public List<string> ScriptFiles { get; set; } = new();
}
