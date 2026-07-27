using MuClient.Core.Automation;
using MuClient.Core.Configuration;
using MuClient.Core.Logging;
using MuClient.Core.Protocols;
using MuClient.Core.Telnet;
using MuClient.Core.Text;
using MuClient.Core.Transport;

namespace MuClient.Core.Session;

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
    private readonly ILogSink? _log;
    private ITelnetSession? _telnet;

    public WorldSession(
        WorldDefinition world,
        Func<ConnectionOptions, ITelnetSession>? sessionFactory = null,
        ILogSink? log = null,
        int scrollbackCapacity = 20_000)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        _sessionFactory = sessionFactory ?? DefaultSessionFactory;
        _log = log;
        _parser = CreateParser(world.ContentFormat);
        _emoji = world.Emoji.Enabled
            ? new EmojiSubstitutor(world.Emoji.Emoticons, world.Emoji.Shortcodes)
            : null;
        Scrollback = new ScrollbackBuffer(scrollbackCapacity);
        Triggers = new TriggerEngine(world.Triggers);
        Aliases = new AliasEngine(world.Aliases);
        Macros = new MacroEngine(world.Macros);
    }

    private static ILineParser CreateParser(ContentFormat format) => format switch
    {
        ContentFormat.Mxp => new MxpParser(),
        ContentFormat.Pueblo => new PuebloParser(),
        _ => new AnsiParser(),
    };

    public WorldDefinition World { get; }

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

    /// <summary>Raised for each trigger-requested script callback (consumed by the scripting layer).</summary>
    public event EventHandler<TriggerScriptInvocation>? TriggerScriptRequested;

    public bool IsConnected => _telnet?.IsConnected == true;

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
        telnet.Disconnected += OnDisconnected;

        try
        {
            await telnet.ConnectAsync(cancellationToken).ConfigureAwait(false);
            SetState(ConnectionState.Connected, null);
            PrintSystem("*** Connected.");
        }
        catch (Exception ex)
        {
            SetState(ConnectionState.Faulted, ex);
            PrintSystem($"*** Connection failed: {ex.Message}");
            throw;
        }
    }

    private void OnOutputReceived(object? sender, TelnetOutputEventArgs e)
    {
        if (e.IsPrompt)
        {
            _parser.Feed(e.Text);
            var prompt = ApplyEmoji(_parser.Flush() ?? StyledLine.Empty);
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

    /// <summary>Substitutes emoji in each span's text when enabled for this world; a no-op otherwise.</summary>
    private StyledLine ApplyEmoji(StyledLine line)
    {
        if (_emoji is null || line.IsEmpty)
        {
            return line;
        }

        StyledSpan[]? rebuilt = null;
        for (var i = 0; i < line.Spans.Count; i++)
        {
            var span = line.Spans[i];
            var replaced = _emoji.Apply(span.Text);
            if (!ReferenceEquals(replaced, span.Text) && replaced != span.Text)
            {
                rebuilt ??= line.Spans.ToArray();
                rebuilt[i] = new StyledSpan(replaced, span.Style, span.Interaction);
            }
        }

        return rebuilt is null ? line : new StyledLine(rebuilt);
    }

    /// <summary>Handles a line of user input: alias expansion, local echo, and send.</summary>
    public async Task SendUserInputAsync(string input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var expansion = Aliases.Expand(input);
        if (World.LocalEcho)
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

    private void OnDisconnected(object? sender, SessionDisconnectedEventArgs e)
    {
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

    private static ITelnetSession DefaultSessionFactory(ConnectionOptions options) =>
        new TelnetSession(new TcpTransport(options));

    public async ValueTask DisposeAsync()
    {
        Scheduler.Dispose();
        _log?.Dispose();
        if (_telnet is not null)
        {
            await _telnet.DisposeAsync().ConfigureAwait(false);
            _telnet = null;
        }
    }
}
