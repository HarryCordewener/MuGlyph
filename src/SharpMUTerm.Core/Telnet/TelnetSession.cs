using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpMUTerm.Core.Telnet.Mssp;
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
    /// What this client calls itself over TTYPE/MTTS, most specific first, or null to leave
    /// TelnetNegotiationCore's own list in place.
    /// <para>
    /// The MTTS convention is that a server may ask for the terminal type repeatedly and gets a
    /// different answer each time: the client's <em>name</em> first, then a terminal type, then
    /// <c>MTTS &lt;bitvector&gt;</c>. The library's default answers <c>TNC</c> to the first — its own
    /// name, not ours — so every server this client has ever connected to has logged the wrong
    /// program. <see cref="SharpMUTermTerminalTypes"/> is the honest answer for the client; a tool that
    /// is not the client (a crawler) states what it is instead.
    /// </para>
    /// </summary>
    public IReadOnlyList<string>? TerminalTypes { get; init; } = SharpMUTermTerminalTypes;

    /// <summary>
    /// What the interactive client announces: its own name, the terminal class it renders as, and an
    /// MTTS bit vector claiming only what it actually does.
    /// <para>
    /// <b>2333</b> = ANSI (1) + UTF-8 (4) + 256 colours (8) + mouse tracking (16) + true colour (256)
    /// + SSL (2048). Every one of those is implemented: SGR through <c>AnsiParser</c>, UTF-8 through
    /// CHARSET negotiation, mouse through the pane drag/wheel/click routing, TLS through
    /// <c>TcpTransport</c>.
    /// </para>
    /// <para>
    /// Not <b>3853</b>, which the library claimed on our behalf and which is wrong in both directions:
    /// it asserts <b>MNES (512)</b> — NEW-ENVIRON is not registered by this client at all — and
    /// <b>MSLP (1024)</b>, which is not implemented anywhere in the stack, while omitting the mouse
    /// tracking that is. A terminal-type string is a claim a server acts on; MTTS exists so a server
    /// can decide what to send us, and over-claiming makes it send what we cannot render.
    /// </para>
    /// <para>
    /// Deliberately absent: VT100 (2), because this is a line-oriented append-only view with no cursor
    /// addressing or erase; OSC colour palette (32), which we never emit; screen reader (64) and
    /// proxy (128), neither of which exist here.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> SharpMUTermTerminalTypes { get; } =
        ["SHARPMUTERM", "XTERM", "MTTS 2333"];

    /// <summary>
    /// Telnet options to request outright on connect, by sending <c>IAC DO &lt;option&gt;</c>, rather
    /// than waiting for the server to offer them.
    /// <para>
    /// <b>This exists because waiting does not work.</b> The MSSP specification says the server "should
    /// send IAC WILL MSSP" when a client connects, and TelnetNegotiationCore is built on that reading:
    /// its whole opening negotiation is <c>IAC WILL NAWS</c> and nothing else, so MSSP is only ever
    /// reached if the server volunteers it. A great many servers that fully support MSSP do not
    /// volunteer anything — they answer <c>IAC DO MSSP</c> and are otherwise silent, which is why the
    /// protocol's own reference client (TinTin++'s <c>#session mssp</c>) asks rather than listens. A
    /// client that only listens simply does not see those servers, and its INFO screen reports them as
    /// publishing no MSSP — which is a different and wronger claim than "we never asked".
    /// </para>
    /// <para>
    /// This is negotiation, not traffic: <c>IAC DO</c> is the client half of the option handshake, it
    /// costs three bytes once per connection, and a server that does not implement the option answers
    /// <c>IAC WONT</c> and is no worse off.
    /// </para>
    /// </summary>
    public IReadOnlyList<byte> RequestOptions { get; init; } = [];

    /// <summary>The MSSP telnet option, 70.</summary>
    public const byte MsspOption = 70;

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
            ApplyTerminalTypes(_interpreter);
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

        // After the read loop is running, so a server that answers instantly is heard.
        await RequestOptionsAsync(cancellationToken).ConfigureAwait(false);
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
    /// Sends <c>IAC DO &lt;option&gt;</c> for each of <see cref="TelnetSessionOptions.RequestOptions"/>.
    /// <para>
    /// Written straight to the transport rather than through <see cref="SendAsync"/>, because that
    /// escapes <c>IAC</c> as data — which is exactly right for a command line and exactly wrong for a
    /// negotiation. This is the same door the interpreter's own negotiation output goes through.
    /// </para>
    /// </summary>
    private async ValueTask RequestOptionsAsync(CancellationToken cancellationToken)
    {
        foreach (var option in _options.RequestOptions)
        {
            _logger.LogDebug("Requesting telnet option {Option}.", option);
            await _transport.SendAsync(new byte[] { 255, 253, option }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tells the terminal-type plugin what to answer with, when this session has an opinion.
    /// <para>
    /// Silent when the plugin is not registered (shouldn't happen with <see cref="AddDefaultMUDProtocols"/>):
    /// being unable to state a name is a cosmetic loss for the client, so it is logged rather than
    /// thrown — a session that refuses to connect because it could not introduce itself would be a
    /// worse outcome than one that connects under the wrong name. A crawler, which has a duty to
    /// identify itself, checks <see cref="TerminalTypesApplied"/> instead of assuming.
    /// </para>
    /// </summary>
    private void ApplyTerminalTypes(TelnetInterpreter interpreter)
    {
        if (_options.TerminalTypes is not { Count: > 0 } types)
        {
            return;
        }

        if (interpreter.PluginManager?.GetPlugin<TerminalTypeProtocol>() is not { } plugin)
        {
            _logger.LogWarning(
                "Terminal types are not settable on this TelnetNegotiationCore build; the server will be told "
                + "the library's name instead of ours.");
            return;
        }

        plugin.WithTerminalTypes(types.ToArray());
        TerminalTypesApplied = true;
    }

    /// <summary>
    /// True once this session has successfully told the telnet layer what to answer TTYPE with. False
    /// when no types were configured, before connecting, or if the library's shape changed under us.
    /// </summary>
    public bool TerminalTypesApplied { get; private set; }

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

    /// <summary>
    /// The library's MSSP callback: one report per <c>IAC SB MSSP … IAC SE</c>, carrying
    /// <c>MSSPConfig.Variables</c> — every variable the server sent, with every value of every array,
    /// in wire order. <see cref="MsspData.From"/> turns that into the shape this application reads.
    /// </summary>
    private ValueTask OnMsspAsync(MSSPConfig config)
    {
        var report = MsspData.From(config.Variables);
        _logger.LogInformation(
            "MSSP: {Count} variables from {Name}.", report.Count, report.Name ?? "an unnamed server");
        MsspReceived?.Invoke(this, new MsspReceivedEventArgs(report));
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

    /// <summary>
    /// Sends one line to the world.
    /// <para>
    /// <b>The terminator is the library's, not ours.</b> <c>TelnetInterpreter.SendAsync</c> is a
    /// <em>line</em> send: it appends the line ending itself. Adding <c>\r\n</c> here as well put
    /// <c>&lt;text&gt;\r\n\r\n</c> on the wire for every line this client has ever sent — a spurious
    /// empty command after each real one. On a MU* connect screen that empty line is a command like any
    /// other, and the servers that redisplay their banner for an unrecognised one did so on every single
    /// connect: the doubled welcome screen in the transcripts is this bug, not the server's.
    /// </para>
    /// <para>
    /// It is bytes rather than a string because the encoding is this session's decision and not the
    /// interpreter's (see <see cref="CurrentEncoding"/>), and it goes through <see cref="SendAsync"/> so
    /// the library still escapes a literal <c>IAC</c> in user text as data.
    /// </para>
    /// </summary>
    public ValueTask SendLineAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = CurrentEncoding.Encoding.GetBytes(text);
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
        // SendNAWS was removed in 2.8.1. The NAWS plugin now owns the send, reports the full
        // RFC 1073 unsigned-16 range, and takes int to avoid the caller having to cast.
        return interpreter.PluginManager?.GetPlugin<NAWSProtocol>()?.SendWindowSizeAsync(width, height)
            ?? ValueTask.CompletedTask;
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
