namespace SharpMUTerm.Core.Automation;

/// <summary>
/// A configured timer: fires a command (or script callback) on a fixed interval while its owning
/// <see cref="SharpMUTerm.Core.Configuration.TriggerSet"/> is active for a session. The
/// <see cref="IntervalScheduler"/> realises these at runtime; this is the persisted definition the
/// F6 timers screen edits.
/// </summary>
public sealed class TimerDefinition
{
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Seconds between firings. Values ≤ 0 are treated as disabled. Settable so the F6 screen can edit
    /// it live; the <see cref="IntervalScheduler"/> reads it when the timer is realised, so a change
    /// applies on the next run.
    /// </summary>
    public double IntervalSeconds { get; set; }

    /// <summary>
    /// The command sent on each firing (blank when a script callback is used instead). Settable so the
    /// F6 screen can edit it live; it is read per firing, so a change applies to the next one.
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Fire only once after the interval rather than repeating. Settable so the F6 screen can flip it
    /// live; the scheduler reads it when the timer is realised, so a change applies on the next run.
    /// </summary>
    public bool OneShot { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Optional named script callback invoked on each firing.</summary>
    public string? ScriptCallback { get; init; }
}
