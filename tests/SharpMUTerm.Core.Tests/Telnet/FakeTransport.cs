using System.Threading.Channels;
using SharpMUTerm.Core.Transport;

namespace SharpMUTerm.Core.Tests.Telnet;

/// <summary>
/// In-memory <see cref="ITransport"/> for tests: queue inbound (server → client) bytes,
/// capture outbound (client → server) bytes, and signal end-of-stream.
/// </summary>
internal sealed class FakeTransport : ITransport
{
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
    private readonly object _sentGate = new();
    private readonly List<byte> _sent = new();
    private byte[]? _leftover;
    private int _leftoverPos;

    public bool IsConnected { get; private set; }

    public string? RemoteDescription => "fake";

    public byte[] SentBytes
    {
        get
        {
            lock (_sentGate)
            {
                return _sent.ToArray();
            }
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        lock (_sentGate)
        {
            _sent.AddRange(data.ToArray());
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_leftover is null)
        {
            try
            {
                _leftover = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                _leftoverPos = 0;
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
        }

        var available = _leftover.Length - _leftoverPos;
        var count = Math.Min(available, buffer.Length);
        _leftover.AsSpan(_leftoverPos, count).CopyTo(buffer.Span);
        _leftoverPos += count;
        if (_leftoverPos >= _leftover.Length)
        {
            _leftover = null;
        }

        return count;
    }

    public void FeedInbound(params byte[] data) => _inbound.Writer.TryWrite(data);

    public void CompleteInbound() => _inbound.Writer.TryComplete();

    public Task CloseAsync()
    {
        IsConnected = false;
        _inbound.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        CompleteInbound();
        return ValueTask.CompletedTask;
    }
}
