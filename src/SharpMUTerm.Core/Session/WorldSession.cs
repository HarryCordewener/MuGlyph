using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Logging;
using SharpMUTerm.Core.Protocols;
using SharpMUTerm.Core.Telnet;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Transport;

namespace SharpMUTerm.Core.Session;

/// <summary>
/// The runtime for a single connected world. Orchestrates the full pipeline:
/// transport → telnet → ANSI parse → trigger engine → scrollback / logging, and the outbound
/// path: user input → alias expansion → local echo → send. UI-agnostic; the view binds to its
/// events and its <see cref="Scrollback"/>.
/// </summary>
public sealed class WorldSession : IAsyncDisposable
{
    private static readonly TextStyle EchoStyle =
        new(TerminalColor.FromIndex(11), TerminalColor.Default, TextAttributes.None);

    private static readonly TextStyle SystemStyle =
        new(TerminalColor.FromIndex(6), TerminalColor.Default, TextAttributes.Italic);

    private readonly Func<ConnectionOptions, ITelnetSession> _sessionFactory;
    private readonly ILineParser _parser;
    private readonly EmojiSubstitutor? _emoji;
    private ILogSink? _log;
    private readonly TextSettings? _text;
    private readonly InputSettings? _input;

    /// <summary>
    /// The app-wide CHARSET preference order (<see cref="AppConfiguration.CharsetOrder"/>), held by
    /// reference like <see cref="_text"/> and <see cref="_input"/>. Null means the built-in default
    /// order. This is what a world on <c>auto</c> states as its preference — before it was threaded
    /// through here the app-wide setting existed in the config and was read by nothing at all.
    /// </summary>
    private readonly IReadOnlyList<string>? _charsetOrder;
    private TimerDefinition[] _timers = Array.Empty<TimerDefinition>();
    private readonly List<IDisposable> _timerHandles = new();
    private ITelnetSession? _telnet;
    private ILogger _logger = NullLogger.Instance;

    /// <summary>
    /// Creates a session for a world and (optionally) the character being connected as. Automation
    /// is composed from the union of <paramref name="triggerSets"/> — resolve them for a character
    /// via <see cref="AppConfiguration.ResolveTriggerSets"/>. A null character yields an anonymous
    /// session (e.g. an ad-hoc command-line connection) with no auto-login.
    /// <para>
    /// <paramref name="triggerSets"/> is not a permanent choice: <see cref="ReloadAutomation"/> re-points
    /// a live session at whatever the configuration says now, which is what makes assigning a set or
    /// adding a rule mid-connection take effect. Without it a session opened before the user finished
    /// configuring it ran an empty engine for the rest of its life.
    /// </para>
    /// <para>
    /// <paramref name="text"/> and <paramref name="input"/> are the app-wide rendering and input
    /// preferences the F7/F8 screens edit. They are held by reference and read <em>per line</em>, not
    /// copied at construction, because those screens edit the live configuration object in place —
    /// copying them here is exactly how a checkbox ends up needing a restart to mean anything. Null
    /// (the default, and what a Core test that doesn't care passes) means "the built-in defaults".
    /// </para>
    /// </summary>
    public WorldSession(
        WorldDefinition world,
        CharacterDefinition? character = null,
        IReadOnlyList<TriggerSet>? triggerSets = null,
        Func<ConnectionOptions, ITelnetSession>? sessionFactory = null,
        ILogSink? log = null,
        int scrollbackCapacity = ScrollbackBuffer.DefaultCapacity,
        TextSettings? text = null,
        InputSettings? input = null,
        ScrollbackSpillOptions? spill = null,
        IReadOnlyList<string>? charsetOrder = null)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        Character = character;
        _sessionFactory = sessionFactory ?? DefaultSessionFactory;
        _log = log;
        _text = text;
        _input = input;
        _charsetOrder = charsetOrder;
        _parser = CreateParser(world.ContentFormat);
        _emoji = world.Emoji.Enabled
            ? new EmojiSubstitutor(world.Emoji.Emoticons, world.Emoji.Shortcodes)
            : null;
        Scrollback = new ScrollbackBuffer(scrollbackCapacity, CreateSpill(spill ?? new ScrollbackSpillOptions()));

