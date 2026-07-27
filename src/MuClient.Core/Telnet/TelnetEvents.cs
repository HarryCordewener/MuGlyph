namespace MuClient.Core.Telnet;

/// <summary>A chunk of server output: a completed line, or an unterminated prompt.</summary>
public sealed class TelnetOutputEventArgs(string text, bool isPrompt) : EventArgs
{
    /// <summary>The decoded text, with ANSI escape sequences still embedded.</summary>
    public string Text { get; } = text;

    /// <summary>True when this text was terminated by a GA/EOR prompt marker rather than a newline.</summary>
    public bool IsPrompt { get; } = isPrompt;
}

/// <summary>A GMCP (Generic MUD Communication Protocol) message.</summary>
public sealed class GmcpMessageEventArgs(string package, string json) : EventArgs
{
    /// <summary>The package name, e.g. <c>Char.Vitals</c>.</summary>
    public string Package { get; } = package;

    /// <summary>The JSON payload (may be empty).</summary>
    public string Json { get; } = json;
}

/// <summary>An MSDP (Mud Server Data Protocol) message, delivered as JSON.</summary>
public sealed class MsdpMessageEventArgs(string json) : EventArgs
{
    public string Json { get; } = json;
}

/// <summary>MSSP (Mud Server Status Protocol) key/value data reported by the server.</summary>
public sealed class MsspReceivedEventArgs(IReadOnlyDictionary<string, string> values) : EventArgs
{
    public IReadOnlyDictionary<string, string> Values { get; } = values;
}

/// <summary>Raised when the session disconnects, cleanly or due to an error.</summary>
public sealed class SessionDisconnectedEventArgs(Exception? error) : EventArgs
{
    /// <summary>The error that caused the disconnect, or null for a clean close.</summary>
    public Exception? Error { get; } = error;

    public bool IsClean => Error is null;
}
