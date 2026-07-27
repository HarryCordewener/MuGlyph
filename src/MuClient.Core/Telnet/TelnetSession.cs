using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MuClient.Core.Transport;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;

namespace MuClient.Core.Telnet;

/// <summary>Tuning knobs for a <see cref="TelnetSession"/>.</summary>
public sealed class TelnetSessionOptions
{
    /// <summary>Preferred charsets, most-preferred first. Defaults to UTF-8 then Latin-1.</summary>
    public Encoding[] CharsetOrder { get; init; } =
    [
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Encoding.Latin1,
    ];

    /// <summary>Read buffer size in bytes.</summary>
    public int ReceiveBufferSize { get; init; } = 8192;
}

/// <summary>
/// Wraps <see cref="TelnetInterpreter"/> (TelnetNegotiationCore) over an
/// <see cref="ITransport"/>. Negotiation output is written to the transport; inbound bytes
/// are fed to the interpreter, which strips telnet framing and hands back decoded data
/// bytes. Complete lines (newline) and prompts (GA/EOR) are surfaced via
/// <see cref="OutputReceived"/>.
/// </summary>
public sealed class TelnetSession : ITelnetSession
{
    // TelnetInterpreter.CallbackOnByteAsync is init-only and not exposed by the builder, so
    // we assign it reflectively after building. This is the one seam where we reach past the
    // library's public surface; a first-class OnByte builder hook is a candidate upstream PR.
    private static readonly PropertyInfo ByteCallbackProperty =
        typeof(TelnetInterpreter).GetProperty(nameof(TelnetInterpreter.CallbackOnByteAsync))
        ?? throw new InvalidOperationException("TelnetInterpreter.CallbackOnByteAsync not found.");

    private readonly ITransport _transport;
    private readonly ILogger _logger;
    private readonly TelnetSessionOptions _options;

    private readonly List<byte> _pending = new();
    private Encoding _currentEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private TelnetInterpreter? _interpreter;
    private CancellationTokenSource? _loopCts;
    private Task? _readLoop;
    private int _disconnected;

    public TelnetSession(ITransport transport, ILogger? logger = null, TelnetSessionOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _logger = logger ?? NullLogger.Instance;
        _options = options ?? new TelnetSessionOptions();
    }

    public bool IsConnected => _transport.IsConnected && _interpreter is not null;

