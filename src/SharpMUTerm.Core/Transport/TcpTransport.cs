using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace SharpMUTerm.Core.Transport;

/// <summary>
/// A TCP transport with optional TLS. DNS resolution via <see cref="TcpClient"/> makes it
/// dual-stack (IPv4 and IPv6). When <see cref="ConnectionOptions.UseTls"/> is set, the
/// network stream is wrapped in an <see cref="SslStream"/> and authenticated as a client.
/// </summary>
public sealed class TcpTransport(ConnectionOptions options) : ITransport
{
    private readonly ConnectionOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpClient? _client;
    private Stream? _stream;

    public bool IsConnected => _client?.Connected == true;

    public string? RemoteDescription { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not null)
        {
            throw new InvalidOperationException("Transport is already connected.");
        }

        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.ConnectTimeout);
            await client.ConnectAsync(_options.Host, _options.Port, timeoutCts.Token).ConfigureAwait(false);

            Stream stream = client.GetStream();
            if (_options.UseTls)
            {
                stream = await AuthenticateTlsAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            _client = client;
            _stream = stream;
            RemoteDescription = client.Client.RemoteEndPoint?.ToString() ?? _options.ToString();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<SslStream> AuthenticateTlsAsync(Stream inner, CancellationToken cancellationToken)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false, ValidateCertificate);
        var sslOptions = new SslClientAuthenticationOptions
        {
            TargetHost = _options.TlsTargetHost ?? _options.Host,
            EnabledSslProtocols = SslProtocols.None, // let the OS negotiate TLS 1.2/1.3
        };

        await ssl.AuthenticateAsClientAsync(sslOptions, cancellationToken).ConfigureAwait(false);
        return ssl;
    }

    private bool ValidateCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
        => errors == SslPolicyErrors.None || _options.AllowInvalidCertificates;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("Transport is not connected.");

        // Serialize writes: telnet negotiation, user commands, and trigger responses can all
        // reach here concurrently, and overlapping WriteAsync calls on one stream corrupt framing.
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("Transport is not connected.");
        try
        {
            return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // Treat a broken/closed socket as a clean end-of-stream.
            return 0;
        }
    }

    public Task CloseAsync()
    {
        try
        {
            _stream?.Dispose();
            _client?.Close();
        }
        catch
        {
            // Best-effort close.
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _client?.Dispose();
        _client = null;
        _stream = null;
        _sendLock.Dispose();
    }
}
