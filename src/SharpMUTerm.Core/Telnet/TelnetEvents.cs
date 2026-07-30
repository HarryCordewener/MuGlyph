namespace SharpMUTerm.Core.Telnet;

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

/// <summary>
/// MSSP (Mud Server Status Protocol) data reported by the server: every variable it sent, with every
/// value of every array, in wire order. See <see cref="Mssp.MsspData"/> for why the model is a list
/// per variable rather than a single value.
/// </summary>
public sealed class MsspReceivedEventArgs(Mssp.MsspData data) : EventArgs
{
    /// <summary>The full report.</summary>
    public Mssp.MsspData Data { get; } = data;

    /// <summary>
    /// The report flattened to one value per variable — the <em>last</em> value of each, which the
    /// specification calls the default. Keyed by the canonical MSSP name (<c>MINIMUM AGE</c>, not
    /// <c>Minimum_Age</c>), so what a caller reads is what the server sent.
    /// <para>
    /// This is a convenience for the many consumers that only want a scalar. Anything that cares about
    /// <c>REFERRAL</c>, <c>PORT</c> or any other array must read <see cref="Data"/>, because a flat map
    /// cannot express one.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Values { get; } =
        data.Where(pair => pair.Value.Count > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value[^1], StringComparer.OrdinalIgnoreCase);
}

/// <summary>Raised when the session disconnects, cleanly or due to an error.</summary>
public sealed class SessionDisconnectedEventArgs(Exception? error) : EventArgs
{
    /// <summary>The error that caused the disconnect, or null for a clean close.</summary>
    public Exception? Error { get; } = error;

    public bool IsClean => Error is null;
}