    public event EventHandler<TelnetOutputEventArgs>? OutputReceived;
    public event EventHandler<GmcpMessageEventArgs>? GmcpReceived;
    public event EventHandler<MsdpMessageEventArgs>? MsdpReceived;
    public event EventHandler<MsspReceivedEventArgs>? MsspReceived;
    public event EventHandler<SessionDisconnectedEventArgs>? Disconnected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_interpreter is not null)
        {
            throw new InvalidOperationException("Session is already connected.");
        }

        // Transport must be open before building: the interpreter emits initial negotiation
        // (e.g. WILL NAWS) during BuildAsync, which is written straight to the transport.
        await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _interpreter = await BuildInterpreterAsync().ConfigureAwait(false);
            ByteCallbackProperty.SetValue(_interpreter, new Func<byte, Encoding, ValueTask>(OnByteAsync));
        }
        catch
        {
            // Building/wiring the interpreter failed after the socket opened; close it so we
            // don't leak a half-open connection, and stay in the not-connected state.
            _interpreter = null;
            await _transport.CloseAsync().ConfigureAwait(false);
            throw;
        }

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readLoop = Task.Run(() => ReadLoopAsync(_loopCts.Token), CancellationToken.None);
    }

    private Task<TelnetInterpreter> BuildInterpreterAsync() =>
        new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(_logger)
            .OnNegotiation(WriteToTransportAsync)
            .OnSubmit(OnSubmitAsync)
            .AddDefaultMUDProtocols(
                onNAWS: static (_, _) => ValueTask.CompletedTask,
                onGMCPMessage: OnGmcpAsync,
                onMSSP: OnMsspAsync,
                msspConfig: static () => new MSSPConfig(),
                onMSDPMessage: OnMsdpAsync,
                onPrompt: OnPromptAsync,
                charsetOrder: _options.CharsetOrder,
                onCompressionEnabled: OnCompressionAsync,
                onMXPEnabled: static () => ValueTask.CompletedTask)
            .BuildAsync();

    private ValueTask WriteToTransportAsync(ReadOnlyMemory<byte> data) =>
        _transport.SendAsync(data);

    private ValueTask OnByteAsync(byte value, Encoding encoding)
    {
        if (encoding is not null)
        {
            _currentEncoding = encoding;
        }

        _pending.Add(value);
        return ValueTask.CompletedTask;
    }

    private ValueTask OnSubmitAsync(byte[] bytes, Encoding? encoding, TelnetInterpreter interpreter)
    {
        // A newline-terminated line. Prefer the interpreter's own line bytes for the content.
        var text = (encoding ?? _currentEncoding).GetString(bytes);
        _pending.Clear();
        Emit(text, isPrompt: false);
        return ValueTask.CompletedTask;
    }

    private ValueTask OnPromptAsync()
    {
        // GA/EOR boundary: flush whatever data has accumulated as a prompt.
        FlushPending(isPrompt: true);
        return ValueTask.CompletedTask;
    }

    private void FlushPending(bool isPrompt)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var text = _currentEncoding.GetString(_pending.ToArray());
        _pending.Clear();
        Emit(text, isPrompt);
    }

    private void Emit(string text, bool isPrompt) =>
        OutputReceived?.Invoke(this, new TelnetOutputEventArgs(text, isPrompt));

    private ValueTask OnGmcpAsync((string Package, string Json) message)
    {
        GmcpReceived?.Invoke(this, new GmcpMessageEventArgs(message.Package, message.Json));
        return ValueTask.CompletedTask;
    }

    private ValueTask OnMsdpAsync(TelnetInterpreter interpreter, string json)
    {
        MsdpReceived?.Invoke(this, new MsdpMessageEventArgs(json));
        return ValueTask.CompletedTask;
    }

    private ValueTask OnMsspAsync(MSSPConfig config)
    {
        MsspReceived?.Invoke(this, new MsspReceivedEventArgs(MsspConfigReader.ToDictionary(config)));
        return ValueTask.CompletedTask;
    }

    private ValueTask OnCompressionAsync(int version, bool enabled)
    {
        _logger.LogInformation("MCCP{Version} compression {State}.", version, enabled ? "enabled" : "disabled");
        return ValueTask.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[_options.ReceiveBufferSize];
        Exception? error = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _transport.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break; // clean end of stream
                }

                await _interpreter!.InterpretByteArrayAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            error = ex;
            _logger.LogError(ex, "Telnet receive loop faulted.");
        }
        finally
        {
            RaiseDisconnected(error);
        }
    }

    public ValueTask SendLineAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = _currentEncoding.GetBytes(text + "\r\n");
        return SendAsync(bytes, cancellationToken);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var interpreter = _interpreter ?? throw new InvalidOperationException("Session is not connected.");
        return interpreter.SendAsync(data.ToArray());
    }

    public ValueTask SendGmcpAsync(string package, string json, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var interpreter = _interpreter ?? throw new InvalidOperationException("Session is not connected.");
        return interpreter.SendGMCPCommand(package, json ?? string.Empty);
    }

    public ValueTask SetWindowSizeAsync(int width, int height)
    {
        var interpreter = _interpreter ?? throw new InvalidOperationException("Session is not connected.");
        return interpreter.SendNAWS((short)Math.Clamp(width, 0, short.MaxValue), (short)Math.Clamp(height, 0, short.MaxValue));
    }

    public async Task DisconnectAsync()
    {
        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
        }

        await _transport.CloseAsync().ConfigureAwait(false);

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch
            {
                // already reported via Disconnected
            }
        }

        // Clear the interpreter so a subsequent ConnectAsync can reconnect and the "not
        // connected" guards on the send methods observe the disconnected state.
        _interpreter = null;
        RaiseDisconnected(null);
    }

    private void RaiseDisconnected(Exception? error)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0)
        {
            return;
        }

        Disconnected?.Invoke(this, new SessionDisconnectedEventArgs(error));
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _loopCts?.Dispose();

        if (_interpreter is not null)
        {
            await _interpreter.DisposeAsync().ConfigureAwait(false);
            _interpreter = null;
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
