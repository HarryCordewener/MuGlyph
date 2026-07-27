namespace MuClient.Scripting;

/// <summary>
/// The narrow surface a <see cref="ScriptHost"/> uses to talk to a world. Abstracted so the host
/// can be unit-tested without a live connection: production wires it to a real
/// <c>MuClient.Core.Session.WorldSession</c> via <see cref="WorldSessionScriptBridge"/>, while
/// tests supply a fake that captures the calls.
/// </summary>
public interface IScriptWorld
{
    /// <summary>Queues or sends a command to the server.</summary>
    void Send(string command);

    /// <summary>Writes a line of text to the client output.</summary>
    void Print(string text);

    /// <summary>
    /// Registers a script-backed trigger. When the pattern matches, the world is expected to
    /// deliver the fire back to the host (see <see cref="ScriptHost.DispatchTrigger"/>) using the
    /// same <paramref name="callbackId"/>.
    /// </summary>
    void AddTrigger(string pattern, string callbackId);

    /// <summary>
    /// Registers an alias. When <paramref name="substitution"/> is non-empty the world expands it
    /// directly; a non-empty <paramref name="callbackId"/> marks the alias as script-backed.
    /// </summary>
    void AddAlias(string pattern, string substitution, string callbackId);

    /// <summary>Schedules a recurring callback; disposing the handle cancels it.</summary>
    IDisposable ScheduleEvery(TimeSpan interval, Action callback);

    /// <summary>Schedules a one-shot callback; disposing the handle cancels it.</summary>
    IDisposable ScheduleAfter(TimeSpan delay, Action callback);

    /// <summary>The display name of the world, exposed to scripts as <c>world.name</c>.</summary>
    string WorldName { get; }
}
