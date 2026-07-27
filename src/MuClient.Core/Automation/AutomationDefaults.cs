namespace MuClient.Core.Automation;

/// <summary>Shared defaults for the automation engines.</summary>
public static class AutomationDefaults
{
    /// <summary>
    /// Match timeout applied to user-supplied trigger/alias regexes so a pathological pattern
    /// (catastrophic backtracking) cannot hang output processing or command entry.
    /// </summary>
    public static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(250);
}
