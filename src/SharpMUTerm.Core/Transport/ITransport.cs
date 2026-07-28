namespace SharpMUTerm.Core.Transport;

/// <summary>
/// A bidirectional byte transport (TCP, optionally TLS). Kept deliberately minimal so it
/// can be faked in unit tests; all telnet/ANSI logic sits above it.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>True once <see cref="ConnectAsync"/> has completed and the link is open.</summary>
    bool IsConnected { get; }

    /// <summary>A human-readable description of the remote endpoint, once connected.</summary>
    string? RemoteDescription { get; }

    /// <summary>Opens the connection (DNS resolution, TCP, and TLS handshake if configured).</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes bytes to the transport.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads available bytes into <paramref name="buffer"/>. Returns the number of bytes read,
    /// or 0 when the remote end has closed the connection.
    /// </summary>
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Closes the connection.</summary>
    Task CloseAsync();
}
