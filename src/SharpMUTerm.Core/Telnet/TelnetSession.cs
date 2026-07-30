using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpMUTerm.Core.Transport;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace SharpMUTerm.Core.Telnet;

/// <summary>Tuning knobs for a <see cref="TelnetSession"/>.</summary>
public sealed class TelnetSessionOptions
{
    /// <summary>
    /// The name a world uses to mean "follow CHARSET negotiation" rather than pinning an encoding.
    /// The default for a world, and the case this whole mechanism exists for.
    /// </summary>
    public const string AutoEncodingName = "auto";

    /// <summary>
    /// Preferred charsets, most-preferred first. Defaults to UTF-8 then Latin-1.
    /// <para>
    /// These must be the instances the platform's own encoding provider yields
    /// (<see cref="Encoding.UTF8"/>, not <c>new UTF8Encoding(false)</c>) — see <see cref="DefaultOrder"/>.
    /// </para>
    /// </summary>
    public Encoding[] CharsetOrder { get; init; } = DefaultOrder;

    /// <summary>
    /// An encoding that overrides whatever CHARSET negotiation settles on, or null — <c>auto</c>, the
    /// default — to follow negotiation.
    /// <para>
    /// An override is used for decoding <em>and</em> encoding regardless of what the server proposes.
    /// It is deliberately not a refusal to negotiate: the same encoding is still stated at the head of
    /// <see cref="CharsetOrder"/>, so a cooperative server agrees and the two sides hold one belief
    /// rather than two. Declining CHARSET outright would leave a server free to send whatever it liked
    /// while telling it nothing about what we can read, which is strictly worse — and RFC 2066 gives no
    /// way to say "this and only this" other than offering exactly that.
    /// </para>
    /// </summary>
    public Encoding? EncodingOverride { get; init; }

    /// <summary>Read buffer size in bytes.</summary>
    public int ReceiveBufferSize { get; init; } = 8192;

    /// <summary>
    /// How long the connection may sit silent before a keepalive goes out, or null for none — a
    /// world's <see cref="SharpMUTerm.Core.Configuration.WorldDefinition.KeepaliveSeconds"/>, resolved
    /// by <see cref="ResolveKeepalive"/>.
    /// <para>
    /// This is applied when the interpreter is built, so changing it takes effect on the next connect
    /// rather than on the open session: the library's <c>KeepAliveInterval</c> is init-only, by design
    /// — the idle loop reads it once when it starts.
    /// </para>
    /// </summary>
    public TimeSpan? KeepaliveInterval { get; init; }

    /// <summary>
    /// Turns a world's configured keepalive seconds into an interval the telnet library will accept,
    /// or null when there should be none.
    /// <para>
    /// Zero — the default, and what "off" is spelled as in the config — means no keepalive. Anything
    /// larger is clamped to the library's maximum rather than thrown at it, because this value arrives
    /// from a config file that may have been hand-edited: refusing to connect at all would be a worse
    /// answer than keeping the connection alive less often than asked.
    /// </para>
    /// <para>
    /// There is deliberately no clamp against the library's <em>minimum</em>. It is one second and this
    /// is a whole number of seconds, so every value that isn't already "off" satisfies it — a guard
    /// there could never fire, and an unreachable branch claiming to be a safety net is worse than
    /// none. If this ever becomes fractional, that changes.
    /// </para>
    /// </summary>
    public static TimeSpan? ResolveKeepalive(int seconds)
    {
        if (seconds <= 0)
        {
            return null;
        }

        var requested = TimeSpan.FromSeconds(seconds);
        return requested > TelnetInterpreter.MaximumKeepAliveInterval
            ? TelnetInterpreter.MaximumKeepAliveInterval
            : requested;
    }

    /// <summary>
    /// The default order, used when the app states none and as the tail of every stated one.
    /// <para>
    /// <b>These are the platform provider's own instances, and that is load-bearing.</b>
    /// TelnetNegotiationCore ranks the charsets a server offers by <c>IndexOf</c> over this list,
    /// against encodings it obtains from <see cref="Encoding.GetEncodings"/> — so an instance that does
    /// not compare <see cref="object.Equals(object)"/>-equal to the provider's ranks <em>below every
    /// one that does</em>. The head used to be <c>new UTF8Encoding(encoderShouldEmitUTF8Identifier:
    /// false)</c>, and <see cref="UTF8Encoding"/> compares that flag, so our first preference silently
    /// sorted last: a server offering both UTF-8 and Latin-1 was answered with Latin-1. The BOM that
    /// instance was avoiding is only ever emitted by <see cref="Encoding.GetPreamble"/>, which nothing
    /// in this stack calls — <see cref="Encoding.GetBytes(string)"/> never prepends one.
    /// </para>
    /// </summary>
    private static Encoding[] DefaultOrder => [Encoding.UTF8, Encoding.Latin1];

