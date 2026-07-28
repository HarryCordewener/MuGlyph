namespace MuClient.Core.Automation;

/// <summary>
/// A configured timer: fires a command (or script callback) on a fixed interval while its owning
/// <see cref="MuClient.Core.Configuration.TriggerSet"/> is active for a session. The
/// <see cref="IntervalScheduler"/> realises these at runtime; this is the persisted definition the
/// F6 timers screen edits.
/// </summary>
public sealed class TimerDefinition
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Seconds between firings. Values ≤ 0 are treated as disabled.</summary>
    public double IntervalSeconds { get; init; }

    /// <summary>The command sent on each firing (blank when a script callback is used instead).</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>Fire only once after the interval rather than repeating.</summary>
    public bool OneShot { get; init; }

    public bool Enabled { get; set; } = true;

    /// <summary>Optional named script callback invoked on each firing.</summary>
    public string? ScriptCallback { get; init; }
}
