using SharpMUTerm.Core.Telnet;

namespace SharpMUTerm.Core.Tests.Session;

/// <summary>An in-memory <see cref="ITelnetSession"/> for driving <c>WorldSession</c> tests.</summary>
internal sealed class FakeTelnetSession : ITelnetSession
{
    public bool IsConnected { get; private set; }

    public List<string> SentLines { get; } = new();

    public event EventHandler<TelnetOutputEventArgs>? OutputReceived;
    public event EventHandler<GmcpMessageEventArgs>? GmcpReceived;
#pragma warning disable CS0067 // Required by the interface; not exercised by current tests.
    public event EventHandler<MsdpMessageEventArgs>? MsdpReceived;
    public event EventHandler<MsspReceivedEventArgs>? MsspReceived;
#pragma warning restore CS0067
    public event EventHandler<SessionDisconnectedEventArgs>? Disconnected;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public ValueTask SendLineAsync(string text, CancellationToken cancellationToken = default)
    {
        SentLines.Add(text);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask SendGmcpAsync(string package, string json, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask SetWindowSizeAsync(int width, int height) => ValueTask.CompletedTask;

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

    // ---- test drivers ----

    public void EmitLine(string text) => OutputReceived?.Invoke(this, new TelnetOutputEventArgs(text, isPrompt: false));

    public void EmitPrompt(string text) => OutputReceived?.Invoke(this, new TelnetOutputEventArgs(text, isPrompt: true));

    public void EmitGmcp(string package, string json) =>
        GmcpReceived?.Invoke(this, new GmcpMessageEventArgs(package, json));
}
