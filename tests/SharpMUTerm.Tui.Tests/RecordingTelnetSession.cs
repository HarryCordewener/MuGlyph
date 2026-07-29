using SharpMUTerm.Core.Telnet;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// A telnet session that connects to nothing and remembers every NAWS report. The size is the whole
/// point, so unlike the Core tests' fake this one keeps them; everything else is the minimum the
/// interface asks for.
/// </summary>
internal sealed class RecordingTelnetSession : ITelnetSession
{
    private readonly List<(int Width, int Height)> _sizes = new();
    private readonly List<string> _lines = new();

    public bool IsConnected { get; private set; }

    /// <summary>
    /// What <see cref="ConnectAsync"/> throws instead of connecting, or null to connect. A refused
    /// connection is the case the old fire-and-forget <c>Reconnect</c> swallowed, so the tests need a
    /// transport that can refuse one.
    /// </summary>
    public Exception? ConnectFault { get; init; }

    /// <summary>How many times a connection was asked for, refused ones included.</summary>
    public int ConnectAttempts { get; private set; }

    /// <summary>Every NAWS report this session was given, oldest first.</summary>
    public IReadOnlyList<(int Width, int Height)> Sizes
    {
        get
        {
            lock (_sizes)
            {
                return _sizes.ToArray();
            }
        }
    }

    /// <summary>
    /// Every line written to the wire, oldest first. Kept so a test can assert that something did
    /// <em>not</em> reach the world — which is the whole claim behind "⏎ in the history surface inserts and
    /// does not send", and one nothing else can see.
    /// </summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToArray();
            }
        }
    }

#pragma warning disable CS0067 // Required by the interface; these tests drive sizes, not output.
    public event EventHandler<TelnetOutputEventArgs>? OutputReceived;
    public event EventHandler<GmcpMessageEventArgs>? GmcpReceived;
    public event EventHandler<MsdpMessageEventArgs>? MsdpReceived;
    public event EventHandler<MsspReceivedEventArgs>? MsspReceived;
#pragma warning restore CS0067
    public event EventHandler<SessionDisconnectedEventArgs>? Disconnected;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectAttempts++;
        if (ConnectFault is { } fault)
        {
            return Task.FromException(fault);
        }

        IsConnected = true;
        return Task.CompletedTask;
    }

    public ValueTask SendLineAsync(string text, CancellationToken cancellationToken = default)
    {
        lock (_lines)
        {
            _lines.Add(text);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask SendGmcpAsync(string package, string json, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask SetWindowSizeAsync(int width, int height)
    {
        lock (_sizes)
        {
            _sizes.Add((width, height));
        }

        return ValueTask.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        if (IsConnected)
        {
            IsConnected = false;
            Disconnected?.Invoke(this, new SessionDisconnectedEventArgs(null));
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
