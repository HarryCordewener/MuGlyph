namespace SharpMUTerm.Core.Telnet;

/// <summary>
/// A telnet session over a transport: option negotiation, charset decoding, prompt
/// detection, and out-of-band protocols (GMCP/MSSP/MSDP/MCCP). Emits decoded server
/// output as text; ANSI styling and scrollback live in higher layers.
/// </summary>
public interface ITelnetSession : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Server output: completed lines and prompts.</summary>
    event EventHandler<TelnetOutputEventArgs>? OutputReceived;

    /// <summary>GMCP messages routed from the server.</summary>
    event EventHandler<GmcpMessageEventArgs>? GmcpReceived;

    /// <summary>MSDP messages routed from the server.</summary>
    event EventHandler<MsdpMessageEventArgs>? MsdpReceived;

    /// <summary>MSSP server-status data.</summary>
    event EventHandler<MsspReceivedEventArgs>? MsspReceived;

    /// <summary>Raised when the connection ends.</summary>
    event EventHandler<SessionDisconnectedEventArgs>? Disconnected;

    /// <summary>Connects the transport and begins negotiation and the receive loop.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a command line to the server (a trailing CR/LF is appended).</summary>
    ValueTask SendLineAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Sends raw bytes to the server (IAC bytes are escaped by the telnet layer).</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Sends a GMCP message.</summary>
    ValueTask SendGmcpAsync(string package, string json, CancellationToken cancellationToken = default);

    /// <summary>Reports the terminal window size to the server via NAWS.</summary>
    ValueTask SetWindowSizeAsync(int width, int height);

    /// <summary>Closes the session.</summary>
    Task DisconnectAsync();
}
