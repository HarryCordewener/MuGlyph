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

/// <summary>A line routed to a named spawn window by a matching trigger.</summary>
public sealed class SpawnLineEventArgs(string target, string pattern, StyledLine line) : EventArgs
{
    /// <summary>The window's name, with the rule's capture groups already substituted.</summary>
    public string Target { get; } = target;

    /// <summary>The pattern of the rule that routed the line here — see <c>SpawnRoute</c>.</summary>
    public string Pattern { get; } = pattern;

    public StyledLine Line { get; } = line;
}

/// <summary>Raised when a session's <see cref="ConnectionState"/> changes.</summary>
public sealed class ConnectionStateChangedEventArgs(ConnectionState state, Exception? error) : EventArgs
{
    public ConnectionState State { get; } = state;

    public Exception? Error { get; } = error;
}