    /// <summary>
    /// The named encoding, or null when the name is missing, <c>auto</c>, or one this machine's
    /// encoding provider doesn't know.
    /// <para>
    /// An unknown name resolves to null — <c>auto</c> — rather than throwing: the F5 field accepts
    /// anything the provider might know, and a world whose encoding was renamed out from under it must
    /// still connect.
    /// </para>
    /// </summary>
    public static Encoding? ResolveEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Equals(AutoEncodingName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(name.Trim());
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The app-wide preference order (<see cref="SharpMUTerm.Core.Configuration.AppConfiguration.CharsetOrder"/>)
    /// resolved to encodings, dropping names this machine doesn't know and never returning empty —
    /// something has to be assumed when a server never negotiates, and an empty order would leave
    /// nothing to assume.
    /// </summary>
    public static Encoding[] ResolveOrder(IEnumerable<string>? names)
    {
        if (names is null)
        {
            return DefaultOrder;
        }

        var resolved = new List<Encoding>();
        foreach (var name in names)
        {
            if (ResolveEncoding(name) is { } encoding && !resolved.Any(e => e.CodePage == encoding.CodePage))
            {
                resolved.Add(encoding);
            }
        }

        return resolved.Count == 0 ? DefaultOrder : resolved.ToArray();
    }

    /// <summary>
    /// The CHARSET preference order to state: a world's own override first — its configured
    /// <see cref="SharpMUTerm.Core.Configuration.WorldDefinition.Encoding"/>, when it is not
    /// <c>auto</c> — followed by <paramref name="appOrder"/>, each encoding listed once.
    /// <para>
    /// An override is offered rather than imposed silently, so a server that speaks RFC 2066 agrees to
    /// the thing we are going to use anyway. Preference order is a <em>negotiation</em>: a server that
    /// doesn't offer the head of the list simply lands further down it, and under <c>auto</c> that
    /// landing place is what the session then decodes with.
    /// </para>
    /// </summary>
    public static Encoding[] PreferEncoding(string? name, IEnumerable<string>? appOrder = null)
    {
        var order = ResolveOrder(appOrder);
        return ResolveEncoding(name) is { } preferred
            ? order.Where(e => e.CodePage != preferred.CodePage).Prepend(preferred).ToArray()
            : order;
    }

    /// <summary>
    /// How a world's encoding setting and the app's preference order become session options: the two
    /// halves are derived from the same string and must not be able to disagree, which is why they are
    /// resolved in one place rather than at the call site.
    /// </summary>
    /// <param name="encoding">A world's <c>encoding</c>: <c>auto</c>, or the name of an override.</param>
    /// <param name="appOrder">The app-wide preference order; null for the built-in default.</param>
    public static (Encoding[] Order, Encoding? Override) ResolveWorldEncoding(
        string? encoding, IEnumerable<string>? appOrder = null) =>
        (PreferEncoding(encoding, appOrder), ResolveEncoding(encoding));
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

    // TelnetInterpreter.CurrentEncoding has an internal setter, which CharsetProtocol itself writes
    // through reflection once negotiation settles. We seed it the same way at connect time, for two
    // reasons. It *defaults to Encoding.ASCII*, and that default is not inert: it is handed to
    // CallbackOnByteAsync/CallbackOnSubmitAsync for every byte, and used to decode GMCP, MSDP and MSSP
    // payloads and to encode everything we send — so before negotiation, or on the many MU* servers
    // that never implement RFC 2066 at all, every byte above 0x7F became '?'. And because the seed is
    // an instance nothing else can produce, "has CHARSET settled?" becomes an exact reference
    // comparison rather than a guess about a value that might legitimately be ASCII.
    private static readonly PropertyInfo? InterpreterEncodingProperty =
        typeof(TelnetInterpreter).GetProperty(nameof(TelnetInterpreter.CurrentEncoding)) is { CanWrite: true } p
            ? p
            : null;

    private readonly ITransport _transport;
    private readonly ILogger _logger;
    private readonly TelnetSessionOptions _options;

    private readonly List<byte> _pending = new();

    /// <summary>
    /// The instance seeded into the interpreter at connect time, held so negotiation can be told apart
    /// from the seed by reference. Null when nothing has been seeded (before connect, or if the
    /// library ever stops exposing a writable property to seed).
    /// </summary>
    private Encoding? _seeded;

    /// <summary>What CHARSET's change callback last reported; written on the read loop, read anywhere.</summary>
    private volatile Encoding? _negotiated;

    private SessionEncoding _lastReported;

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

    /// <summary>
    /// The encoding this session is decoding and encoding with now, and why — derived, never cached,
    /// so it is exact at the moment of every decode rather than as of the last event.
    /// <para>
    /// Precedence is override, then negotiation, then the head of the stated preference order. The
    /// last arm is what a server that never speaks CHARSET lands on: the app's first preference,
    /// normally UTF-8. It is emphatically <em>not</em> the interpreter's own <c>Encoding.ASCII</c>
    /// default, which is what used to reach the decode path and mangle every non-ASCII byte.
    /// </para>
    /// </summary>
    public SessionEncoding CurrentEncoding
    {
        get
        {
            if (_options.EncodingOverride is { } forced)
            {
                return new SessionEncoding(forced, EncodingSource.Override);
            }

            return Negotiated is { } negotiated
                ? new SessionEncoding(negotiated, EncodingSource.Negotiated)
                : new SessionEncoding(Fallback, EncodingSource.Assumed);
        }
    }

    /// <summary>
    /// What CHARSET has settled on, or null while it has not.
    /// <para>
    /// Two sources, because neither alone is both prompt and complete. <see cref="_negotiated"/> is
    /// what CHARSET's own callback handed us, and it is authoritative the instant negotiation
    /// completes — the library writes the interpreter's own <c>CurrentEncoding</c> <em>after</em> the
    /// callback and, as it turns out, after the read batch has already returned, so waiting for that
    /// would decode a whole batch of text with the old encoding. The interpreter's encoding is the
    /// second source and covers the direction the callback is not raised for (a server accepting the
    /// list we offered); the seed we planted is an instance nothing else can produce, so reference
    /// inequality there is exactly "something replaced it".
    /// </para>
    /// </summary>
    private Encoding? Negotiated
    {
        get
        {
            if (_negotiated is { } signalled)
            {
                return signalled;
            }

            return _interpreter is { } interpreter && _seeded is not null &&
                   !ReferenceEquals(interpreter.CurrentEncoding, _seeded)
                ? interpreter.CurrentEncoding
                : null;
        }
    }

    /// <summary>The head of the stated preference order — what is assumed when nothing negotiates.</summary>
    private Encoding Fallback =>
        _options.CharsetOrder is [var first, ..] ? first : Encoding.UTF8;

    public event EventHandler<TelnetOutputEventArgs>? OutputReceived;

    /// <summary>Raised when <see cref="CurrentEncoding"/> changes — in practice, when CHARSET settles.</summary>
    public event EventHandler<SessionEncodingEventArgs>? EncodingChanged;

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
            SeedInterpreterEncoding(_interpreter);
            WireCharsetCallback(_interpreter);
        }
        catch
        {
            // Building/wiring the interpreter failed after the socket opened; close it so we
            // don't leak a half-open connection, and stay in the not-connected state.
            _interpreter = null;
            _seeded = null;
            _negotiated = null;
            await _transport.CloseAsync().ConfigureAwait(false);
            throw;
        }

