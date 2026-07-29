namespace SharpMUTerm.Core.Diagnostics;

/// <summary>How loud a client-side message is, which is what the viewer colours and filters on.</summary>
public enum MessageSeverity
{
    /// <summary>Something happened and the user asked for it (a character switched, a log started).</summary>
    Info,

    /// <summary>The client refused to do something, or did less than the label promised.</summary>
    Warning,

    /// <summary>Something failed: a connection that would not open, a settings file that would not save.</summary>
    Error,
}

/// <summary>One recorded client message: when it was said, how loud it was, and what it said.</summary>
/// <param name="At">When the message was raised (UTC).</param>
/// <param name="Severity">How loud it is.</param>
/// <param name="Text">The message itself, as plain text — no markup, so any surface can render it.</param>
/// <param name="Source">
/// What raised it, short enough for a column: the client itself, or the logger category a library
/// message arrived under (telnet negotiation, transport). Null when unattributed.
/// </param>
public sealed record ClientMessage(DateTimeOffset At, MessageSeverity Severity, string Text, string? Source = null);

/// <summary>
/// A capped, in-memory record of the client's <em>own</em> messages — the transient status-line notices
/// that dismiss themselves after a few seconds.
/// <para>
/// It exists because those notices auto-dismiss: a message you looked away from is otherwise gone. It is
/// deliberately <em>not</em> the output window. That window is the server's stream, and everything
/// printed into it is written to the character's log sink as well, so a UI refusal about pane splits
/// would land in a transcript someone keeps for roleplay or evidence. Client chrome gets its own
/// surface, and this is the model behind it.
/// </para>
/// <para>
/// UI-agnostic and pure so both the recording and the retention are unit-testable without a terminal.
/// </para>
/// </summary>
public sealed class ClientMessageLog
{
    /// <summary>
    /// How many messages are kept. A long session must not accumulate them without bound, and this is a
    /// debugging aid rather than an archive: two hundred entries is far more than the handful anyone
    /// scrolls back through, and the oldest fall off the end first.
    /// </summary>
    public const int DefaultCapacity = 200;

    private readonly List<ClientMessage> _entries = new();
    private readonly object _gate = new();

    public ClientMessageLog(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>The most messages this log keeps before dropping its oldest.</summary>
    public int Capacity { get; }

    /// <summary>Everything kept, oldest first.</summary>
    public IReadOnlyList<ClientMessage> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>Records a message, dropping the oldest once the log is at <see cref="Capacity"/>.</summary>
    public void Record(DateTimeOffset at, MessageSeverity severity, string text, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            _entries.Add(new ClientMessage(at, severity, text, source));
            if (_entries.Count > Capacity)
            {
                _entries.RemoveRange(0, _entries.Count - Capacity);
            }
        }
    }

    /// <summary>Forgets everything recorded so far.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }
}
