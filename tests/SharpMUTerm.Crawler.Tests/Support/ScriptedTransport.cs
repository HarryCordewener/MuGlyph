using System.Threading.Channels;
using SharpMUTerm.Core.Transport;

namespace SharpMUTerm.Crawler.Tests.Support;

/// <summary>
/// An in-memory transport standing in for a server: queue bytes to be delivered, capture everything
/// written, and optionally react to what the client sends.
/// <para>
/// This is a *server*, not just a pipe. The MSSP handshake is a conversation — the server says
/// <c>IAC WILL MSSP</c> and only sends its report once the client has answered <c>IAC DO MSSP</c> — so
/// a transport that replayed a fixed script would test the telnet layer's reader against bytes no real
/// server would have sent yet, and would never exercise the negotiation the crawler depends on.
/// </para>
/// </summary>
internal sealed class ScriptedTransport : ITransport
{
    private const byte Iac = 255;
    private const byte Do = 253;

    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
    private readonly Lock _gate = new();
    private readonly List<byte> _sent = [];
    private readonly Dictionary<byte, byte[]> _onDo = [];

    private byte[]? _leftover;
    private int _leftoverPos;

    public bool IsConnected { get; private set; }

    public string? RemoteDescription => "scripted";

    /// <summary>Everything the client has written, in order.</summary>
    public byte[] Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    /// <summary>Bytes to deliver as soon as the connection opens — the server's opening negotiation.</summary>
    public byte[] Greeting { get; init; } = [];

    /// <summary>
    /// Delivers everything one byte per read — the worst case a TCP stream can produce, and the one an
    /// incremental reader has to survive. A payload split this way must parse the same as one that
    /// arrives whole.
    /// </summary>
    public bool Fragmented { get; init; }

    /// <summary>
    /// When the client answers <c>IAC DO &lt;option&gt;</c>, deliver <paramref name="response"/>. This is
    /// how the MSSP report is made conditional on the client having asked for it.
    /// </summary>
    public ScriptedTransport RespondingToDo(byte option, byte[] response)
    {
        _onDo[option] = response;
        return this;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        if (Greeting.Length > 0)
        {
            Deliver(Greeting);
        }

        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var bytes = data.ToArray();
        lock (_gate)
        {
            _sent.AddRange(bytes);
        }

        // Scan for IAC DO <option> and answer it. Crude on purpose: the client's negotiation is small
        // and this only needs to find the three bytes it is waiting for.
        for (var i = 0; i + 2 < bytes.Length + 1 && i + 2 <= bytes.Length; i++)
        {
            if (i + 2 < bytes.Length && bytes[i] == Iac && bytes[i + 1] == Do &&
                _onDo.TryGetValue(bytes[i + 2], out var response))
            {
                Deliver(response);
            }
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

    /// <summary>Delivers bytes to the client, as a server would mid-conversation.</summary>
    public void SendToClient(params byte[] bytes) => Deliver(bytes);

    /// <summary>
    /// Queues bytes for the reader. <see cref="ReceiveAsync"/> hands back at most one queued chunk per
    /// call, so queuing one byte at a time is what makes <see cref="Fragmented"/> arrive that way.
    /// </summary>
    private void Deliver(byte[] bytes)
    {
        if (!Fragmented)
        {
            _inbound.Writer.TryWrite(bytes);
            return;
        }

        foreach (var b in bytes)
        {
            _inbound.Writer.TryWrite([b]);
        }
    }

    /// <summary>
    /// Waits (up to a second) for the client to have written <paramref name="expected"/>. Polling, but
    /// against a real asynchronous negotiation there is nothing to await: the bytes appear when the
    /// telnet layer's own read loop gets to them.
    /// </summary>
    public async Task<bool> WaitForSentAsync(byte[] expected)
    {
        for (var i = 0; i < 100; i++)
        {
            var sent = Sent;
            for (var at = 0; at + expected.Length <= sent.Length; at++)
            {
                if (sent.AsSpan(at, expected.Length).SequenceEqual(expected))
                {
                    return true;
                }
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>The server hanging up.</summary>
    public void Close() => _inbound.Writer.TryComplete();

    public Task CloseAsync()
    {
        IsConnected = false;
        _inbound.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}