        Triggers = new TriggerEngine();
        Aliases = new AliasEngine();
        Macros = new MacroEngine();
        ReloadAutomation(triggerSets ?? Array.Empty<TriggerSet>());
    }

    /// <summary>
    /// Re-points this session's automation at <paramref name="triggerSets"/> — the sets the configuration
    /// resolves for its character <em>now</em>. Rules added at runtime by the scripting layer are kept;
    /// see <see cref="TriggerEngine.ReplaceConfigured"/>.
    /// <para>
    /// This is the answer to the bug it was written for: the engines used to be handed a list at
    /// construction, so a session opened before its character had a trigger set assigned — or before a
    /// set gained the rule the user was in the middle of writing — went on matching nothing until it was
    /// reconnected, with no way to tell from the client that this was what had happened. Membership is
    /// now as live as a rule's own <see cref="Trigger.Pattern"/> already was.
    /// </para>
    /// <para>
    /// Running <see cref="Scheduler"/> timers are deliberately <em>not</em> re-armed. A timer's period is
    /// baked into its schedule and re-periodising one means tearing it down, which resets every other
    /// timer's phase — and this method runs after every committed settings change, so doing that here
    /// would reset the phase of every timer on the session each time a checkbox was ticked. The new
    /// definitions apply at the next connect, which is what the F6 screen says. Enabled/command edits
    /// were already live, because <see cref="StartTimers"/> reads those inside the callback.
    /// </para>
    /// </summary>
    public void ReloadAutomation(IReadOnlyList<TriggerSet> triggerSets)
    {
        ArgumentNullException.ThrowIfNull(triggerSets);

        Triggers.ReplaceConfigured(triggerSets.SelectMany(s => s.Triggers));
        Aliases.ReplaceConfigured(triggerSets.SelectMany(s => s.Aliases));
        Macros.ReplaceConfigured(triggerSets.SelectMany(s => s.Macros));
        _timers = triggerSets.SelectMany(s => s.Timers).ToArray();
        ScriptFiles = triggerSets.SelectMany(s => s.ScriptFiles)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The disk cache behind this session's scrollback, or null when it is memory-only. Created
    /// lazily — the directory does not exist until a line is actually evicted from memory — and
    /// deleted when the session is disposed. See <see cref="FileScrollbackSpill"/> for why this is a
    /// cache and not a transcript: the session <em>log</em> is <see cref="AttachLog"/>'s business and
    /// stays opt-in.
    /// </summary>
    private IScrollbackSpill? CreateSpill(ScrollbackSpillOptions options) =>
        options.Enabled ? new FileScrollbackSpill(options, SessionKey) : null;

    private static ILineParser CreateParser(ContentFormat format) => format switch
    {
        ContentFormat.Mxp => new MxpParser(),
        ContentFormat.Pueblo => new PuebloParser(),
        _ => new AnsiParser(),
    };

    /// <summary>
    /// Where this session's diagnostics go — its own, and the telnet stack's, which
    /// <see cref="DefaultSessionFactory"/> hands straight to <see cref="TelnetSession"/>. Defaults to
    /// <see cref="NullLogger.Instance"/>, so Core stays free of any logging implementation; the app sets
    /// it (via <see cref="SessionManager.Logger"/>) to its client diagnostics pipeline.
    /// <para>
    /// Assigning it also re-points the <see cref="Scrollback"/> buffer (and its spill cache), which is
    /// constructed before the app gets a chance to set this — otherwise a disk fault in the scrollback
    /// cache would report itself to <c>NullLogger</c>, i.e. nowhere.
    /// </para>
    /// </summary>
    public ILogger Logger
    {
        get => _logger;
        set
        {
            _logger = value ?? NullLogger.Instance;
            Scrollback.Logger = _logger;
        }
    }

    public WorldDefinition World { get; }

    /// <summary>The character this session connects as, or null for an anonymous connection.</summary>
    public CharacterDefinition? Character { get; }

    /// <summary>
    /// Stable identity for this session: <c>world.character</c>, or just the world name when
    /// connecting anonymously. Used to key open sessions in the <see cref="SessionManager"/>.
    /// </summary>
    public string SessionKey => Character is null ? World.Name : $"{World.Name}.{Character.Name}";

    /// <summary>Lua script files contributed by the active trigger sets, de-duplicated.</summary>
    public IReadOnlyList<string> ScriptFiles { get; private set; } = Array.Empty<string>();

    public ScrollbackBuffer Scrollback { get; }

    public TriggerEngine Triggers { get; }

    public AliasEngine Aliases { get; }

    public MacroEngine Macros { get; }

    public IntervalScheduler Scheduler { get; } = new();

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    /// <summary>The most recent prompt, or null if none is active.</summary>
    public StyledLine? CurrentPrompt { get; private set; }

    public event EventHandler<StyledLine>? LinePrinted;

    public event EventHandler<StyledLine?>? PromptChanged;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public event EventHandler<GmcpMessageEventArgs>? GmcpReceived;

    public event EventHandler<MsdpMessageEventArgs>? MsdpReceived;

    public event EventHandler<MsspReceivedEventArgs>? MsspReceived;

    public event EventHandler<SpawnLineEventArgs>? SpawnLine;

    /// <summary>Raised when the encoding in force changes — see <see cref="CurrentEncoding"/>.</summary>
    public event EventHandler<SessionEncodingEventArgs>? EncodingChanged;

    /// <summary>Raised for each trigger-requested script callback (consumed by the scripting layer).</summary>
    public event EventHandler<TriggerScriptInvocation>? TriggerScriptRequested;

    public bool IsConnected => _telnet?.IsConnected == true;

    /// <summary>
    /// The encoding this session's text is actually going over the wire in, and why — negotiated by
    /// CHARSET, pinned by the world, or assumed because nothing negotiated. Null when there is no
    /// telnet session to ask, which is the only honest answer before a connect: <see cref="World"/>'s
    /// <see cref="WorldDefinition.Encoding"/> is a <em>preference</em>, and reporting it as though it
    /// were in force is what this property exists to stop.
    /// </summary>
    public SessionEncoding? CurrentEncoding => _telnet?.CurrentEncoding;

    /// <summary>Whether this session is writing its output to a log right now.</summary>
    public bool IsLogging => _log is not null;

    /// <summary>
    /// Starts logging this session's output to <paramref name="log"/>, mid-connection. Whatever it was
    /// logging to is flushed and closed first: a session writes to one log, and swapping a sink in
    /// without closing the old one leaves a handle open on a file the user believes they stopped.
    /// </summary>
    public void AttachLog(ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(log);
        DetachLog();
        _log = log;
    }

    /// <summary>
    /// Stops logging, flushing and closing the sink. Does nothing when there is no log, so "stop" is
    /// safe to call on a session that never started one.
    /// </summary>
    public void DetachLog()
    {
        var log = _log;
        _log = null;
        if (log is null)
        {
            return;
        }

        log.Flush();
        log.Dispose();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is ConnectionState.Connecting or ConnectionState.Connected)
        {
            return;
        }

        // A prior faulted/disconnected session may still be referenced; dispose it before
        // reconnecting so its read loop and transport are released (its events won't fire again).
        if (_telnet is not null)
        {
            await _telnet.DisposeAsync().ConfigureAwait(false);
            _telnet = null;
        }

        SetState(ConnectionState.Connecting, null);
        PrintSystem($"*** Connecting to {World.Host}:{World.Port}...");

        var telnet = _sessionFactory(World.ToConnectionOptions());
        _telnet = telnet;
        telnet.OutputReceived += OnOutputReceived;
        telnet.GmcpReceived += (_, e) => GmcpReceived?.Invoke(this, e);
        telnet.MsdpReceived += (_, e) => MsdpReceived?.Invoke(this, e);
        telnet.MsspReceived += (_, e) => MsspReceived?.Invoke(this, e);
        telnet.EncodingChanged += OnEncodingChanged;
        telnet.Disconnected += OnDisconnected;

        try
        {
            await telnet.ConnectAsync(cancellationToken).ConfigureAwait(false);
            SetState(ConnectionState.Connected, null);
            PrintSystem("*** Connected.");
            await SendLoginAsync(cancellationToken).ConfigureAwait(false);
            StartTimers();
        }
        catch (Exception ex)
        {
            SetState(ConnectionState.Faulted, ex);
            PrintSystem($"*** Connection failed: {ex.Message}");
            Logger.LogWarning(ex, "Connect to {Host}:{Port} failed", World.Host, World.Port);
            throw;
        }
    }

    private void OnOutputReceived(object? sender, TelnetOutputEventArgs e)
    {
        if (e.IsPrompt)
        {
            _parser.Feed(e.Text);
            var raw = _parser.Flush() ?? StyledLine.Empty;
            var prompt = ApplyEmoji(_text?.StripIncomingColour == true ? StyledText.StripColour(raw) : raw);
            CurrentPrompt = prompt;
            PromptChanged?.Invoke(this, prompt);
            return;
        }

        foreach (var completed in _parser.Feed(e.Text))
        {
            ProcessOutputLine(completed);
        }

        var tail = _parser.Flush();
        if (tail is not null)
        {
            ProcessOutputLine(tail);
        }
    }

    private void ProcessOutputLine(StyledLine line)
    {
        // Colour is stripped from what the *server* sent, before the triggers run: a highlight rule
        // and this client's own system/echo lines are not "incoming ANSI colour" and keep theirs.
        if (_text?.StripIncomingColour == true)
        {
            line = StyledText.StripColour(line);
        }

        var result = Triggers.Process(line);

        foreach (var invocation in result.ScriptInvocations)
        {
            TriggerScriptRequested?.Invoke(this, invocation);
        }

        foreach (var target in result.SpawnTargets)
        {
            SpawnLine?.Invoke(this, new SpawnLineEventArgs(target, result.Line));
        }

        foreach (var response in result.Responses)
        {
            _ = SendRawAsync(response);
        }

        if (!result.Suppress)
        {
            Print(ApplyEmoji(result.Line));
        }
    }

    /// <summary>
    /// Substitutes emoji across the whole line when enabled for this world (preserving word
    /// boundaries across span seams and each span's style/interaction); a no-op otherwise.
    /// <para>
    /// The world opts in and says <em>which</em> substitutions it wants
    /// (<see cref="WorldDefinition.Emoji"/>); F7's <c>emoji substitution</c> is the app-wide off
    /// switch over the top of it, read here rather than at construction so unticking it stops the
    /// next line rather than the next session.
    /// </para>
    /// </summary>
    private StyledLine ApplyEmoji(StyledLine line) =>
        _emoji is null || _text?.EmojiSubstitution == false ? line : _emoji.ApplyToLine(line);

    /// <summary>Handles a line of user input: alias expansion, local echo, and send.</summary>
    public async Task SendUserInputAsync(string input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var expansion = Aliases.Expand(input);

        // Two switches, and both have to be on: the world's (some servers echo for you, some don't)
        // and F8's app-wide one. The app-wide one is read per line, not captured, so unticking it
        // stops the echo on the next command rather than the next session.
        if (World.LocalEcho && _input?.LocalEcho != false)
        {
            Print(StyledLine.FromText(input, EchoStyle));
        }

        if (expansion.Matched)
        {
            foreach (var command in expansion.Commands)
            {
                await SendRawAsync(command, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await SendRawAsync(input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the character's auto-login line (when <see cref="CharacterDefinition.AutoLogin"/> is
    /// set) followed by its semicolon-separated <see cref="CharacterDefinition.OnConnect"/> commands.
    /// A no-op for anonymous sessions.
    /// <para>
    /// This is deliberately <see cref="SendRawAsync"/> and not <see cref="SendUserInputAsync"/>: raw
    /// goes straight to telnet with no local echo and no transcript write, so the one line in this app
    /// that carries a password never reaches the output pane or the session log. Typing the same line by
    /// hand <em>is</em> echoed and logged — which is the whole reason the password is a field and the
    /// login line a template (see <see cref="ConnectStringTemplate"/>).
    /// </para>
    /// </summary>
    private async Task SendLoginAsync(CancellationToken cancellationToken)
    {
        var character = Character;
        if (character is null)
        {
            return;
        }

        if (character.AutoLogin)
        {
            // A template can legitimately resolve to nothing — `%PASSWORD%` alone, with no password set
            // — and a bare newline at a login prompt is not "no command", it is a command some servers
            // answer to. Send nothing rather than something meaningless.
            var login = character.ResolveConnectString();
            if (!string.IsNullOrWhiteSpace(login))
            {
                await SendRawAsync(login, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var command in SplitCommands(character.OnConnect))
        {
            await SendRawAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Realises the active trigger sets' <see cref="TimerDefinition"/>s on the session's
    /// <see cref="Scheduler"/>. Called once the connection is up, because a timer's whole job is to
    /// send something, and cancelled again on disconnect (<see cref="StopTimers"/>) so a dropped
    /// session doesn't keep firing into a closed socket.
    /// <para>
    /// <see cref="TimerDefinition.Enabled"/> and <see cref="TimerDefinition.Command"/> are read
    /// <em>inside</em> the callback rather than here, so flipping the F6 checkbox or retyping the
    /// command takes effect on the next firing. The interval and one-shot flag are baked into the
    /// schedule itself and so apply from the next connect — a running <see cref="Timer"/> cannot be
    /// re-periodised without being torn down, and tearing one down mid-cycle would silently reset
    /// every other timer's phase.
    /// </para>
    /// </summary>
    private void StartTimers()
    {
        StopTimers();

        foreach (var timer in _timers)
        {
            if (timer.IntervalSeconds <= 0)
            {
                continue;
            }

            var definition = timer;
            var interval = TimeSpan.FromSeconds(definition.IntervalSeconds);
            void Fire()
            {
                if (!definition.Enabled || string.IsNullOrWhiteSpace(definition.Command))
                {
                    return;
                }

                _ = SendRawAsync(definition.Command);
            }

            _timerHandles.Add(definition.OneShot
                ? Scheduler.After(interval, Fire, definition.Name)
                : Scheduler.Every(interval, Fire, definition.Name));
        }
    }

    /// <summary>Cancels every schedule <see cref="StartTimers"/> created, leaving script timers alone.</summary>
    private void StopTimers()
    {
        foreach (var handle in _timerHandles)
        {
            handle.Dispose();
        }

        _timerHandles.Clear();
    }

    /// <summary>Splits a semicolon-separated command string, trimming and dropping blank segments.</summary>
    private static IEnumerable<string> SplitCommands(string? commands)
    {
        if (string.IsNullOrWhiteSpace(commands))
        {
            yield break;
        }

        foreach (var part in commands.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return part;
        }
    }

    /// <summary>Sends a command verbatim (no alias expansion, no echo).</summary>
    public async Task SendRawAsync(string command, CancellationToken cancellationToken = default)
    {
        var telnet = _telnet;
        if (telnet is null || !telnet.IsConnected)
        {
            return;
        }

        await telnet.SendLineAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves a key to a macro and sends its command. Returns the command sent, or null.</summary>
    public async Task<string?> HandleKeyAsync(string keyDescriptor, CancellationToken cancellationToken = default)
    {
        var macro = Macros.Resolve(keyDescriptor);
        if (macro is null || string.IsNullOrEmpty(macro.Command))
        {
            return null;
        }

        await SendRawAsync(macro.Command, cancellationToken).ConfigureAwait(false);
        return macro.Command;
    }

    public ValueTask SetWindowSizeAsync(int width, int height) =>
        _telnet?.SetWindowSizeAsync(width, height) ?? ValueTask.CompletedTask;

    /// <summary>Appends a client-generated informational line.</summary>
    public void PrintSystem(string text)
    {
        var line = StyledLine.FromText(text, SystemStyle);
        Scrollback.Append(line);
        _log?.WriteSystem(text);
        LinePrinted?.Invoke(this, line);
    }

    private void Print(StyledLine line)
    {
        Scrollback.Append(line);
        _log?.WriteLine(line);
        LinePrinted?.Invoke(this, line);
    }

    /// <summary>
    /// A charset change gets one line in the client message log and nothing else.
    /// <para>
    /// Not <c>PrintSystem</c>: that writes into the character's transcript, which someone keeps, and a
    /// protocol detail is not part of what was said in the room. And not a status-row notice either —
    /// a notice displaces the row for something the user cannot act on, and the overwhelmingly common
    /// case is a successful negotiation at connect time, which is not news. The log is where you look
    /// when the text is wrong, and the status row already says what is in force.
    /// </para>
    /// </summary>
    private void OnEncodingChanged(object? sender, SessionEncodingEventArgs e)
    {
        Logger.LogInformation(
            "{World}: text encoding is now {Encoding} ({Source}).",
            World.Name,
            e.Encoding.Name,
            e.Encoding.Source);
        EncodingChanged?.Invoke(this, e);
    }

    private void OnDisconnected(object? sender, SessionDisconnectedEventArgs e)
    {
        StopTimers();
        SetState(e.IsClean ? ConnectionState.Disconnected : ConnectionState.Faulted, e.Error);
        PrintSystem(e.IsClean ? "*** Disconnected." : $"*** Connection lost: {e.Error?.Message}");
    }

    public async Task DisconnectAsync()
    {
        if (_telnet is not null)
        {
            await _telnet.DisconnectAsync().ConfigureAwait(false);
        }
    }

    private void SetState(ConnectionState state, Exception? error)
    {
        State = state;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, error));
    }

    /// <summary>
    /// The real transport + telnet stack for this world.
    /// <para>
    /// The world's <see cref="WorldDefinition.Encoding"/> is an <em>override</em> when it names one:
    /// stated at the head of the CHARSET preference order so a cooperative server agrees to it, and
    /// used for decoding whatever the server says. <c>auto</c> — the default — states the app-wide
    /// order instead and uses whatever negotiation settles on. Instance-level (not static) because the
    /// factory has to see the world to do either.
    /// </para>
    /// <para>
    /// The session's <see cref="Logger"/> goes in with it. It used to be left out, so every diagnostic
    /// TelnetNegotiationCore produces — negotiation traces, option state, keepalive and MCCP/GMCP/MSDP
    /// errors — went to <c>NullLogger</c> and was discarded. That is the detail you want when a MU*
    /// misbehaves, and it now reaches the client's diagnostics pipeline.
    /// </para>
    /// </summary>
    private ITelnetSession DefaultSessionFactory(ConnectionOptions options)
    {
        var (order, encodingOverride) =
            TelnetSessionOptions.ResolveWorldEncoding(World.Encoding, _charsetOrder);

        return new TelnetSession(
            new TcpTransport(options),
            Logger,
            options: new TelnetSessionOptions
            {
                CharsetOrder = order,
                EncodingOverride = encodingOverride,
                KeepaliveInterval = TelnetSessionOptions.ResolveKeepalive(World.KeepaliveSeconds),
            });
    }

    public async ValueTask DisposeAsync()
    {
        StopTimers();
        Scheduler.Dispose();
        DetachLog(); // flushes what the session logged before closing the file behind it
        Scrollback.Dispose(); // deletes this session's scrollback spill cache; the log above is kept
        if (_telnet is not null)
        {
            await _telnet.DisposeAsync().ConfigureAwait(false);
            _telnet = null;
        }
    }
}
