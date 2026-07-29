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

    public bool IsConnected { get; private set; }

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

#pragma warning disable CS0067 // Required by the interface; these tests drive sizes, not output.
    public event EventHandler<TelnetOutputEventArgs>? OutputReceived;
    public event EventHandler<GmcpMessageEventArgs>? GmcpReceived;
    public event EventHandler<MsdpMessageEventArgs>? MsdpReceived;
    public event EventHandler<MsspReceivedEventArgs>? MsspReceived;
#pragma warning restore CS0067
    public event EventHandler<SessionDisconnectedEventArgs>? Disconnected;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public ValueTask SendLineAsync(string text, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

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
