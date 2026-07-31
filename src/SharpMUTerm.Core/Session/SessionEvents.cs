using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Session;

/// <summary>The lifecycle state of a <see cref="WorldSession"/>.</summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted,
}

/// <summary>
/// A line routed to a named spawn window by a matching trigger.
/// <para>
/// It carried the routing rule's <c>Pattern</c> as well, for the one consumer that wanted it: the dim
/// <c>⇱ capture …</c> header a spawn pane drew over its output. That header is gone, and with it the
/// only reason the rule's identity ever left the trigger engine — a routed line is a line and where it
/// goes, and nothing downstream needs to know which rule sent it.
/// </para>
/// </summary>
public sealed class SpawnLineEventArgs(string target, StyledLine line) : EventArgs
{
    /// <summary>The window's name, with the rule's capture groups already substituted.</summary>
    public string Target { get; } = target;

    public StyledLine Line { get; } = line;
}

/// <summary>Raised when a session's <see cref="ConnectionState"/> changes.</summary>
public sealed class ConnectionStateChangedEventArgs(ConnectionState state, Exception? error) : EventArgs
{
    public ConnectionState State { get; } = state;

    public Exception? Error { get; } = error;
}