        _lastReported = CurrentEncoding;
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readLoop = Task.Run(() => ReadLoopAsync(_loopCts.Token), CancellationToken.None);
    }

    private Task<TelnetInterpreter> BuildInterpreterAsync()
    {
        var builder = new TelnetInterpreterBuilder()
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
                onMXPEnabled: static () => ValueTask.CompletedTask);

        // An idle keepalive, when the world asks for one: IAC NOP after the configured silence, so a
        // NAT or load balancer doesn't evict a connection that is merely quiet. It is filler traffic,
        // not a liveness probe — it proves our write succeeded, not that the server answered.
        if (_options.KeepaliveInterval is { } interval)
        {
            builder = builder.WithKeepAlive(interval);
        }

        return builder.BuildAsync();
    }

    /// <summary>
    /// Replaces the interpreter's <see cref="Encoding.ASCII"/> default with what this session would
    /// otherwise assume, so nothing decodes as ASCII merely because negotiation hasn't happened yet —
    /// including the GMCP/MSDP/MSSP payloads and outbound bytes the library encodes for itself. The
    /// library overwrites this the moment CHARSET settles.
    /// </summary>
    private void SeedInterpreterEncoding(TelnetInterpreter interpreter)
    {
        if (InterpreterEncodingProperty is null)
        {
            _logger.LogWarning(
                "TelnetInterpreter.CurrentEncoding is not writable; pre-negotiation payloads will decode as ASCII.");
            return;
        }

        // The override, when there is one, so the library's own encoding matches what this session
        // decodes with — otherwise a world pinned to Latin-1 would still send UTF-8 bytes.
        //
        // A *clone*, because the seed doubles as the marker for "nothing has negotiated yet" and that
        // test is reference identity. The order we state holds the platform provider's own instances
        // (it has to — see DefaultOrder), so seeding one of those directly would make a successful
        // negotiation of that same charset indistinguishable from the seed, and UTF-8 agreed by the
        // server would be reported as merely assumed. A clone behaves identically and is nobody else's.
        var seed = (Encoding)(_options.EncodingOverride ?? Fallback).Clone();
        InterpreterEncodingProperty.SetValue(interpreter, seed);
        _seeded = seed;
    }

    /// <summary>
    /// Subscribes to CHARSET's own change callback, which fires <em>inside</em> the batch that
    /// completed the negotiation — so a line arriving after the handshake in the same read decodes
    /// with the newly agreed encoding rather than one batch late.
    /// <para>
    /// It only covers one of RFC 2066's two directions: TelnetNegotiationCore signals it when the
    /// server offers a list and we choose from it, but not when we offer and the server accepts, which
    /// updates the interpreter silently. <see cref="ReportEncodingIfChanged"/> after each read batch is
    /// the second arm that catches that one. (An <c>OnCharsetChange</c> on the accepted path is a good
    /// upstream PR; until then, both arms are needed and neither is redundant.)
    /// </para>
    /// </summary>
    private void WireCharsetCallback(TelnetInterpreter interpreter) =>
        interpreter.PluginManager?.GetPlugin<CharsetProtocol>()?.OnCharsetChange(encoding =>
        {
            _negotiated = encoding;
            ReportEncodingIfChanged();
            return ValueTask.CompletedTask;
        });

    private void ReportEncodingIfChanged()
    {
        var current = CurrentEncoding;
        if (current == _lastReported)
        {
            return;
        }

        _lastReported = current;
        _logger.LogInformation(
            "Text encoding is now {Encoding} ({Source}).", current.Name, current.Source);
        EncodingChanged?.Invoke(this, new SessionEncodingEventArgs(current));
    }

    private ValueTask WriteToTransportAsync(ReadOnlyMemory<byte> data) =>
        _transport.SendAsync(data);

    private ValueTask OnByteAsync(byte value, Encoding encoding)
    {
        // The encoding the interpreter offers here is deliberately ignored: it is the negotiated one,
        // which CurrentEncoding already consults and an override deliberately outranks.
        _pending.Add(value);
        return ValueTask.CompletedTask;
    }

    private ValueTask OnSubmitAsync(byte[] bytes, Encoding? encoding, TelnetInterpreter interpreter)
    {
        // A newline-terminated line. Prefer the interpreter's own line bytes for the content, but
        // decode them with what this session has decided is in force — the interpreter's own encoding
        // is the negotiated one, and an override outranks that.
        var text = CurrentEncoding.Encoding.GetString(bytes);
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

        var text = CurrentEncoding.Encoding.GetString(_pending.ToArray());
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

                // The second arm of charset observation: the direction whose callback the library
                // does not raise (see WireCharsetCallback). Cheap — a reference comparison — and
                // idempotent, so the arm that already fired mid-batch makes this a no-op.
                ReportEncodingIfChanged();
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
        var bytes = CurrentEncoding.Encoding.GetBytes(text + "\r\n");
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

        // Dispose and clear the interpreter here (not in DisposeAsync, which calls this first
        // and would then see it already null) so no interpreter leaks per disconnect/reconnect,
        // and so the "not connected" send guards observe the disconnected state.
        var interpreter = _interpreter;
        _interpreter = null;
        _seeded = null;
        _negotiated = null;
        if (interpreter is not null)
        {
            await interpreter.DisposeAsync().ConfigureAwait(false);
        }

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
        // DisconnectAsync disposes and clears the interpreter.
        await DisconnectAsync().ConfigureAwait(false);
        _loopCts?.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
